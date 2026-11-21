using VoxelEngine.Chunks;
using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Features;

/// <summary>
/// The obsidian crust of a Charred lava lake (<see cref="ColumnData.LavaPool"/>): the top terrain cell under the lava,
/// and the top cell of every bank column that touches the lava's surface, cool to obsidian, so a lava lake reads as a
/// glassy black-rimmed pool. A pure per-column function of the column data: a bank column beside a lava column across
/// a chunk border reads that neighbor's <see cref="ColumnCache"/> column, the same split the mud stamp uses, so every
/// chunk stamps the same cells. Runs on a generation worker right after the fluids fill.
/// </summary>
public static class LavaPoolCrust
{
    /// <summary>The block the crust is made of; resolved once.</summary>
    public static readonly ushort CrustBlock = ContentRegistry.RequireBlock("obsidian").Id;

    /// <summary>
    /// Turns the crust cells of the chunk whose minimum corner is (<paramref name="worldX0"/>, <paramref name="worldY0"/>,
    /// <paramref name="worldZ0"/>) to obsidian: each lava column's top terrain cell and each shore column's top cell
    /// (<see cref="IsShore"/>). Only solid cells change; air and fluids are left as they are.
    /// </summary>
    public static void Stamp(ushort[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var local = (x << Chunk.SizeShift) | z;
            var ly = column.Heights[local] - worldY0;
            if ((uint)ly >= Chunk.Size) continue;
            if (!column.LavaPool[local] && !IsShore(column, x, z, worldX0, worldZ0)) continue;
            var i = Chunk.Idx(x, ly, z);
            if (blocks[i] == BlockType.Air || BlockType.IsFluid(blocks[i])) continue;
            blocks[i] = CrustBlock;
        }
    }

    /// <summary>
    /// Whether chunk-local column (<paramref name="x"/>, <paramref name="z"/>) is a lava lake's shore: a dry column with a
    /// lava column on one of its four sides whose surface stands at most <see cref="WorldFeatureConfig.LavaCrustBankRise"/>
    /// cells under this column's top (a bank the lava laps against, not a cliff above it).
    /// </summary>
    public static bool IsShore(ColumnData column, int x, int z, int worldX0, int worldZ0)
    {
        var local = (x << Chunk.SizeShift) | z;
        if (column.LavaPool[local]) return false;
        var top = column.Heights[local];
        if (top < column.WaterLevel[local]) return false;
        return LavaBeside(column, x - 1, z, worldX0, worldZ0, top) || LavaBeside(column, x + 1, z, worldX0, worldZ0, top)
            || LavaBeside(column, x, z - 1, worldX0, worldZ0, top) || LavaBeside(column, x, z + 1, worldX0, worldZ0, top);
    }

    // Whether the column at chunk-local (x, z), possibly in a neighbor chunk, is lava standing close enough under top
    private static bool LavaBeside(ColumnData column, int x, int z, int worldX0, int worldZ0, int top)
    {
        var source = column;
        if ((uint)x >= Chunk.Size || (uint)z >= Chunk.Size)
        {
            var wx = worldX0 + x;
            var wz = worldZ0 + z;
            source = ColumnCache.Get(wx >> Chunk.SizeShift, wz >> Chunk.SizeShift);
            x = wx & Chunk.SizeMask;
            z = wz & Chunk.SizeMask;
        }
        var local = (x << Chunk.SizeShift) | z;
        return source.LavaPool[local] && source.WaterLevel[local] >= top - WorldFeatureConfig.LavaCrustBankRise;
    }
}
