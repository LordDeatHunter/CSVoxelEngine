using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace silkgltriangle;

/// <summary>
/// Manages far-terrain LOD regions across all LOD levels: a background thread builds
/// region meshes from the heightmap cache (CPU only), the main thread uploads them to
/// the GPU with a per-frame cap and unloads regions that fall out of range.
/// Levels are filled finest-first so detail appears near the player before the horizon.
/// </summary>
public class LodManager(Silk.NET.OpenGL.GL gl)
{
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

    /// <summary>
    /// LOD-sized twin of <see cref="ChunkManager.MeshBuffers"/>. The LodGen worker rents a region
    /// build's four output lists here and the main thread hands them back in
    /// <see cref="ApplyCompletedMeshes"/>, after the upload or on the out-of-range drop — a queued
    /// LodMeshData can wait frames, so returning on the worker would hand out a live list.
    /// Water lists come back even when empty (retainEmptyTranslucent): unlike a chunk mesher, a
    /// region build rents them every time, so dropping the empty ones would drain the pool inland.
    /// The retention caps are 4 because one LodGen thread has a single rent outstanding while the
    /// main thread returns at most MaxLodUploadsPerFrame per frame; a deeper burst simply misses
    /// the pool and allocates, exactly as every build did before.
    /// </summary>
    public MeshBufferPool MeshBuffers { get; } = new(
        vertexCapacity: LodRegion.MeshVertexCapacity,
        indexCapacity: LodRegion.MeshIndexCapacity,
        translucentVertexCapacity: LodRegion.WaterVertexCapacity,
        translucentIndexCapacity: LodRegion.WaterIndexCapacity,
        maxPooled: 4,
        maxPooledTranslucent: 4,
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
    /// dereferences the camera through <c>CameraRegion</c> — without spawning the LodGen thread.
    /// </summary>
    internal void AttachCamera(Camera camera) => _camera = camera;

    public void Start(Camera camera)
    {
        if (_isRunning) return;
        AttachCamera(camera);
        _isRunning = true;

        var thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "LodGen",
            Priority = ThreadPriority.BelowNormal
        };
        thread.Start();
    }

    public void Stop()
    {
        _isRunning = false;
    }

    private void WorkerLoop()
    {
        while (_isRunning)
        {
            try
            {
                var next = TakeDirtyRegion() ?? FindClosestNeededRegion();
                if (next.HasValue)
                {
                    var (level, x, z) = next.Value;
                    EnqueueCompletedMesh(LodRegion.BuildMeshData(level, x, z, MeshBuffers));
                }
                else
                {
                    Thread.Sleep(WorldGenerationConfig.GenerationDelayIdleMs);
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine($"Error in LOD generation: {ex}");
                Thread.Sleep(1000);
            }
        }
    }

    /// <summary>The LodGen worker's only handoff to the main thread; internal so tests can drive
    /// <see cref="ApplyCompletedMeshes"/> without starting a thread.</summary>
    internal void EnqueueCompletedMesh(LodMeshData data) => _completedMeshes.Enqueue(data);

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
    /// Internal so tests can drive it without starting the LodGen thread.
    /// </summary>
    internal (int Level, int X, int Z)? TakeDirtyRegion()
    {
        foreach (var kvp in _dirtyRegions)
        {
            if (_dirtyRegions.TryRemove(kvp.Key, out _))
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
                if (_queued.TryAdd(key, 0))
                    return key;
            }
        }

        return null;
    }

    private (int X, int Z) CameraRegion(int level)
    {
        var pos = _camera!.Position;
        var size = LodRegion.Levels[level].RegionBlocks;
        return ((int)MathF.Floor(pos.X / size), (int)MathF.Floor(pos.Z / size));
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
        while (uploaded < WorldGenerationConfig.MaxLodUploadsPerFrame && _completedMeshes.TryDequeue(out var data))
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

            var region = new LodRegion(gl, data);
            // BufferData copies synchronously, so the lists can be recycled at once.
            MeshBuffers.Return(data.Vertices, data.Indices, data.WaterVertices, data.WaterIndices);
            if (Regions.TryRemove(key, out var replaced))
            {
                region.Lifetime = replaced.Lifetime; // rebuilt region keeps its fade state
                RemoveRegion(replaced);
                replaced.Dispose();
            }

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
    internal void AddRegion((int Level, int X, int Z) key, LodRegion region)
    {
        Regions[key] = region;
        region.ListIndex = RegionList.Count;
        RegionList.Add(region);
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
        _pendingCover.Remove(region);
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
        }
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
                if (!chunkManager.Chunks.TryGetValue(new Vector3(baseCx + ci, cy, baseCz + cj), out var chunk)
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
