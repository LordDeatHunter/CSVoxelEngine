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

    public void Update()
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
    }
}
