using System;
using VoxelEngine.Chunks;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Lod;

/// <summary>
/// One root's region-independent wood base, below every LOD shell sample that can contain it.
/// The fixed square encloses each level's cell window regardless of cell/region alignment.
/// </summary>
internal static class LodTreeAnchor
{
    private static readonly Func<int, int, int[]> GeneratedHeights =
        static (cx, cz) => ColumnCache.Get(cx, cz).Heights;

    internal static int ForRoot(int x, int z) => Compute(x, z, GeneratedHeights, LodRegion.Levels);

    internal static int Compute(int x, int z, Func<int, int, int[]> chunkHeights, LodLevelSpec[] levels)
    {
        var radius = 0;
        var maxBias = 0;
        var maxCellSize = 1;
        foreach (var level in levels)
        {
            radius = Math.Max(radius, level.CellSize + level.SampleMargin - 1);
            maxBias = Math.Max(maxBias, level.HeightBias);
            maxCellSize = Math.Max(maxCellSize, level.CellSize);
        }
        var min = int.MaxValue;
        for (var cx = (x - radius) >> Chunk.SizeShift; cx <= (x + radius) >> Chunk.SizeShift; cx++)
        for (var cz = (z - radius) >> Chunk.SizeShift; cz <= (z + radius) >> Chunk.SizeShift; cz++)
        {
            var heights = chunkHeights(cx, cz);
            var minX = Math.Max(x - radius, cx * Chunk.Size);
            var maxX = Math.Min(x + radius, (cx + 1) * Chunk.Size - 1);
            var minZ = Math.Max(z - radius, cz * Chunk.Size);
            var maxZ = Math.Min(z + radius, (cz + 1) * Chunk.Size - 1);
            for (var hx = minX; hx <= maxX; hx++)
            for (var hz = minZ; hz <= maxZ; hz++)
            {
                var index = ((hx & Chunk.SizeMask) << Chunk.SizeShift) | (hz & Chunk.SizeMask);
                min = Math.Min(min, heights[index]);
            }
        }
        var shift = System.Numerics.BitOperations.TrailingZeroCount(maxCellSize);
        return ((min - maxBias) >> shift) << shift;
    }
}
