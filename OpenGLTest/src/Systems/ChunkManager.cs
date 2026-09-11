using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
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
public class ChunkManager(GL gl, ChunkGeometryPool? opaqueGeometry = null,
    ChunkGeometryPool? translucentGeometry = null)
{
    public readonly ConcurrentDictionary<Vector3, Chunk> Chunks = new();

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

    /// <summary>
    /// How many chunks are loaded. A maintained counter rather than <c>Chunks.Count</c>,
    /// which locks every segment of the dictionary to add the per-segment counts up.
    /// </summary>
    public int LoadedChunkCount => _loadedChunkCount;

    /// <summary>Dirty-set size, for tests asserting the scan cap paces the drain.</summary>
    internal int DirtyCount => _dirtyChunks.Count;

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
    private readonly ConcurrentDictionary<Vector3, byte> _dirtyChunks = new();
    private readonly ConcurrentDictionary<Vector3, byte> _queuedChunkPositions = new();

    // Chunks whose block data was just generated, handed to the main thread so the fluid tick
    // can wake the water they brought in (FluidManager.WakeLoadedChunks). Filled by the
    // generation workers, drained on the main thread; entries can outlive an unload, so the
    // consumer re-checks that the chunk is still the live one at its position.
    private readonly ConcurrentQueue<Chunk> _loadedChunks = new();

    // Track active mesh generation to avoid duplicates
    private readonly ConcurrentDictionary<Vector3, byte> _generatingMeshes = new();
    private readonly SemaphoreSlim _meshSemaphore = new(WorldGenerationConfig.MaxConcurrentMeshGenerations);

    // World cells the player built (integer coords): breaking one never fells a tree, it just
    // drops as-is with no toppling entity. Main thread only, like every other block edit.
    // Unloaded chunks regenerate from worldgen (edits don't persist), so their entries are
    // purged on unload to keep the set — and the felling decision — truthful.
    private readonly HashSet<(int X, int Y, int Z)> _playerPlaced = new();

    /// <summary>Whether the world cell was built by the player rather than generated.</summary>
    public bool IsPlayerPlaced(int x, int y, int z) => _playerPlaced.Contains((x, y, z));

    /// <summary>Records a player placement (called by <see cref="Chunk.PlaceBlockAt"/>).</summary>
    public void MarkPlayerPlaced(int x, int y, int z) => _playerPlaced.Add((x, y, z));

    /// <summary>Forgets a placement once its block is gone (mined, felled or blasted).</summary>
    public void UnmarkPlayerPlaced(int x, int y, int z) => _playerPlaced.Remove((x, y, z));

    public void Update(Camera camera)
    {
        if (camera == null) return;

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
        // The ConcurrentDictionary enumerator is lock-free and copies nothing (unlike Keys),
        // and removing entries through it while enumerating is explicitly supported.
        foreach (var kvp in Chunks)
        {
            var pos = kvp.Key;
            if (Vector3.DistanceSquared(pos, camChunk) <= WorldGenerationConfig.ChunkRadiusSq)
                continue;

            if (!Chunks.TryRemove(pos, out var chunk)) continue;

            // Dispose nulls both slices, so the Renderable bookkeeping has to happen first
            RemoveRenderable(chunk);
            // A chunk leaving the world changes the LOD cover even when it had nothing to draw.
            MeshVersion++;
            chunk.Dispose();
            Interlocked.Decrement(ref _loadedChunkCount);
            _queuedChunkPositions.TryRemove(pos, out _);
            _generatingMeshes.TryRemove(pos, out _);
            _dirtyChunks.TryRemove(pos, out _);

            // Edits don't survive an unload (the chunk regenerates from worldgen), so
            // placements inside it stop being player-placed too
            var x0 = (int)pos.X * Chunk.Size;
            var y0 = (int)pos.Y * Chunk.Size;
            var z0 = (int)pos.Z * Chunk.Size;
            _playerPlaced.RemoveWhere(cell =>
                cell.X >= x0 && cell.X < x0 + Chunk.Size &&
                cell.Y >= y0 && cell.Y < y0 + Chunk.Size &&
                cell.Z >= z0 && cell.Z < z0 + Chunk.Size);
        }
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

                if (appliedCount < WorldGenerationConfig.MaxMeshUploadsPerFrame)
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
    private readonly List<(Vector3 pos, Chunk chunk, float distSq)> _dirtyCandidates = new(256);

    // Reused across frames: finished mesh builds waiting for upload, with the distance the
    // upload budget sorts on. Same clear-after-pass contract as _dirtyCandidates.
    private readonly List<(Vector3 pos, Chunk chunk, ChunkMeshData meshData, long seq, float distSq)> _applyScratch = new(64);

    private void ProcessDirtyChunks(Vector3 camChunk)
    {
        if (_dirtyChunks.IsEmpty) return;

        _dirtyCandidates.Clear();

        // The set holds thousands of entries mid-stream and every one costs several
        // dictionary probes, so the scan itself is capped: leftovers wait for the next
        // frames, which meshgen (64 starts/frame, 4 concurrent builds) cannot outrun
        // anyway. Drops do not need a second visit — out-of-range entries are removed
        // on sight and missing-neighbour ones are re-added by QueueChunk on arrival.
        var scanned = 0;

        // Lock-free enumeration, no snapshot; removing through it while enumerating is safe
        foreach (var kvp in _dirtyChunks)
        {
            if (++scanned > WorldGenerationConfig.MaxDirtyScanPerFrame)
                break;

            var pos = kvp.Key;
            var distSq = Vector3.DistanceSquared(pos, camChunk);

            // Out of range, or unloaded — just drop the entry
            if (distSq > WorldGenerationConfig.ChunkRadiusSq || !Chunks.TryGetValue(pos, out var chunk))
            {
                _dirtyChunks.TryRemove(pos, out _);
                continue;
            }

            // A build is already in flight for this chunk. The entry stays: it was added after
            // that build took its snapshot, so the chunk has to mesh again once it finishes.
            if (_generatingMeshes.ContainsKey(pos))
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
            if (!_dirtyChunks.TryRemove(pos, out _))
                continue;

            if (!HasAllNeighbors(pos))
                continue;

            _dirtyCandidates.Add((pos, chunk, distSq));
        }

        if (_dirtyCandidates.Count > WorldGenerationConfig.MaxDirtyChunksPerFrame)
        {
            // Only sorted when the budget actually has to choose between candidates
            _dirtyCandidates.Sort((a, b) => a.distSq.CompareTo(b.distSq));

            for (var i = 0; i < WorldGenerationConfig.MaxDirtyChunksPerFrame; i++)
                StartMeshGeneration(_dirtyCandidates[i].pos, _dirtyCandidates[i].chunk);

            // Everything past the budget goes back in the dirty set for next frame
            for (var i = WorldGenerationConfig.MaxDirtyChunksPerFrame; i < _dirtyCandidates.Count; i++)
                _dirtyChunks.TryAdd(_dirtyCandidates[i].pos, 0);
        }
        else
        {
            for (var i = 0; i < _dirtyCandidates.Count; i++)
                StartMeshGeneration(_dirtyCandidates[i].pos, _dirtyCandidates[i].chunk);
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

        if (chunk.RenderIndex >= 0) return;

        chunk.RenderIndex = Renderable.Count;
        Renderable.Add(chunk);
        MeshVersion++;
    }

    /// <summary>
    /// Swap-removes a chunk from <see cref="Renderable"/>; a no-op for a chunk that is not in
    /// the list. Must run before <see cref="Chunk.Dispose"/>, which nulls the slices this
    /// bookkeeping is keyed on. Main thread only.
    /// </summary>
    private void RemoveRenderable(Chunk chunk)
    {
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

    private void StartMeshGeneration(Vector3 pos, Chunk chunk)
    {
        if (!_generatingMeshes.TryAdd(pos, 0))
            return;
        
        Task.Run(async () =>
        {
            await _meshSemaphore.WaitAsync();
            try
            {
                if (!Chunks.ContainsKey(pos))
                    return;

                var meshData = chunk.GenerateMeshData(this);
                if (meshData == null)
                {
                    // A face neighbour was unloaded between the scheduling check and this
                    // snapshot; meshing across that hole would bake border artifacts, so the
                    // build is dropped and the chunk retried once the world settles
                    _dirtyChunks.TryAdd(pos, 0);
                    return;
                }

                _completedMeshQueue.Enqueue((pos, chunk, meshData, Interlocked.Increment(ref _meshSequence)));
            }
            finally
            {
                _generatingMeshes.TryRemove(pos, out _);
                _meshSemaphore.Release();
            }
        });
    }

    /// <summary>
    /// Creates a chunk and generates its block data synchronously.
    /// Called from the generation worker thread which already iterates by distance.
    /// </summary>
    public void QueueChunk(Vector3 pos)
    {
        if (Chunks.ContainsKey(pos) || !_queuedChunkPositions.TryAdd(pos, 0))
            return;

        // Create chunk AND generate block data right here.
        // This is called from the generation worker's background thread,
        // which already processes closest chunks first.
        var chunk = new Chunk(gl, pos, generateBlockData: true);
        Chunks[pos] = chunk;
        Interlocked.Increment(ref _loadedChunkCount);
        _loadedChunks.Enqueue(chunk);

        // Mark this chunk and neighbors dirty for meshing
        _dirtyChunks.TryAdd(pos, 0);
        
        foreach (var offset in Vector3Directions.CardinalDirections)
        {
            var neighborPos = pos + offset;
            if (Chunks.ContainsKey(neighborPos))
                _dirtyChunks.TryAdd(neighborPos, 0);
        }
    }

    /// <summary>
    /// Pops one chunk that finished generating since the last call, oldest first. The caller
    /// must confirm it is still the chunk registered at its position, because an unload can
    /// happen between the enqueue and the drain.
    /// </summary>
    public bool TryDequeueLoaded(out Chunk chunk) => _loadedChunks.TryDequeue(out chunk);

    /// <summary>
    /// Marks a chunk as dirty (needs remeshing).
    /// </summary>
    public void MarkDirty(Vector3 pos)
    {
        _dirtyChunks.TryAdd(pos, 0);
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
        var x0 = (int)MathF.Floor((min.X - radiusBlocks) / Chunk.Size);
        var y0 = (int)MathF.Floor((min.Y - radiusBlocks) / Chunk.Size);
        var z0 = (int)MathF.Floor((min.Z - radiusBlocks) / Chunk.Size);
        var x1 = (int)MathF.Floor((max.X + radiusBlocks) / Chunk.Size);
        var y1 = (int)MathF.Floor((max.Y + radiusBlocks) / Chunk.Size);
        var z1 = (int)MathF.Floor((max.Z + radiusBlocks) / Chunk.Size);

        for (var x = x0; x <= x1; x++)
        for (var y = y0; y <= y1; y++)
        for (var z = z0; z <= z1; z++)
        {
            var pos = new Vector3(x, y, z);
            if (skip.HasValue && pos == skip.Value) continue;
            if (Chunks.ContainsKey(pos))
                _dirtyChunks.TryAdd(pos, 0);
        }
    }

    /// <summary>
    /// Immediately regenerates the mesh for a chunk (used for block breaking).
    /// </summary>
    public void ImmediateRemesh(Vector3 pos)
    {
        if (!Chunks.TryGetValue(pos, out var chunk)) return;
        if (!chunk.IsBlockDataReady) return;
        if (!HasAllNeighbors(pos)) return;

        var meshData = chunk.GenerateMeshData(this);
        if (meshData == null)
        {
            // Lost a face neighbour to an unload mid-call: retry rather than bake a hole in
            _dirtyChunks.TryAdd(pos, 0);
            return;
        }

        ApplyMesh(chunk, meshData);

        // A build that started before this edit can still finish afterwards with its pre-edit
        // snapshot and overwrite this fresh mesh; leaving a dirty entry guarantees the
        // post-edit rebuild wins in the end.
        if (_generatingMeshes.ContainsKey(pos))
            _dirtyChunks.TryAdd(pos, 0);
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
        var chunkPos = new Vector3(x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift);
        return Chunks.TryGetValue(chunkPos, out var chunk)
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
        var chunkPos = new Vector3(x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift);
        return Chunks.TryGetValue(chunkPos, out var chunk) && chunk.IsBlockDataReady ? chunk : null;
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

    // The four horizontal neighbours a flow can be pulled toward, as (dx, dz)
    private static readonly (int X, int Z)[] FlowNeighbours = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>
    /// Unit-length horizontal direction the fluid in the cell (x, y, z) flows, or
    /// <see cref="Vector3.Zero"/> when the cell holds no fluid or the fluid is still. Mirrors
    /// Minecraft's <c>FlowingFluid.getFlow</c>: every horizontal neighbour pulls the flow toward
    /// itself in proportion to how much lower its own surface sits, a replaceable cell the fluid
    /// can drop off into counting as a whole source lower still, and a solid neighbour
    /// contributing nothing. A pool of sources therefore cancels out to zero, while a skirt
    /// points away from its source and toward the edge it pours over.
    /// </summary>
    public Vector3 GetFluidFlowGlobal(int x, int y, int z)
    {
        if (!TryGetCell(x, y, z, out var id, out var state) || !BlockType.IsFluid(id))
            return Vector3.Zero;

        // The cell's own level, deliberately not GetFluidSurfaceGlobal: pinning a cell with
        // fluid above it to a full block would flatten a falling column's gradient to nothing
        var ownHeight = FluidState.SurfaceHeight(state);
        var flow = Vector3.Zero;

        foreach (var (dx, dz) in FlowNeighbours)
        {
            if (!TryGetCell(x + dx, y, z + dz, out var neighbourId, out var neighbourState))
                continue;

            float neighbourHeight;
            if (neighbourId == id)
            {
                neighbourHeight = FluidState.SurfaceHeight(neighbourState);
            }
            else if (BlockType.IsReplaceable(neighbourId))
            {
                // Air or a fragile block: the fluid only gets pulled that way if it would drop
                // off there, and a drop counts as a full source lower than any level could be
                if (TryGetCell(x + dx, y - 1, z + dz, out var belowId, out var belowState) && belowId == id)
                    neighbourHeight = FluidState.SurfaceHeight(belowState) - FluidState.SourceHeight;
                else
                    continue;
            }
            else
            {
                continue; // a solid neighbour dams the flow instead of steering it
            }

            flow += new Vector3(dx, 0f, dz) * (ownHeight - neighbourHeight);
        }

        return flow.LengthSquared() > 1e-6f ? Vector3.Normalize(flow) : Vector3.Zero;
    }

    /// <summary>
    /// Checks if a chunk needs to be created at the given position.
    /// </summary>
    public bool NeedsChunk(Vector3 pos)
    {
        return !Chunks.ContainsKey(pos) && !_queuedChunkPositions.ContainsKey(pos);
    }
    
    /// <summary>
    /// Checks if all 6 cardinal neighbors of a chunk are loaded and have block data ready.
    /// </summary>
    private bool HasAllNeighbors(Vector3 pos)
    {
        foreach (var offset in Vector3Directions.CardinalDirections)
        {
            if (!Chunks.TryGetValue(pos + offset, out var neighbor) || !neighbor.IsBlockDataReady)
                return false;
        }
        return true;
    }
}
