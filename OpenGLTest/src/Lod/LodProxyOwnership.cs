using System;
using System.Collections.Generic;

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
