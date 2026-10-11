namespace silkgltriangle;

/// <summary>
/// Underground pools: cave floors between the lava band and the sea pond over with water sources
/// where a low-frequency patch mask and a per-cell roll agree — a mirror of
/// <see cref="LavaGenerator"/> above <see cref="WorldGenerationConfig.LavaMaxY"/> and below
/// <see cref="WorldGenerationConfig.SeaLevel"/>, buried under the terrain, so entrances and ravine
/// floors (which open to the sky) stay dry. Generated water is a source, so the fluid wake path
/// handles a breach like the sea's. A pure function of world position; runs on a generation worker
/// thread after the carvers and <c>FillWater</c>.
/// </summary>
public static class UndergroundPoolGenerator
{
    private const int PatchSalt = 0x0C1A11;
    private const int PoolSalt = 0x0C1A12;

    private static FastNoiseLite PatchNoise;

    /// <summary>Block this generator stamps; resolved once in the static constructor.</summary>
    public static readonly byte WaterBlock;

    static UndergroundPoolGenerator()
    {
        Reseed();
        WaterBlock = ContentRegistry.RequireBlock("water").Id;
    }

    /// <summary>
    /// Builds the noise from the current <see cref="WorldGenerationConfig.Seed"/>: once from the
    /// static constructor, and again by <see cref="WorldSeed.Apply"/> while no worker thread runs.
    /// </summary>
    internal static void Reseed() => PatchNoise = TerrainGenerator.MakeNoise(94, WorldGenerationConfig.PoolPatchFrequency);

    /// <summary>
    /// Whether the air cell (x, y, z) ponds water: inside the band between the lava and the sea,
    /// buried under the terrain, inside a patch and winning the pool roll.
    /// </summary>
    public static bool HasPool(int x, int y, int z, int surfaceHeight)
    {
        if (y <= WorldGenerationConfig.LavaMaxY) return false;
        if (y > WorldGenerationConfig.SeaLevel) return false;
        if (y > surfaceHeight - WorldGenerationConfig.PoolBurial) return false;
        if (PatchNoise.GetNoise(x, z) <= WorldGenerationConfig.PoolPatchThreshold) return false;
        return UnitHash(x, y, z) < WorldGenerationConfig.PoolChance;
    }

    /// <summary>
    /// Stamps pools onto cave floors: air cells over a solid, non-fluid floor. The bottom layer is
    /// skipped (its floor lives in the chunk below), so ponded water never floats.
    /// </summary>
    public static void Stamp(byte[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        if (worldY0 > WorldGenerationConfig.SeaLevel) return;

        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            if (PatchNoise.GetNoise(worldX0 + x, worldZ0 + z) <= WorldGenerationConfig.PoolPatchThreshold)
                continue;
            var surfaceHeight = column.Heights[(x << Chunk.SizeShift) | z];

            for (var ly = 1; ly < Chunk.Size; ly++)
            {
                var y = worldY0 + ly;
                if (y > WorldGenerationConfig.SeaLevel) break;
                if (y <= WorldGenerationConfig.LavaMaxY) continue;
                if (y > surfaceHeight - WorldGenerationConfig.PoolBurial) continue;

                var i = Chunk.Idx(x, ly, z);
                if (blocks[i] != BlockType.Air) continue;
                var floor = blocks[Chunk.Idx(x, ly - 1, z)];
                if (floor == BlockType.Air || BlockType.IsFluid(floor)) continue;
                if (UnitHash(worldX0 + x, y, worldZ0 + z) >= WorldGenerationConfig.PoolChance) continue;

                blocks[i] = WaterBlock;
            }
        }
    }

    private static float UnitHash(int x, int y, int z)
    {
        var h = SplitMix64.Hash(x ^ (y * 0x9E37), z, WorldGenerationConfig.Seed ^ PoolSalt);
        return (SplitMix64.Mix(h + 0x9E3779B97F4A7C15UL) >> 40) * (1f / (1 << 24));
    }
}
