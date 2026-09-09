namespace silkgltriangle;

/// <summary>
/// Lava pockets deep underground: cave floors at or below <see cref="WorldGenerationConfig.LavaMaxY"/>
/// pond over with lava sources where a low-frequency patch mask and a per-cell roll agree.
/// A pure function of world position like the other generators, so neighbours agree without
/// shared state. Runs on a generation worker thread after the carvers and <c>FillWater</c>:
/// flooded cells are water, not air, so sea caves never pond lava, and the height check keeps
/// ravine floors and entrances (which reach deep but open to the sky) dry.
/// </summary>
public static class LavaGenerator
{
    private const int PatchSalt = 0x5A1A90;
    private const int PoolSalt = 0x5A1A91;

    private static readonly FastNoiseLite PatchNoise;

    /// <summary>Block this generator stamps; resolved once, like the noise.</summary>
    public static readonly byte LavaBlock;

    static LavaGenerator()
    {
        PatchNoise = TerrainGenerator.MakeNoise(71, WorldGenerationConfig.LavaPatchFrequency);
        LavaBlock = ContentRegistry.RequireBlock("lava").Id;
    }

    /// <summary>
    /// Whether the air cell (x, y, z) ponds lava. Deep interior cells only: below the lava
    /// ceiling and buried under the terrain, inside a patch and winning the pool roll.
    /// </summary>
    public static bool HasLava(int x, int y, int z, int surfaceHeight)
    {
        if (y > WorldGenerationConfig.LavaMaxY) return false;
        if (y > surfaceHeight - WorldGenerationConfig.LavaBurial) return false;
        if (PatchNoise.GetNoise(x, z) <= WorldGenerationConfig.LavaPatchThreshold) return false;
        return UnitHash(x, y, z) < WorldGenerationConfig.LavaPoolChance;
    }

    /// <summary>
    /// Stamps lava onto cave floors: air cells over a solid, non-fluid floor. The bottom
    /// layer is skipped (its floor lives in the chunk below, which this chunk cannot see),
    /// so ponded lava never floats.
    /// </summary>
    public static void Stamp(byte[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        if (worldY0 > WorldGenerationConfig.LavaMaxY) return;

        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            if (PatchNoise.GetNoise(worldX0 + x, worldZ0 + z) <= WorldGenerationConfig.LavaPatchThreshold)
                continue;
            var surfaceHeight = column.Heights[(x << Chunk.SizeShift) | z];

            for (var ly = 1; ly < Chunk.Size; ly++)
            {
                var y = worldY0 + ly;
                if (y > WorldGenerationConfig.LavaMaxY) break;
                if (y > surfaceHeight - WorldGenerationConfig.LavaBurial) continue;

                var i = Chunk.Idx(x, ly, z);
                if (blocks[i] != BlockType.Air) continue;
                var floor = blocks[Chunk.Idx(x, ly - 1, z)];
                if (floor == BlockType.Air || BlockType.IsFluid(floor)) continue;
                if (UnitHash(worldX0 + x, y, worldZ0 + z) >= WorldGenerationConfig.LavaPoolChance) continue;

                blocks[i] = LavaBlock;
            }
        }
    }

    private static float UnitHash(int x, int y, int z)
    {
        var h = SplitMix64.Hash(x ^ (y * 0x9E37), z, WorldGenerationConfig.Seed ^ PoolSalt);
        return (SplitMix64.Mix(h + 0x9E3779B97F4A7C15UL) >> 40) * (1f / (1 << 24));
    }
}
