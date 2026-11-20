using System;
using System.Collections.Generic;
using VoxelEngine.Lod;

namespace VoxelEngine.Networking;

/// <summary>Bounded main-thread coalescing of region keys touched by relevant LOD edits.</summary>
internal sealed class LodEditInvalidations
{
    internal const int MaxPending = 512;
    private readonly HashSet<LodTransfer.Key> _keys = [];
    internal int Generation { get; private set; }
    internal bool All { get; private set; }
    internal int Count => _keys.Count;

    internal void AddColumn(int x, int z, ReadOnlySpan<int> reaches)
    {
        if (reaches.Length != LodRegion.Levels.Length)
            throw new ArgumentException("One reach is required per LOD level.", nameof(reaches));
        Generation = Generation == int.MaxValue ? 1 : Generation + 1;
        if (All) return;
        for (var level = 0; level < reaches.Length; level++)
        {
            var spec = LodRegion.Levels[level];
            var (x0, x1) = LodTreeAnchor.RegionRange(x, spec, reaches[level]);
            var (z0, z1) = LodTreeAnchor.RegionRange(z, spec, reaches[level]);
            for (var rx = x0; rx <= x1; rx++)
            for (var rz = z0; rz <= z1; rz++)
            {
                _keys.Add(new LodTransfer.Key(level, rx, rz));
                if (_keys.Count <= MaxPending) continue;
                _keys.Clear();
                All = true;
                return;
            }
        }
    }

    internal void Drain(Action<LodTransfer.Key, int> send)
    {
        if (All) send(new LodTransfer.Key(-1, 0, 0), Generation);
        else foreach (var key in _keys) send(key, Generation);
        _keys.Clear();
        All = false;
    }

    internal bool Affects(LodTransfer.Key key) => All || _keys.Contains(key);
    internal void Clear() { _keys.Clear(); All = false; Generation = 0; }
}
