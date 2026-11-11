using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Features;

/// <summary>
/// Reeds: clumps of the <c>reed</c> cross block on the columns at a water's edge, placed through
/// <see cref="PlantClumps"/> over <see cref="Clumps"/> and gated per column on its ground sitting
/// at or just under the water level (the shallows), so a clump grows whole or not at all. A pure
/// function of (x, z) and the seed, so chunks agree without shared state. The plant replaces the
/// shallow water cell or takes the dry edge above the ground; the far LOD shows none.
/// </summary>
public static class ReedGenerator
{
    private const int ClumpSalt = 0x5EED;

    /// <summary>The clump shape every reed is placed by.</summary>
    public static readonly PlantClumpSpec Clumps = new(
        WorldFeatureConfig.ReedCellShift,
        WorldFeatureConfig.ReedRadius,
        WorldFeatureConfig.ReedJitter,
        WorldFeatureConfig.ReedMinMembers,
        WorldFeatureConfig.ReedClumpChance,
        WorldFeatureConfig.ReedEdgeDensity,
        ClumpSalt);

    /// <summary>Block this generator stamps; resolved once in the static constructor.</summary>
    public static readonly ushort ReedBlock;

    static ReedGenerator() => ReedBlock = ContentRegistry.RequireBlock("reed").Id;

    /// <summary>Whether the surface column (x, z) is a reed clump member, before the terrain checks.</summary>
    public static bool TryGetReed(int x, int z) =>
        // An oasis pond edge is reedy whatever the clump rule says
        OasisGenerator.IsDisc(x, z)
        || PlantClumps.TryGetMember(Clumps, WorldGenerationConfig.Seed, x, z, null, out _, out _);

    /// <summary>
    /// Stamps reeds at the water's edge: one cell above the ground where the ground sits within
    /// <see cref="WorldFeatureConfig.ReedShallowDepth"/> under the column's water level (the
    /// shallow water cell itself, or the dry edge level with it). Runs on a generation worker
    /// thread after the water, so a reed may replace the shallow cell it stands in. Every test
    /// is pure, so the order is free: the clump read waits for a column the cheap depth and cell
    /// window admit.
    /// </summary>
    public static void Stamp(ushort[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var i2 = (x << Chunk.SizeShift) | z;
            var height = column.Heights[i2];
            var depth = column.WaterLevel[i2] - height;
            if (depth < 0 || depth > WorldFeatureConfig.ReedShallowDepth) continue;

            var ly = height + 1 - worldY0;
            if ((uint)ly >= Chunk.Size || ly == 0) continue;
            if (!TryGetReed(worldX0 + x, worldZ0 + z)) continue;

            var i = Chunk.Idx(x, ly, z);
            // The shallow water cell itself, or the air of a dry edge; never a solid block
            if (blocks[i] != BlockType.Air && !BlockType.IsFluid(blocks[i])) continue;
            blocks[i] = ReedBlock;
        }
    }

    /// <summary>
    /// Every world y <see cref="Stamp"/> can write in a chunk over <paramref name="column"/>: its one
    /// write is at <c>height + 1</c>, and only where <c>0 &lt;= level - height</c>, so
    /// <c>MinHeight &lt;= height &lt;= min(MaxHeight, level)</c> with <c>level &lt;= MaxWaterLevel</c>.
    /// A dry chunk column (<see cref="TerrainGenerator.NoWater"/>) gives an empty band; the min never
    /// subtracts from it.
    /// </summary>
    internal static StampBand WriteBand(ColumnData column) =>
        new(column.MinHeight + 1, Math.Min(column.MaxHeight, column.MaxWaterLevel) + 1);
}
