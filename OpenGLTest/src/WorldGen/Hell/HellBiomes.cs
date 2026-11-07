using System;
using VoxelEngine.Content;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.WorldGen.Hell;

/// <summary>
/// Which <see cref="HellBiome"/> a hell cell belongs to (plans/LAYERS.md L-7), and what each biome
/// is made of. A coherent low-frequency 3D noise (seed offset 164) is sampled at the column's x and z
/// and its layer's mid row, plus a hashed lean per layer (<see cref="WorldGenerationConfig.HellBiomeLayerBias"/>),
/// and cut into three bands: so a biome region spans a few hundred blocks, one layer mixes biomes,
/// and the layer under it is laid out differently. The biome is constant down a column of one layer,
/// so a pocket's floor and ceiling over the same spot agree. A pure function of (x, y, z) and the seed.
/// </summary>
public static class HellBiomes
{
    private static FastNoiseLite BiomeNoise;
    private static FastNoiseLite PoolNoise;

    private static readonly HellBiomePalette[] Palettes;

    static HellBiomes()
    {
        Reseed();

        byte Block(string name) => ContentRegistry.RequireBlock(name).Id;
        var hellrock = Block("hellrock");
        var scorched = Block("scorched_hellrock");
        var basalt = Block("basalt");
        var magma = Block("magma_block");
        var emberCrystal = Block("ember_crystal");
        var fungalSoil = Block("fungal_soil");

        Palettes = new HellBiomePalette[Enum.GetValues<HellBiome>().Length];
        Palettes[(int)HellBiome.Ashlands] = new HellBiomePalette(
            Surface: Block("ash"), Soil: Block("cinder_soil"), Wall: scorched, Ceiling: scorched,
            PoolBed: scorched, CeilingLight: emberCrystal, CeilingLightRate: WorldGenerationConfig.HellEmberCrystalRate,
            FloorLight: BlockType.Air, FloorLightRate: 1, LavaShare: WorldGenerationConfig.HellAshlandsLavaShare,
            GroundPlant: Block("ember_bloom"), GroundPlantRate: WorldGenerationConfig.HellEmberBloomRate,
            VineRate: WorldGenerationConfig.HellAshlandsVineRate,
            Tree: TreeSpeciesRegistry.Require("charred_tree"), TreeChance: WorldGenerationConfig.HellCharredTreeChance);
        Palettes[(int)HellBiome.MagmaCaverns] = new HellBiomePalette(
            Surface: basalt, Soil: scorched, Wall: basalt, Ceiling: basalt,
            PoolBed: magma, CeilingLight: emberCrystal, CeilingLightRate: WorldGenerationConfig.HellEmberCrystalRate,
            FloorLight: magma, FloorLightRate: WorldGenerationConfig.HellMagmaFloorRate, LavaShare: 1f,
            GroundPlant: BlockType.Air, GroundPlantRate: 1, VineRate: 0, Tree: null, TreeChance: 0f);
        Palettes[(int)HellBiome.FungalGrove] = new HellBiomePalette(
            Surface: Block("fungal_moss"), Soil: fungalSoil, Wall: hellrock, Ceiling: fungalSoil,
            PoolBed: fungalSoil, CeilingLight: Block("fungal_glow"), CeilingLightRate: WorldGenerationConfig.HellFungalGlowRate,
            FloorLight: BlockType.Air, FloorLightRate: 1, LavaShare: WorldGenerationConfig.HellFungalLavaShare,
            GroundPlant: Block("glow_fern"), GroundPlantRate: WorldGenerationConfig.HellGlowFernRate,
            VineRate: WorldGenerationConfig.HellFungalVineRate,
            Tree: TreeSpeciesRegistry.Require("fungal_tree"), TreeChance: WorldGenerationConfig.HellFungalTreeChance);
    }

    /// <summary>Builds the noises from the current seed (static constructor and <see cref="WorldSeed.Apply"/>).</summary>
    internal static void Reseed()
    {
        BiomeNoise = TerrainGenerator.MakeNoise(164, WorldGenerationConfig.HellBiomeFrequency, octaves: 2);
        PoolNoise = TerrainGenerator.MakeNoise(165, WorldGenerationConfig.HellPoolFrequency);
    }

    /// <summary>The palette of <paramref name="biome"/>.</summary>
    public static HellBiomePalette Palette(HellBiome biome) => Palettes[(int)biome];

    /// <summary>The biome of hell cell (x, y, z); y must lie in hell.</summary>
    public static HellBiome At(int x, int y, int z)
    {
        if (!HellGenerator.InHell(y)) throw new ArgumentOutOfRangeException(nameof(y), y, "above hell");
        return AtLayer(x, HellGenerator.LayerIndex(y), z);
    }

    /// <summary>The biome of column (x, z) in hell layer <paramref name="layer"/>.</summary>
    public static HellBiome AtLayer(int x, int layer, int z)
    {
        var bx = x & ~BiomeMask;
        var bz = z & ~BiomeMask;
        return Classify(Bilerp(
            BiomeSample(bx, layer, bz), BiomeSample(bx, layer, bz + BiomeStride),
            BiomeSample(bx + BiomeStride, layer, bz), BiomeSample(bx + BiomeStride, layer, bz + BiomeStride),
            x, z, BiomeStride), layer);
    }

    /// <summary>Whether the pool rows of column (x, z) in <paramref name="layer"/> hold lava, given
    /// that column's biome: always at a lava share of 1, never at 0, else where the pool noise falls
    /// under the share.</summary>
    public static bool PoolIsLava(int x, int layer, int z, HellBiome biome)
    {
        if (PoolShareDecides(biome, out var lava)) return lava;

        var bx = x & ~PoolMask;
        var bz = z & ~PoolMask;
        return PoolUnder(biome, Bilerp(
            PoolSample(bx, layer, bz), PoolSample(bx, layer, bz + PoolStride),
            PoolSample(bx + PoolStride, layer, bz), PoolSample(bx + PoolStride, layer, bz + PoolStride),
            x, z, PoolStride));
    }

    // The biome and pool noises are read on 2D lattices per layer, blended bilinearly at the column's
    // center: the biome lattice is chunk-aligned, so one chunk reads 4 biome points and 9 pool points
    // per layer. The stamp caches the points and calls the same blend and rules as the point query.

    /// <summary>The biome lattice spacing: one chunk.</summary>
    internal const int BiomeStride = WorldGenerationConfig.HellBiomeLatticeStride;

    /// <summary>The pool lattice spacing.</summary>
    internal const int PoolStride = WorldGenerationConfig.HellPoolLatticeStride;

    private const int BiomeMask = BiomeStride - 1;
    private const int PoolMask = PoolStride - 1;

    /// <summary>The biome noise at lattice point (x, z) of <paramref name="layer"/>.</summary>
    internal static float BiomeSample(int x, int layer, int z) => BiomeNoise.GetNoise(x, MidRow(layer), z);

    /// <summary>The pool noise at lattice point (x, z) of <paramref name="layer"/>.</summary>
    internal static float PoolSample(int x, int layer, int z) =>
        PoolNoise.GetNoise(x, layer * WorldGenerationConfig.HellLayerPeriodBlocks, z);

    /// <summary>The bilinear blend of a lattice cell's corners (c00 at its base, c01 one step in z, c10
    /// one step in x) at column (x, z)'s center, the one expression both paths use.</summary>
    internal static float Bilerp(float c00, float c01, float c10, float c11, int x, int z, int stride)
    {
        var fx = ((x & (stride - 1)) + 0.5f) / stride;
        var fz = ((z & (stride - 1)) + 0.5f) / stride;
        var near = c00 + (c01 - c00) * fz;
        var far = c10 + (c11 - c10) * fz;
        return near + (far - near) * fx;
    }

    /// <summary>The biome a blended biome value picks in <paramref name="layer"/>, after its lean.</summary>
    internal static HellBiome Classify(float value, int layer)
    {
        value += LayerLean(layer);
        if (value < WorldGenerationConfig.HellAshlandsMaxValue) return HellBiome.Ashlands;
        return value > WorldGenerationConfig.HellFungalMinValue ? HellBiome.FungalGrove : HellBiome.MagmaCaverns;
    }

    /// <summary>Whether <paramref name="biome"/>'s lava share alone decides its pools: all lava at 1,
    /// none at 0.</summary>
    internal static bool PoolShareDecides(HellBiome biome, out bool lava)
    {
        var share = Palette(biome).LavaShare;
        lava = share >= 1f;
        return lava || share <= 0f;
    }

    /// <summary>Whether a blended pool value falls under <paramref name="biome"/>'s lava share.</summary>
    internal static bool PoolUnder(HellBiome biome, float value) => 0.5f * (value + 1f) < Palette(biome).LavaShare;

    /// <summary>A hash of world cell (x, y, z) and the seed, for the scattered light blocks: one cell in
    /// <paramref name="rate"/> is picked.</summary>
    public static bool Picks(int x, int y, int z, int rate) => Hash(x, y, z, 0) % (ulong)rate == 0;

    /// <summary>A hash of world cell (x, y, z), <paramref name="salt"/> and the seed: each salt picks
    /// its own scatter, so two features hashing one cell never line up.</summary>
    public static ulong Hash(int x, int y, int z, ulong salt)
    {
        var packed = ((ulong)(uint)x * 0x9E3779B97F4A7C15UL) ^ ((ulong)(uint)y * 0xC2B2AE3D27D4EB4FUL)
                     ^ ((ulong)(uint)z * 0x165667B19E3779F9UL);
        return SplitMix64.Mix(packed ^ SplitMix64.Mix((ulong)(uint)WorldGenerationConfig.Seed ^ salt));
    }

    /// <summary>The world row the biome noise is sampled at for <paramref name="layer"/>: its pocket's middle.</summary>
    private static int MidRow(int layer) =>
        WorldGenerationConfig.HellTopY - layer * WorldGenerationConfig.HellLayerPeriodBlocks - HellGenerator.PocketHeight / 2;

    /// <summary>The hashed lean <paramref name="layer"/> adds to the biome noise, within
    /// +-<see cref="WorldGenerationConfig.HellBiomeLayerBias"/>.</summary>
    private static float LayerLean(int layer) =>
        WorldGenerationConfig.HellBiomeLayerBias * (2f * SplitMix64.Unit(SplitMix64.Hash(layer, 0x4845, WorldGenerationConfig.Seed), 0) - 1f);
}
