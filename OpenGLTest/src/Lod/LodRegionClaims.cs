using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using VoxelEngine.Chunks;

namespace VoxelEngine.Lod;

/// <summary>
/// The LodGen workers' claim and build protocol (item C5): the queued, dirty and building sets, the closest-first
/// region scan, the in-flight cap and the queue of finished builds waiting for LodManager's upload. The worker side
/// is lock-free and correct at any pool size; reads LodManager's Regions, MeshBuffers, WorldSource and camera through
/// its owner when it uses them.
/// </summary>
internal sealed class LodRegionClaims(LodManager owner)
{
    private readonly LodManager _owner = owner;

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
    /// Read on the main thread through <c>Interlocked.Read</c> because the
    /// increment happens on a LodGen worker and a long store is not atomic on 32-bit runtimes.
    /// </summary>
    public long CompletedBuildCount => Interlocked.Read(ref _completedBuildCount);

    /// <summary>The LodGen worker's only handoff to the main thread; internal so tests can drive
    /// <see cref="LodManager.ApplyCompletedMeshes"/> without starting a thread. Counting here rather than at
    /// the upload is what makes <see cref="CompletedBuildCount"/> a measure of BUILDER throughput:
    /// a build that is dropped out of range in <see cref="LodManager.ApplyCompletedMeshes"/> still cost the
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

            var rxMin = MathUtils.FloorDiv(x - reach, spec.RegionBlocks);
            var rxMax = MathUtils.FloorDiv(x + reach, spec.RegionBlocks);
            var rzMin = MathUtils.FloorDiv(z - reach, spec.RegionBlocks);
            var rzMax = MathUtils.FloorDiv(z + reach, spec.RegionBlocks);

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
    /// The closest region that is neither loaded nor already claimed; claims it in <c>_queued</c> and
    /// returns it, or null when every level is full. The levels are merged by each region's nearest-point
    /// distance to the camera in blocks, the finer level first at equal distance. A coarse region its finer
    /// children cover well inside their ring is skipped (<see cref="LodRegion.IsSupersededByFinerRing"/>), so a
    /// new world or a long teleport builds the finest level under the camera and never a coarse shell there.
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
        var levels = LodRegion.Levels.Length;
        var camera = _owner.CameraXZ;
        Span<(int X, int Z)> camRegions = stackalloc (int, int)[levels];
        Span<int> cursors = stackalloc int[levels];
        for (var level = 0; level < levels; level++)
            camRegions[level] = _owner.CameraRegion(level);

        while (true)
        {
            // Each level's next candidate in its own closest-first order; the nearest of them wins
            var best = -1;
            var bestDistSq = float.MaxValue;
            for (var level = 0; level < levels; level++)
            {
                var offsets = SortedOffsets[level];
                while (cursors[level] < offsets.Length
                       && (IsTaken(KeyAt(level, camRegions[level], offsets[cursors[level]]))
                           || IsSuperseded(KeyAt(level, camRegions[level], offsets[cursors[level]]), camRegions)))
                    cursors[level]++;
                if (cursors[level] == offsets.Length) continue;

                // Strictly nearer only, so a tie keeps the finer level (the loop runs finest first)
                var distSq = NearestDistanceSq(KeyAt(level, camRegions[level], offsets[cursors[level]]), camera);
                if (distSq >= bestDistSq) continue;
                best = level;
                bestDistSq = distSq;
            }
            if (best < 0) return null;

            var key = KeyAt(best, camRegions[best], SortedOffsets[best][cursors[best]++]);
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

    // Covered by its finer children well inside their ring, so never worth building (LodRegion.IsSupersededByFinerRing)
    private static bool IsSuperseded((int Level, int X, int Z) key, ReadOnlySpan<(int X, int Z)> camRegions) =>
        key.Level > 0 && LodRegion.IsSupersededByFinerRing(key.Level, key.X, key.Z, camRegions[key.Level - 1],
            StreamingConfig.LodSupersededBuildMarginRegions);

    private static (int Level, int X, int Z) KeyAt(int level, (int X, int Z) camRegion, (int dx, int dz) offset) =>
        (level, camRegion.X + offset.dx, camRegion.Z + offset.dz);

    // Loaded, or claimed by a worker that is building it (or holds it waiting for the upload)
    private bool IsTaken((int Level, int X, int Z) key) => _owner.Regions.ContainsKey(key) || _queued.ContainsKey(key);

    /// <summary>The squared distance in blocks from the camera's column to the nearest point of a region's footprint
    /// (0 for the region the camera stands in), so regions of different sizes compare fairly.</summary>
    internal static float NearestDistanceSq((int Level, int X, int Z) key, Vector2 camera)
    {
        var size = LodRegion.Levels[key.Level].RegionBlocks;
        var dx = MathF.Max(0f, MathF.Max(key.X * size - camera.X, camera.X - (key.X + 1) * size));
        var dz = MathF.Max(0f, MathF.Max(key.Z * size - camera.Y, camera.Y - (key.Z + 1) * size));
        return dx * dx + dz * dz;
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
        if (_completedMeshes.Count >= StreamingConfig.MaxLodBuildsInFlight)
            return LodBuildOutcome.Throttled;

        var next = ClaimNextRegion();
        if (next is not { } key)
            return LodBuildOutcome.Idle;

        try
        {
            EnqueueCompletedMesh(LodRegion.BuildMeshData(key.Level, key.X, key.Z, _owner.MeshBuffers, source: _owner.WorldSource));
        }
        finally
        {
            ReleaseRegion(key);
        }

        return LodBuildOutcome.Built;
    }

    /// <summary>Drops a region's <c>_queued</c> claim on the main thread, so the closest-needed scan may claim it again: <see cref="LodManager.Update"/>'s unload loop and <see cref="LodManager.ApplyCompletedMeshes"/>' out-of-range drop. Idempotent.</summary>
    internal void Unqueue((int Level, int X, int Z) key) => _queued.TryRemove(key, out _);

    /// <summary>The claims half of <see cref="LodManager.ReleaseAll"/>, under its precondition: empties every claim set and hands every waiting build's lists back to <see cref="LodManager.MeshBuffers"/>. Main thread only.</summary>
    internal void ReleaseClaims()
    {
        _queued.Clear();
        _dirtyRegions.Clear();
        _building.Clear();

        while (_completedMeshes.TryDequeue(out var data))
            _owner.MeshBuffers.Return(data.Vertices, data.Indices, data.WaterVertices, data.WaterIndices);
    }
}
