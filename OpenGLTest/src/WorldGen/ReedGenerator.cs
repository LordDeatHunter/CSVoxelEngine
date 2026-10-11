using System;

namespace silkgltriangle;

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
        WorldGenerationConfig.ReedCellShift,
        WorldGenerationConfig.ReedRadius,
        WorldGenerationConfig.ReedJitter,
        WorldGenerationConfig.ReedMinMembers,
        WorldGenerationConfig.ReedClumpChance,
        WorldGenerationConfig.ReedEdgeDensity,
        ClumpSalt);

    /// <summary>Block this generator stamps; resolved once in the static constructor.</summary>
    public static readonly byte ReedBlock;

    static ReedGenerator() => ReedBlock = ContentRegistry.RequireBlock("reed").Id;

    /// <summary>Whether the surface column (x, z) is a reed clump member, before the terrain checks.</summary>
    public static bool TryGetReed(int x, int z) =>
        PlantClumps.TryGetMember(Clumps, WorldGenerationConfig.Seed, x, z, null, out _, out _);

    /// <summary>
    /// Stamps reeds at the water's edge: one cell above the ground where the ground sits within
    /// <see cref="WorldGenerationConfig.ReedShallowDepth"/> under the column's water level (the
    /// shallow water cell itself, or the dry edge level with it). Runs on a generation worker
    /// thread after the water, so a reed may replace the shallow cell it stands in.
    /// </summary>
    public static void Stamp(byte[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var i2 = (x << Chunk.SizeShift) | z;
            var height = column.Heights[i2];
            var depth = column.WaterLevel[i2] - height;
            if (depth < 0 || depth > WorldGenerationConfig.ReedShallowDepth) continue;
            if (!TryGetReed(worldX0 + x, worldZ0 + z)) continue;

            var ly = height + 1 - worldY0;
            if ((uint)ly >= Chunk.Size || ly == 0) continue;
            var i = Chunk.Idx(x, ly, z);
            // The shallow water cell itself, or the air of a dry edge; never a solid block
            if (blocks[i] != BlockType.Air && !BlockType.IsFluid(blocks[i])) continue;
            blocks[i] = ReedBlock;
        }
    }
}
