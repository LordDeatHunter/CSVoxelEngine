using System;
using System.Threading;
using VoxelEngine.Chunks;

namespace VoxelEngine.Lod;

/// <summary>
/// One root's region-independent wood base, below every LOD shell sample that can contain it.
/// The fixed square encloses each level's cell window regardless of cell/region alignment.
/// </summary>
internal static class LodTreeAnchor
{
    /// <summary>One authoritative source column and its per-world edit buckets.</summary>
    internal readonly record struct HeightColumn(int[] Heights, long[]? Overrides, int[]? Caps);

    internal static int ForRoot(int x, int z, Func<int, int, HeightColumn> columnAt) =>
        Compute(x, z, columnAt, LodRegion.Levels);

    internal static int RadiusFor(LodLevelSpec[] levels)
    {
        var radius = 0;
        foreach (var level in levels)
            radius = Math.Max(radius, level.CellSize + level.SampleMargin - 1);
        return radius;
    }

    internal static int EditReach(LodLevelSpec spec, int treeReach, bool disturbed) =>
        Math.Max(Math.Max(spec.SampleMargin + spec.CellSize, RadiusFor(LodRegion.Levels) + treeReach),
            disturbed ? 2 * treeReach : 0);

    internal static (int Min, int Max) RegionRange(int coordinate, LodLevelSpec spec, int reach) =>
        (MathUtils.FloorDiv(coordinate - reach, spec.RegionBlocks),
            MathUtils.FloorDiv(coordinate + reach, spec.RegionBlocks));

    internal static int EffectiveHeight(in HeightColumn column, int index)
    {
        var height = column.Heights[index];
        if (column.Overrides is { } overrides)
        {
            var packed = Volatile.Read(ref overrides[index]);
            if (packed != LodSurface.None && LodSurface.IsShellOverride(packed))
                LodSurface.Unpack(packed, out height, out _);
        }
        if (column.Caps is { } caps)
        {
            var cap = Volatile.Read(ref caps[index]);
            if (LodColumnCaps.Applies(cap, height)) height = cap;
        }
        return height;
    }

    internal static int Compute(int x, int z, Func<int, int, HeightColumn> columnAt, LodLevelSpec[] levels)
    {
        var radius = RadiusFor(levels);
        var maxBias = 0;
        var maxCellSize = 1;
        foreach (var level in levels)
        {
            maxBias = Math.Max(maxBias, level.HeightBias);
            maxCellSize = Math.Max(maxCellSize, level.CellSize);
        }
        var min = int.MaxValue;
        for (var cx = (x - radius) >> Chunk.SizeShift; cx <= (x + radius) >> Chunk.SizeShift; cx++)
        for (var cz = (z - radius) >> Chunk.SizeShift; cz <= (z + radius) >> Chunk.SizeShift; cz++)
        {
            var column = columnAt(cx, cz);
            var minX = Math.Max(x - radius, cx * Chunk.Size);
            var maxX = Math.Min(x + radius, (cx + 1) * Chunk.Size - 1);
            var minZ = Math.Max(z - radius, cz * Chunk.Size);
            var maxZ = Math.Min(z + radius, (cz + 1) * Chunk.Size - 1);
            for (var hx = minX; hx <= maxX; hx++)
            for (var hz = minZ; hz <= maxZ; hz++)
            {
                var index = ((hx & Chunk.SizeMask) << Chunk.SizeShift) | (hz & Chunk.SizeMask);
                min = Math.Min(min, EffectiveHeight(column, index));
            }
        }
        var shift = System.Numerics.BitOperations.TrailingZeroCount(maxCellSize);
        return ((min - maxBias) >> shift) << shift;
    }
}
