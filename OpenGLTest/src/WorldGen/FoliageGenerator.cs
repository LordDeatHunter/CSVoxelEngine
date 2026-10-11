using System;

namespace silkgltriangle;

/// <summary>
/// Ground cover on the biome's own surface: a forest grass tuft on dry grass columns and a desert
/// dead shrub on sand. Both are X-shaped cross blocks one cell above the heightmap, and the only
/// difference is the surface each accepts. A pure function of (x, z) like
/// <see cref="PebbleGenerator"/>, so chunks agree without shared state: a low-frequency patch
/// gates, then a position-hash density roll thins inside it.
/// </summary>
public static class FoliageGenerator
{
    private const int GrassPatchSalt = 0x0F011A;
    private const int GrassDensitySalt = 0x0F011B;
    private const int ShrubPatchSalt = 0x0F011C;
    private const int ShrubDensitySalt = 0x0F011D;

    private static FastNoiseLite GrassPatchNoise;
    private static FastNoiseLite ShrubPatchNoise;

    /// <summary>Forest grass tuft this generator stamps; resolved once in the static constructor.</summary>
    public static readonly byte GrassTuftBlock;

    /// <summary>Desert dead shrub this generator stamps; resolved once in the static constructor.</summary>
    public static readonly byte DeadShrubBlock;

    private static readonly byte GrassGroundBlock;
    private static readonly byte SandGroundBlock;

    static FoliageGenerator()
    {
        Reseed();
        GrassTuftBlock = ContentRegistry.RequireBlock("tall_grass").Id;
        DeadShrubBlock = ContentRegistry.RequireBlock("dead_shrub").Id;
        GrassGroundBlock = ContentRegistry.RequireBlock("grass").Id;
        SandGroundBlock = ContentRegistry.RequireBlock("sand").Id;
    }

    /// <summary>
    /// Builds the two patch noises from the current <see cref="WorldGenerationConfig.Seed"/>: once
    /// from the static constructor, and again by <see cref="WorldSeed.Apply"/> while no worker
    /// thread runs.
    /// </summary>
    internal static void Reseed()
    {
        GrassPatchNoise = TerrainGenerator.MakeNoise(63, WorldGenerationConfig.GrassPatchFrequency);
        ShrubPatchNoise = TerrainGenerator.MakeNoise(64, WorldGenerationConfig.ShrubPatchFrequency);
    }

    /// <summary>Whether the forest surface column (x, z) grows a grass tuft.</summary>
    public static bool TryGetGrassTuft(int x, int z)
    {
        if (!TerrainGenerator.IsForest(x, z)) return false;
        if (GrassPatchNoise.GetNoise(x, z) <= WorldGenerationConfig.GrassPatchThreshold) return false;
        // Denser where the climate is wetter (Climate's own wetness, the tint's input)
        TerrainGenerator.Climate(x, z, out _, out var wetness);
        var density = GrassDensityAt(wetness);
        return UnitHash(x, z, GrassDensitySalt) < density;
    }

    /// <summary>The tuft density a column of this climate wetness takes: the dry end's share at
    /// 0, the wet end's at 1, linear between.</summary>
    public static float GrassDensityAt(float wetness) =>
        MathUtils.Lerp(WorldGenerationConfig.GrassDensityDry, WorldGenerationConfig.GrassDensityWet,
            MathUtils.Clamp01(wetness));

    /// <summary>Whether the desert surface column (x, z) grows a dead shrub.</summary>
    public static bool TryGetDeadShrub(int x, int z)
    {
        if (!TerrainGenerator.IsDesert(x, z)) return false;
        if (ShrubPatchNoise.GetNoise(x, z) <= WorldGenerationConfig.ShrubPatchThreshold) return false;
        return UnitHash(x, z, ShrubDensitySalt) < WorldGenerationConfig.ShrubDensity;
    }

    /// <summary>
    /// Stamps ground cover onto the surface: one block above the heightmap, into air only, and only
    /// over the biome's own ground block inside this chunk (a carve below leaves the cell empty
    /// instead of floating). Runs on a generation worker thread, after the flowers.
    /// </summary>
    public static void Stamp(byte[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var height = column.Heights[(x << Chunk.SizeShift) | z];
            // Wet columns and beaches stay bare: ground cover is a dry-land plant
            if (TerrainGenerator.IsWaterColumn(worldX0 + x, worldZ0 + z, height)) continue;
            if (TerrainGenerator.IsBeachHeight(height)) continue;
            var ly = height + 1 - worldY0;
            if ((uint)ly >= Chunk.Size) continue;
            // ly == 0 puts the ground in the chunk below, out of reach of a pure per-chunk stamp
            if (ly == 0) continue;

            var i = Chunk.Idx(x, ly, z);
            if (blocks[i] != BlockType.Air) continue;

            // The ground itself decides, so a tree trunk, a ravine rock or a dither patch
            // disqualifies the column without a second biome test
            var ground = blocks[Chunk.Idx(x, ly - 1, z)];
            if (ground == GrassGroundBlock && TryGetGrassTuft(worldX0 + x, worldZ0 + z))
                blocks[i] = GrassTuftBlock;
            else if (ground == SandGroundBlock && TryGetDeadShrub(worldX0 + x, worldZ0 + z))
                blocks[i] = DeadShrubBlock;
        }
    }

    private static float UnitHash(int x, int z, int salt)
    {
        var h = SplitMix64.Hash(x, z, WorldGenerationConfig.Seed ^ salt);
        return (SplitMix64.Mix(h + 0x9E3779B97F4A7C15UL) >> 40) * (1f / (1 << 24));
    }
}
