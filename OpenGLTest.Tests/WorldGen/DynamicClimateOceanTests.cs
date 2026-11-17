using System;
using System.Numerics;
using VoxelEngine.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>Bounded behavior checks for the Dynamic climate, coast and ocean stage.</summary>
[Collection("Worldgen seed")]
public sealed class DynamicClimateOceanTests(ITestOutputHelper output)
{
    private static readonly int[] Seeds = [WorldSeed.Default, WorldSeed.Default + 7, WorldSeed.Default + 7919];

    [Fact]
    public void DynamicClimateAndContinentsChangeMoreGraduallyAcrossSpace()
    {
        TestContent.EnsureLoaded();
        try
        {
            var legacy = Variation(WorldGenProfile.Legacy);
            var dynamic = Variation(WorldGenProfile.Dynamic);
            output.WriteLine($"16x16 x 3 seeds, 512-block neighbor: legacy {legacy}; dynamic {dynamic}");
            Assert.True(dynamic.Temperature < legacy.Temperature, "temperature regions did not broaden");
            Assert.True(dynamic.Humidity < legacy.Humidity, "humidity regions did not broaden");
            Assert.True(dynamic.Continent < legacy.Continent, "continent regions did not broaden");
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
    }

    private static (float Temperature, float Humidity, float Continent) Variation(WorldGenProfile profile)
    {
        float temperature = 0, humidity = 0, continent = 0;
        foreach (var seed in Seeds)
        {
            WorldSeed.Apply(seed, profile);
            if (profile == WorldGenProfile.Dynamic)
                Assert.Equal(Biome.Forest, ColumnCache.Get(0, 0).Biomes[0]);
            for (var ix = 0; ix < 16; ix++)
            for (var iz = 0; iz < 16; iz++)
            {
                var x = -4096 + ix * 512;
                var z = -4096 + iz * 512;
                BiomeClassifier.SampleClimate(x, z, out var t0, out var h0);
                BiomeClassifier.SampleClimate(x + 512, z, out var t1, out var h1);
                temperature += MathF.Abs(t1 - t0);
                humidity += MathF.Abs(h1 - h0);
                continent += MathF.Abs(BiomeClassifier.PreIslandOceanWeight(x + 512, z)
                    - BiomeClassifier.PreIslandOceanWeight(x, z));
            }
        }
        return (temperature, humidity, continent);
    }

    [Fact]
    public void DynamicOceanHasLandAndADeeperBasin()
    {
        TestContent.EnsureLoaded();
        try
        {
            var legacy = Basin(WorldGenProfile.Legacy);
            var dynamic = Basin(WorldGenProfile.Dynamic);
            output.WriteLine($"17x17 x 3 seeds, 600-block grid: legacy {legacy}; dynamic {dynamic}");
            Assert.True(dynamic.Land > 0 && dynamic.Ocean > 0, "Dynamic lost land or ocean coverage");
            Assert.True(dynamic.Deepest < legacy.Deepest, "Dynamic never deepened a sampled basin");
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
    }

    private static (int Land, int Ocean, float Deepest, int Seed, int X, int Z,
        float DefaultDeepest, int DefaultX, int DefaultZ) Basin(WorldGenProfile profile)
    {
        var land = 0;
        var ocean = 0;
        var deepest = float.PositiveInfinity;
        var deepestSeed = 0;
        var deepestX = 0;
        var deepestZ = 0;
        var defaultDeepest = float.PositiveInfinity;
        var defaultX = 0;
        var defaultZ = 0;
        foreach (var seed in Seeds)
        {
            WorldSeed.Apply(seed, profile);
            for (var ix = 0; ix < 17; ix++)
            for (var iz = 0; iz < 17; iz++)
            {
                var x = -4800 + ix * 600;
                var z = -4800 + iz * 600;
                var biome = TerrainGenerator.BiomeWeights(x, z, out var oceanWeight, out _);
                if (biome == Biome.Ocean) ocean++;
                else land++;
                if (oceanWeight < 0.95f) continue;
                var height = TerrainGenerator.LandHeight(x, z, out _, out _, out _, out _);
                if (height < deepest)
                {
                    deepest = height;
                    deepestSeed = seed;
                    deepestX = x;
                    deepestZ = z;
                }
                if (seed == WorldSeed.Default && height < defaultDeepest)
                {
                    defaultDeepest = height;
                    defaultX = x;
                    defaultZ = z;
                }
            }
        }
        return (land, ocean, deepest, deepestSeed, deepestX, deepestZ,
            defaultDeepest, defaultX, defaultZ);
    }

    [Fact]
    public void DynamicEcotonesInterleaveNonDesertLandAcrossChunkSeams()
    {
        TestContent.EnsureLoaded();
        try
        {
            var nonDesertFlips = 0;
            (int Seed, int X, int Z)? firstFlip = null;
            foreach (var seed in Seeds)
            {
                WorldSeed.Apply(seed, WorldGenProfile.Dynamic);
                for (var ix = 0; ix < 40; ix++)
                for (var iz = 0; iz < 40; iz++)
                {
                    var x = -1920 + ix * 96;
                    var z = -1920 + iz * 96;
                    var pure = TerrainGenerator.BiomeWeights(x, z, out _, out var desertWeight);
                    if (pure == Biome.Ocean) continue;
                    var stored = BiomeClassifier.DitherBiome(x, z, pure, desertWeight);
                    if (stored == pure) continue;
                    BiomeClassifier.SampleClimate(x, z, out var temperature, out var humidity);
                    var near = MathF.Min(
                        MathF.Min(MathF.Abs(BiomeClassifier.ColdOf(temperature) - WorldGenerationConfig.ColdBiomeMinCold),
                            MathF.Abs(BiomeClassifier.WarmOf(temperature) - 0.5f)),
                        MathF.Min(MathF.Abs(BiomeClassifier.DryOf(humidity) - 0.5f),
                            MathF.Min(MathF.Abs(BiomeClassifier.SemiDryOf(humidity) - 0.5f),
                                MathF.Abs(BiomeClassifier.JungleOf(temperature, humidity) - 0.5f))));
                    Assert.True(near <= WorldGenerationConfig.DynamicEcotoneBand,
                        $"surface biome changed away from a climate boundary at ({x},{z})");
                    var column = ColumnCache.Get(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
                    Assert.Equal(stored, column.Biomes[0]);
                    Assert.Equal(stored, TerrainGenerator.FloraBiomeAt(x, z));
                    if (pure is Biome.Desert or Biome.Savanna or Biome.Steppe) continue;
                    var left = ColumnCache.Get((x - 1) >> Chunk.SizeShift, z >> Chunk.SizeShift);
                    var leftPure = TerrainGenerator.BiomeWeights(x - 1, z, out _, out var leftDesert);
                    Assert.Equal(BiomeClassifier.DitherBiome(x - 1, z, leftPure, leftDesert),
                        left.Biomes[Chunk.SizeMask << Chunk.SizeShift]);
                    firstFlip ??= (seed, x, z);
                    nonDesertFlips++;
                }
            }
            output.WriteLine($"40x40 x 3 seeds, 96-block grid: {nonDesertFlips} non-desert ecotone flips");
            Assert.True(nonDesertFlips > 0, "no non-desert ecotone appeared");
            var (chosenSeed, seamX, seamZ) = firstFlip!.Value;
            output.WriteLine($"first non-desert border: seed {chosenSeed}, ({seamX},{seamZ})");
            WorldSeed.Apply(chosenSeed, WorldGenProfile.Dynamic);
            foreach (var x in new[] { seamX - 1, seamX })
            {
                var cx = x >> Chunk.SizeShift;
                var cz = seamZ >> Chunk.SizeShift;
                var column = ColumnCache.Get(cx, cz);
                var local = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (seamZ & Chunk.SizeMask);
                var cy = column.Heights[local] >> Chunk.SizeShift;
                var position = new Vector3(cx, cy, cz);
                var first = new Chunk(null!, position, generateBlockData: false);
                var repeated = new Chunk(null!, position, generateBlockData: false);
                ChunkTerrainFill.GenerateBlockData(first);
                ChunkTerrainFill.GenerateBlockData(repeated);
                Assert.Equal(first.Storage.ToDense(), repeated.Storage.ToDense());
            }
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
    }

    [Fact]
    public void DynamicEcotoneSoloTreeShapeUsesTheAcceptedSpecies()
    {
        TestContent.EnsureLoaded();
        try
        {
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Dynamic);
            // The first sampled non-desert flip is near (-1728,1824). Search only its
            // local ecotone for a real accepted trunk, rather than inventing a species.
            for (var x = -1776; x <= -1680; x++)
            for (var z = 1776; z <= 1872; z++)
            {
                var pure = TerrainGenerator.BiomeAt(x, z);
                var flora = TerrainGenerator.FloraBiomeAt(x, z);
                if (pure == flora || !TreeGenerator.TryGetTree(x, z, out var accepted, out _)) continue;
                if (TreeGenerator.SpeciesByBiome[(int)flora] != accepted
                    || TreeGenerator.SpeciesByBiome[(int)pure] == accepted) continue;

                var solo = TreeBatches.ShapeOf(x, z);
                output.WriteLine($"real Dynamic ecotone trunk ({x},{z}): pure {pure}, flora {flora}, accepted {accepted.Name}");
                Assert.Same(accepted, solo.Species);
                return;
            }
            Assert.Fail("no accepted tree with changed species in the bounded ecotone");
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
    }

    [Fact]
    public void DynamicShoreKeepsAWetShelfWithoutAnAbruptStep()
    {
        TestContent.EnsureLoaded();
        try
        {
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Dynamic);
            var wetShelves = 0;
            (int X, int Z)? firstShelf = null;
            for (var ix = 0; ix < 41; ix++)
            for (var iz = 0; iz < 41; iz++)
            {
                var x = -3840 + ix * 192;
                var z = -3840 + iz * 192;
                TerrainGenerator.BiomeWeights(x, z, out var ocean, out _);
                if (ocean < WorldGenerationConfig.OceanShoreWeight || ocean >= 0.5f) continue;
                var h = TerrainGenerator.LandHeight(x, z, out _, out _, out _, out _);
                if (h >= WorldGenerationConfig.SeaLevel
                    || !TerrainGenerator.IsWaterColumn(x, z, (int)h)) continue;
                var neighbor = TerrainGenerator.LandHeight(x + 1, z, out _, out _, out _, out _);
                Assert.True(MathF.Abs(neighbor - h) < 4f, $"shore step at ({x},{z}): {h} to {neighbor}");
                firstShelf ??= (x, z);
                wetShelves++;
            }
            output.WriteLine($"41x41 default-seed shore samples: {wetShelves} wet shelves");
            output.WriteLine($"first wet shelf: {firstShelf}");
            Assert.True(wetShelves > 0, "no shallow wet shelf in the bounded sample");
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
    }
}
