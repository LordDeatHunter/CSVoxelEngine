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

    private readonly ConcurrentDictionary<(int Level, int X, int Z), byte> _queued = new();
    private readonly ConcurrentDictionary<(int Level, int X, int Z), byte> _dirtyRegions = new();
    private readonly ConcurrentQueue<LodMeshData> _completedMeshes = new();
    private volatile bool _isRunning;
    private Camera? _camera;

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

    public void Start(Camera camera)
    {
        if (_isRunning) return;
        _camera = camera;
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
                    _completedMeshes.Enqueue(LodRegion.BuildMeshData(level, x, z));
                }
                else
                {
                    Thread.Sleep(WorldGenerationConfig.GenerationDelayIdleMs);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in LOD generation: {ex}");
                Thread.Sleep(1000);
            }
        }
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

    private (int Level, int X, int Z)? TakeDirtyRegion()
    {
        foreach (var key in _dirtyRegions.Keys)
        {
            if (_dirtyRegions.TryRemove(key, out _))
                return key;
        }

        return null;
    }

    private (int Level, int X, int Z)? FindClosestNeededRegion()
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

        Span<(int camX, int camZ)> camRegions = stackalloc (int, int)[LodRegion.Levels.Length];
        for (var level = 0; level < LodRegion.Levels.Length; level++)
            camRegions[level] = CameraRegion(level);

        foreach (var (key, region) in Regions)
        {
            var radius = LodRegion.Levels[key.Level].RegionRadius + 1;
            var dx = key.X - camRegions[key.Level].camX;
            var dz = key.Z - camRegions[key.Level].camZ;
            if (dx * dx + dz * dz <= radius * radius)
                continue;

            if (!Regions.TryRemove(key, out var removed)) continue;
            removed.Dispose();
            _queued.TryRemove(key, out _);
        }

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
                continue;
            }

            var region = new LodRegion(gl, data);
            if (Regions.TryRemove(key, out var replaced))
            {
                region.Lifetime = replaced.Lifetime; // rebuilt region keeps its fade state
                replaced.Dispose();
            }

            Regions[key] = region;
            uploaded++;
        }

        UpdateBuriedFlags(chunkManager);
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
    /// </summary>
    private void UpdateBuriedFlags(ChunkManager chunkManager)
    {
        var camChunk = (_camera!.Position / Chunk.Size).Floor();

        foreach (var (key, region) in Regions)
        {
            region.IsBuried = key.Level > 0
                ? IsCoveredByFinerLevel(key.Level, key.X, key.Z)
                : IsCoveredByChunks(region, chunkManager, camChunk);
        }
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
        var finer = level - 1;
        // Region grids are aligned at the origin and sizes are power-of-two multiples,
        // so one region maps onto exactly ratio x ratio regions of the finer level
        var ratio = LodRegion.Levels[level].RegionBlocks / LodRegion.Levels[finer].RegionBlocks;

        for (var dx = 0; dx < ratio; dx++)
        for (var dz = 0; dz < ratio; dz++)
        {
            if (!Regions.ContainsKey((finer, x * ratio + dx, z * ratio + dz)))
                return false;
        }

        return true;
    }
}
