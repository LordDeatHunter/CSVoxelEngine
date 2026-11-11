using System;
using VoxelEngine.Content;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.WorldGen.Sky;

/// <summary>
/// Which <see cref="SkyBiome"/> the sky holds where (plans/LAYERS.md <c>L-13</c>), and what each biome
/// builds its islands of. A coherent low-frequency 2D noise (seed offset 174, regions about a zone across)
/// is cut at <see cref="SkyIslandConfig.SkyFrostMinValue"/>: meadow below, frost highland above. An island
/// takes the biome at its center (<see cref="OfIsland"/>), so the whole island wears one palette and its
/// neighbors in a zone mostly share it. A pure function of (x, z) and the seed.
/// </summary>
public static class SkyBiomes
{
    private static FastNoiseLite BiomeNoise;

    private static readonly SkyIslandPalette[] Palettes;

    static SkyBiomes()
    {
        Reseed();

        ushort Block(string name) => ContentRegistry.RequireBlock(name).Id;
        Palettes = new SkyIslandPalette[Enum.GetValues<SkyBiome>().Length];
        Palettes[(int)SkyBiome.Meadow] = new SkyIslandPalette(
            Top: TerrainGenerator.SurfaceBlock, Soil: TerrainGenerator.SoilBlock, Core: TerrainGenerator.RockBlock,
            SoilDepth: SkyIslandConfig.IslandSoilDepth, Sparkle: BlockType.Air, SparkleRate: 1,
            // Tall grass twice as often as a flower
            Plants: [Block("tall_grass"), Block("tall_grass"), Block("flower")], PlantRate: SkyIslandConfig.MeadowPlantRate,
            VineRate: SkyIslandConfig.MeadowVineRate,
            Tree: TreeSpeciesRegistry.Require("sky_oak"), TreeChance: SkyIslandConfig.MeadowTreeChance);
        Palettes[(int)SkyBiome.FrostHighland] = new SkyIslandPalette(
            Top: Block("frost_grass"), Soil: Block("permafrost"), Core: Block("frost_stone"),
            SoilDepth: SkyIslandConfig.IslandSoilDepth, Sparkle: Block("crystal_block"), SparkleRate: SkyIslandConfig.FrostCrystalRate,
            Plants: [Block("frost_fern")], PlantRate: SkyIslandConfig.FrostPlantRate,
            VineRate: SkyIslandConfig.FrostVineRate,
            Tree: TreeSpeciesRegistry.Require("crystal_tree"), TreeChance: SkyIslandConfig.FrostTreeChance);
    }

    /// <summary>Builds the noise from the current seed (static constructor and <see cref="WorldSeed.Apply"/>).</summary>
    internal static void Reseed() => BiomeNoise = TerrainGenerator.MakeNoise(174, SkyIslandConfig.SkyBiomeFrequency, octaves: 2);

    /// <summary>The sky biome at column (x, z).</summary>
    public static SkyBiome At(float x, float z) =>
        BiomeNoise.GetNoise(x, z) > SkyIslandConfig.SkyFrostMinValue ? SkyBiome.FrostHighland : SkyBiome.Meadow;

    /// <summary>The biome <paramref name="island"/> wears: the one at its center.</summary>
    public static SkyBiome OfIsland(in SkyIsland island) => At(island.CenterX, island.CenterZ);

    /// <summary>The biome of column (x, z) of <paramref name="island"/>: an ordinary island wears its center's biome
    /// whole, a landmass changes biome across its length with the noise.</summary>
    public static SkyBiome OfColumn(in SkyIsland island, float x, float z) =>
        island.IsLandmass ? At(x, z) : At(island.CenterX, island.CenterZ);

    /// <summary>The palette of <paramref name="biome"/>.</summary>
    public static SkyIslandPalette Palette(SkyBiome biome) => Palettes[(int)biome];

    /// <summary>A hash of column (x, z), <paramref name="salt"/> and the seed: each salt picks its own
    /// scatter, so two features hashing one column never line up.</summary>
    public static ulong Hash(int x, int z, ulong salt)
    {
        var packed = ((ulong)(uint)x * 0x9E3779B97F4A7C15UL) ^ ((ulong)(uint)z * 0x165667B19E3779F9UL);
        return SplitMix64.Mix(packed ^ SplitMix64.Mix((ulong)(uint)WorldGenerationConfig.Seed ^ salt));
    }
}
