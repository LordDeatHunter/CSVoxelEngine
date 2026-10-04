using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Threading;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// Manages chunk loading, unloading, and mesh generation.
/// </summary>
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
public class ChunkManager(GL gl, ChunkGeometryPool? opaqueGeometry = null,
    ChunkGeometryPool? translucentGeometry = null, ChunkSaveStore? saveStore = null)
{
    public readonly ChunkMap Chunks = new();

    /// <summary>The save store this manager persists edited chunks into, or null.</summary>
    public ChunkSaveStore? SaveStore => saveStore;

    /// <summary>
    /// Every loaded chunk that currently has an opaque or a translucent mesh. The render
    /// passes walk this flat list instead of <see cref="Chunks"/>: enumerating the dictionary
    /// by <c>Values</c> takes a locked full-table copy and then hands the passes every loaded
    /// chunk, the large majority of which have nothing to draw. Order is irrelevant — each
    /// pass sorts what it draws — so entries are swap-removed.
    /// Main thread only: it is maintained by <see cref="Update"/> and
    /// <see cref="ImmediateRemesh"/>, the only places a chunk's mesh appears or disappears.
    /// </summary>
    public readonly List<Chunk> Renderable = new(4096);

    /// <summary>
    /// The <see cref="Renderable"/> subset with a translucent mesh (water, glass), so
    /// <c>CullWater</c> walks hundreds of candidates instead of every meshed chunk.
    /// Same swap-remove contract (<see cref="Chunk.TranslucentRenderIndex"/>), same thread.
    /// </summary>
    public readonly List<Chunk> TranslucentRenderable = new(512);

    /// <summary>Recycled opaque mesh buffers; see <see cref="MeshBufferPool"/>.</summary>
    public MeshBufferPool MeshBuffers { get; } = new();

    /// <summary>
    /// Bumped whenever the set of loaded, meshed chunks changes: a chunk enters or leaves
    /// <see cref="Renderable"/>, a chunk is meshed for the first time (even with an empty mesh,
    /// which never enters <see cref="Renderable"/>), or a chunk is unloaded. A consumer that
    /// derives something from which chunks are loaded and drawn (the LOD buried flags) compares
    /// it to skip the recompute when nothing moved. Main thread only.
    /// </summary>
    public int MeshVersion { get; private set; }

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
    internal int DirtyCount => _dirtyChunks.Count;

    /// <summary>Mesh-dispatcher state, for MeshSchedulingTests.</summary>
    internal int PendingMeshWorkCount => _pendingMeshWork.Count;

    internal int ActiveMeshRunnerCount => Volatile.Read(ref _activeMeshRunners);

    internal int GeneratingMeshCount => _generatingMeshes.Count;

    /// <summary>
    /// Finished meshes <see cref="ApplyCompletedMeshes"/> uploads per frame (the rest wait for the
    /// next). Defaults to <see cref="WorldGenerationConfig.MaxMeshUploadsPerFrame"/>; World raises it
    /// while a world is loading and restores it when the world starts playing. Main thread only.
    /// </summary>
    public int MeshUploadBudget { get; set; } = WorldGenerationConfig.MaxMeshUploadsPerFrame;

    /// <summary>
    /// A server world sets this: finished meshes are marked and their light published but their
    /// geometry is discarded (<see cref="Chunk.MarkMeshedHeadless"/>), since a headless world has
    /// no pools and never draws. False for the game, whose meshes upload to the shared pools.
    /// </summary>
    public bool Headless { get; set; }

    /// <summary>
    /// A multiplayer client sets this so streamed chunks light from server-supplied column heights
    /// instead of worldgen (see <see cref="Chunk.RestoreFrom(ChunkSnapshot, bool)"/>): it takes
    /// (chunkX, chunkZ) and returns the 256 heights or null when that column has not arrived.
    /// Null in singleplayer, where worldgen is the source.
    /// </summary>
    public Func<int, int, int[]?>? ColumnHeightSource { get; set; }

    /// <summary>
    /// Mesh builds <see cref="ProcessDirtyChunks"/> starts per frame. Defaults to
    /// <see cref="WorldGenerationConfig.MaxDirtyChunksPerFrame"/>; raised by World while loading and
    /// restored at the flip to playing, like <see cref="MeshUploadBudget"/>. Main thread only.
    /// </summary>
    public int DirtyStartBudget { get; set; } = WorldGenerationConfig.MaxDirtyChunksPerFrame;

    private int _loadedChunkCount;

    // The camera chunk the last unload scan ran for, and how many frames ago that was. See
    // Update: chunks only leave the load sphere when the camera crosses a chunk border.
    private Vector3? _lastUnloadCamChunk;
    private int _framesSinceUnloadScan;

    // Frames after which the unload scan runs even though the camera has not changed chunk.
    // Covers a chunk a generation worker registered just after the camera moved, which the
    // scan for that move could have missed.
    private const int UnloadScanFallbackFrames = 120;

    // The sequence number stamps each result with the order it finished in. ApplyCompletedMeshes
    // sorts by distance, and two results for the same chunk (an older one re-queued by the
    // upload budget, plus a newer rebuild) tie there, so without it the unstable sort could
    // apply them newest-first and leave the stale mesh on screen.
    private readonly ConcurrentQueue<(Vector3 pos, Chunk chunk, ChunkMeshData meshData, long seq)> _completedMeshQueue = new();
    private long _meshSequence;
    // Keyed on ChunkKey.Pack rather than the Vector3 chunk position: every probe here sits on a
    // hot path (the dirty scan, QueueChunk's six-neighbour fan-out, NeedsChunk on the generation
    // workers' scan frontier) and a packed long hashes once where a Vector3 hashes three floats
    // and compares twelve bytes per node visit (PERF_TODO D1). FluidManager's cell keys are the
    // in-repo precedent; ChunkKey deliberately reuses its 21-bit budget at chunk scale.
    private readonly ConcurrentDictionary<long, byte> _dirtyChunks = new();
    private readonly ConcurrentDictionary<long, byte> _queuedChunkPositions = new();

    // Chunks whose block data was just generated, handed to the main thread so the fluid tick
    // can wake the water they brought in (FluidManager.WakeLoadedChunks). Filled by the
    // generation workers, drained on the main thread; entries can outlive an unload, so the
    // consumer re-checks that the chunk is still the live one at its position.
    private readonly ConcurrentQueue<Chunk> _loadedChunks = new();

    // Chunks restored from the save store whose player-placed cells still have to be adopted
    // into _playerPlacedByChunk. Filled by the generation workers (CreateChunk), drained on the
    // main thread at the top of Update, since the placement index is main-thread only. Like
    // _loadedChunks, an entry can outlive an unload, so the drain re-checks the chunk is live.
    private readonly ConcurrentQueue<Chunk> _restoredChunks = new();

    // Track active mesh generation to avoid duplicates. Packed keys, like _dirtyChunks above.
    private readonly ConcurrentDictionary<long, byte> _generatingMeshes = new();

    // Mesh builds are dispatched as plain thread-pool work items rather than one
    // Task.Run(async ...) per chunk: at up to MaxDirtyChunksPerFrame starts a frame, the closure +
    // delegate + Task + state machine per start fed the streaming gen0 rate (PERF_TODO C4).
    // Scheduling now only enqueues a (pos, chunk) pair; at most MaxConcurrentMeshGenerations
    // runners are in flight and each drains the queue, so the old semaphore's cap survives with
    // nothing ever blocking on a wait. Main thread enqueues (ProcessDirtyChunks), pool threads
    // dequeue.
    private readonly ConcurrentQueue<(Vector3 pos, Chunk chunk)> _pendingMeshWork = new();
    private int _activeMeshRunners;

    /// <summary>
    /// One drain pass over <see cref="_pendingMeshWork"/>, queued straight to the thread pool
    /// instead of wrapped in a Task. It carries nothing but its manager, so scheduling a chunk
    /// costs no closure, delegate, state machine or Task - the chunk travels in the queue. One
    /// instance is allocated per dispatch, not per chunk: a runner keeps draining until the queue
    /// is empty, so dispatches are far rarer than builds under load and at most
    /// <see cref="WorldGenerationConfig.MaxConcurrentMeshGenerations"/> exist at once. A fresh
    /// instance per dispatch also means no work item is ever queued twice.
    /// </summary>
    private sealed class MeshRunner(ChunkManager owner) : IThreadPoolWorkItem
    {
        public void Execute() => owner.RunMeshWork();
    }

    // World cells the player built (integer coords): breaking one never fells a tree, it just
    // drops as-is with no toppling entity. Main thread only, like every other block edit, so
    // this stays a plain Dictionary/HashSet pair — AGENTS.md reserves the concurrent
    // collections for genuinely shared state, and nothing off the main thread reads this.
    // A chunk's entries are purged on unload to keep the index — and the felling decision —
    // truthful. With a save store attached they are first written into the chunk's snapshot
    // (PlacedIndicesOf), and a restored chunk brings them back through _restoredChunks; with no
    // store the chunk regenerates from worldgen and its placements are simply forgotten.
    // Bucketing by chunk position is what makes that purge cheap: the unload drops one bucket instead of
    // scanning every placement in the world, which was O(unloads x placements) per unload pass
    // (PERF_TODO C6). The key is the same packed chunk key Chunks is keyed on (ChunkKey, see
    // _dirtyChunks above), so the unload site already holds it. A bucket is removed as soon as
    // it empties; without that the index would grow one permanent entry per chunk ever edited,
    // trading the scan for a slow leak.
    private readonly Dictionary<long, HashSet<(int X, int Y, int Z)>> _playerPlacedByChunk = new();

    /// <summary>Whether the world cell was built by the player rather than generated.</summary>
    public bool IsPlayerPlaced(int x, int y, int z) =>
        _playerPlacedByChunk.TryGetValue(ChunkKey.PackBlock(x, y, z), out var cells) &&
        cells.Contains((x, y, z));

    /// <summary>Records a player placement (called by <see cref="Chunk.PlaceBlockAt"/>).</summary>
    public void MarkPlayerPlaced(int x, int y, int z)
    {
        var chunkKey = ChunkKey.PackBlock(x, y, z);
        if (!_playerPlacedByChunk.TryGetValue(chunkKey, out var cells))
        {
            cells = new HashSet<(int X, int Y, int Z)>();
            _playerPlacedByChunk[chunkKey] = cells;
        }

        cells.Add((x, y, z));
    }

    /// <summary>Forgets a placement once its block is gone (mined, felled or blasted).</summary>
    public void UnmarkPlayerPlaced(int x, int y, int z)
    {
        // Most callers unmark unconditionally on any block removal, so the miss path has to
        // stay a lookup that allocates nothing.
        var chunkKey = ChunkKey.PackBlock(x, y, z);
        if (!_playerPlacedByChunk.TryGetValue(chunkKey, out var cells)) return;

        cells.Remove((x, y, z));
        if (cells.Count == 0) _playerPlacedByChunk.Remove(chunkKey);
    }

    /// <summary>
    /// How many chunks currently hold at least one player placement. Diagnostics and tests
    /// only: it exists so the empty-bucket invariant above is pinnable.
    /// </summary>
    internal int PlayerPlacedChunkCount => _playerPlacedByChunk.Count;

    public void Update(Camera camera)
    {
        if (camera == null) return;

        // Before the unload scan, so a chunk restored and then unloaded again is snapshotted
        // with its placements already in the index (PlacedIndicesOf also merges any still held
        // on the chunk, so neither order loses them)
        AdoptRestoredPlacements();

        var camChunk = (camera.Position / Chunk.Size).Floor();

        // A loaded chunk can only fall out of range when the camera crosses a chunk border,
        // so scanning every loaded chunk on the frames in between finds nothing. The periodic
        // fallback covers a chunk registered by a generation worker just after a move.
        FrameProfiler.Begin(ProfileSection.ChunkUnload);
        if (camChunk != _lastUnloadCamChunk || ++_framesSinceUnloadScan >= UnloadScanFallbackFrames)
        {
            _lastUnloadCamChunk = camChunk;
            _framesSinceUnloadScan = 0;
            UnloadDistantChunks(camChunk);
        }
        FrameProfiler.End(ProfileSection.ChunkUnload);

        FrameProfiler.Begin(ProfileSection.ChunkApply);
        ApplyCompletedMeshes(camChunk);
        FrameProfiler.End(ProfileSection.ChunkApply);

        FrameProfiler.Begin(ProfileSection.ChunkDirty);
        ProcessDirtyChunks(camChunk);
        FrameProfiler.End(ProfileSection.ChunkDirty);
    }

    private void UnloadDistantChunks(Vector3 camChunk)
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
            if (Vector3.DistanceSquared(pos, camChunk) <= WorldGenerationConfig.ChunkRadiusSq)
                continue;

            if (!Chunks.TryRemovePacked(key, out var chunk)) continue;

            removed++;

            // Persist the edits before anything forgets them: Enqueue publishes the pending
            // snapshot, and only after that may the position leave _queuedChunkPositions (which
            // is what lets a generation worker queue it again and look it up in the store) and
            // the placement bucket be dropped. No profiler span of its own: this already runs
            // inside ChunkUnload, and sections cannot nest.
            if (saveStore != null && chunk.ModifiedSinceSave)
                saveStore.Enqueue(key, chunk.TakeSnapshot(PlacedIndicesOf(chunk, key)));

            // Dispose nulls both slices, so the Renderable bookkeeping has to happen first
            RemoveRenderable(chunk);
            // A chunk leaving the world changes the LOD cover even when it had nothing to draw.
            MeshVersion++;
            chunk.Dispose();
            Interlocked.Decrement(ref _loadedChunkCount);
            _queuedChunkPositions.TryRemove(key, out _);
            _generatingMeshes.TryRemove(key, out _);
            _dirtyChunks.TryRemove(key, out _);

            // The placements leave with the chunk: into its snapshot above when a store is
            // attached (a restored chunk brings them back), forgotten with the regenerated
            // chunk otherwise. The bucket IS the chunk, so that is one dictionary removal
            // instead of a scan of every placement.
            _playerPlacedByChunk.Remove(key);
        }

        // After the loop, not inside it: the position only becomes needed once it has left
        // _queuedChunkPositions too, and the gate keeps a settled world (the 120-frame fallback scan
        // at Update, which finds nothing) from bumping at all and restarting four scans twice a second.
        if (removed > 0) Interlocked.Increment(ref _chunkRemovalVersion);
    }

    /// <summary>
    /// Moves the player-placed cells of every chunk restored since the last call into the
    /// placement index, then clears them off the chunk (adopted). Main thread only. A chunk
    /// that was unloaded (or replaced) before the drain reached it is skipped: its snapshot
    /// already carried the placements back to the store.
    /// </summary>
    private void AdoptRestoredPlacements()
    {
        while (_restoredChunks.TryDequeue(out var chunk))
        {
            if (!Chunks.TryGetPacked(ChunkKey.Pack(chunk.Position), out var live) || live != chunk)
                continue;

            var placed = chunk.RestoredPlayerPlaced;
            if (placed == null) continue;

            var min = chunk.WorldMin;
            foreach (var index in placed)
            {
                var cell = min + Chunk.LocalOfIdx(index);
                MarkPlayerPlaced((int)cell.X, (int)cell.Y, (int)cell.Z);
            }

            chunk.RestoredPlayerPlaced = null;
        }
    }

    /// <summary>
    /// The chunk's player-placed cells as local <see cref="Chunk.Idx"/> indices for its
    /// snapshot: the adopted ones from the placement index plus any the chunk still holds from
    /// its restore and has not handed over yet. Null when there are none.
    /// </summary>
    private ushort[]? PlacedIndicesOf(Chunk chunk, long key)
    {
        _playerPlacedByChunk.TryGetValue(key, out var cells);
        var pending = chunk.RestoredPlayerPlaced;
        if ((cells == null || cells.Count == 0) && (pending == null || pending.Length == 0)) return null;

        var minX = (int)chunk.WorldMin.X;
        var minY = (int)chunk.WorldMin.Y;
        var minZ = (int)chunk.WorldMin.Z;
        var indices = new HashSet<ushort>();
        if (cells != null)
        {
            foreach (var (x, y, z) in cells)
                indices.Add((ushort)Chunk.Idx(x - minX, y - minY, z - minZ));
        }
        if (pending != null)
        {
            foreach (var index in pending) indices.Add(index);
        }

        var result = new ushort[indices.Count];
        indices.CopyTo(result);
        Array.Sort(result);
        return result;
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
        if (saveStore == null || maxSnapshots <= 0) return 0;

        var written = 0;
        foreach (var kvp in Chunks.Packed)
        {
            var chunk = kvp.Value;
            if (!chunk.IsBlockDataReady || !chunk.ModifiedSinceSave) continue;

            saveStore.Enqueue(kvp.Key, chunk.TakeSnapshot(PlacedIndicesOf(chunk, kvp.Key)));
            if (++written >= maxSnapshots) break;
        }

        return written;
    }

    private void ApplyCompletedMeshes(Vector3 camChunk)
    {
        // Drain into the reusable scratch to sort by distance. Distances are computed once
        // on drain: the old per-compare DistanceSquared plus the sort lambda's closure
        // allocated every frame the queue was non-empty.
        while (_completedMeshQueue.TryDequeue(out var item))
        {
            _applyScratch.Add((item.pos, item.chunk, item.meshData, item.seq,
                Vector3.DistanceSquared(item.pos, camChunk)));
        }

        if (_applyScratch.Count == 0) return;

        try
        {
            // Sort closest first, then oldest first. List.Sort is unstable, so the sequence
            // tiebreak is what guarantees a newer mesh for a chunk is applied after an older one
            // rather than being overwritten by it.
            _applyScratch.Sort((a, b) =>
            {
                var byDistance = a.distSq.CompareTo(b.distSq);
                return byDistance != 0 ? byDistance : a.seq.CompareTo(b.seq);
            });

            var appliedCount = 0;

            foreach (var (pos, chunk, meshData, seq, _) in _applyScratch)
            {
                // Skip chunks that moved out of range, were unloaded, or were replaced by a
                // freshly generated chunk at the same position while this build ran: uploading
                // into the dead one would leave a mesh in Renderable that no unload ever removes
                if (Vector3.DistanceSquared(pos, camChunk) > WorldGenerationConfig.ChunkRadiusSq ||
                    !Chunks.TryGetValue(pos, out var live) || !ReferenceEquals(live, chunk))
                {
                    MeshBuffers.Return(meshData);
                    continue;
                }

                if (appliedCount < MeshUploadBudget)
                {
                    ApplyMesh(chunk, meshData);
                    appliedCount++;
                }
                else
                {
                    // Re-queue for next frame, keeping the original sequence so a rebuild that
                    // finishes in the meantime still sorts after it
                    _completedMeshQueue.Enqueue((pos, chunk, meshData, seq));
                }
            }
        }
        finally
        {
            // Don't keep the chunk references alive between frames
            _applyScratch.Clear();
        }
    }

    // Reused across frames: the dirty entries eligible to mesh this frame, with the distance
    // the per-frame budget sorts on. Cleared after every pass so it holds no chunk references
    // at rest.
    // The packed key travels with the candidate so the over-budget re-add below reuses the one
    // the scan already computed instead of re-packing on the frame the budget is saturated.
    private readonly List<(long key, Vector3 pos, Chunk chunk, float distSq)> _dirtyCandidates = new(256);

    // Reused across frames: finished mesh builds waiting for upload, with the distance the
    // upload budget sorts on. Same clear-after-pass contract as _dirtyCandidates.
    private readonly List<(Vector3 pos, Chunk chunk, ChunkMeshData meshData, long seq, float distSq)> _applyScratch = new(64);

    private void ProcessDirtyChunks(Vector3 camChunk)
    {
        if (_dirtyChunks.IsEmpty) return;

        _dirtyCandidates.Clear();

        // The set holds thousands of entries mid-stream and every one costs several
        // dictionary probes, so the scan itself is capped: leftovers wait for the next
        // frames, which meshgen (DirtyStartBudget starts/frame, 4 concurrent builds) cannot outrun
        // anyway. Drops do not need a second visit — out-of-range entries are removed
        // on sight and missing-neighbour ones are re-added by QueueChunk on arrival.
        var scanned = 0;

        // Lock-free enumeration, no snapshot; removing through it while enumerating is safe
        foreach (var kvp in _dirtyChunks)
        {
            if (++scanned > WorldGenerationConfig.MaxDirtyScanPerFrame)
                break;

            var key = kvp.Key;
            // Unpacked once per entry and reused by everything below that still speaks Vector3:
            // the distance test, HasAllNeighbors and StartMeshGeneration. The Chunks probe takes
            // the packed key directly - it is the same key this entry is stored under.
            ChunkKey.Unpack(key, out var cx, out var cy, out var cz);
            var pos = new Vector3(cx, cy, cz);
            var distSq = Vector3.DistanceSquared(pos, camChunk);

            // Out of range, or unloaded — just drop the entry
            if (distSq > WorldGenerationConfig.ChunkRadiusSq || !Chunks.TryGetPacked(key, out var chunk))
            {
                _dirtyChunks.TryRemove(key, out _);
                continue;
            }

            // A build is already in flight for this chunk. The entry stays: it was added after
            // that build took its snapshot, so the chunk has to mesh again once it finishes.
            if (_generatingMeshes.ContainsKey(key))
                continue;

            // Block data is always ready for a chunk in Chunks (QueueChunk generates before
            // inserting), but the check is cheap and keeps the contract honest
            if (!chunk.IsBlockDataReady)
                continue;

            // Claim the entry BEFORE the neighbour test, and drop a chunk that is still
            // missing a face neighbour instead of retrying it every frame. QueueChunk
            // re-dirties all six neighbours of a chunk when it arrives (with its block data
            // already generated), so the missing neighbour's arrival is what wakes this chunk
            // back up. Removing first closes the race where that TryAdd would be a no-op
            // because this entry still existed. This is what lets _dirtyChunks reach empty at
            // rest: the rim chunks of the load sphere never get their outer neighbour until
            // the camera moves, and used to be rescanned every single frame.
            if (!_dirtyChunks.TryRemove(key, out _))
                continue;

            if (!HasAllNeighbors(pos))
                continue;

            _dirtyCandidates.Add((key, pos, chunk, distSq));
        }

        if (_dirtyCandidates.Count > DirtyStartBudget)
        {
            // Only sorted when the budget actually has to choose between candidates
            _dirtyCandidates.Sort((a, b) => a.distSq.CompareTo(b.distSq));

            for (var i = 0; i < DirtyStartBudget; i++)
                StartMeshGeneration(_dirtyCandidates[i].key, _dirtyCandidates[i].pos, _dirtyCandidates[i].chunk);

            // Everything past the budget goes back in the dirty set for next frame
            for (var i = DirtyStartBudget; i < _dirtyCandidates.Count; i++)
                _dirtyChunks.TryAdd(_dirtyCandidates[i].key, 0);
        }
        else
        {
            for (var i = 0; i < _dirtyCandidates.Count; i++)
                StartMeshGeneration(_dirtyCandidates[i].key, _dirtyCandidates[i].pos, _dirtyCandidates[i].chunk);
        }

        // Don't keep the chunk references alive between frames
        _dirtyCandidates.Clear();
    }

    /// <summary>
    /// Uploads a finished mesh to the chunk and keeps <see cref="Renderable"/> in step with it.
    /// Main thread only, like every GL call.
    /// </summary>
    private void ApplyMesh(Chunk chunk, ChunkMeshData meshData)
    {
        var wasMeshed = chunk.IsMeshed;
        if (Headless)
            chunk.MarkMeshedHeadless(meshData);
        else
            chunk.UpdateMesh(meshData, opaqueGeometry, translucentGeometry);
        // BufferSubData copies synchronously, so the lists can be recycled at once.
        MeshBuffers.Return(meshData);
        SyncRenderable(chunk);
        // The first mesh makes the chunk count as "drawn" for the LOD cover test even when it is
        // empty (all air, or enclosed) and therefore never enters Renderable
        if (!wasMeshed) MeshVersion++;
    }

    /// <summary>
    /// Adds <paramref name="chunk"/> to <see cref="Renderable"/> or drops it, to match whether
    /// it currently has anything to draw — a rebuild can also empty a chunk that had a mesh
    /// (the last block mined out of it). Main thread only.
    /// </summary>
    private void SyncRenderable(Chunk chunk)
    {
        var wants = chunk.HasMesh || chunk.HasTranslucentMesh;

        if (!wants)
        {
            RemoveRenderable(chunk);
            return;
        }

        if (chunk.RenderIndex < 0)
        {
            chunk.RenderIndex = Renderable.Count;
            Renderable.Add(chunk);
            MeshVersion++;
        }

        SyncTranslucent(chunk);
    }

    /// <summary>
    /// Keeps <see cref="TranslucentRenderable"/> matching the chunk's translucent mesh.
    /// No <see cref="MeshVersion"/> bump: its only consumer is the LOD cover gate, which
    /// reads <c>IsMeshed</c>, never slices.
    /// </summary>
    private void SyncTranslucent(Chunk chunk)
    {
        if (chunk.HasTranslucentMesh)
        {
            if (chunk.TranslucentRenderIndex >= 0) return;
            chunk.TranslucentRenderIndex = TranslucentRenderable.Count;
            TranslucentRenderable.Add(chunk);
            return;
        }

        RemoveTranslucent(chunk);
    }

    private void RemoveTranslucent(Chunk chunk)
    {
        var index = chunk.TranslucentRenderIndex;
        if (index < 0) return;

        var last = TranslucentRenderable.Count - 1;
        if (index != last)
        {
            var moved = TranslucentRenderable[last];
            TranslucentRenderable[index] = moved;
            moved.TranslucentRenderIndex = index;
        }

        TranslucentRenderable.RemoveAt(last);
        chunk.TranslucentRenderIndex = -1;
    }

    /// <summary>
    /// Swap-removes a chunk from <see cref="Renderable"/>; a no-op for a chunk that is not in
    /// the list. Must run before <see cref="Chunk.Dispose"/>, which nulls the slices this
    /// bookkeeping is keyed on. Main thread only.
    /// </summary>
    private void RemoveRenderable(Chunk chunk)
    {
        // Unload calls this while slices are still set (Dispose nulls them after), so the
        // translucent membership is dropped here too rather than trusting the caller.
        RemoveTranslucent(chunk);

        var index = chunk.RenderIndex;
        if (index < 0) return;

        var last = Renderable.Count - 1;
        if (index != last)
        {
            var moved = Renderable[last];
            Renderable[index] = moved;
            moved.RenderIndex = index;
        }

        Renderable.RemoveAt(last);
        chunk.RenderIndex = -1;
        MeshVersion++;
    }

    /// <summary>
    /// Claims the dedupe slot and queues the build. Takes the packed key alongside the position
    /// because both callers (ProcessDirtyChunks' two budget branches) already hold it.
    /// </summary>
    private void StartMeshGeneration(long key, Vector3 pos, Chunk chunk)
    {
        if (!_generatingMeshes.TryAdd(key, 0))
            return;

        _pendingMeshWork.Enqueue((pos, chunk));
        TryStartMeshRunner();
    }

    /// <summary>
    /// Queues one more runner if the concurrency cap allows. Callers must have made their work
    /// visible - enqueued it, or decremented the runner count - BEFORE calling: the interlocked
    /// read below is what orders against that store, and doing it the other way round would let
    /// the last runner exit with work still queued. The cap cannot be exceeded because the count
    /// is claimed by CAS before the item is queued, and every queued runner decrements exactly
    /// once in its finally.
    /// </summary>
    private void TryStartMeshRunner()
    {
        while (true)
        {
            // Interlocked rather than a plain or volatile read: neither orders against the
            // preceding store, and a stale "cap is full" here strands the work.
            var active = Interlocked.CompareExchange(ref _activeMeshRunners, 0, 0);
            if (active >= WorldGenerationConfig.MaxConcurrentMeshGenerations)
                return;
            if (Interlocked.CompareExchange(ref _activeMeshRunners, active + 1, active) != active)
                continue;
            // preferLocal: false - a thread's local queue is LIFO and drained only by that thread,
            // so a runner queued from inside another runner could sit behind its own work.
            ThreadPool.UnsafeQueueUserWorkItem(new MeshRunner(this), preferLocal: false);
            return;
        }
    }

    private void RunMeshWork()
    {
        try
        {
            while (_pendingMeshWork.TryDequeue(out var work))
                BuildMeshFor(work.pos, work.chunk);
        }
        finally
        {
            Interlocked.Decrement(ref _activeMeshRunners);
        }

        // Work enqueued after the last TryDequeue but before the decrement would have found the cap
        // full and queued nothing; this second look is what keeps it from being stranded.
        if (!_pendingMeshWork.IsEmpty)
            TryStartMeshRunner();
    }

    /// <summary>
    /// Waits, for at most <paramref name="timeout"/>, until every queued mesh build has run and
    /// every pool runner has exited; returns whether that happened. Main thread only: only
    /// <see cref="ProcessDirtyChunks"/> (reached through <see cref="Update"/>) starts runners, so
    /// none can start while this waits, and a runner's own second look in <c>RunMeshWork</c>
    /// only fires on work that is still queued, which this waits out too. Finished results stay
    /// in the completed queue; nothing is applied here.
    /// <para>
    /// A mesh build reads worldgen state (<c>Chunk.SubmergedSurfaceCode</c> falls back to
    /// <see cref="ColumnCache.Get"/>), so a world-seed swap must wait for this as well as
    /// for the worker joins. <c>World.SaveAndDispose</c> calls <see cref="ChunkGenerationWorker.Stop"/>,
    /// then <see cref="LodManager.Stop"/>, then this, and only when all three return true does it
    /// free the chunks through <see cref="ReleaseAll"/> and call <see cref="WorldSeed.EndWorld"/>
    /// (after which <see cref="WorldSeed.Apply"/> is legal again).
    /// </para>
    /// </summary>
    public bool DrainMeshWork(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (_pendingMeshWork.IsEmpty && Volatile.Read(ref _activeMeshRunners) == 0)
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;
            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Unloads every chunk at once and empties every table this manager keeps, handing each
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
            RemoveRenderable(chunk);
            chunk.Dispose();
        }

        // Belt and braces: every renderable chunk was in Chunks, so both lists are empty already
        Renderable.Clear();
        TranslucentRenderable.Clear();

        // Finished builds nobody will apply: their lists go back to the buffer pool
        while (_completedMeshQueue.TryDequeue(out var item))
            MeshBuffers.Return(item.meshData);

        // Empty by the precondition, drained anyway so no chunk reference outlives the world
        while (_pendingMeshWork.TryDequeue(out _)) { }
        while (_restoredChunks.TryDequeue(out _)) { }
        while (_loadedChunks.TryDequeue(out _)) { }

        _dirtyChunks.Clear();
        _queuedChunkPositions.Clear();
        _generatingMeshes.Clear();
        _playerPlacedByChunk.Clear();
        _applyScratch.Clear();
        _dirtyCandidates.Clear();

        Interlocked.Exchange(ref _loadedChunkCount, 0);
        MeshVersion++;
        Interlocked.Increment(ref _chunkRemovalVersion);
        _lastUnloadCamChunk = null;
    }

    /// <summary>
    /// One mesh build, on a thread-pool thread - the old Task body unchanged, plus a catch:
    /// Task.Run turned a throw into an unobserved task exception, while one escaping a raw work
    /// item tears the process down, so it is logged and the runner carries on exactly as
    /// <see cref="ChunkGenerationWorker"/> does. The finally sits outside the catch so a failed
    /// build still releases its dedupe slot.
    /// </summary>
    private void BuildMeshFor(Vector3 pos, Chunk chunk)
    {
        // One pack for the whole call: the re-dirty on a vanished neighbour and the finally's
        // dedupe release both take it. The work queue carries the position, not the key.
        var key = ChunkKey.Pack(pos);

        try
        {
            if (!Chunks.ContainsPacked(key))
                return;

            var meshData = chunk.GenerateMeshData(this);
            if (meshData == null)
            {
                // A face neighbour was unloaded between the scheduling check and this
                // snapshot; meshing across that hole would bake border artifacts, so the
                // build is dropped and the chunk retried once the world settles
                _dirtyChunks.TryAdd(key, 0);
                return;
            }

            _completedMeshQueue.Enqueue((pos, chunk, meshData, Interlocked.Increment(ref _meshSequence)));
        }
        catch (Exception ex)
        {
            Log.WriteLine($"Error meshing chunk {pos}: {ex}");
        }
        finally
        {
            _generatingMeshes.TryRemove(key, out _);
        }
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

        // Mark this chunk and neighbors dirty for meshing
        _dirtyChunks.TryAdd(key, 0);
        
        // CardinalOffsets is the integer companion to CardinalDirections, in the same order, so
        // the six neighbour keys come from ints in hand instead of six Vector3 additions
        var px = MathUtils.FloorToInt(pos.X);
        var py = MathUtils.FloorToInt(pos.Y);
        var pz = MathUtils.FloorToInt(pos.Z);
        foreach (var d in Vector3Directions.CardinalOffsets)
        {
            var nx = px + d.X;
            var ny = py + d.Y;
            var nz = pz + d.Z;
            if (Chunks.ContainsKey(nx, ny, nz))
                _dirtyChunks.TryAdd(ChunkKey.Pack(nx, ny, nz), 0);
        }
    }

    /// <summary>
    /// Builds the chunk at <paramref name="pos"/> with its block data ready: restored from the
    /// save store when it holds one for <paramref name="key"/> (a pending snapshot the writer
    /// has not landed yet counts, which is what makes an immediate fly-back safe), generated
    /// from worldgen otherwise. A restored chunk is also queued for the main thread to adopt its
    /// player-placed cells. Runs on a generation worker; registering the chunk is the caller's.
    /// </summary>
    internal Chunk CreateChunk(Vector3 pos, long key)
    {
        if (saveStore != null && saveStore.TryLoad(key, out var snapshot))
        {
            var restored = new Chunk(gl, pos, generateBlockData: false);
            restored.RestoreFrom(snapshot);
            _restoredChunks.Enqueue(restored);
            return restored;
        }

        return new Chunk(gl, pos, generateBlockData: true);
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
    /// <see cref="Chunk.RestoreFrom(ChunkSnapshot, bool)"/>), and schedules it and its neighbours
    /// for meshing. Main thread only. An already-present chunk is left alone: a client-side edit
    /// must not be overwritten by a snapshot that merely arrived late.
    /// </summary>
    public void ApplyStreamedChunk(Vector3 pos, ChunkSnapshot snapshot)
    {
        var key = ChunkKey.Pack(pos);
        if (Chunks.ContainsPacked(key))
            return;

        var chunk = new Chunk(gl, pos, generateBlockData: false);
        chunk.RestoreFrom(snapshot, streamedColumns: true);
        Chunks.SetPacked(key, chunk);
        Interlocked.Increment(ref _loadedChunkCount);
        _loadedChunks.Enqueue(chunk);
        _dirtyChunks.TryAdd(key, 0);

        var px = MathUtils.FloorToInt(pos.X);
        var py = MathUtils.FloorToInt(pos.Y);
        var pz = MathUtils.FloorToInt(pos.Z);
        foreach (var d in Vector3Directions.CardinalOffsets)
        {
            if (Chunks.ContainsKey(px + d.X, py + d.Y, pz + d.Z))
                _dirtyChunks.TryAdd(ChunkKey.Pack(px + d.X, py + d.Y, pz + d.Z), 0);
        }
    }

    /// <summary>
    /// Marks a chunk as dirty (needs remeshing).
    /// </summary>
    public void MarkDirty(Vector3 pos)
    {
        _dirtyChunks.TryAdd(ChunkKey.Pack(pos), 0);
    }

    /// <summary>
    /// Marks every loaded chunk within <paramref name="radiusBlocks"/> of the world-space
    /// box [<paramref name="min"/>, <paramref name="max"/>] dirty. Voxel light travels up
    /// to <see cref="WorldGenerationConfig.LightPropagationRadius"/> blocks from an edit,
    /// so all of these chunks need their light recomputed — not just face-adjacent ones.
    /// Async and budgeted like any other dirty chunk; the edited chunk itself is usually
    /// remeshed synchronously by the caller already, so it is skipped here.
    /// </summary>
    public void MarkAreaDirty(Vector3 min, Vector3 max, int radiusBlocks, Vector3? skip = null)
    {
        var low = VoxelCoords.ChunkPosOf(min - new Vector3(radiusBlocks));
        var high = VoxelCoords.ChunkPosOf(max + new Vector3(radiusBlocks));
        var x0 = (int)low.X;
        var y0 = (int)low.Y;
        var z0 = (int)low.Z;
        var x1 = (int)high.X;
        var y1 = (int)high.Y;
        var z1 = (int)high.Z;

        for (var x = x0; x <= x1; x++)
        for (var y = y0; y <= y1; y++)
        for (var z = z0; z <= z1; z++)
        {
            var pos = new Vector3(x, y, z);
            if (skip.HasValue && pos == skip.Value) continue;
            if (Chunks.ContainsKey(x, y, z))
                _dirtyChunks.TryAdd(ChunkKey.Pack(x, y, z), 0);
        }
    }

    /// <summary>
    /// Immediately regenerates the mesh for a chunk (used for block breaking).
    /// </summary>
    public void ImmediateRemesh(Vector3 pos)
    {
        // One pack for the whole call: the lookup, the _generatingMeshes probe and both dirty
        // re-adds below all take it, where the Vector3 spelling packed once for the lookup and
        // again below.
        var key = ChunkKey.Pack(pos);

        if (!Chunks.TryGetPacked(key, out var chunk)) return;
        if (!chunk.IsBlockDataReady) return;
        if (!HasAllNeighbors(pos)) return;

        var meshData = chunk.GenerateMeshData(this);
        if (meshData == null)
        {
            // Lost a face neighbour to an unload mid-call: retry rather than bake a hole in
            _dirtyChunks.TryAdd(key, 0);
            return;
        }

        ApplyMesh(chunk, meshData);

        // A build that started before this edit can still finish afterwards with its pre-edit
        // snapshot and overwrite this fresh mesh; leaving a dirty entry guarantees the
        // post-edit rebuild wins in the end.
        if (_generatingMeshes.ContainsKey(key))
            _dirtyChunks.TryAdd(key, 0);
    }

    /// <summary>
    /// Synchronously remeshes the edited chunk plus every face neighbour sharing the edited
    /// cell, so a block broken or placed on a chunk border never leaves a one-frame hole in
    /// the neighbour's mesh while the async dirty budget catches up (the neighbour's border
    /// face was culled against the old block and only a rebuild brings it back). Chunks in
    /// light radius still rebuild async through <see cref="MarkAreaDirty"/>.
    /// </summary>
    public void ImmediateRemeshWithBorders(Vector3 chunkPos, Vector3 cell)
    {
        ImmediateRemesh(chunkPos);

        var localX = (int)(cell.X - chunkPos.X * Chunk.Size);
        var localY = (int)(cell.Y - chunkPos.Y * Chunk.Size);
        var localZ = (int)(cell.Z - chunkPos.Z * Chunk.Size);

        if (localX == 0) ImmediateRemesh(chunkPos + new Vector3(-1, 0, 0));
        else if (localX == Chunk.Size - 1) ImmediateRemesh(chunkPos + new Vector3(1, 0, 0));
        if (localY == 0) ImmediateRemesh(chunkPos + new Vector3(0, -1, 0));
        else if (localY == Chunk.Size - 1) ImmediateRemesh(chunkPos + new Vector3(0, 1, 0));
        if (localZ == 0) ImmediateRemesh(chunkPos + new Vector3(0, 0, -1));
        else if (localZ == Chunk.Size - 1) ImmediateRemesh(chunkPos + new Vector3(0, 0, 1));
    }

    /// <summary>
    /// Reads a block at world coordinates. Unloaded chunks read as air.
    /// </summary>
    public byte GetBlockGlobal(int x, int y, int z)
    {
        return Chunks.TryGetValue(x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift,
            out var chunk)
            ? chunk.GetLocalBlock(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask)
            : (byte)0;
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
    public bool TryGetCell(int x, int y, int z, out byte id, out byte state)
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

    /// <summary>
    /// World Y of the fluid surface in the cell (x, y, z): <c>y + 1</c> when another fluid cell
    /// sits directly above (stacked water has no seam), otherwise <c>y</c> plus the cell's own
    /// <see cref="FluidState.SurfaceHeight"/>. Returns <see cref="float.NegativeInfinity"/>
    /// when the cell holds no fluid, so callers can compare against it unconditionally.
    /// </summary>
    public float GetFluidSurfaceGlobal(int x, int y, int z)
    {
        if (!TryGetCell(x, y, z, out var id, out var state) || !BlockType.IsFluid(id))
            return float.NegativeInfinity;

        if (TryGetCell(x, y + 1, z, out var above, out _) && BlockType.IsFluid(above))
            return y + 1f;

        return y + FluidState.SurfaceHeight(state);
    }

    /// <summary>
    /// World Y of the real surface of the fluid column the cell (x, y, z) belongs to, or
    /// <see cref="float.NegativeInfinity"/> when the cell is not a loaded fluid cell.
    /// <see cref="GetFluidSurfaceGlobal"/> only resolves one cell up; this walks the column to its
    /// top (at most <see cref="WorldGenerationConfig.FluidSurfaceSearchCells"/> cells, reading the
    /// cap's upper edge beyond that) and, when the walk ends under a solid ceiling (an overhang,
    /// a placed block, a flooded sea cave), sees through it to the column's generated water
    /// level, so the rays under a ledge still march against the sea or lake above it.
    /// </summary>
    public float GetFluidColumnTopGlobal(int x, int y, int z)
    {
        if (!TryGetCell(x, y, z, out var id, out _) || !BlockType.IsFluid(id))
            return float.NegativeInfinity;

        var top = y;
        var hitCap = true;
        var ceiling = false;
        for (var step = 0; step < WorldGenerationConfig.FluidSurfaceSearchCells; step++)
        {
            if (TryGetCell(x, top + 1, z, out var above, out _))
            {
                if (BlockType.IsFluid(above))
                {
                    top++;
                    continue;
                }

                ceiling = BlockType.IsSolid(above);
            }

            hitCap = false;
            break;
        }

        var walked = hitCap ? top + 1f : GetFluidSurfaceGlobal(x, top, z);
        if (!ceiling)
            return walked;

        var column = ColumnCache.Get(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
        var level = column.WaterLevel[((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)];
        if (level == TerrainGenerator.NoWater)
            return walked;

        return MathF.Max(walked, level + FluidState.SourceHeight);
    }

    // The live world as a FluidFlow cell reader: unloaded chunks report no cell
    private readonly struct LiveCells(ChunkManager m) : IFluidCellReader
    {
        public bool TryGetCell(int x, int y, int z, out byte id, out byte state) =>
            m.TryGetCell(x, y, z, out id, out state);
    }

    /// <summary>
    /// Unit-length horizontal direction the fluid in the cell (x, y, z) flows, or
    /// <see cref="Vector3.Zero"/> when the cell holds no fluid or the fluid is still: the
    /// normalised <see cref="FluidFlow.Raw"/> (Minecraft's <c>FlowingFluid.getFlow</c>).
    /// </summary>
    public Vector3 GetFluidFlowGlobal(int x, int y, int z) =>
        FluidFlow.Raw(new LiveCells(this), x, y, z).NormalizeSafe();

    /// <summary>The un-normalised <see cref="FluidFlow.Raw"/> flow at (x, y, z).</summary>
    internal Vector3 GetFluidFlowRawGlobal(int x, int y, int z) =>
        FluidFlow.Raw(new LiveCells(this), x, y, z);

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
    
    /// <summary>
    /// Checks if all 6 cardinal neighbors of a chunk are loaded and have block data ready.
    /// </summary>
    private bool HasAllNeighbors(Vector3 pos)
    {
        // Hoisted once, then six probes from ints in hand. CardinalOffsets is in the same order
        // as CardinalDirections, so the early return still fires on the same neighbour.
        var cx = MathUtils.FloorToInt(pos.X);
        var cy = MathUtils.FloorToInt(pos.Y);
        var cz = MathUtils.FloorToInt(pos.Z);
        foreach (var d in Vector3Directions.CardinalOffsets)
        {
            if (!Chunks.TryGetValue(cx + d.X, cy + d.Y, cz + d.Z, out var neighbor)
                || !neighbor.IsBlockDataReady)
                return false;
        }
        return true;
    }
}
