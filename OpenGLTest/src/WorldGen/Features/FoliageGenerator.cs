using VoxelEngine.Chunks;
using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Features;

/// <summary>
/// Ground cover on the biome's own surface: a forest grass tuft on dry grass columns and a dead
/// shrub on desert sand and frozen permafrost. Both are X-shaped cross blocks one cell above the
/// heightmap, and the only difference is the surface each accepts. A shrub on permafrost takes the
/// snow cell the snow pass already laid there (see <see cref="Stamp"/>). A pure function of
/// (x, z) like <see cref="PebbleGenerator"/>, so chunks agree without shared state: a
/// low-frequency patch gates, then a position-hash density roll thins inside it.
/// </summary>
public static class FoliageGenerator
{
    private const int GrassDensitySalt = 0x0F011B;
    private const int ShrubDensitySalt = 0x0F011D;

    private static FastNoiseLite GrassPatchNoise;
    private static FastNoiseLite ShrubPatchNoise;

    /// <summary>Forest grass tuft this generator stamps; resolved once in the static constructor.</summary>
    public static readonly byte GrassTuftBlock;

    /// <summary>Desert and tundra dead shrub this generator stamps; resolved once in the static constructor.</summary>
    public static readonly byte DeadShrubBlock;

    private static readonly byte GrassGroundBlock;
    private static readonly byte SandGroundBlock;
    private static readonly byte PermafrostGroundBlock;

    static FoliageGenerator()
    {
        Reseed();
        GrassTuftBlock = ContentRegistry.RequireBlock("tall_grass").Id;
        DeadShrubBlock = ContentRegistry.RequireBlock("dead_shrub").Id;
        GrassGroundBlock = ContentRegistry.RequireBlock("grass").Id;
        SandGroundBlock = ContentRegistry.RequireBlock("sand").Id;
        PermafrostGroundBlock = ContentRegistry.RequireBlock("permafrost").Id;
    }

    /// <summary>
    /// Builds the two patch noises from the current <see cref="WorldGenerationConfig.Seed"/>: once
    /// from the static constructor, and again by <see cref="WorldSeed.Apply"/> while no worker
    /// thread runs.
    /// </summary>
    internal static void Reseed()
    {
        GrassPatchNoise = TerrainGenerator.MakeNoise(63, WorldFeatureConfig.GrassPatchFrequency);
        ShrubPatchNoise = TerrainGenerator.MakeNoise(64, WorldFeatureConfig.ShrubPatchFrequency);
    }

    /// <summary>Whether the forest, steppe or savanna surface column (x, z) grows a grass tuft.</summary>
    public static bool TryGetGrassTuft(int x, int z)
    {
        // An oasis disc is a dense meadow whatever the patch noise says
        if (OasisGenerator.IsDisc(x, z)) return true;
        if (TerrainGenerator.BiomeAt(x, z) is not (Biome.Forest or Biome.Steppe or Biome.Savanna)) return false;
        if (GrassPatchNoise.GetNoise(x, z) <= WorldFeatureConfig.GrassPatchThreshold) return false;
        // Denser where the climate is wetter (Climate's own wetness, the tint's input)
        TerrainGenerator.Climate(x, z, out _, out var wetness);
        var density = GrassDensityAt(wetness);
        return UnitHash(x, z, GrassDensitySalt) < density;
    }

    /// <summary>The tuft density a column of this climate wetness takes: the dry end's share at
    /// 0, the wet end's at 1, linear between.</summary>
    public static float GrassDensityAt(float wetness) =>
        MathUtils.Lerp(WorldFeatureConfig.GrassDensityDry, WorldFeatureConfig.GrassDensityWet,
            MathUtils.Clamp01(wetness));

    /// <summary>Whether the desert or frozen surface column (x, z) grows a dead shrub.</summary>
    public static bool TryGetDeadShrub(int x, int z) =>
        TerrainGenerator.BiomeAt(x, z) is Biome.Desert or Biome.Frozen && ShrubRoll(x, z);

    /// <summary>
    /// Whether a dead shrub grows over <paramref name="ground"/> at (x, z): sand only in a desert
    /// column and permafrost only in a frozen one, so neither ground grows one in the other biome.
    /// </summary>
    private static bool TryGetDeadShrubOn(byte ground, int x, int z)
    {
        Biome biome;
        if (ground == SandGroundBlock) biome = Biome.Desert;
        else if (ground == PermafrostGroundBlock) biome = Biome.Frozen;
        else return false;
        return TerrainGenerator.BiomeAt(x, z) == biome && ShrubRoll(x, z);
    }

    /// <summary>The shrub scatter inside its biome: a low-frequency patch, then a density roll.</summary>
    private static bool ShrubRoll(int x, int z)
    {
        if (ShrubPatchNoise.GetNoise(x, z) <= WorldFeatureConfig.ShrubPatchThreshold) return false;
        return UnitHash(x, z, ShrubDensitySalt) < WorldFeatureConfig.ShrubDensity;
    }

    /// <summary>
    /// Stamps ground cover onto the surface: one block above the heightmap, into air only, and only
    /// over the biome's own ground block inside this chunk (a carve below leaves the cell empty
    /// instead of floating). The one exception: over permafrost a shrub may also replace the snow
    /// cell, since the snow pass stamps before this one; a snow cell over any other ground is never
    /// taken. The snow cell's state byte is 0 (untouched), so nothing else needs clearing. Runs on
    /// a generation worker thread, after the flowers.
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

            // The ground decides first, so a tree trunk, a ravine rock or a dither patch
            // disqualifies the column before any biome or patch test runs
            var ground = blocks[Chunk.Idx(x, ly - 1, z)];
            var i = Chunk.Idx(x, ly, z);
            var free = blocks[i] == BlockType.Air
                       || (ground == PermafrostGroundBlock && blocks[i] == TerrainGenerator.SnowBlock);
            if (!free) continue;

            if (ground == GrassGroundBlock && TryGetGrassTuft(worldX0 + x, worldZ0 + z))
                blocks[i] = GrassTuftBlock;
            else if (TryGetDeadShrubOn(ground, worldX0 + x, worldZ0 + z))
                blocks[i] = DeadShrubBlock;
        }
    }

    private static float UnitHash(int x, int z, int salt)
    {
        var h = SplitMix64.Hash(x, z, WorldGenerationConfig.Seed ^ salt);
        return (SplitMix64.Mix(h + 0x9E3779B97F4A7C15UL) >> 40) * (1f / (1 << 24));
    }
}
