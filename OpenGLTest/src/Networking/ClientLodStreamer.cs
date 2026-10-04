using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Requests far-LOD regions from the server and hands the built meshes to the client's
/// <see cref="LodManager"/>. A client generates no LOD itself, so this is how it sees terrain past
/// the streamed chunks, for any server worldgen. Main thread only.
/// </summary>
public sealed class ClientLodStreamer
{
    private const double ResendSeconds = 2.0;
    private const int MaxRequestsPerUpdate = 8;

    private readonly LodManager _lod;
    private readonly Action<int, int, int> _request;
    private readonly Dictionary<(int Level, int X, int Z), double> _asked = new();
    private double _time;

    public ClientLodStreamer(LodManager lod, Action<int, int, int> request)
    {
        _lod = lod;
        _request = request;
    }

    private static readonly (int Dx, int Dz)[][] Offsets = BuildOffsets();

    /// <summary>Requests the regions around the camera that are not present yet, nearest first.</summary>
    public void Update(Vector3 cameraPos, double deltaTime)
    {
        _time += deltaTime;
        var budget = MaxRequestsPerUpdate;

        for (var level = 0; level < LodRegion.Levels.Length; level++)
        {
            var spec = LodRegion.Levels[level];
            var rx = MathUtils.FloorToInt(cameraPos.X / spec.RegionBlocks);
            var rz = MathUtils.FloorToInt(cameraPos.Z / spec.RegionBlocks);
            foreach (var (dx, dz) in Offsets[level])
            {
                var key = (level, rx + dx, rz + dz);
                if (_lod.Regions.ContainsKey(key))
                    continue;
                if (budget <= 0)
                    return;
                if (_asked.TryGetValue(key, out var at) && _time - at < ResendSeconds)
                    continue;
                _asked[key] = _time;
                _request(level, rx + dx, rz + dz);
                budget--;
            }
        }
    }

    /// <summary>Uploads a server-built region mesh into the client's LOD pool.</summary>
    public void Apply(byte[] payload) => _lod.AddStreamedRegion(LodNetCodec.Decode(payload));

    // Region offsets within each level's radius, nearest first.
    private static (int Dx, int Dz)[][] BuildOffsets()
    {
        var result = new (int Dx, int Dz)[LodRegion.Levels.Length][];
        for (var level = 0; level < LodRegion.Levels.Length; level++)
        {
            var radius = LodRegion.Levels[level].RegionRadius;
            var offsets = new List<(int Dist2, int Dx, int Dz)>();
            for (var dx = -radius; dx <= radius; dx++)
            for (var dz = -radius; dz <= radius; dz++)
            {
                var d2 = dx * dx + dz * dz;
                if (d2 <= radius * radius)
                    offsets.Add((d2, dx, dz));
            }

            offsets.Sort(static (a, b) => a.Dist2.CompareTo(b.Dist2));
            var level2 = new (int, int)[offsets.Count];
            for (var i = 0; i < level2.Length; i++)
                level2[i] = (offsets[i].Dx, offsets[i].Dz);
            result[level] = level2;
        }

        return result;
    }
}
