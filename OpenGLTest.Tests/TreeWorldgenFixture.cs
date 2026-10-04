using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace silkgltriangle.Tests;

/// <summary>
/// Shared helpers of the worldgen tree facts: finding a wooded forest chunk column, generating
/// its 3x3 neighbourhood and the wood/leaf id sets.
/// </summary>
internal static class TreeWorldgenFixture
{
    public static int HeightAt(int x, int z) =>
        ColumnCache.GetHeights(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
            [((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)];

    /// <summary>
    /// A chunk column whose 3x3 neighbourhood is forest and whose centre roots a tree, searched in
    /// rings outward from <paramref name="origin"/> (chunk coordinates) up to <paramref name="maxRadius"/>.
    /// </summary>
    public static (int Cx, int Cz) FindForestTreeColumn((int OriginCx, int OriginCz) origin = default, int maxRadius = 48)
    {
        for (var r = 1; r <= maxRadius; r++)
        for (var cx = origin.OriginCx - r; cx <= origin.OriginCx + r; cx++)
        for (var cz = origin.OriginCz - r; cz <= origin.OriginCz + r; cz++)
        {
            if (Math.Max(Math.Abs(cx - origin.OriginCx), Math.Abs(cz - origin.OriginCz)) != r) continue;

            var allForest = true;
            for (var dx = -1; dx <= 1 && allForest; dx++)
            for (var dz = -1; dz <= 1 && allForest; dz++)
                allForest = TerrainGenerator.IsForest((cx + dx) * Chunk.Size + 8, (cz + dz) * Chunk.Size + 8);
            if (!allForest) continue;

            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
                if (TreeGenerator.TryGetTree(cx * Chunk.Size + x, cz * Chunk.Size + z, out _, out _))
                    return (cx, cz);
        }

        throw new InvalidOperationException(
            $"no wooded forest chunk column within {maxRadius} chunks of {origin}");
    }

    /// <summary>
    /// Generates the 3x3 chunk columns around (cx, cz) over the band from below the lowest surface
    /// to above the tallest tree, and returns every cell's (block, state) by world position.
    /// </summary>
    public static Dictionary<(int X, int Y, int Z), (byte Block, byte State)> Generate(int cx, int cz, bool reverse)
    {
        var minHeight = int.MaxValue;
        var maxHeight = int.MinValue;
        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
        {
            var column = ColumnCache.Get(cx + dx, cz + dz);
            minHeight = Math.Min(minHeight, column.MinHeight);
            maxHeight = Math.Max(maxHeight, column.MaxHeight);
        }

        var cyLo = (minHeight >> Chunk.SizeShift) - 1;
        var cyHi = ((maxHeight + TreeGenerator.ReachUp) >> Chunk.SizeShift) + 1;
        var positions = new List<Vector3>();
        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
        for (var cy = cyLo; cy <= cyHi; cy++)
            positions.Add(new Vector3(cx + dx, cy, cz + dz));
        if (reverse) positions.Reverse();

        var cells = new Dictionary<(int X, int Y, int Z), (byte Block, byte State)>();
        foreach (var position in positions)
        {
            var chunk = new Chunk(null!, position, generateBlockData: false);
            chunk.GenerateBlockData();
            var x0 = (int)position.X * Chunk.Size;
            var y0 = (int)position.Y * Chunk.Size;
            var z0 = (int)position.Z * Chunk.Size;
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
                cells[(x0 + x, y0 + y, z0 + z)] = (chunk.GetLocalBlock(x, y, z), chunk.GetLocalState(x, y, z));
        }

        return cells;
    }

    public static HashSet<byte> WoodIds() => TreeSpeciesRegistry.All.Select(s => s.WoodId).ToHashSet();

    public static HashSet<byte> LeafIds() =>
        TreeSpeciesRegistry.All.Where(s => s.HasLeaves).Select(s => s.LeavesId).ToHashSet();
}
