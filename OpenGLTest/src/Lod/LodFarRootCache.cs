using System;
using System.Collections.Generic;
using VoxelEngine.Chunks;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Lod;

/// <summary>Bounded, immutable natural root metadata for one generated chunk column.</summary>
internal sealed class LodFarRootCache
{
    internal static readonly LodFarRootCache Natural = new(static (x, z) =>
        TreeGenerator.TryGetTree(x, z, out var species, out var y) && species.HasLeaves
            ? LodTreeProxy.FromFar(species, x, z, y) : null,
        WorldGenerationConfig.LodFarRootCacheCapacity,
        WorldGenerationConfig.LodFarRootCacheGenerations);

    private readonly Func<int, int, LodTreeProxy?> _resolve;
    private readonly GenerationalCache<(int X, int Z), LodTreeProxy[]> _columns;

    internal LodFarRootCache(Func<int, int, LodTreeProxy?> resolve, int capacity, int generations)
    {
        _resolve = resolve;
        _columns = new GenerationalCache<(int X, int Z), LodTreeProxy[]>(capacity, generations);
    }

    // Call only after source.Supplies has rejected the root chunk. A miss may inspect the
    // rest of that admitted generated column; the caller clips the returned roots to its window.
    internal LodTreeProxy[] Column(int cx, int cz) => _columns.GetOrAdd((cx, cz), BuildColumn);

    internal void Reset() => _columns.Clear();

    private LodTreeProxy[] BuildColumn((int X, int Z) key)
    {
        var roots = new List<LodTreeProxy>();
        var x0 = key.X << Chunk.SizeShift;
        var z0 = key.Z << Chunk.SizeShift;
        for (var dx = 0; dx < Chunk.Size; dx++)
        for (var dz = 0; dz < Chunk.Size; dz++)
            if (_resolve(x0 + dx, z0 + dz) is { } root)
                roots.Add(root);
        return [.. roots]; // X then Z, matching the original point walk.
    }

    internal static LodTreeProxy? At(LodTreeProxy[] roots, int x, int z)
    {
        var low = 0;
        var high = roots.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            var root = roots[middle];
            var order = root.RootX != x ? root.RootX.CompareTo(x) : root.RootZ.CompareTo(z);
            if (order == 0) return root;
            if (order < 0) low = middle + 1;
            else high = middle - 1;
        }
        return null;
    }
}
