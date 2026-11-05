using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using VoxelEngine.Chunks.Sources;
using VoxelEngine.Chunks.Streaming;
using VoxelEngine.Data;
using VoxelEngine.Lod;
using VoxelEngine.Rendering.Geometry;

namespace VoxelEngine.Chunks.Meshing;

/// <summary>
/// The chunk mesh pipeline: the dirty set, the per-frame dirty scan that starts builds, the
/// thread-pool runners that build them, the completed queue and the budgeted main-thread upload,
/// plus the synchronous remesh paths edits use. Owned by <see cref="ChunkManager"/>, which
/// forwards the public members and drives <see cref="ApplyCompletedMeshes"/> and
/// <see cref="ProcessDirtyChunks"/> from its Update. Builds run on pool threads; everything
/// else here is main thread only except <see cref="MarkDirtyKey"/> (generation workers too).
/// </summary>
internal sealed class ChunkMeshScheduler(ChunkManager manager, ChunkRenderLists renderLists,
    ChunkGeometryPool? opaqueGeometry, ChunkGeometryPool? translucentGeometry)
{
    /// <summary>Dirty-set size, for tests asserting the scan cap paces the drain.</summary>
    internal int DirtyCount => _dirtyChunks.Count;

    /// <summary>Mesh-dispatcher state, for MeshSchedulingTests.</summary>
    internal int PendingMeshWorkCount => _pendingMeshWork.Count;

    internal int ActiveMeshRunnerCount => Volatile.Read(ref _activeMeshRunners);

    internal int GeneratingMeshCount => _generatingMeshes.Count;

    /// <summary>Completed output count for deterministic scheduler tests.</summary>
    internal int CompletedMeshCount => _completedMeshQueue.Count;

    /// <summary>Observes result payloads before the main-thread apply path in headless tests.</summary>
    internal Action<Chunk, ChunkMeshData>? MeshBeforeApplyForTests { get; set; }

    /// <summary>Pauses after a worker has produced its snapshot result but before publication.</summary>
    internal Action<Chunk, ChunkMeshData>? MeshGeneratedForTests { get; set; }

    // Deterministic worker barriers for the unload/reload regression tests.
    internal Action<Chunk>? BeforeBuildForTests { get; set; }
    internal Action<Chunk>? BuildAcceptedForTests { get; set; }
    internal Action<Chunk>? BuildFinishedForTests { get; set; }

    /// <summary>
    /// Finished meshes <see cref="ApplyCompletedMeshes"/> uploads per frame (the rest wait for the
    /// next). Defaults to <see cref="StreamingConfig.MaxMeshUploadsPerFrame"/>; World raises it
    /// while a world is loading and restores it when the world starts playing. Main thread only.
    /// </summary>
    public int MeshUploadBudget { get; set; } = StreamingConfig.MaxMeshUploadsPerFrame;

    /// <summary>
    /// Mesh builds <see cref="ProcessDirtyChunks"/> starts per frame. Defaults to
    /// <see cref="StreamingConfig.MaxDirtyChunksPerFrame"/>; raised by World while loading and
    /// restored at the flip to playing, like <see cref="MeshUploadBudget"/>. Main thread only.
    /// </summary>
    public int DirtyStartBudget { get; set; } = StreamingConfig.MaxDirtyChunksPerFrame;

    // The sequence number stamps each result with the order it finished in. ApplyCompletedMeshes
    // sorts by distance, and two results for the same chunk (an older one re-queued by the
    // upload budget, plus a newer rebuild) tie there, so without it the unstable sort could
    // apply them newest-first and leave the stale mesh on screen.
    private readonly ConcurrentQueue<(Vector3 pos, Chunk chunk, ChunkMeshData meshData, long seq)> _completedMeshQueue = new();
    private long _meshSequence;
    // Keyed on ChunkKey.Pack rather than the Vector3 chunk position: every probe here sits on a
    // hot path (the dirty scan, QueueChunk's six-neighbor fan-out, NeedsChunk on the generation
    // workers' scan frontier) and a packed long hashes once where a Vector3 hashes three floats
    // and compares twelve bytes per node visit (PERF_TODO D1). CellKey's block-cell keys share the
    // same 21-bit bit math at block scale.
    private readonly ConcurrentDictionary<long, byte> _dirtyChunks = new();

    // The value owns the claim. An unloaded chunk's old worker must not remove a replacement's
    // claim for the same packed position when it eventually leaves its finally block.
    private readonly ConcurrentDictionary<long, Chunk> _generatingMeshes = new();

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
    /// instead of wrapped in a Task. It carries nothing but its scheduler, so scheduling a chunk
    /// costs no closure, delegate, state machine or Task - the chunk travels in the queue. One
    /// instance is allocated per dispatch, not per chunk: a runner keeps draining until the queue
    /// is empty, so dispatches are far rarer than builds under load and at most
    /// <see cref="StreamingConfig.MaxConcurrentMeshGenerations"/> exist at once. A fresh
    /// instance per dispatch also means no work item is ever queued twice.
    /// </summary>
    private sealed class MeshRunner(ChunkMeshScheduler owner) : IThreadPoolWorkItem
    {
        public void Execute() => owner.RunMeshWork();
    }

    internal void ApplyCompletedMeshes(Vector3[] foci)
    {
        // Drain into the reusable scratch to sort by distance to the nearest focus. Distances
        // are computed once on drain: the old per-compare DistanceSquared plus the sort lambda's
        // closure allocated every frame the queue was non-empty.
        while (_completedMeshQueue.TryDequeue(out var item))
        {
            _applyScratch.Add((item.pos, item.chunk, item.meshData, item.seq,
                StreamingFocus.MinDistanceSquared(foci, item.pos)));
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

            foreach (var (pos, chunk, meshData, seq, distSq) in _applyScratch)
            {
                // Skip chunks that moved out of range of every focus, were unloaded, or were
                // replaced by a freshly generated chunk at the same position while this build ran:
                // uploading into the dead one would leave a mesh in Renderable that no unload ever
                // removes
                if (distSq > StreamingConfig.ChunkRadiusSq ||
                    !manager.Chunks.TryGetValue(pos, out var live) || !ReferenceEquals(live, chunk))
                {
                    manager.MeshBuffers.Return(meshData);
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

    internal void ProcessDirtyChunks(Vector3[] foci)
    {
        if (_dirtyChunks.IsEmpty) return;

        _dirtyCandidates.Clear();

        // The set holds thousands of entries mid-stream and every one costs several
        // dictionary probes, so the scan itself is capped: leftovers wait for the next
        // frames, which meshgen (DirtyStartBudget starts/frame, 4 concurrent builds) cannot outrun
        // anyway. Drops do not need a second visit — out-of-range entries are removed
        // on sight and missing-neighbor ones are re-added by QueueChunk on arrival.
        var scanned = 0;

        // Lock-free enumeration, no snapshot; removing through it while enumerating is safe
        foreach (var kvp in _dirtyChunks)
        {
            if (++scanned > StreamingConfig.MaxDirtyScanPerFrame)
                break;

            var key = kvp.Key;
            // Unpacked once per entry and reused by everything below that still speaks Vector3:
            // the distance test, HasAllNeighbors and StartMeshGeneration. The Chunks probe takes
            // the packed key directly - it is the same key this entry is stored under.
            ChunkKey.Unpack(key, out var cx, out var cy, out var cz);
            var pos = new Vector3(cx, cy, cz);
            var distSq = StreamingFocus.MinDistanceSquared(foci, pos);

            // Out of range of every focus, or unloaded — just drop the entry
            if (distSq > StreamingConfig.ChunkRadiusSq || !manager.Chunks.TryGetPacked(key, out var chunk))
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

            // Claim the entry BEFORE the neighbor test, and drop a chunk that is still
            // missing a face neighbor instead of retrying it every frame. QueueChunk
            // re-dirties all six neighbors of a chunk when it arrives (with its block data
            // already generated), so the missing neighbor's arrival is what wakes this chunk
            // back up. Removing first closes the race where that TryAdd would be a no-op
            // because this entry still existed. This is what lets _dirtyChunks reach empty at
            // rest: the rim chunks of the load sphere never get their outer neighbor until
            // the camera moves, and used to be rescanned every single frame. A chunk whose
            // streamed columns have not arrived is dropped the same way:
            // ClientChunkStreamer.ApplyColumn re-dirties the 3x3 chunk columns around every
            // column it stores, which is what wakes it.
            if (!_dirtyChunks.TryRemove(key, out _))
                continue;

            if (!HasAllNeighbors(pos))
                continue;

            if (!HasColumnNeighborhood(chunk, cx, cz))
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
    /// Uploads a finished mesh to the chunk and keeps <see cref="ChunkRenderLists.Renderable"/> in step with it.
    /// Main thread only, like every GL call.
    /// </summary>
    private void ApplyMesh(Chunk chunk, ChunkMeshData meshData)
    {
        MeshBeforeApplyForTests?.Invoke(chunk, meshData);
        var wasMeshed = chunk.IsMeshed;
        if (manager.Headless)
            chunk.MarkMeshedHeadless(meshData);
        else
            chunk.UpdateMesh(meshData, opaqueGeometry, translucentGeometry);
        // BufferSubData copies synchronously, so the lists can be recycled at once.
        manager.MeshBuffers.Return(meshData);
        renderLists.SyncRenderable(chunk);
        // The first mesh makes the chunk count as "drawn" for the LOD cover test even when it is
        // empty (all air, or enclosed) and therefore never enters Renderable
        if (!wasMeshed) renderLists.MeshVersion++;
    }

    /// <summary>
    /// Claims the dedupe slot and queues the build. Takes the packed key alongside the position
    /// because both callers (ProcessDirtyChunks' two budget branches) already hold it.
    /// </summary>
    private void StartMeshGeneration(long key, Vector3 pos, Chunk chunk)
    {
        if (!_generatingMeshes.TryAdd(key, chunk))
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
            if (active >= StreamingConfig.MaxConcurrentMeshGenerations)
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
    /// <see cref="ProcessDirtyChunks"/> (reached through <see cref="ChunkManager.Update(Vector3[])"/>) starts runners, so
    /// none can start while this waits, and a runner's own second look in <c>RunMeshWork</c>
    /// only fires on work that is still queued, which this waits out too. Finished results stay
    /// in the completed queue; nothing is applied here.
    /// <para>
    /// A mesh build reads worldgen state - the nine chunk columns around it through
    /// <see cref="ChunkManager.ColumnSource"/> (<see cref="WorldgenColumnSource"/> outside a client, resolved up
    /// front), plus the seed-keyed position hashes and the snow-depth noise the pebble, branch-pile
    /// and snow-layer emitters read for untouched cells - so a world-seed swap must wait for this
    /// as well as for the worker joins. <c>World.SaveAndDispose</c> calls <see cref="ChunkGenerationWorker.Stop"/>,
    /// then <see cref="LodManager.Stop"/>, then this, and only when all three return true does it
    /// free the chunks through <see cref="ChunkManager.ReleaseAll"/> and call <see cref="WorldSeed.EndWorld"/>
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
    /// One mesh build, on a thread-pool thread - the old Task body unchanged, plus a catch:
    /// Task.Run turned a throw into an unobserved task exception, while one escaping a raw work
    /// item tears the process down, so it is logged and the runner carries on exactly as
    /// <see cref="ChunkGenerationWorker"/> does. The finally sits outside the catch so a failed
    /// build still releases its dedupe slot.
    /// </summary>
    private void BuildMeshFor(Vector3 pos, Chunk chunk)
    {
        // One pack for the whole call: the re-dirty on a vanished neighbor and the finally's
        // dedupe release both take it. The work queue carries the position, not the key.
        var key = ChunkKey.Pack(pos);

        try
        {
            BeforeBuildForTests?.Invoke(chunk);
            if (!manager.Chunks.TryGetPacked(key, out var live) || !ReferenceEquals(live, chunk))
                return;

            BuildAcceptedForTests?.Invoke(chunk);

            var meshData = chunk.GenerateMeshData(manager);
            if (meshData == null)
            {
                // A face neighbor was unloaded between the scheduling check and this
                // snapshot, or one of the nine streamed columns is missing; meshing across
                // that hole would bake border artifacts (or dark light and no water), so the
                // build is dropped and the chunk retried once the world settles
                if (manager.Chunks.TryGetPacked(key, out live) && ReferenceEquals(live, chunk))
                    _dirtyChunks.TryAdd(key, 0);
                return;
            }

            MeshGeneratedForTests?.Invoke(chunk, meshData);

            _completedMeshQueue.Enqueue((pos, chunk, meshData, Interlocked.Increment(ref _meshSequence)));
        }
        catch (Exception ex)
        {
            Log.WriteLine($"Error meshing chunk {pos}: {ex}");
        }
        finally
        {
            ((ICollection<KeyValuePair<long, Chunk>>)_generatingMeshes)
                .Remove(new KeyValuePair<long, Chunk>(key, chunk));
            BuildFinishedForTests?.Invoke(chunk);
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
    /// to <see cref="LightingConfig.LightPropagationRadius"/> blocks from an edit,
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
            if (manager.Chunks.ContainsKey(x, y, z))
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

        if (!manager.Chunks.TryGetPacked(key, out var chunk)) return;
        if (!chunk.IsBlockDataReady) return;
        if (!HasAllNeighbors(pos)) return;

        // A streamed column is missing: mesh once it arrives instead of baking dark light and
        // no water now. The entry is re-added once per edit, never per frame, and the next
        // ProcessDirtyChunks drops it until ApplyColumn wakes it, so nothing spins.
        if (!HasColumnNeighborhood(chunk, (int)pos.X, (int)pos.Z))
        {
            _dirtyChunks.TryAdd(key, 0);
            return;
        }

        var meshData = chunk.GenerateMeshData(manager);
        if (meshData == null)
        {
            // Lost a face neighbor to an unload mid-call: retry rather than bake a hole in
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
    /// Synchronously remeshes the edited chunk plus every face neighbor sharing the edited
    /// cell, so a block broken or placed on a chunk border never leaves a one-frame hole in
    /// the neighbor's mesh while the async dirty budget catches up (the neighbor's border
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
    /// Checks if all 6 cardinal neighbors of a chunk are loaded and have block data ready.
    /// </summary>
    private bool HasAllNeighbors(Vector3 pos)
    {
        // Hoisted once, then six probes from ints in hand. CardinalOffsets is in the same order
        // as CardinalDirections, so the early return still fires on the same neighbor.
        var cx = MathUtils.FloorToInt(pos.X);
        var cy = MathUtils.FloorToInt(pos.Y);
        var cz = MathUtils.FloorToInt(pos.Z);
        foreach (var d in Vector3Directions.CardinalOffsets)
        {
            if (!manager.Chunks.TryGetValue(cx + d.X, cy + d.Y, cz + d.Z, out var neighbor)
                || !neighbor.IsBlockDataReady)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether <see cref="ChunkManager.ColumnSource"/> has the nine chunk columns around (<paramref name="cx"/>,
    /// <paramref name="cz"/>) that a build of <paramref name="chunk"/> resolves: the light volume's
    /// heights, the water surface codes and the +1 tint corners. The worldgen source always says
    /// yes, so singleplayer pays nine trivial virtual calls per dirty entry. An all-air chunk is
    /// exempt, since its build returns before it reads any column and a sky chunk at a client's
    /// rim must keep meshing at once; a fully enclosed chunk cannot be told apart without its
    /// neighbors and so waits for its columns too, which only delays <see cref="Chunk.IsMeshed"/>
    /// for <c>LodCoverPass.IsCoveredByChunks</c> at a client's load-sphere rim.
    /// </summary>
    private bool HasColumnNeighborhood(Chunk chunk, int cx, int cz)
    {
        if (chunk.SolidCount == 0)
            return true;

        var source = manager.ColumnSource;
        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (!source.IsReady(cx + dx, cz + dz))
                return false;
        }
        return true;
    }

    /// <summary>Marks the chunk with this packed key dirty; thread-safe, called by QueueChunk on the generation workers and by ApplyStreamedChunk.</summary>
    internal void MarkDirtyKey(long key) => _dirtyChunks.TryAdd(key, 0);

    /// <summary>Forgets an unloading chunk's in-flight and dirty entries, in that order (the unload's own order).</summary>
    internal void ForgetChunk(long key)
    {
        _generatingMeshes.TryRemove(key, out _);
        _dirtyChunks.TryRemove(key, out _);
    }

    /// <summary>Empties every table of the pipeline for <see cref="ChunkManager.ReleaseAll"/>, under its precondition (no worker, no runner).</summary>
    internal void Release()
    {
        // Finished builds nobody will apply: their lists go back to the buffer pool
        while (_completedMeshQueue.TryDequeue(out var item))
            manager.MeshBuffers.Return(item.meshData);

        // Empty by the precondition, drained anyway so no chunk reference outlives the world
        while (_pendingMeshWork.TryDequeue(out _)) { }

        _dirtyChunks.Clear();
        _generatingMeshes.Clear();
        _applyScratch.Clear();
        _dirtyCandidates.Clear();
    }
}
