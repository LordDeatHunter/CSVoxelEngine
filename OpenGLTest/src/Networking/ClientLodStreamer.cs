using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Requests far-LOD regions from the server and hands the built meshes to the client's
/// <see cref="LodManager"/>. A client generates no LOD itself, so this is how it sees terrain past
/// the streamed chunks, for any server worldgen. At most
/// <see cref="WorldGenerationConfig.NetLodInFlightMax"/> requests are outstanding at once, and an
/// unanswered one is only re-sent after <see cref="WorldGenerationConfig.NetLodResendSeconds"/>
/// (TCP is reliable; the resend only covers a server that dropped the request). Main thread only.
/// </summary>
public sealed class ClientLodStreamer
{
    private const int MaxRequestsPerUpdate = 8;

    private readonly LodManager _lod;
    private readonly Action<int, int, int> _request;
    // Outstanding requests and when each was sent; an answer removes its entry and an entry past
    // the resend age is dropped so the region is asked again.
    private readonly Dictionary<(int Level, int X, int Z), double> _asked = new();
    private readonly List<(int Level, int X, int Z)> _expired = [];
    private double _time;

    public ClientLodStreamer(LodManager lod, Action<int, int, int> request)
    {
        _lod = lod;
        _request = request;
    }

    private static readonly (int Dx, int Dz)[][] Offsets = BuildOffsets();

    /// <summary>Requests outstanding right now (sent, unanswered and not yet expired).</summary>
    public int InFlight => _asked.Count;

    /// <summary>Requests the regions around the camera that are not present yet, nearest first.</summary>
    public void Update(Vector3 cameraPos, double deltaTime)
    {
        _time += deltaTime;
        ExpireStale();
        var budget = Math.Min(MaxRequestsPerUpdate, WorldGenerationConfig.NetLodInFlightMax - _asked.Count);

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
                if (_asked.ContainsKey(key))
                    continue;
                _asked[key] = _time;
                _request(level, rx + dx, rz + dz);
                budget--;
            }
        }
    }

    /// <summary>Uploads a server-built region mesh into the client's LOD pool, which also frees
    /// its in-flight slot.</summary>
    public void Apply(byte[] payload)
    {
        var data = LodNetCodec.Decode(payload);
        _asked.Remove((data.Level, data.X, data.Z));
        _lod.AddStreamedRegion(data);
    }

    // Drops the requests older than the resend age, so they are asked again and stop holding an
    // in-flight slot.
    private void ExpireStale()
    {
        _expired.Clear();
        foreach (var (key, at) in _asked)
            if (_time - at >= WorldGenerationConfig.NetLodResendSeconds)
                _expired.Add(key);
        foreach (var key in _expired)
            _asked.Remove(key);
    }

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
