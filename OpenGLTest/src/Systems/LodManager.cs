using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace silkgltriangle;

/// <summary>
/// Manages far-terrain LOD regions: a background thread builds region meshes from the
/// heightmap cache (CPU only), the main thread uploads them to the GPU with a per-frame
/// cap and unloads regions that fall out of range.
/// </summary>
public class LodManager(Silk.NET.OpenGL.GL gl)
{
    public readonly ConcurrentDictionary<(int X, int Z), LodRegion> Regions = new();

    private readonly ConcurrentDictionary<(int X, int Z), byte> _queued = new();
    private readonly ConcurrentDictionary<(int X, int Z), byte> _dirtyRegions = new();
    private readonly ConcurrentQueue<LodMeshData> _completedMeshes = new();
    private volatile bool _isRunning;
    private Camera? _camera;

    // 2D region offsets within the LOD radius, sorted closest-first
    private static readonly (int dx, int dz)[] SortedOffsets = BuildSortedOffsets();

    private static (int dx, int dz)[] BuildSortedOffsets()
    {
        const int radius = WorldGenerationConfig.LodRegionRadius;

        var offsets = new List<(int dx, int dz, int distSq)>();
        for (var dx = -radius; dx <= radius; dx++)
        for (var dz = -radius; dz <= radius; dz++)
        {
            var distSq = dx * dx + dz * dz;
            if (distSq <= radius * radius)
                offsets.Add((dx, dz, distSq));
        }

        offsets.Sort((a, b) => a.distSq.CompareTo(b.distSq));

        var result = new (int, int)[offsets.Count];
        for (var i = 0; i < offsets.Count; i++)
            result[i] = (offsets[i].dx, offsets[i].dz);
        return result;
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
                    _completedMeshes.Enqueue(LodRegion.BuildMeshData(next.Value.X, next.Value.Z));
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
    /// Marks the LOD regions whose sampling window covers the edited column for rebuild.
    /// The erosion window reaches 3 fine blocks past region bounds, so an edit near a
    /// region border can affect up to 4 regions.
    /// </summary>
    public void MarkEditedColumn(int x, int z)
    {
        var rxMin = (x - 3) >> 6; // fine coords / LodRegion.SizeBlocks (64), floor division
        var rxMax = (x + 3) >> 6;
        var rzMin = (z - 3) >> 6;
        var rzMax = (z + 3) >> 6;

        for (var rx = rxMin; rx <= rxMax; rx++)
        for (var rz = rzMin; rz <= rzMax; rz++)
        {
            var key = (rx, rz);
            // Only rebuild regions that are loaded or being built; anything else picks up
            // the column caps when it gets built later
            if (_queued.ContainsKey(key))
                _dirtyRegions.TryAdd(key, 0);
        }
    }

    private (int X, int Z)? TakeDirtyRegion()
    {
        foreach (var key in _dirtyRegions.Keys)
        {
            if (_dirtyRegions.TryRemove(key, out _))
                return key;
        }

        return null;
    }

    private (int X, int Z)? FindClosestNeededRegion()
    {
        var (camX, camZ) = CameraRegion();

        foreach (var (dx, dz) in SortedOffsets)
        {
            var key = (camX + dx, camZ + dz);
            if (Regions.ContainsKey(key)) continue;
            if (_queued.TryAdd(key, 0))
                return key;
        }

        return null;
    }

    private (int X, int Z) CameraRegion()
    {
        var pos = _camera!.Position;
        return ((int)MathF.Floor(pos.X / LodRegion.SizeBlocks), (int)MathF.Floor(pos.Z / LodRegion.SizeBlocks));
    }

    public void Update()
    {
        if (_camera == null) return;

        var (camX, camZ) = CameraRegion();
        const int unloadRadiusSq = (WorldGenerationConfig.LodRegionRadius + 1) * (WorldGenerationConfig.LodRegionRadius + 1);

        foreach (var (key, region) in Regions)
        {
            var dx = key.X - camX;
            var dz = key.Z - camZ;
            if (dx * dx + dz * dz <= unloadRadiusSq)
                continue;

            if (!Regions.TryRemove(key, out var removed)) continue;
            removed.Dispose();
            _queued.TryRemove(key, out _);
        }

        var uploaded = 0;
        while (uploaded < WorldGenerationConfig.MaxLodUploadsPerFrame && _completedMeshes.TryDequeue(out var data))
        {
            var key = (data.X, data.Z);
            var dx = data.X - camX;
            var dz = data.Z - camZ;

            // Moved out of range while queued; allow it to regenerate later
            if (dx * dx + dz * dz > unloadRadiusSq)
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
