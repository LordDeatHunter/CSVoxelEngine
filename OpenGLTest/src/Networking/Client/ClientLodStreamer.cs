using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Networking.Client;

/// <summary>
/// Requests far-LOD regions from the server and hands the built meshes to the client's
/// <see cref="LodManager"/>. A client generates no LOD itself, so this is how it sees terrain past
/// the streamed chunks, for any server worldgen. At most
/// <see cref="NetworkConfig.NetLodInFlightMax"/> requests are outstanding at once, and an
/// unanswered one is only re-sent after <see cref="NetworkConfig.NetLodResendSeconds"/>
/// (TCP is reliable; the resend only covers a server that dropped the request). Main thread only.
/// </summary>
public sealed class ClientLodStreamer
{
    private const int MaxRequestsPerUpdate = 8;

    private readonly LodManager _lod;
    private readonly Action<int, int, int, int> _request;
    private readonly Action<int, LodTransfer.Key, int, byte> _ack;
    // Outstanding requests and when each was sent; an answer removes its entry and an entry past
    // the resend age is dropped so the region is asked again.
    private readonly Dictionary<(int Level, int X, int Z), (double At, int Nonce, int Revision)> _asked = new();
    private readonly HashSet<(int Level, int X, int Z)> _dirty = [];
    private readonly Dictionary<(int Level, int X, int Z), int> _revisions = new();
    private LodTransfer.Assembly? _assembly;
    private int _nextNonce;
    private readonly List<(int Level, int X, int Z)> _expired = [];
    private double _time;
    private readonly WireIds? _wire;

    /// <summary><paramref name="wire"/> is the connection's id tables (null keeps the mesh's atlas layers as they came).</summary>
    internal ClientLodStreamer(LodManager lod, Action<int, int, int, int> request,
        Action<int, LodTransfer.Key, int, byte> ack, WireIds? wire = null)
    {
        _lod = lod;
        _wire = wire;
        _request = request;
        _ack = ack;
    }

    internal ClientLodStreamer(LodManager lod, Action<int, int, int, int> request,
        Action<int, LodTransfer.Key, byte> ack, WireIds? wire = null)
        : this(lod, request, (nonce, key, _, status) => ack(nonce, key, status), wire) { }

    // Compatibility for headless request-scanning callers. A live connection supplies both
    // nonce and ACK delegates through the four-argument constructor.
    public ClientLodStreamer(LodManager lod, Action<int, int, int> request, WireIds? wire = null)
        : this(lod, (l, x, z, _) => request(l, x, z), static (_, _, _, _) => { }, wire) { }

    private static readonly (int Dx, int Dz)[][] Offsets = BuildOffsets();

    /// <summary>Requests outstanding right now (sent, unanswered and not yet expired).</summary>
    public int InFlight => _asked.Count;

    internal int RetainedRevisions => _revisions.Count;

    /// <summary>Cancel pending identities when leaving a usable connection/world.</summary>
    internal void CancelAll()
    {
        foreach (var (key, pending) in _asked)
            _ack(pending.Nonce, new LodTransfer.Key(key.Level, key.X, key.Z),
                _assembly?.Header.Nonce == pending.Nonce ? _assembly.Header.Generation : 0,
                LodTransfer.Canceled);
        _asked.Clear();
        _assembly = null;
        _dirty.Clear();
        _revisions.Clear();
    }

    /// <summary>Requests the regions around the camera that are not present yet, nearest first.</summary>
    public void Update(Vector3 cameraPos, double deltaTime)
    {
        _time += deltaTime;
        ExpireStale();
        // An unloaded region will be rebuilt from the server if asked later; retain no
        // edit generation for keys that no longer have a drawable or pending instance.
        _expired.Clear();
        foreach (var key in _revisions.Keys)
            if (!_lod.Regions.ContainsKey(key) && !_asked.ContainsKey(key) &&
                (_assembly == null ||
                 (_assembly.Header.Region.Level, _assembly.Header.Region.X,
                  _assembly.Header.Region.Z) != key))
                _expired.Add(key);
        foreach (var key in _expired)
        {
            _dirty.Remove(key);
            _revisions.Remove(key);
        }
        var budget = Math.Min(MaxRequestsPerUpdate, NetworkConfig.NetLodInFlightMax - _asked.Count);

        for (var level = 0; level < LodRegion.Levels.Length; level++)
        {
            var spec = LodRegion.Levels[level];
            var rx = MathUtils.FloorToInt(cameraPos.X / spec.RegionBlocks);
            var rz = MathUtils.FloorToInt(cameraPos.Z / spec.RegionBlocks);
            var finerCamera = level > 0
                ? (MathUtils.FloorToInt(cameraPos.X / LodRegion.Levels[level - 1].RegionBlocks),
                    MathUtils.FloorToInt(cameraPos.Z / LodRegion.Levels[level - 1].RegionBlocks))
                : default;
            foreach (var (dx, dz) in Offsets[level])
            {
                var key = (level, rx + dx, rz + dz);
                if (_lod.Regions.ContainsKey(key) && !_dirty.Contains(key))
                    continue;
                // The local LodManager drops it again (LodRegion.IsSupersededByFinerRing), so never ask for it
                if (LodRegion.IsSupersededByFinerRing(level, rx + dx, rz + dz, finerCamera,
                        StreamingConfig.LodSupersededBuildMarginRegions))
                    continue;
                if (budget <= 0)
                    return;
                if (_asked.ContainsKey(key))
                    continue;
                var nonce = ++_nextNonce;
                if (nonce <= 0) nonce = _nextNonce = 1;
                _asked[key] = (_time, nonce, _revisions.GetValueOrDefault(key));
                _request(level, rx + dx, rz + dz, nonce);
                budget--;
            }
        }
    }

    /// <summary>Start one bounded logical region. A stale nonce cannot replace a newer edit.</summary>
    internal void Begin(PacketReader reader)
    {
        var begin = LodTransfer.ReadBegin(reader);
        var key = (begin.Region.Level, begin.Region.X, begin.Region.Z);
        if (_assembly is { } current && current.Header.Nonce == begin.Nonce &&
            current.Header.Region == begin.Region) return;
        if (!_asked.TryGetValue(key, out var pending) || pending.Nonce != begin.Nonce ||
            _assembly != null)
        {
            _ack(begin.Nonce, begin.Region, begin.Generation, LodTransfer.Canceled);
            return;
        }
        _assembly = new LodTransfer.Assembly(begin);
        _asked[key] = (_time, pending.Nonce, pending.Revision);
    }

    /// <summary>Accept only the next page of the active, checked logical region.</summary>
    internal void Page(PacketReader reader)
    {
        var nonce = reader.ReadInt();
        var generation = reader.ReadInt();
        var ordinal = reader.ReadInt();
        var bytes = reader.ReadBytes(LodTransfer.PageBytes);
        if (_assembly is not { } assembly || assembly.Header.Nonce != nonce) return;
        if (assembly.Add(nonce, ordinal, bytes, generation))
        {
            var key = assembly.Header.Region;
            var tuple = (key.Level, key.X, key.Z);
            if (_asked.TryGetValue(tuple, out var pending) && pending.Nonce == nonce)
                _asked[tuple] = (_time, nonce, pending.Revision);
            return;
        }
        Reject(assembly.Header, LodTransfer.Rejected);
    }

    internal void Complete(PacketReader reader)
    {
        var nonce = reader.ReadInt();
        var key = new LodTransfer.Key(reader.ReadInt(), reader.ReadInt(), reader.ReadInt());
        var generation = reader.ReadInt();
        var status = reader.ReadByte();
        var tuple = (key.Level, key.X, key.Z);
        if (status == LodTransfer.Rejected)
        {
            if (_asked.TryGetValue(tuple, out var pending) && pending.Nonce == nonce)
                _asked.Remove(tuple);
            if (_assembly?.Header.Nonce == nonce) _assembly = null;
            _ack(nonce, key, generation, LodTransfer.Rejected);
            return;
        }
        if (status != LodTransfer.Applied) return;
        if (_assembly is not { } assembly ||
            assembly.Header.Nonce != nonce || assembly.Header.Region != key ||
            assembly.Header.Generation != generation)
        {
            if (_asked.TryGetValue(tuple, out var pending) && pending.Nonce == nonce)
            {
                _asked.Remove(tuple);
                _ack(nonce, key, generation, LodTransfer.Rejected);
            }
            return;
        }
        try
        {
            var data = LodNetCodec.Decode(assembly.Finish(nonce), _wire);
            if ((data.Level, data.X, data.Z) != tuple ||
                !_asked.TryGetValue(tuple, out var pending) || pending.Nonce != nonce)
                throw new InvalidDataException("LOD reply no longer matches the requested region.");
            if (pending.Revision != _revisions.GetValueOrDefault(tuple))
                throw new InvalidDataException("LOD reply was superseded by a newer edit.");
            _lod.AddStreamedRegion(data);
            _dirty.Remove(tuple);
            _asked.Remove(tuple);
            _ack(nonce, key, generation, LodTransfer.Applied);
            _assembly = null;
        }
        catch (Exception e) when (e is InvalidDataException or OverflowException or InvalidOperationException)
        {
            Reject(assembly.Header, LodTransfer.Rejected);
        }
    }

    /// <summary>Keep the old region visible while a server edit's replacement is requested.</summary>
    internal void MarkEditedColumn(int x, int z)
    {
        for (var level = 0; level < LodRegion.Levels.Length; level++)
        {
            var spec = LodRegion.Levels[level];
            var reach = LodTreeAnchor.EditReach(spec, TreeGenerator.ReachHorizontal,
                TreeCanopy.IsDisturbed(x, z));
            var (rxMin, rxMax) = LodTreeAnchor.RegionRange(x, spec, reach);
            var (rzMin, rzMax) = LodTreeAnchor.RegionRange(z, spec, reach);
            for (var rx = rxMin; rx <= rxMax; rx++)
            for (var rz = rzMin; rz <= rzMax; rz++)
                MarkKey((level, rx, rz));
        }
    }

    /// <summary>Server-owned relevant edit; absent keys have no client history to retain.</summary>
    internal void Invalidate(PacketReader reader)
    {
        var level = reader.ReadInt();
        var x = reader.ReadInt();
        var z = reader.ReadInt();
        _ = reader.ReadInt(); // server build generation; the nonce/assembly also carries it
        if (level == -1)
        {
            var keys = new HashSet<(int Level, int X, int Z)>(_lod.Regions.Keys);
            keys.UnionWith(_asked.Keys);
            foreach (var key in keys) MarkKey(key);
        }
        else if ((uint)level < (uint)LodRegion.Levels.Length)
            MarkKey((level, x, z));
    }

    private void MarkKey((int Level, int X, int Z) key)
    {
        if (!_lod.Regions.ContainsKey(key) && !_asked.ContainsKey(key)) return;
        _dirty.Add(key);
        _revisions[key] = unchecked(_revisions.GetValueOrDefault(key) + 1);
        if (!_asked.Remove(key, out var pending)) return;
        var wireKey = new LodTransfer.Key(key.Level, key.X, key.Z);
        var generation = 0;
        if (_assembly is { } assembly && assembly.Header.Nonce == pending.Nonce &&
            assembly.Header.Region == wireKey)
        {
            generation = assembly.Header.Generation;
            _assembly = null;
        }
        _ack(pending.Nonce, wireKey, generation, LodTransfer.Canceled);
    }

    // Exact key path used by server invalidations; exposed for content-free state tests.
    internal void MarkRegionEdited(int level, int x, int z) => MarkKey((level, x, z));

    private void Reject(LodTransfer.Begin begin, byte status)
    {
        _assembly = null;
        var key = (begin.Region.Level, begin.Region.X, begin.Region.Z);
        if (_asked.TryGetValue(key, out var pending) && pending.Nonce == begin.Nonce)
            _asked.Remove(key);
        _ack(begin.Nonce, begin.Region, begin.Generation, status);
    }

    // Drops the requests older than the resend age, so they are asked again and stop holding an
    // in-flight slot.
    private void ExpireStale()
    {
        _expired.Clear();
        foreach (var (key, pending) in _asked)
            if (_time - pending.At >= NetworkConfig.NetLodResendSeconds)
                _expired.Add(key);
        foreach (var key in _expired)
        {
            if (_assembly is { } assembly &&
                (assembly.Header.Region.Level, assembly.Header.Region.X, assembly.Header.Region.Z) == key)
                Reject(assembly.Header, LodTransfer.Canceled);
            else if (_asked.TryGetValue(key, out var pending))
                _ack(pending.Nonce, new LodTransfer.Key(key.Level, key.X, key.Z), 0,
                    LodTransfer.Canceled);
            _asked.Remove(key);
        }
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
