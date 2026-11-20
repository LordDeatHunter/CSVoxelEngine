using System;
using System.Collections.Generic;
using VoxelEngine.Chunks;

namespace VoxelEngine.Lod;

/// <summary>Pure source-ownership gates used before distant-tree generation work.</summary>
internal static class LodProxyOwnership
{
    internal static bool HasGeneratedOwnedColumn(bool[] supplied, int chunkSpan, int ownedSpan)
    {
        for (var x = 1; x <= ownedSpan; x++)
        for (var z = 1; z <= ownedSpan; z++)
            if (!supplied[x * chunkSpan + z]) return true;
        return false;
    }

    internal static void CollectFarCached(bool[] supplied, int chunkSpan, int ownedSpan,
        int x0, int z0, int regionBlocks, int reach,
        Func<int, int, bool> supplies, LodFarRootCache cache, List<LodTreeProxy> output)
    {
        if (!HasGeneratedOwnedColumn(supplied, chunkSpan, ownedSpan)) return;
        // A build-local view amortizes the shared cache lookup without changing the original
        // global X-then-Z walk. Source ownership is checked before even this local lookup.
        var columns = new Dictionary<(int X, int Z), LodTreeProxy[]>();
        for (var x = x0 - reach; x < x0 + regionBlocks + reach; x++)
        for (var z = z0 - reach; z < z0 + regionBlocks + reach; z++)
        {
            if (supplies(x, z)) continue;
            var key = (x >> Chunk.SizeShift, z >> Chunk.SizeShift);
            if (!columns.TryGetValue(key, out var roots))
            {
                roots = cache.Column(key.Item1, key.Item2);
                columns.Add(key, roots);
            }
            if (LodFarRootCache.At(roots, x, z) is { } root) output.Add(root);
        }
    }

    internal static void CollectFar<T>(bool[] supplied, int chunkSpan, int ownedSpan,
        int x0, int z0, int regionBlocks, int reach,
        Func<int, int, bool> supplies, Func<int, int, T?> resolve, List<T> output) where T : struct
    {
        if (!HasGeneratedOwnedColumn(supplied, chunkSpan, ownedSpan)) return;
        for (var x = x0 - reach; x < x0 + regionBlocks + reach; x++)
        for (var z = z0 - reach; z < z0 + regionBlocks + reach; z++)
        {
            if (supplies(x, z)) continue;
            if (resolve(x, z) is { } value) output.Add(value);
        }
    }
}
