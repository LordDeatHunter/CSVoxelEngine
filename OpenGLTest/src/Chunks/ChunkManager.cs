using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Threading;
using Silk.NET.OpenGL;
using VoxelEngine.Chunks.Meshing;
using VoxelEngine.Chunks.Sources;
using VoxelEngine.Chunks.Streaming;
using VoxelEngine.Data;
using VoxelEngine.Networking.Client;
using VoxelEngine.Player;
using VoxelEngine.Rendering;
using VoxelEngine.Rendering.Geometry;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Chunks;

/// <summary>
/// The chunk lifecycle: loads (generates or restores), streams in and unloads chunks around the streaming foci,
/// persists their edits, and composes the mesh pipeline (<see cref="ChunkMeshScheduler"/>), the drawn-chunk lists
/// (<see cref="ChunkRenderLists"/>) and the player-placed index (<see cref="PlayerPlacedIndex"/>), forwarding their
/// public members; the world cell queries live here and the fluid ones in <see cref="ChunkFluidQueries"/>.
/// </summary>
public class ChunkManager
{
    private readonly GL _gl;
    private readonly ChunkSaveStore? _saveStore;
    private readonly ChunkRenderLists _renderLists = new();
    private readonly PlayerPlacedIndex _placedIndex;
    private readonly ChunkMeshScheduler _meshScheduler;

    public readonly ChunkMap Chunks = new();

    /// <summary>The cave cull over <see cref="Chunks"/>: which chunks the camera's line of sight can reach.</summary>
    public ChunkVisibilityGraph CaveCull => _caveCull ??= new ChunkVisibilityGraph(Chunks);
    private ChunkVisibilityGraph? _caveCull;

    /// <summary>Re-walks the cave cull from <paramref name="eye"/> when the camera's chunk or the meshed chunks
    /// changed (<see cref="ChunkVisibilityGraph.Update"/>). Main thread only, before the chunk passes.</summary>
    public void UpdateCaveCull(Vector3 eye) =>
        CaveCull.Update(eye, _renderLists.MeshVersion, _renderLists.VisibilityVersion);

    /// <summary>Builds an empty manager: nothing is loaded until a generation worker or a stream registers chunks.</summary>
    /// <param name="gl">The GL context new chunks are constructed with.</param>
    /// <param name="opaqueGeometry">The shared opaque chunk geometry pool finished meshes upload
    /// into, owned by <see cref="RenderManager"/>. Null in the headless tests, which only ever
    /// apply empty meshes.</param>
    /// <param name="translucentGeometry">The shared translucent (water and glass) pool; see
    /// <paramref name="opaqueGeometry"/>.</param>
    /// <param name="saveStore">Where edited chunks are persisted: a modified chunk is snapshotted
    /// into it when it unloads (and by <see cref="FlushModified"/>), and a chunk the store holds is
    /// restored from it instead of generated. Null means no persistence — the headless tests,
    /// TopplingTree's scratch manager and a storeless World — and unloaded edits are forgotten.</param>
    public ChunkManager(GL gl, ChunkGeometryPool? opaqueGeometry = null,
        ChunkGeometryPool? translucentGeometry = null, ChunkSaveStore? saveStore = null)
    {
        _gl = gl;
        _saveStore = saveStore;
        _placedIndex = new PlayerPlacedIndex(Chunks);
        _meshScheduler = new ChunkMeshScheduler(this, _renderLists, opaqueGeometry, translucentGeometry);
    }

    /// <summary>The save store this manager persists edited chunks into, or null.</summary>
    public ChunkSaveStore? SaveStore => _saveStore;

    /// <inheritdoc cref="ChunkRenderLists.Renderable"/>
    public List<Chunk> Renderable => _renderLists.Renderable;

    /// <inheritdoc cref="ChunkRenderLists.TranslucentRenderable"/>
    public List<Chunk> TranslucentRenderable => _renderLists.TranslucentRenderable;

    /// <inheritdoc cref="ChunkRenderLists.ChestChunks"/>
    public List<Chunk> ChestChunks => _renderLists.ChestChunks;

    /// <summary>Recycled opaque mesh buffers; see <see cref="MeshBufferPool"/>.</summary>
    public MeshBufferPool MeshBuffers { get; } = new();

    /// <summary>The permanent chop damage of natural trunk cells, which the mesher reads (see <see cref="WoodDamage"/>).</summary>
    public WoodDamage ChopDamage { get; } = new();

    /// <inheritdoc cref="ChunkRenderLists.MeshVersion"/>
    public int MeshVersion => _renderLists.MeshVersion;

    private int _chunkRemovalVersion;

    /// <summary>
    /// Bumped once per unload pass that actually dropped a chunk. <see cref="NeedsChunk"/> can only
    /// go from false back to true for a position when that position has left BOTH <see cref="Chunks"/>
    /// and _queuedChunkPositions, and UnloadDistantChunks is the only place in the tree that removes
    /// from either. The bump is published AFTER the whole loop, so by the time a reader sees a new
    /// value every position dropped by that pass is genuinely needed again — a reader that saw this
    /// value unchanged across an interval knows no removal pass completed during it, which is what
    /// lets the generation workers' shared scan frontier trust a prefix it proved not-needed earlier
    /// (PERF_TODO C3, see ChunkScanFrontier).
    /// Do not move the increment up next to Chunks.TryRemove: a loaded chunk is in BOTH dictionaries
    /// (QueueChunk TryAdds to _queuedChunkPositions and never removes it), so the position only becomes
    /// needed at the _queuedChunkPositions.TryRemove below, and a bump published before that announces
    /// a transition that has not happened yet — which lets a frontier skip the position permanently.
    /// Distinct from <see cref="MeshVersion"/>, which also moves on meshing events and would reset the
    /// frontier constantly mid-stream.
    /// Written on the main thread, read by the generation workers; wraparound needs 2^31 removal passes
    /// inside one worker call and is not defended against.
    /// </summary>
    public int ChunkRemovalVersion => Volatile.Read(ref _chunkRemovalVersion);

    /// <summary>
    /// How many chunks are loaded. A maintained counter rather than <c>Chunks.Count</c>,
    /// which locks every segment of the dictionary to add the per-segment counts up.
    /// </summary>
    public int LoadedChunkCount => _loadedChunkCount;

    /// <summary>Dirty-set size, for tests asserting the scan cap paces the drain.</summary>
    internal int DirtyCount => _meshScheduler.DirtyCount;

    internal bool IsChunkDirty(long key) => _meshScheduler.IsDirty(key);

    /// <summary>Mesh-dispatcher state, for MeshSchedulingTests.</summary>
    internal int PendingMeshWorkCount => _meshScheduler.PendingMeshWorkCount;

    internal int ActiveMeshRunnerCount => _meshScheduler.ActiveMeshRunnerCount;

    internal int GeneratingMeshCount => _meshScheduler.GeneratingMeshCount;

    internal ChunkMeshScheduler MeshSchedulerForTests => _meshScheduler;

    /// <inheritdoc cref="ChunkMeshScheduler.MeshUploadBudget"/>
    public int MeshUploadBudget { get => _meshScheduler.MeshUploadBudget; set => _meshScheduler.MeshUploadBudget = value; }

    /// <summary>
    /// A server world sets this: finished meshes are marked and their light published but their
    /// geometry is discarded (<see cref="Chunk.MarkMeshedHeadless"/>), since a headless world has
    /// no pools and never draws. False for the game, whose meshes upload to the shared pools.
    /// </summary>
    public bool Headless { get; set; }

    /// <summary>
    /// The terrain a mesh build, <see cref="GetFluidColumnTopGlobal"/> and the mining pop read per
    /// chunk column beyond the blocks (<see cref="ColumnTerrain"/>: heights, water levels, corner
    /// climate). Worldgen by default (<see cref="WorldgenColumnSource"/>, singleplayer and the
    /// server); a multiplayer client installs its <see cref="ClientChunkStreamer"/>, so streamed
    /// chunks (restored without worldgen, see <see cref="Chunk.RestoreFrom(ChunkSnapshot, bool)"/>)
    /// light, resolve their water surfaces and take their tint from server-supplied columns, and
    /// a chunk whose streamed columns have not arrived is not meshed yet (see
    /// <see cref="ChunkMeshScheduler.ProcessDirtyChunks"/>). Read by pool mesh builds
    /// (<see cref="ChunkLighting.FillColumns"/>), so an implementation must be thread-safe.
    /// </summary>
    public IColumnSource ColumnSource { get; set; } = WorldgenColumnSource.Instance;

    /// <summary>
    /// Called on the main thread each time a chunk takes a new mesh (its first, or a remesh after any block edit,
    /// fluid step or streamed change), after the mesh is in place. The map (<c>MapTracker</c>) listens to rescan the
    /// chunk's column; null when nothing listens (a server).
    /// </summary>
    public Action<Chunk>? ChunkMeshed { get; set; }

    /// <summary>
    /// Where this world's terrain comes from: the blocks of every chunk the save store does not
    /// hold (<see cref="CreateChunk"/>) and the <see cref="ColumnData"/> of every chunk column.
    /// Worldgen by default (<see cref="GeneratedWorldSource"/>); a server hosting a custom or
    /// imported map installs its own. Set before the generation workers start (their
    /// <c>Thread.Start</c> publishes it) and never changed afterwards; read on the generation
    /// workers (<see cref="CreateChunk"/>), the main thread (fluid wake, LOD edit hooks, spawn) and
    /// the LOD threads, so an implementation must be thread-safe.
    /// </summary>
    public IWorldSource WorldSource { get; set; } = GeneratedWorldSource.Instance;

    /// <inheritdoc cref="ChunkMeshScheduler.DirtyStartBudget"/>
    public int DirtyStartBudget { get => _meshScheduler.DirtyStartBudget; set => _meshScheduler.DirtyStartBudget = value; }

    private int _loadedChunkCount;

    // The focus array the last unload scan ran for, and how many frames ago that was. See
    // Update: chunks only leave the load sphere when a focus crosses a chunk border, and
    // StreamingFocus publishes a new array exactly then, so the reference is the whole test.
    private Vector3[]? _lastUnloadFoci;
    private int _framesSinceUnloadScan;

    // Frames after which the unload scan runs even though no focus has changed chunk.
    // Covers a chunk a generation worker registered just after a focus moved, which the
    // scan for that move could have missed.
    private const int UnloadScanFallbackFrames = 120;

    // The single camera focus the Update(Camera) overload streams around: it republishes only on
    // a chunk crossing, which keeps the reference-keyed unload gate quiet while the camera is still.
    private readonly StreamingFocus _cameraFocus = new();

    // Packed keys, like ChunkMeshScheduler's dirty set (PERF_TODO D1).
    private readonly ConcurrentDictionary<long, byte> _queuedChunkPositions = new();

    // Chunks whose block data was just generated, handed to the main thread so the fluid tick
    // can wake the water they brought in (FluidManager.WakeLoadedChunks). Filled by the
    // generation workers, drained on the main thread; entries can outlive an unload, so the
    // consumer re-checks that the chunk is still the live one at its position.
    private readonly ConcurrentQueue<Chunk> _loadedChunks = new();

    /// <inheritdoc cref="PlayerPlacedIndex.IsPlayerPlaced"/>
    public bool IsPlayerPlaced(int x, int y, int z) => _placedIndex.IsPlayerPlaced(x, y, z);

    /// <inheritdoc cref="PlayerPlacedIndex.MarkPlayerPlaced"/>
    public void MarkPlayerPlaced(int x, int y, int z) => _placedIndex.MarkPlayerPlaced(x, y, z);

    /// <inheritdoc cref="PlayerPlacedIndex.UnmarkPlayerPlaced"/>
    public void UnmarkPlayerPlaced(int x, int y, int z) => _placedIndex.UnmarkPlayerPlaced(x, y, z);

    /// <inheritdoc cref="PlayerPlacedIndex.PlayerPlacedChunkCount"/>
    internal int PlayerPlacedChunkCount => _placedIndex.PlayerPlacedChunkCount;

    /// <summary>
    /// One frame streaming around <paramref name="camera"/> alone: the single-focus form of
    /// <see cref="Update(Vector3[])"/>, for callers with one camera and no
    /// <see cref="StreamingFocus"/> of their own. A null camera does nothing.
    /// </summary>
    public void Update(Camera camera)
    {
        if (camera == null) return;

        _cameraFocus.SetEye(camera.Position);
        Update(_cameraFocus.Chunks);
    }

    /// <summary>
    /// One frame of chunk lifecycle around every focus in <paramref name="foci"/> (chunk positions,
    /// a <see cref="StreamingFocus.Chunks"/> snapshot): chunks outside the load radius of all of
    /// them unload, and finished meshes and dirty chunks are taken nearest-focus first. Pass the
    /// same array reference for as long as the foci are unchanged - a new reference is what runs the
    /// unload scan. An empty array streams nothing: no unload, no upload, no new build, and every
    /// queue is kept for the frame a focus comes back. Main thread only.
    /// </summary>
    public void Update(Vector3[] foci)
    {
        // Before the unload scan, so a chunk restored and then unloaded again is snapshotted
        // with its placements already in the index (PlacedIndicesOf also merges any still held
        // on the chunk, so neither order loses them)
        _placedIndex.AdoptRestoredPlacements();

        if (foci.Length == 0) return;

        // A loaded chunk can only fall out of range when a focus crosses a chunk border (or one
        // leaves), which is exactly when a new focus array is published, so scanning every loaded
        // chunk on the frames in between finds nothing. The periodic fallback covers a chunk
        // registered by a generation worker just after a move.
        FrameProfiler.Begin(ProfileSection.ChunkUnload);
        if (!ReferenceEquals(foci, _lastUnloadFoci) || ++_framesSinceUnloadScan >= UnloadScanFallbackFrames)
        {
            _lastUnloadFoci = foci;
            _framesSinceUnloadScan = 0;
            UnloadDistantChunks(foci);
        }
        FrameProfiler.End(ProfileSection.ChunkUnload);

        FrameProfiler.Begin(ProfileSection.ChunkApply);
        _meshScheduler.ApplyCompletedMeshes(foci);
        FrameProfiler.End(ProfileSection.ChunkApply);

        FrameProfiler.Begin(ProfileSection.ChunkDirty);
        _meshScheduler.ProcessDirtyChunks(foci);
        FrameProfiler.End(ProfileSection.ChunkDirty);
    }

    /// <summary>
    /// Drops every loaded chunk that lies outside the load radius of every focus: a chunk stays
    /// while ANY focus keeps it, so two players far apart each keep their own sphere.
    /// </summary>
    private void UnloadDistantChunks(Vector3[] foci)
    {
        var removed = 0;

        // The ConcurrentDictionary enumerator is lock-free and copies nothing (unlike Keys),
        // and removing entries through it while enumerating is explicitly supported. Enumerating
        // the PACKED table hands the key straight to the four dictionary removals below instead
        // of packing the position back up once per unloaded chunk.
        foreach (var kvp in Chunks.Packed)
        {
            var key = kvp.Key;
            var pos = ChunkKey.ToVector3(key);
            if (StreamingFocus.WithinRadius(foci, pos, StreamingConfig.ChunkRadiusSq))
                continue;

            if (!Chunks.TryRemovePacked(key, out var chunk)) continue;

            removed++;

            // Persist the edits before anything forgets them: Enqueue publishes the pending
            // snapshot, and only after that may the position leave _queuedChunkPositions (which
            // is what lets a generation worker queue it again and look it up in the store) and
            // the placement bucket be dropped. No profiler span of its own: this already runs
            // inside ChunkUnload, and sections cannot nest.
            if (_saveStore != null && chunk.ModifiedSinceSave)
                _saveStore.Enqueue(key, chunk.TakeSnapshot(_placedIndex.PlacedIndicesOf(chunk, key)));

            // Dispose nulls both slices, so the Renderable bookkeeping has to happen first
            _renderLists.RemoveRenderable(chunk);
            // A chunk leaving the world changes the LOD cover even when it had nothing to draw.
            _renderLists.MeshVersion++;
            chunk.Dispose();
            Interlocked.Decrement(ref _loadedChunkCount);
            _queuedChunkPositions.TryRemove(key, out _);
            _meshScheduler.ForgetChunk(key);

            // The placements leave with the chunk: into its snapshot above when a store is
            // attached (a restored chunk brings them back), forgotten with the regenerated
            // chunk otherwise. The bucket IS the chunk, so that is one dictionary removal
            // instead of a scan of every placement.
            _placedIndex.ForgetChunk(key);
        }

        // After the loop, not inside it: the position only becomes needed once it has left
        // _queuedChunkPositions too, and the gate keeps a settled world (the 120-frame fallback scan
        // at Update, which finds nothing) from bumping at all and restarting four scans twice a second.
        if (removed > 0) Interlocked.Increment(ref _chunkRemovalVersion);
    }

    /// <summary>
    /// Snapshots up to <paramref name="maxSnapshots"/> loaded chunks holding unsaved edits into
    /// the save store without unloading them, and returns how many it wrote (0 with no store).
    /// The autosave calls it with a per-call cap and continues next frame while the cap is hit;
    /// a final save passes <see cref="int.MaxValue"/>. Main thread only; not profiler-bracketed,
    /// since the caller already runs inside its own section.
    /// </summary>
    public int FlushModified(int maxSnapshots)
    {
        if (_saveStore == null || maxSnapshots <= 0) return 0;

        var written = 0;
        foreach (var kvp in Chunks.Packed)
        {
            var chunk = kvp.Value;
            if (!chunk.IsBlockDataReady || !chunk.ModifiedSinceSave) continue;

            _saveStore.Enqueue(kvp.Key, chunk.TakeSnapshot(_placedIndex.PlacedIndicesOf(chunk, kvp.Key)));
            if (++written >= maxSnapshots) break;
        }

        return written;
    }

    /// <summary>
    /// Unloads every chunk at once and empties every table this manager and its components keep, handing each
    /// chunk's geometry-pool slices back to the shared pools (which stay alive for the next
    /// world). Takes no snapshots: the caller has already flushed the modified chunks. Main
    /// thread only (it frees GL slices).
    /// <para>
    /// PRECONDITION: the generation worker is stopped and joined
    /// (<see cref="ChunkGenerationWorker.Stop"/> returned true) and <see cref="DrainMeshWork"/>
    /// returned true, so no thread can add to <see cref="Chunks"/>, the completed-mesh queue or
    /// the pending mesh work while this runs. Never call it while the workers run: a straggler
    /// could register a chunk after the sweep, or write into a chunk that was just freed.
    /// Calling it twice is a no-op the second time.
    /// </para>
    /// </summary>
    public void ReleaseAll()
    {
        foreach (var kvp in Chunks.Packed)
        {
            if (!Chunks.TryRemovePacked(kvp.Key, out var chunk)) continue;

            // Dispose nulls both slices, so the Renderable bookkeeping has to happen first,
            // exactly as UnloadDistantChunks orders it
            _renderLists.RemoveRenderable(chunk);
            chunk.Dispose();
        }

        // Belt and braces: every renderable chunk was in Chunks, so both lists are empty already
        Renderable.Clear();
        TranslucentRenderable.Clear();
        ChestChunks.Clear();

        _meshScheduler.Release();
        _placedIndex.Release();
        while (_loadedChunks.TryDequeue(out _)) { }

        _queuedChunkPositions.Clear();

        Interlocked.Exchange(ref _loadedChunkCount, 0);
        _renderLists.MeshVersion++;
        Interlocked.Increment(ref _chunkRemovalVersion);
        _lastUnloadFoci = null;
    }

    /// <summary>
    /// Creates a chunk and generates (or restores, see <see cref="CreateChunk"/>) its block
    /// data synchronously.
    /// Called from the generation worker thread which already iterates by distance.
    /// </summary>
    public void QueueChunk(Vector3 pos)
    {
        var key = ChunkKey.Pack(pos);
        if (Chunks.ContainsPacked(key) || !_queuedChunkPositions.TryAdd(key, 0))
            return;

        // Create chunk AND generate block data right here.
        // This is called from the generation worker's background thread,
        // which already processes closest chunks first.
        var chunk = CreateChunk(pos, key);
        Chunks.SetPacked(key, chunk);
        Interlocked.Increment(ref _loadedChunkCount);
        _loadedChunks.Enqueue(chunk);
        // Only once published: AdoptRestoredPlacements skips a chunk that is not the live one at
        // its position, so a restored chunk queued before SetPacked could be dequeued early, read
        // as stale and lose its player-placed cells for the session.
        if (chunk.IsRestored)
            _placedIndex.EnqueueRestored(chunk);

        // Mark this chunk and neighbors dirty for meshing
        _meshScheduler.MarkDirtyKey(key);
        
        // CardinalOffsets is the integer companion to CardinalDirections, in the same order, so
        // the six neighbor keys come from ints in hand instead of six Vector3 additions
        var px = MathUtils.FloorToInt(pos.X);
        var py = MathUtils.FloorToInt(pos.Y);
        var pz = MathUtils.FloorToInt(pos.Z);
        foreach (var d in Vector3Directions.CardinalOffsets)
        {
            var nx = px + d.X;
            var ny = py + d.Y;
            var nz = pz + d.Z;
            if (Chunks.ContainsKey(nx, ny, nz))
                _meshScheduler.MarkDirtyKey(ChunkKey.Pack(nx, ny, nz));
        }
    }

    /// <summary>
    /// Builds the chunk at <paramref name="pos"/> with its block data ready; the single seam for
    /// where a chunk's blocks come from. The save store wins: a chunk it holds for
    /// <paramref name="key"/> (a pending snapshot the writer has not landed yet counts, which is
    /// what makes an immediate fly-back safe) is restored against the world source's column.
    /// Otherwise <see cref="WorldSource"/> fills it, which for the default source is worldgen
    /// (<see cref="Chunk.IsRestored"/> tells a generated chunk from a restored or supplied one).
    /// Runs on a generation worker; registering the chunk is the caller's, and so is queueing a
    /// restored one for the main thread to adopt its player-placed cells, which must happen after
    /// the chunk is published (see <see cref="QueueChunk"/>).
    /// </summary>
    internal Chunk CreateChunk(Vector3 pos, long key)
    {
        var cx = MathUtils.FloorToInt(pos.X);
        var cz = MathUtils.FloorToInt(pos.Z);
        if (_saveStore != null && _saveStore.TryLoad(key, out var snapshot))
        {
            var restored = new Chunk(_gl, pos, generateBlockData: false);
            restored.RestoreFrom(snapshot, WorldSource.GetColumn(cx, cz));
            return restored;
        }

        // The default source's Fill is the GenerateBlockData call the constructor used to make
        var chunk = new Chunk(_gl, pos, generateBlockData: false);
        WorldSource.Fill(chunk);
        return chunk;
    }

    /// <summary>
    /// Pops one chunk that finished generating since the last call, oldest first. The caller
    /// must confirm it is still the chunk registered at its position, because an unload can
    /// happen between the enqueue and the drain.
    /// </summary>
    public bool TryDequeueLoaded(out Chunk chunk) => _loadedChunks.TryDequeue(out chunk);

    /// <summary>
    /// A network snapshot of the loaded chunk at a position, for a server streaming it to a client.
    /// False when that chunk is not loaded (it is never generated here, so a client request cannot
    /// force extra generation). Main thread only, like every chunk-table read.
    /// </summary>
    public bool TryCaptureChunk(int cx, int cy, int cz, [MaybeNullWhen(false)] out ChunkSnapshot snapshot)
    {
        snapshot = null;
        if (!Chunks.TryGetValue(cx, cy, cz, out var chunk) || !chunk.IsBlockDataReady)
            return false;
        snapshot = chunk.CaptureForNetwork();
        return true;
    }

    /// <summary>
    /// Registers a chunk streamed from the server, restored with no worldgen (see
    /// <see cref="Chunk.RestoreFrom(ChunkSnapshot, bool)"/>), and schedules it and its neighbors
    /// for meshing. Main thread only. An already-present chunk is left alone: a client-side edit
    /// must not be overwritten by a snapshot that merely arrived late.
    /// </summary>
    public void ApplyStreamedChunk(Vector3 pos, ChunkSnapshot snapshot)
    {
        var key = ChunkKey.Pack(pos);
        if (Chunks.ContainsPacked(key))
            return;

        var chunk = new Chunk(_gl, pos, generateBlockData: false);
        chunk.RestoreFrom(snapshot, streamedColumns: true);
        Chunks.SetPacked(key, chunk);
        Interlocked.Increment(ref _loadedChunkCount);
        // Not queued on _loadedChunks: that queue feeds the worldgen water wake
        // (FluidManager.WakeLoadedChunks), which a client never runs, so every streamed chunk
        // would sit in it for the rest of the session.
        _meshScheduler.MarkDirtyKey(key);

        var px = MathUtils.FloorToInt(pos.X);
        var py = MathUtils.FloorToInt(pos.Y);
        var pz = MathUtils.FloorToInt(pos.Z);
        foreach (var d in Vector3Directions.CardinalOffsets)
        {
            if (Chunks.ContainsKey(px + d.X, py + d.Y, pz + d.Z))
                _meshScheduler.MarkDirtyKey(ChunkKey.Pack(px + d.X, py + d.Y, pz + d.Z));
        }
    }

    /// <inheritdoc cref="ChunkMeshScheduler.DrainMeshWork"/>
    public bool DrainMeshWork(TimeSpan timeout) => _meshScheduler.DrainMeshWork(timeout);

    /// <inheritdoc cref="ChunkMeshScheduler.MarkDirty"/>
    public void MarkDirty(Vector3 pos) => _meshScheduler.MarkDirty(pos);

    /// <inheritdoc cref="ChunkMeshScheduler.MarkAreaDirty"/>
    public void MarkAreaDirty(Vector3 min, Vector3 max, int radiusBlocks, Vector3? skip = null) => _meshScheduler.MarkAreaDirty(min, max, radiusBlocks, skip);

    /// <inheritdoc cref="ChunkMeshScheduler.ImmediateRemesh"/>
    public void ImmediateRemesh(Vector3 pos) => _meshScheduler.ImmediateRemesh(pos);

    /// <inheritdoc cref="ChunkMeshScheduler.ImmediateRemeshWithBorders"/>
    public void ImmediateRemeshWithBorders(Vector3 chunkPos, Vector3 cell) => _meshScheduler.ImmediateRemeshWithBorders(chunkPos, cell);

    /// <summary>
    /// Reads a block at world coordinates. Unloaded chunks read as air.
    /// </summary>
    public ushort GetBlockGlobal(int x, int y, int z)
    {
        return Chunks.TryGetValue(x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift,
            out var chunk)
            ? chunk.GetLocalBlock(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask)
            : BlockType.Air;
    }

    /// <summary>
    /// The loaded chunk owning a world cell, or null when it is not loaded. A chunk present in
    /// <see cref="Chunks"/> always has ready block data (<see cref="QueueChunk"/> generates
    /// before inserting), but the flag is checked anyway so the contract holds if that changes.
    /// </summary>
    public Chunk? ChunkAt(int x, int y, int z)
    {
        return Chunks.TryGetValue(x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift,
            out var chunk) && chunk.IsBlockDataReady ? chunk : null;
    }

    /// <summary>
    /// Reads a world cell's block id and state byte, returning false when its chunk is not
    /// loaded. The fluid simulation must use this rather than <see cref="GetBlockGlobal"/>:
    /// "unloaded" reads as air there, which would make water pour into unloaded space.
    /// </summary>
    public bool TryGetCell(int x, int y, int z, out ushort id, out byte state)
    {
        var chunk = ChunkAt(x, y, z);
        if (chunk == null)
        {
            id = 0;
            state = 0;
            return false;
        }

        var lx = x & Chunk.SizeMask;
        var ly = y & Chunk.SizeMask;
        var lz = z & Chunk.SizeMask;
        id = chunk.GetLocalBlock(lx, ly, lz);
        state = chunk.GetLocalState(lx, ly, lz);
        return true;
    }

    /// <summary>Reads a world cell's state byte; 0 when its chunk is not loaded (which is also
    /// what a source reads as, so pair it with a loaded check when that matters).</summary>
    public byte GetStateGlobal(int x, int y, int z)
    {
        var chunk = ChunkAt(x, y, z);
        return chunk?.GetLocalState(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask) ?? 0;
    }

    /// <summary>
    /// Reads a world cell's sun and block light (0..15) from the snapshot its chunk's current
    /// mesh was baked with. Returns false when the chunk is unloaded or has not published a
    /// snapshot (not meshed yet, or an all-air/fully-enclosed chunk).
    /// </summary>
    public bool TryGetLightGlobal(int x, int y, int z, out byte sun, out byte block)
    {
        var chunk = ChunkAt(x, y, z);
        if (chunk == null)
        {
            sun = 0;
            block = 0;
            return false;
        }

        var lx = x & Chunk.SizeMask;
        var ly = y & Chunk.SizeMask;
        var lz = z & Chunk.SizeMask;
        return chunk.TryGetLocalLight(lx, ly, lz, out sun, out block);
    }

    /// <inheritdoc cref="ChunkFluidQueries.GetFluidSurfaceGlobal"/>
    public float GetFluidSurfaceGlobal(int x, int y, int z) => ChunkFluidQueries.GetFluidSurfaceGlobal(this, x, y, z);

    /// <inheritdoc cref="ChunkFluidQueries.GetFluidColumnTopGlobal"/>
    public float GetFluidColumnTopGlobal(int x, int y, int z) => ChunkFluidQueries.GetFluidColumnTopGlobal(this, x, y, z);

    /// <inheritdoc cref="ChunkFluidQueries.GetFluidFlowGlobal"/>
    public Vector3 GetFluidFlowGlobal(int x, int y, int z) => ChunkFluidQueries.GetFluidFlowGlobal(this, x, y, z);

    /// <inheritdoc cref="ChunkFluidQueries.GetFluidFlowRawGlobal"/>
    internal Vector3 GetFluidFlowRawGlobal(int x, int y, int z) => ChunkFluidQueries.GetFluidFlowRawGlobal(this, x, y, z);

    /// <summary>
    /// Checks if a chunk needs to be created at the given position.
    /// </summary>
    public bool NeedsChunk(Vector3 pos)
    {
        // One pack for both probes. The && still short-circuits, so a position already loaded
        // costs exactly one probe, as it did before.
        var key = ChunkKey.Pack(pos);
        return !Chunks.ContainsPacked(key) && !_queuedChunkPositions.ContainsKey(key);
    }
}
