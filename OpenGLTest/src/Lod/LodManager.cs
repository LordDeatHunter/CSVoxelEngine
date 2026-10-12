using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using VoxelEngine.Chunks;
using VoxelEngine.Player;
using VoxelEngine.Rendering;

namespace VoxelEngine.Lod;

/// <summary>
/// Manages far-terrain LOD regions across all LOD levels: a pool of
/// <see cref="WorldGenerationConfig.MaxConcurrentLodGenerations"/> BelowNormal LodGen threads
/// builds region meshes from the heightmap cache (CPU only), the main thread uploads them into
/// the render manager's shared LOD <see cref="ChunkGeometryPool"/> with a per-frame cap and
/// unloads regions that fall out of range. That constant ships at 1 — C5 measured a wider pool
/// and declined it (the reasoning is on the constant) — but everything here is written for N
/// workers and is correct at any N.
/// Levels are filled finest-first so detail appears near the player before the horizon.
/// Each worker claims a region through <see cref="ClaimNextRegion"/>, which registers the key in
/// <c>_building</c>; the claim protocol, not the concurrent collections, is what keeps two
/// workers off one region (item C5).
/// </summary>
public class LodManager(ChunkGeometryPool? lodGeometry = null)
{
    /// <summary>
    /// The shared pool every resident region's terrain shell and sea sheet is uploaded into, or
    /// null headless (the tests and a headless World), where a non-empty build is dropped by
    /// <see cref="ApplyCompletedMeshes"/> instead of uploaded. One pool covers both meshes
    /// because a region's pair is born from one <see cref="LodMeshData"/> and freed together.
    /// Render thread only, like the two chunk pools.
    /// </summary>
    private readonly ChunkGeometryPool? _lodGeometry = lodGeometry;

    /// <summary>
    /// Finished region builds <see cref="ApplyCompletedMeshes"/> uploads per frame. Defaults to
    /// <see cref="WorldGenerationConfig.MaxLodUploadsPerFrame"/>; World raises it while a world is
    /// loading and restores it when the world starts playing. Main thread only.
    /// </summary>
    public int UploadBudget { get; set; } = WorldGenerationConfig.MaxLodUploadsPerFrame;

    /// <summary>
    /// Where the far LOD reads its terrain: worldgen by default (<see cref="GeneratedWorldSource"/>),
    /// the world's own <see cref="ChunkManager.WorldSource"/> otherwise (World copies it). Set
    /// before <see cref="Start"/> (the LodGen <c>Thread.Start</c> publishes it) and never changed
    /// afterwards; read on the LodGen workers.
    /// </summary>
    public IWorldSource WorldSource { get; set; } = GeneratedWorldSource.Instance;

    public readonly ConcurrentDictionary<(int Level, int X, int Z), LodRegion> Regions = new();

    /// <summary>
    /// The same regions as <see cref="Regions"/>, flat, so the main thread can walk them
    /// without the locked full-table copy <c>Regions.Values</c> makes every frame. Order is
    /// irrelevant (the render passes sort what they draw), so entries are swap-removed.
    /// Main thread only — <see cref="Update"/> maintains it and the render passes read it.
    /// <see cref="Regions"/> stays concurrent because the background builder probes it by key.
    /// </summary>
    public readonly List<LodRegion> RegionList = new(512);

    private readonly ConcurrentDictionary<(int Level, int X, int Z), byte> _queued = new();
    private readonly ConcurrentDictionary<(int Level, int X, int Z), byte> _dirtyRegions = new();
    private readonly ConcurrentQueue<LodMeshData> _completedMeshes = new();
    private long _completedBuildCount;

    /// <summary>
    /// The regions currently claimed by a LodGen worker: entered by whichever claim path handed
    /// the key out and removed by <see cref="ReleaseRegion"/> once the build has been enqueued.
    /// It is the ONLY thing that stops two workers building the same region concurrently, which
    /// neither <c>_queued</c> nor <c>_dirtyRegions</c> can do -- a key stays in <c>_queued</c> for
    /// its whole build, and <see cref="MarkEditedColumn"/> re-dirties exactly those keys. Item C5.
    /// </summary>
    private readonly ConcurrentDictionary<(int Level, int X, int Z), byte> _building = new();

    /// <summary>
    /// LOD-sized twin of <see cref="ChunkManager.MeshBuffers"/>. The LodGen worker rents a region
    /// build's four output lists here and the main thread hands them back in
    /// <see cref="ApplyCompletedMeshes"/>, after the upload or on the out-of-range drop — a queued
    /// LodMeshData can wait frames, so returning on the worker would hand out a live list.
    /// Water lists come back even when empty (retainEmptyTranslucent): unlike a chunk mesher, a
    /// region build rents them every time, so dropping the empty ones would drain the pool inland.
    /// <para>
    /// The retention ceilings are DERIVED from the in-flight bound, not chosen. Outstanding rents
    /// are capped at <see cref="WorldGenerationConfig.MaxLodBuildsInFlight"/> = 8 (finished builds
    /// waiting in <c>_completedMeshes</c>, each pinning its four lists) plus
    /// <see cref="WorldGenerationConfig.MaxConcurrentLodGenerations"/> (builds in progress), so
    /// the bound is 8 + N. The ceiling is sized for the largest pool C5 measured (N = 3, bound 11)
    /// rather than for the N = 1 the measurement told it to ship, so that raising the constant
    /// stays a one-token experiment: 16 keeps the whole in-flight set recyclable with headroom for
    /// the frame's returns at any N up to 8. Below the bound a steady fraction of returns would be
    /// dropped and re-rented as fresh LOH allocations, inverting the win C8 landed.
    /// The arithmetic, so the next round need not redo it: Vertex is 32 bytes, so one build's
    /// four lists are 24576*32 + 36864*2 + 4096*32 + 6144*2 ~= 1.00 MB; 11 in flight ~= 11 MB,
    /// 16 retained ~= 15.7 MB, never trimmed once the burst has filled the pool. If that idle
    /// retention ever matters the lever is MaxLodBuildsInFlight, not the ceiling.
    /// The return end is still MAIN THREAD ONLY and unchanged by C5 — only the rent end became
    /// MaxConcurrentLodGenerations-wide, and every Rent/Return is locked, so the contract holds at
    /// any N even though the shipped N is 1.
    /// </para>
    /// </summary>
    public MeshBufferPool MeshBuffers { get; } = new(
        vertexCapacity: LodRegion.MeshVertexCapacity,
        indexCapacity: LodRegion.MeshIndexCapacity,
        translucentVertexCapacity: LodRegion.WaterVertexCapacity,
        translucentIndexCapacity: LodRegion.WaterIndexCapacity,
        maxPooled: 16,
        maxPooledTranslucent: 16,
        retainEmptyTranslucent: true);
    private volatile bool _isRunning;
    private Camera? _camera;

    // Inputs of the last buried-flag pass; see Update. -1 / null force the first pass.
    private int _regionSetVersion;
    private int _lastBuriedMeshVersion = -1;
    private int _lastBuriedRegionSetVersion = -1;
    private Vector3? _lastBuriedCamChunk;

    // Regions added since the last cover pass. They start unburied, so the next pass
    // tests them first: same-frame evaluation, exactly like the uncapped pass did.
    // Each entry carries its own index back in LodRegion.PendingCoverIndex, so an unload
    // swap-removes instead of scanning (item C12(c)); UpdateBuriedFlags resets those
    // indices when it drains the list.
    private readonly List<LodRegion> _pendingCover = new();

    // How many of a coarse region's finer-level children (at its own key) are currently
    // loaded, keyed by the COARSE region's (Level, X, Z) — i.e. a level-1 region's count is
    // the number of its level-0 children present in Regions, and a level-2 region's count is
    // its level-1 children. Maintained incrementally by AddRegion/RemoveRegion so
    // IsCoveredByFinerLevel becomes a single lookup and integer compare instead of
    // ratio*ratio ContainsKey probes. Main thread only, like RegionList/AddRegion/RemoveRegion.
    private readonly Dictionary<(int Level, int X, int Z), int> _finerChildCounts = new();

    // Round-robin cursor into RegionList for the unburied level-0 slice, and whether
    // the slice has lapped it since the inputs last changed. While false the pass runs
    // every frame, so a region added at rest still converges instead of waiting for
    // the next input change that may never come.
    private int _coverCursor;
    private bool _coverConverged = true;

    // Per level: 2D region offsets within that level's radius, sorted closest-first
    private static readonly (int dx, int dz)[][] SortedOffsets = BuildSortedOffsets();

    private static (int dx, int dz)[][] BuildSortedOffsets()
    {
        var perLevel = new (int dx, int dz)[LodRegion.Levels.Length][];

        for (var level = 0; level < LodRegion.Levels.Length; level++)
        {
            var radius = LodRegion.Levels[level].RegionRadius;
            var offsets = new List<(int dx, int dz, int distSq)>();

            for (var dx = -radius; dx <= radius; dx++)
            for (var dz = -radius; dz <= radius; dz++)
            {
                var distSq = dx * dx + dz * dz;
                if (distSq <= radius * radius)
                    offsets.Add((dx, dz, distSq));
            }

            offsets.Sort((a, b) => a.distSq.CompareTo(b.distSq));

            perLevel[level] = new (int, int)[offsets.Count];
            for (var i = 0; i < offsets.Count; i++)
                perLevel[level][i] = (offsets[i].dx, offsets[i].dz);
        }

        return perLevel;
    }

    /// <summary>Binds the camera the region scan measures distance from. Split out of
    /// <see cref="Start"/> so tests can reach <see cref="FindClosestNeededRegion"/> — which
    /// dereferences the camera through <c>CameraRegion</c> — without spawning the LodGen threads.
    /// </summary>
    internal void AttachCamera(Camera camera) => _camera = camera;

    // The threads the last Start spawned, kept until a Stop has joined every one of them so
    // AnyThreadAlive stays truthful after a timed-out join and a later Stop can re-join.
    private Thread[]? _threads;

    // The workers' idle, throttle and error back-offs wait on this instead of Thread.Sleep, so
    // Stop can cut a sleeping worker's wait short and the join returns promptly.
    private readonly ManualResetEventSlim _wake = new(false);

    /// <summary>
    /// Whether any thread from the last <see cref="Start"/> is still running. False once
    /// <see cref="Stop"/> has returned true.
    /// </summary>
    internal bool AnyThreadAlive
    {
        get
        {
            var threads = _threads;
            if (threads == null) return false;
            foreach (var thread in threads)
                if (thread.IsAlive) return true;
            return false;
        }
    }

    /// <summary>
    /// Binds the camera and starts the LodGen pool. Refuses (with a warning) while a thread from
    /// an earlier pool is still alive, which only happens after a <see cref="Stop"/> timed out: a
    /// second pool then would double the workers and leave the straggler building regions from
    /// worldgen statics the caller believes are quiet.
    /// </summary>
    public void Start(Camera camera)
    {
        if (_isRunning) return;
        if (AnyThreadAlive)
        {
            Log.Warn("LodGen: Start refused, a worker from the previous pool is still running");
            return;
        }

        AttachCamera(camera);
        // A timed-out Stop leaves the event set; a new pool must start with it clear or every
        // idle worker would spin instead of waiting.
        _wake.Reset();
        _isRunning = true;

        var threads = new Thread[WorldGenerationConfig.MaxConcurrentLodGenerations];
        for (var i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"LodGen-{i}",
                Priority = ThreadPriority.BelowNormal
            };
        }

        // Published before any thread runs, so AnyThreadAlive can never miss a started worker.
        _threads = threads;
        foreach (var thread in threads)
            thread.Start();

        // Managed thread names are not observable from Process.GetCurrentProcess().Threads, so
        // this line is the only way to confirm the pool size a running build actually spawned.
        Log.WriteLine($"LodGen: {WorldGenerationConfig.MaxConcurrentLodGenerations} workers");
    }

    /// <summary>
    /// Stops the LodGen workers and joins them, each for at most
    /// <see cref="WorldGenerationConfig.WorkerStopJoinTimeoutMs"/>. A worker mid-build finishes
    /// (and enqueues) that region first; one waiting out a back-off is woken.
    /// <para>
    /// True means every LodGen thread has exited, so none can read the heightmap cache or the
    /// worldgen statics any more: part of the precondition <see cref="WorldSeed.Apply"/> needs,
    /// together with <see cref="ChunkGenerationWorker.Stop"/> and
    /// <see cref="ChunkManager.DrainMeshWork"/>. False means a thread outlived its join; the pool
    /// is kept so a later Stop can re-join it, and <see cref="WorldSeed.Apply"/> must NOT run.
    /// Calling it when <see cref="Start"/> never ran returns true.
    /// </para>
    /// </summary>
    public bool Stop()
    {
        _isRunning = false;
        _wake.Set();

        var threads = _threads;
        if (threads == null) return true;

        var allJoined = true;
        foreach (var thread in threads)
        {
            if (thread.Join(WorldGenerationConfig.WorkerStopJoinTimeoutMs)) continue;
            allJoined = false;
            Log.Warn($"LodGen: {thread.Name} did not stop within {WorldGenerationConfig.WorkerStopJoinTimeoutMs} ms");
        }

        if (!allJoined) return false;

        _threads = null;
        _wake.Reset();
        return true;
    }

    /// <summary>
    /// Unloads every region at once and empties every table this manager keeps, handing each
    /// region's slices back to the shared LOD pool (which stays alive for the next world) and
    /// every waiting build's lists back to <see cref="MeshBuffers"/>. Main thread only (it frees
    /// GL slices).
    /// <para>
    /// PRECONDITION: <see cref="Stop"/> returned true, so no LodGen thread can claim a key or
    /// enqueue a build while this runs. Calling it twice is a no-op the second time.
    /// </para>
    /// </summary>
    public void ReleaseAll()
    {
        // Backwards, like Update's unload loop: RemoveRegion swap-removes the last entry into
        // the hole, which a descending walk has already visited
        for (var i = RegionList.Count - 1; i >= 0; i--)
        {
            var region = RegionList[i];
            Regions.TryRemove((region.Level, region.X, region.Z), out _);
            RemoveRegion(region);
            region.Dispose();
        }

        // Defensive: a region in the table but not in the list (none should exist)
        foreach (var kvp in Regions)
        {
            if (Regions.TryRemove(kvp.Key, out var stray))
                stray.Dispose();
        }

        Regions.Clear();
        RegionList.Clear();
        _pendingCover.Clear();
        _finerChildCounts.Clear();
        _queued.Clear();
        _dirtyRegions.Clear();
        _building.Clear();

        while (_completedMeshes.TryDequeue(out var data))
            MeshBuffers.Return(data.Vertices, data.Indices, data.WaterVertices, data.WaterIndices);

        _regionSetVersion++;
        _coverConverged = true;
        _coverCursor = 0;
    }

    private void WorkerLoop()
    {
        while (_isRunning)
        {
            try
            {
                switch (TryBuildOneRegion())
                {
                    // Nothing left to build anywhere: the long poll, exactly as before.
                    case LodBuildOutcome.Idle:
                        _wake.Wait(WorldGenerationConfig.GenerationDelayIdleMs);
                        break;
                    // Work exists and only the main thread's drain is behind, so backing off for
                    // the idle poll would stall the streaming burst a fifth of a second at a time.
                    case LodBuildOutcome.Throttled:
                        _wake.Wait(WorldGenerationConfig.LodBuildThrottleDelayMs);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine($"Error in LOD generation: {ex}");
                _wake.Wait(1000);
            }
        }
    }

    /// <summary>
    /// How many region builds the LodGen worker has finished since startup. The counter is
    /// CUMULATIVE and monotone: it counts BUILDS, never live regions, so a region that is
    /// uploaded, unloaded and rebuilt counts three times and nothing ever decrements it.
    /// That is deliberately unlike <see cref="ChunkManager.LoadedChunkCount"/>, which is a NET
    /// counter (incremented in <c>QueueChunk</c>, decremented in <c>UnloadDistantChunks</c>) and
    /// therefore carries the purge bias PERF_TODO's C3 capture rule documents: once the streaming
    /// ring is full, loads and unloads cancel and the net rate plateaus near zero even while the
    /// generator is at full tilt. A cumulative counter has no purge bias and no plateau, so a
    /// throughput capture over it can use a fixed-duration window instead of chasing convergence.
    /// Read on the main thread through <see cref="Interlocked.Read(ref long)"/> because the
    /// increment happens on a LodGen worker and a long store is not atomic on 32-bit runtimes.
    /// </summary>
    public long CompletedBuildCount => Interlocked.Read(ref _completedBuildCount);

    /// <summary>The LodGen worker's only handoff to the main thread; internal so tests can drive
    /// <see cref="ApplyCompletedMeshes"/> without starting a thread. Counting here rather than at
    /// the upload is what makes <see cref="CompletedBuildCount"/> a measure of BUILDER throughput:
    /// a build that is dropped out of range in <see cref="ApplyCompletedMeshes"/> still cost the
    /// worker its full build, so it still counts.</summary>
    internal void EnqueueCompletedMesh(LodMeshData data)
    {
        Interlocked.Increment(ref _completedBuildCount);
        _completedMeshes.Enqueue(data);
    }

    /// <summary>
    /// Marks the LOD regions (on every level) whose sampling window covers the edited
    /// column for rebuild. An edit near a region border can affect adjacent regions.
    /// </summary>
    public void MarkEditedColumn(int x, int z)
    {
        for (var level = 0; level < LodRegion.Levels.Length; level++)
        {
            var spec = LodRegion.Levels[level];
            var reach = spec.SampleMargin + spec.CellSize;

            var rxMin = FloorDiv(x - reach, spec.RegionBlocks);
            var rxMax = FloorDiv(x + reach, spec.RegionBlocks);
            var rzMin = FloorDiv(z - reach, spec.RegionBlocks);
            var rzMax = FloorDiv(z + reach, spec.RegionBlocks);

            for (var rx = rxMin; rx <= rxMax; rx++)
            for (var rz = rzMin; rz <= rzMax; rz++)
            {
                var key = (level, rx, rz);
                // Only rebuild regions that are loaded or being built; anything else picks
                // up the column caps when it gets built later
                if (_queued.ContainsKey(key))
                    _dirtyRegions.TryAdd(key, 0);
            }
        }
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    /// <summary>
    /// Claims one dirty region for rebuild, or null if none are pending. The worker calls this on
    /// every spin and the set is empty for nearly all of them, so the walk goes over the dictionary
    /// itself rather than over <c>.Keys</c>: the ConcurrentDictionary enumerator is lock-free and
    /// copies nothing, and removing entries through it while enumerating is explicitly supported
    /// (the same shape ChunkManager's unload pass and its dirty-chunk scan already use and document).
    /// Internal so tests can drive it without starting the LodGen threads.
    ///
    /// The returned key is ALREADY registered in <c>_building</c> and the caller must hand it to
    /// <see cref="ReleaseRegion"/> -- see <see cref="TryBuildOneRegion"/>. The <c>_building</c>
    /// probe at the top of the loop is a cheap PRE-FILTER and never the guarantee; the
    /// registration gated on <c>TryAdd</c>'s result, taken inside the same take, is. The window the
    /// pre-filter alone leaves open is the subtlest fact of item C5: <see cref="MarkEditedColumn"/>
    /// re-dirties any key that is still in <c>_queued</c>, and a key stays in <c>_queued</c> for
    /// its whole build, so between worker A's <c>TryRemove</c> here and any registration made after
    /// the return, the main thread can re-add that same key and worker B can take it. Two builds of
    /// one region then run concurrently and apply in COMPLETION order, so the older snapshot can
    /// land last: a stale LOD shell left standing over a freshly mined column, which is exactly
    /// what the region's column caps exist to prevent. An advisory mark plus an unchecked TryAdd
    /// after the return does not close that; branching on the TryAdd result does.
    /// </summary>
    internal (int Level, int X, int Z)? TakeDirtyRegion()
    {
        foreach (var kvp in _dirtyRegions)
        {
            // Pre-filter: SKIP without removing, so a region that is mid-build keeps its dirty
            // mark and is rebuilt on a later spin instead of losing the edit.
            if (_building.ContainsKey(kvp.Key)) continue;

            if (!_dirtyRegions.TryRemove(kvp.Key, out _)) continue;

            // The real gate. Losing the race means another worker claimed the key between the
            // pre-filter and here, so the mark goes BACK rather than being dropped: a collision
            // costs a rebuild delay, never an edit.
            if (!_building.TryAdd(kvp.Key, 0))
            {
                _dirtyRegions.TryAdd(kvp.Key, 0);
                continue;
            }

            return kvp.Key;
        }

        return null;
    }

    /// <summary>
    /// The closest region, finest level first, that is neither loaded nor already claimed; claims
    /// it in <c>_queued</c> and returns it, or null when every level is full.
    /// Internal (rather than private) because it is the only path that fills <c>_queued</c>, and
    /// <see cref="MarkEditedColumn"/> refuses to mark a region that is not in it — so a test of the
    /// dirty-set drain can only produce a dirty region by going through this scan first.
    ///
    /// The returned key is ALREADY registered in <c>_building</c> and the caller must hand it to
    /// <see cref="ReleaseRegion"/>. The scan is deliberately UNSHARED between workers, unlike the
    /// chunk lattice's scan frontier (item C3): a full scan is at most 591 probes (197 offsets x 3
    /// levels at RegionRadius 8) against that lattice's 17077, and it runs once per BUILD rather
    /// than once per probe, so it is noise beside a build costing tens of milliseconds. Concurrent
    /// callers racing on <c>_queued.TryAdd</c> is not a problem to solve here but the very thing
    /// that fans the workers out onto distinct regions.
    /// </summary>
    internal (int Level, int X, int Z)? FindClosestNeededRegion()
    {
        for (var level = 0; level < LodRegion.Levels.Length; level++)
        {
            var (camX, camZ) = CameraRegion(level);

            foreach (var (dx, dz) in SortedOffsets[level])
            {
                var key = (level, camX + dx, camZ + dz);
                if (Regions.ContainsKey(key)) continue;
                if (!_queued.TryAdd(key, 0)) continue;

                // The _queued claim succeeded but another worker already holds the key in
                // _building, so this one must UN-CLAIM _queued before moving on. The rollback is
                // not optional: ApplyCompletedMeshes' out-of-range branch removes a key from
                // _queued while its builder may still hold it in _building, so winning _queued
                // here while losing _building is reachable -- and a _queued entry with nothing
                // building is a region that never builds and can never re-enter this scan (the
                // TryAdd above would keep failing), i.e. a permanent hole in the horizon.
                if (!_building.TryAdd(key, 0))
                {
                    _queued.TryRemove(key, out _);
                    continue;
                }

                return key;
            }
        }

        return null;
    }

    /// <summary>
    /// The next region a LodGen worker should build: pending rebuilds first, then the closest
    /// region the horizon is still missing. A thin alias now that both paths register their own
    /// key -- the returned key IS registered in <c>_building</c> and the caller MUST hand it back
    /// to <see cref="ReleaseRegion"/>, or that region never builds again.
    /// </summary>
    internal (int Level, int X, int Z)? ClaimNextRegion() => TakeDirtyRegion() ?? FindClosestNeededRegion();

    /// <summary>Drops a worker's claim on a region, making it takeable again. Idempotent.</summary>
    internal void ReleaseRegion((int Level, int X, int Z) key) => _building.TryRemove(key, out _);

    /// <summary>Test seam: how many finished builds are waiting for the main thread's drain.</summary>
    internal int PendingUploadCount => _completedMeshes.Count;

    /// <summary>Test seam: lets a test play the main thread's drain without a GL context.</summary>
    internal bool TryTakeCompletedMesh(out LodMeshData data) => _completedMeshes.TryDequeue(out data!);

    /// <summary>Test seam: regions claimed by the closest-needed scan and not yet dropped.</summary>
    internal int QueuedClaimCount => _queued.Count;

    /// <summary>Test seam: regions a worker currently holds. Zero once every worker released.</summary>
    internal int BuildingClaimCount => _building.Count;

    /// <summary>
    /// One iteration of a LodGen worker: claim a region, build it, hand the result to the main
    /// thread. The whole loop body, factored out so the concurrency can be driven headlessly.
    ///
    /// Two orderings here are load-bearing. (a) The in-flight cap is checked BEFORE any claim is
    /// taken. Checking it after would mean either leaking a <c>_queued</c> claim that nothing would
    /// ever build (a permanent horizon hole, see <see cref="FindClosestNeededRegion"/>) or silently
    /// dropping a dirty mark that had already been removed from the set. (b) The release sits in a
    /// <c>finally</c> that runs AFTER <see cref="EnqueueCompletedMesh"/>, so a rebuild of the same
    /// key cannot start until the earlier build is already on the queue: queue order is build
    /// order, which is what makes the last mesh applied for a key the newest one.
    /// </summary>
    internal LodBuildOutcome TryBuildOneRegion()
    {
        if (_completedMeshes.Count >= WorldGenerationConfig.MaxLodBuildsInFlight)
            return LodBuildOutcome.Throttled;

        var next = ClaimNextRegion();
        if (next is not { } key)
            return LodBuildOutcome.Idle;

        try
        {
            EnqueueCompletedMesh(LodRegion.BuildMeshData(key.Level, key.X, key.Z, MeshBuffers, source: WorldSource));
        }
        finally
        {
            ReleaseRegion(key);
        }

        return LodBuildOutcome.Built;
    }

    private (int X, int Z) CameraRegion(int level)
    {
        var pos = _camera!.Position;
        var size = LodRegion.Levels[level].RegionBlocks;
        return (MathUtils.FloorToInt(pos.X / size), MathUtils.FloorToInt(pos.Z / size));
    }

    public void Update(ChunkManager chunkManager)
    {
        if (_camera == null) return;

        var camChunk = (_camera.Position / Chunk.Size).Floor();
        Span<(int camX, int camZ)> camRegions = stackalloc (int, int)[LodRegion.Levels.Length];
        for (var level = 0; level < LodRegion.Levels.Length; level++)
            camRegions[level] = CameraRegion(level);

        // Backwards by index because this loop removes as it goes: a swap-remove drops the
        // last entry into the hole, and a descending walk has already visited that entry.
        for (var i = RegionList.Count - 1; i >= 0; i--)
        {
            var region = RegionList[i];
            var radius = LodRegion.Levels[region.Level].RegionRadius + 1;
            var dx = region.X - camRegions[region.Level].camX;
            var dz = region.Z - camRegions[region.Level].camZ;
            if (dx * dx + dz * dz <= radius * radius)
                continue;

            var key = (region.Level, region.X, region.Z);
            if (!Regions.TryRemove(key, out var removed)) continue;
            RemoveRegion(removed);
            removed.Dispose();
            _queued.TryRemove(key, out _);
        }

        ApplyCompletedMeshes(camRegions);

        // The flags depend only on which chunks are loaded and meshed (MeshVersion), which regions
        // exist (_regionSetVersion) and the camera chunk (the distance early-out in
        // IsCoveredByChunks), so the pass is skipped while none of them moved. A region added above
        // starts with IsBuried == false; the add bumps the region-set version, so the pass always runs
        // in the frame a region appears and never draws a shell for a frame it should not.
        UpdateCoverFlags(chunkManager, camChunk);
    }

    /// <summary>
    /// Uploads finished region builds to the GPU, capped per frame, and drops the ones that left
    /// the camera's range while they waited. Owns the returned end of the rent/return contract on
    /// <see cref="MeshBuffers"/>: every dequeued <see cref="LodMeshData"/> hands its four lists
    /// back exactly once here, on both exits, and nothing reads them afterwards. Main thread only
    /// (GL); internal so tests can drive both paths without a worker thread.
    /// </summary>
    internal void ApplyCompletedMeshes(ReadOnlySpan<(int camX, int camZ)> camRegions)
    {
        var uploaded = 0;
        while (uploaded < UploadBudget && _completedMeshes.TryDequeue(out var data))
        {
            var key = (data.Level, data.X, data.Z);
            var radius = LodRegion.Levels[data.Level].RegionRadius + 1;
            var dx = data.X - camRegions[data.Level].camX;
            var dz = data.Z - camRegions[data.Level].camZ;

            // Moved out of range while queued; allow it to regenerate later
            if (dx * dx + dz * dz > radius * radius)
            {
                _queued.TryRemove(key, out _);
                // Nothing was ever built from this data, so its lists recycle straight away.
                MeshBuffers.Return(data.Vertices, data.Indices, data.WaterVertices, data.WaterIndices);
                continue;
            }

            // Headless (no pool): a real build has nowhere to go, so it is dropped as if uploaded.
            // The key stays in _queued so the builder does not spin on it. The LodRegion
            // constructor's throw for this case was unreachable in every headless test, and this
            // is what lets a headless World drive Update over real terrain.
            if (_lodGeometry == null && (data.Vertices.Count > 0 || data.WaterVertices.Count > 0))
            {
                MeshBuffers.Return(data.Vertices, data.Indices, data.WaterVertices, data.WaterIndices);
                uploaded++;
                continue;
            }

            // A rebuild frees the region it replaces BEFORE uploading the new one, so a
            // same-sized rebuild lands back in the exact hole it just vacated: the pool's free
            // list comes out of a rebuild unchanged instead of gaining a fragment, and the two
            // copies never occupy the pool at the same time. The fade state is the only thing
            // the new region needs from the old one, so it is carried across by hand.
            //
            // The order does change one failure path, deliberately. LodRegion's constructor can
            // throw out of ChunkGeometryPool.Upload, whose single reachable throw is "could not
            // fit N units after growing"; after this reorder the replaced region is already out
            // of Regions and RegionList when that happens, with nothing scheduled to rebuild it,
            // where the old order left the stale region standing. Accepted: that throw means the
            // pool could neither fit the slice nor grow to hold it, which is unrecoverable and
            // already fatal in practice.
            var carriedLifetime = 0.0;
            if (Regions.TryRemove(key, out var replaced))
            {
                carriedLifetime = replaced.Lifetime; // rebuilt region keeps its fade state
                RemoveRegion(replaced);
                replaced.Dispose();
            }

            var region = new LodRegion(_lodGeometry, data) { Lifetime = carriedLifetime };
            // ChunkGeometryPool.Upload copies synchronously (glNamedBufferSubData), so the lists
            // can be recycled at once.
            MeshBuffers.Return(data.Vertices, data.Indices, data.WaterVertices, data.WaterIndices);

            AddRegion(key, region);
            uploaded++;
        }
    }

    /// <summary>
    /// Refreshes buried flags when their inputs moved, spreading the expensive unburied
    /// level-0 cover tests over frames (see <see cref="UpdateBuriedFlags"/>). Headless-safe:
    /// it needs only the region list and a chunk manager, so tests drive it directly.
    /// </summary>
    internal void UpdateCoverFlags(ChunkManager chunkManager, Vector3 camChunk)
    {
        if (chunkManager.MeshVersion != _lastBuriedMeshVersion
            || _regionSetVersion != _lastBuriedRegionSetVersion
            || camChunk != _lastBuriedCamChunk)
        {
            _lastBuriedMeshVersion = chunkManager.MeshVersion;
            _lastBuriedRegionSetVersion = _regionSetVersion;
            _lastBuriedCamChunk = camChunk;
            _coverConverged = false;
        }

        if (!_coverConverged)
            _coverConverged = UpdateBuriedFlags(chunkManager, camChunk);
    }

    /// <summary>
    /// Registers a region in both the lookup table and <see cref="RegionList"/>.
    /// Main thread only.
    /// </summary>
    /// <summary>
    /// Binds the camera without starting any LodGen worker: a multiplayer client's regions arrive
    /// over the network (<see cref="AddStreamedRegion"/>), so it needs the removal/cover passes of
    /// <see cref="Update"/> but never builds anything itself.
    /// </summary>
    public void BindCamera(Camera camera) => _camera = camera;

    /// <summary>
    /// Adds a far-LOD region built by the server, uploading its mesh to this client's LOD pool and
    /// handing it to the same lifetime/burial machinery as a locally built one. A key already
    /// present is left alone (a rebuild the client did not ask for is stale).
    /// </summary>
    public void AddStreamedRegion(LodMeshData data)
    {
        var key = (data.Level, data.X, data.Z);
        if (Regions.ContainsKey(key))
            return;
        AddRegion(key, new LodRegion(_lodGeometry, data));
    }

    internal void AddRegion((int Level, int X, int Z) key, LodRegion region)
    {        Regions[key] = region;
        region.ListIndex = RegionList.Count;
        RegionList.Add(region);
        region.PendingCoverIndex = _pendingCover.Count;
        _pendingCover.Add(region);
        _regionSetVersion++;

        // Derived from the region, not the caller-supplied key, so this increment and
        // RemoveRegion's decrement read the same source and cannot disagree.
        if (TryParentKey((region.Level, region.X, region.Z), out var parentKey))
            _finerChildCounts[parentKey] = _finerChildCounts.GetValueOrDefault(parentKey) + 1;
    }

    /// <summary>
    /// Swap-removes a region from <see cref="RegionList"/>; a no-op for one that is not in it.
    /// The caller removes it from <see cref="Regions"/> itself, which is where the key lives.
    /// Main thread only.
    /// </summary>
    internal void RemoveRegion(LodRegion region)
    {
        var index = region.ListIndex;
        if (index < 0) return;

        var last = RegionList.Count - 1;
        if (index != last)
        {
            var moved = RegionList[last];
            RegionList[index] = moved;
            moved.ListIndex = index;
        }

        RegionList.RemoveAt(last);
        region.ListIndex = -1;

        // Same swap-remove as above, against _pendingCover (item C12(c)). Honest sizing: this
        // list is drained in the frame it is filled -- AddRegion bumps _regionSetVersion, which
        // forces a UpdateBuriedFlags pass, which Clear()s it unconditionally at the top -- so its
        // length is bounded by MaxLodUploadsPerFrame and the List.Remove this replaces scanned at
        // most four entries. This removes an O(n) a future change could make hot, not a measured
        // cost.
        var pending = region.PendingCoverIndex;
        if (pending >= 0)
        {
            var lastPending = _pendingCover.Count - 1;
            if (pending != lastPending)
            {
                var movedPending = _pendingCover[lastPending];
                _pendingCover[pending] = movedPending;
                movedPending.PendingCoverIndex = pending;
            }

            _pendingCover.RemoveAt(lastPending);
            region.PendingCoverIndex = -1;
        }

        _regionSetVersion++;

        if (TryParentKey((region.Level, region.X, region.Z), out var parentKey))
        {
            var remaining = _finerChildCounts[parentKey] - 1;
            if (remaining <= 0)
                _finerChildCounts.Remove(parentKey);
            else
                _finerChildCounts[parentKey] = remaining;
        }
    }

    /// <summary>
    /// The coarser-level key a region at <paramref name="key"/> feeds its loaded-child count
    /// into, or false for the topmost level, which has no parent. Region grids are aligned at
    /// the origin and sizes are power-of-two multiples of each other, so a child maps onto
    /// its parent by a plain integer division (floored, since region coordinates go negative).
    /// </summary>
    private static bool TryParentKey((int Level, int X, int Z) key, out (int Level, int X, int Z) parentKey)
    {
        if (key.Level >= LodRegion.Levels.Length - 1)
        {
            parentKey = default;
            return false;
        }

        var ratio = LodRegion.Levels[key.Level + 1].RegionBlocks / LodRegion.Levels[key.Level].RegionBlocks;
        parentKey = (key.Level + 1, FloorDiv(key.X, ratio), FloorDiv(key.Z, ratio));
        return true;
    }

    /// <summary>
    /// Flags every region whose whole footprint is already covered by loaded finer-level
    /// regions so the renderer draws a single LOD level per area. The levels are nested
    /// shells stacked below one another: drawing all of them is invisible from above the
    /// terrain (the depth buffer sorts it out) but shows up as layered surfaces once the
    /// camera goes underground. The finest level is likewise flagged where the full-detail
    /// chunks are drawn, because caves cut into the band its shell occupies and would show it.
    ///
    /// Coverage is tested against the regions and chunks that actually exist rather than each
    /// level's radius, so a finer region or chunk that has not been built yet can never open a
    /// hole in the horizon — the coarser mesh keeps drawing until its replacement is loaded.
    /// Runs only when a chunk was meshed or unloaded, a region was added or removed, or the
    /// camera changed chunk; see <see cref="Update"/>.
    /// </summary>
    private bool UpdateBuriedFlags(ChunkManager chunkManager, Vector3 camChunk)
    {
        // New regions first: they start unburied and the uploads cap additions per
        // frame below the slice budget, so same-frame evaluation is preserved.
        var budget = WorldGenerationConfig.MaxLodCoverTestsPerFrame;
        foreach (var region in _pendingCover)
        {
            region.IsBuried = region.Level > 0
                ? IsCoveredByFinerLevel(region.Level, region.X, region.Z)
                : IsCoveredByChunks(region, chunkManager, camChunk);
            if (region.Level == 0)
                budget--;
            region.PendingCoverIndex = -1;
        }

        // Every entry is leaving the list, so every index must go back to -1 here. Without this
        // a region added in one pass and removed after a later one would swap-remove against a
        // stale index and corrupt whichever region happens to sit there now.
        _pendingCover.Clear();

        // Coarser levels cost four lookups each and a stale buried flag is a hole in
        // the horizon, so both run every pass. Only stale-unburied is harmless
        // (overdraw the depth buffer already hides from above), which is what the
        // bounded slice below spends its budget on.
        for (var i = 0; i < RegionList.Count; i++)
        {
            var region = RegionList[i];
            if (region.Level > 0 || region.IsBuried)
                region.IsBuried = region.Level > 0
                    ? IsCoveredByFinerLevel(region.Level, region.X, region.Z)
                    : IsCoveredByChunks(region, chunkManager, camChunk);
        }

        if (RegionList.Count == 0)
            return true;

        // Round-robin over the list; swap-removal may skip or repeat an entry when the
        // list changes mid-cycle, which only ever delays overdraw, never a hole.
        if (_coverCursor >= RegionList.Count || _coverCursor < 0)
            _coverCursor = RegionList.Count - 1;
        var idx = _coverCursor;
        for (var visited = 0; visited < RegionList.Count; visited++)
        {
            var region = RegionList[idx];
            if (region.Level == 0 && !region.IsBuried)
            {
                if (budget <= 0)
                {
                    _coverCursor = idx;
                    return false;
                }
                region.IsBuried = IsCoveredByChunks(region, chunkManager, camChunk);
                budget--;
            }
            if (--idx < 0)
                idx = RegionList.Count - 1;
        }
        _coverCursor = idx;
        return true;
    }

    /// <summary>
    /// Whether every chunk this region's shell could show through is loaded and drawn. The
    /// chunk ranges come from the region build (<see cref="LodRegion.ColumnCyMin"/>); presence
    /// is tested per chunk rather than by distance because the load volume is a sphere, so
    /// terrain far below the camera near its horizontal edge is not loaded.
    /// </summary>
    private static bool IsCoveredByChunks(LodRegion region, ChunkManager chunkManager, Vector3 camChunk)
    {
        var span = region.SizeBlocks / Chunk.Size;
        var baseCx = region.X * span;
        var baseCz = region.Z * span;

        // Chunks only exist within ChunkRadius of the camera, so a region whose nearest chunk
        // column is farther than that cannot be covered; skips the lookups for most of the ring
        var dx = Math.Max(0, Math.Max(baseCx - (int)camChunk.X, (int)camChunk.X - (baseCx + span - 1)));
        var dz = Math.Max(0, Math.Max(baseCz - (int)camChunk.Z, (int)camChunk.Z - (baseCz + span - 1)));
        if (dx * dx + dz * dz > WorldGenerationConfig.ChunkRadiusSq)
            return false;

        for (var ci = 0; ci < span; ci++)
        for (var cj = 0; cj < span; cj++)
        {
            var c = ci * span + cj;
            for (var cy = region.ColumnCyMin[c]; cy <= region.ColumnCyMax[c]; cy++)
            {
                if (!chunkManager.Chunks.TryGetValue(baseCx + ci, cy, baseCz + cj, out var chunk)
                    || !chunk.IsMeshed)
                    return false;
            }
        }

        return true;
    }

    private bool IsCoveredByFinerLevel(int level, int x, int z)
    {
        // Region grids are aligned at the origin and sizes are power-of-two multiples, so one
        // region maps onto exactly ratio x ratio regions of the finer level: this region is
        // covered once that many of its finer-level children are loaded. AddRegion/RemoveRegion
        // keep _finerChildCounts current, so this is one lookup instead of ratio*ratio probes.
        var finer = level - 1;
        var ratio = LodRegion.Levels[level].RegionBlocks / LodRegion.Levels[finer].RegionBlocks;
        return _finerChildCounts.TryGetValue((level, x, z), out var count) && count == ratio * ratio;
    }
}

/// <summary>What one spin of a LodGen worker did, which is what tells the loop how to back off:
/// <see cref="Idle"/> means there is nothing to build anywhere (the long poll), while
/// <see cref="Throttled"/> means work exists and only the main thread's upload drain is behind,
/// which must not be waited on for <c>GenerationDelayIdleMs</c>. Item C5.</summary>
internal enum LodBuildOutcome
{
    Built,
    Idle,
    Throttled
}
