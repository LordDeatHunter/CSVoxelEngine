using VoxelEngine.Chunks;
using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Features;

/// <summary>
/// Lily pads: a flat pad stamped on the cell above a still lake's top water cell. A lake is a
/// column whose own <see cref="ColumnData.WaterLevel"/> sits above the ground and is not the sea
/// level (<see cref="TerrainGenerator.WaterLevelAt"/> gives the ocean sea level, a lake its own
/// surface and a river its own level, which is the sea level except on an elevated great lake's
/// river ramp, so a stepped ramp river above the sea can carry pads too), and generated water is all
/// sources, so the surface is still by construction.
/// A pure per-column hash of (x, z) and the seed; the far LOD shows none.
/// </summary>
public static class LilyPadGenerator
{
    /// <summary>Block this generator stamps; resolved once in the static constructor.</summary>
    public static readonly ushort PadBlock;

    static LilyPadGenerator() => PadBlock = ContentRegistry.RequireBlock("lily_pad").Id;

    /// <summary>Whether the lake column (x, z) carries a pad, before the terrain checks.</summary>
    public static bool TryGetPad(int x, int z)
    {
        var h = SplitMix64.Hash(x, z, WorldGenerationConfig.Seed ^ 0x71D5);
        return (SplitMix64.Mix(h) >> 40) * (1f / (1 << 24)) < WorldFeatureConfig.LilyPadChance;
    }

    /// <summary>
    /// Stamps pads onto lake surfaces: the cell above the top water cell of a wet, non-sea column,
    /// into air only. Runs on a generation worker thread after the water. Every test is pure, so
    /// the order is free: the pad hash waits for a column the cheap level and cell window admit.
    /// </summary>
    public static void Stamp(ushort[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var i2 = (x << Chunk.SizeShift) | z;
            var waterLevel = column.WaterLevel[i2];
            // The ocean and rivers are sea level; a lake carries its own surface, above its ground
            if (waterLevel == WorldGenerationConfig.SeaLevel || waterLevel <= column.Heights[i2]) continue;

            var ly = waterLevel - worldY0;
            if ((uint)ly >= Chunk.Size) continue;
            if (!TryGetPad(worldX0 + x, worldZ0 + z)) continue;

            var i = Chunk.Idx(x, ly, z);
            if (blocks[i] != BlockType.Air) continue;
            blocks[i] = PadBlock;
        }
    }

    /// <summary>
    /// Every world y <see cref="Stamp"/> can write in a chunk over <paramref name="column"/>: its one
    /// write is at the column's own <c>waterLevel</c>, and only where <c>waterLevel &gt; height</c>,
    /// so <c>MinHeight &lt; waterLevel &lt;= MaxWaterLevel</c>. A dry chunk column
    /// (<see cref="TerrainGenerator.NoWater"/>) gives an empty band.
    /// </summary>
    internal static StampBand WriteBand(ColumnData column) => new(column.MinHeight + 1, column.MaxWaterLevel);
}
