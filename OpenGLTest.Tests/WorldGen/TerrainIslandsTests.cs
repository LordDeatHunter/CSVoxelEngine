using System;
using System.Collections.Generic;
using VoxelEngine.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>
/// The island term (<see cref="TerrainIslands"/>) and its fold into the biome weights: the folded ocean
/// weight is the pre-island weight times one minus the island land weight and nothing else, nothing at
/// or inside the shore band and nothing in the spawn clearing changes, deeper ocean and a stronger
/// archipelago never hold less island, an archipelago strings more islets along its own direction than
/// the same water without it, and island land is dry land of its climate.
/// </summary>
public sealed class TerrainIslandsTests
{
    private const int ScanHalfExtent = 16000;
    private const int ScanStride = 97;
    private const int FootprintStride = 8;

    private readonly ITestOutputHelper _output;

    public TerrainIslandsTests(ITestOutputHelper output)
    {
        TestContent.EnsureLoaded();
        _output = output;
    }

    private readonly record struct Sample(float X, float Z, float Pre, float Ocean, float Desert, float Arid,
        Biome Biome, float Island)
    {
        public bool IsIslandLand => Pre > WorldGenerationConfig.OceanShoreWeight
                                    && Ocean < WorldGenerationConfig.OceanShoreWeight;
    }

    private static readonly Lazy<Sample[]> Samples = new(Scan);

    private static Sample[] Scan()
    {
        var samples = new List<Sample>();
        for (var x = -ScanHalfExtent; x <= ScanHalfExtent; x += ScanStride)
        for (var z = -ScanHalfExtent; z <= ScanHalfExtent; z += ScanStride)
        {
            var pre = BiomeClassifier.PreIslandOceanWeight(x, z);
            var biome = BiomeClassifier.BiomeWeights(x, z, out var ocean, out var desert, out var arid);
            samples.Add(new Sample(x, z, pre, ocean, desert, arid, biome, TerrainIslands.LandWeight(x, z, pre)));
        }

        return samples.ToArray();
    }

    private static int Bits(float v) => BitConverter.SingleToInt32Bits(v);

    [Fact]
    public void TheFoldOnlyEverLowersTheOceanWeightThroughTheIslandTerm()
    {
        var islands = 0;
        foreach (var s in Samples.Value)
        {
            var expected = s.Island > 0f ? s.Pre * (1f - s.Island) : s.Pre;
            var biome = BiomeClassifier.WeightsForOcean(s.X, s.Z, expected, out var desert, out var arid);
            Assert.True(Bits(s.Ocean) == Bits(expected),
                $"({s.X}, {s.Z}): folded ocean {s.Ocean} is not pre {s.Pre} through island {s.Island} ({expected})");
            Assert.True(Bits(s.Desert) == Bits(desert), $"({s.X}, {s.Z}): desert {s.Desert} != {desert}");
            Assert.True(Bits(s.Arid) == Bits(arid), $"({s.X}, {s.Z}): arid {s.Arid} != {arid}");
            Assert.Equal(biome, s.Biome);
            if (s.Island > 0f) islands++;
        }

        // Positive control: the island term is live somewhere, so the fold was exercised
        Assert.True(islands > 0, "no scanned column has any island weight");
    }

    [Fact]
    public void NoColumnAtOrInsideTheShoreBandChanges()
    {
        var land = 0;
        var band = 0;
        foreach (var s in Samples.Value)
        {
            if (s.Pre > WorldGenerationConfig.OceanShoreWeight) continue;
            Assert.True(s.Island == 0f, $"({s.X}, {s.Z}): pre {s.Pre} at or inside the shore band has island {s.Island}");
            Assert.True(Bits(s.Ocean) == Bits(s.Pre),
                $"({s.X}, {s.Z}): pre {s.Pre} at or inside the shore band folded to {s.Ocean}");
            if (s.Pre == 0f) land++;
            else band++;
        }

        // Positive control: both continental land and the shore band itself were checked
        Assert.True(land > 0, "no scanned column is continental land");
        Assert.True(band > 0, "no scanned column lies inside the shore band");
    }

    [Fact]
    public void IslandsRiseOnlyWhereThePreIslandOceanWasPastTheShore()
    {
        var islandLand = 0;
        foreach (var s in Samples.Value)
        {
            if (s.IsIslandLand) islandLand++;
            if (Bits(s.Ocean) == Bits(s.Pre)) continue;
            Assert.True(s.Pre > WorldGenerationConfig.OceanShoreWeight,
                $"({s.X}, {s.Z}): the ocean weight changed from {s.Pre} to {s.Ocean} at or inside the shore band");
        }

        // Positive control: some ocean really turned into island land
        Assert.True(islandLand > 0, "no scanned column is island land");
        _output.WriteLine($"{islandLand} island land columns of {Samples.Value.Length}");
    }

    [Fact]
    public void TheIslandTermVanishesInTheSpawnClearing()
    {
        const int rings = 48;
        const int spokes = 64;
        for (var r = 0; r < rings; r++)
        {
            var radius = WorldGenerationConfig.ReliefSpawnClearStart * r / rings;
            for (var k = 0; k < spokes; k++)
            {
                var angle = MathF.Tau * k / spokes;
                var x = radius * MathF.Cos(angle);
                var z = radius * MathF.Sin(angle);
                Assert.True(TerrainIslands.LandWeight(x, z, 1f) == 0f,
                    $"({x}, {z}), {radius} blocks from the origin, holds island weight under a full ocean");
            }
        }

        // Positive control: the same full ocean does raise islands far out
        var far = 0;
        foreach (var s in Samples.Value)
            if (TerrainIslands.LandWeight(s.X, s.Z, 1f) > 0f) far++;
        Assert.True(far > 0, "no scanned column raises an island even under a full ocean");
    }

    [Fact]
    public void DeeperOceanNeverHoldsLessIsland()
    {
        var rising = 0;
        var samples = Samples.Value;
        for (var n = 0; n < samples.Length; n += 37)
        {
            var s = samples[n];
            var previous = 0f;
            for (var k = 0; k <= 32; k++)
            {
                var w = k / 32f;
                var island = TerrainIslands.LandWeight(s.X, s.Z, w);
                Assert.InRange(island, 0f, 1f);
                Assert.True(island >= previous,
                    $"({s.X}, {s.Z}): island {island} at ocean {w} fell below {previous} at a shallower ocean");
                if (island > previous) rising++;
                previous = island;
            }
        }

        // Positive control: the curve actually rises somewhere, so monotonicity was not vacuous
        Assert.True(rising > 0, "the island weight never rose with the ocean depth");
    }

    [Fact]
    public void AnArchipelagoNeverLowersIslandLand()
    {
        var rising = 0;
        var samples = Samples.Value;
        for (var n = 0; n < samples.Length; n += 37)
        {
            var s = samples[n];
            foreach (var w in new[] { 0.5f, 0.75f, 1f })
            {
                var previous = TerrainIslands.LandWeight(s.X, s.Z, w, 0f);
                for (var k = 1; k <= 16; k++)
                {
                    var a = k / 16f;
                    var island = TerrainIslands.LandWeight(s.X, s.Z, w, a);
                    Assert.True(island >= previous,
                        $"({s.X}, {s.Z}) at ocean {w}: archipelago {a} lowered the island from {previous} to {island}");
                    if (island > previous) rising++;
                    previous = island;
                }
            }
        }

        // Positive control: a stronger archipelago does raise island land somewhere
        Assert.True(rising > 0, "the archipelago weight never raised any island");
    }

    [Fact]
    public void AnArchipelagoHoldsMoreSmallIslandColumnsThanTheSameWindowWithoutIt()
    {
        var with = 0;
        var without = 0;
        var sampled = 0;
        foreach (var (gx, gz) in ArchipelagoCells())
        {
            TerrainIslands.TryGetArchipelago(gx, gz, out var cx, out var cz, out var halfLength, out _, out _);
            for (var x = cx - halfLength; x <= cx + halfLength; x += FootprintStride)
            for (var z = cz - halfLength; z <= cz + halfLength; z += FootprintStride)
            {
                if (TerrainIslands.FootprintWeight(gx, gz, x, z) <= 0f) continue;
                var pre = BiomeClassifier.PreIslandOceanWeight(x, z);
                if (pre <= WorldGenerationConfig.OceanShoreWeight) continue;
                sampled++;
                var island = TerrainIslands.LandWeight(x, z, pre, TerrainIslands.ArchipelagoWeight(x, z));
                if (pre * (1f - island) < WorldGenerationConfig.OceanShoreWeight) with++;
                if (pre * (1f - TerrainIslands.LandWeight(x, z, pre, 0f)) < WorldGenerationConfig.OceanShoreWeight)
                    without++;
            }
        }

        _output.WriteLine($"{sampled} footprint columns over open ocean: {with} island columns with the " +
                          $"archipelago, {without} without");
        // Positive control: some footprint lies over open ocean at all
        Assert.True(sampled > 0, "no archipelago footprint lies over ocean past the shore band");
        Assert.True(with > without, $"the archipelagos hold {with} island columns, no more than {without} without them");
    }

    [Fact]
    public void AnArchipelagoRunsAlongItsDirection()
    {
        var checkedCells = 0;
        foreach (var (gx, gz) in ArchipelagoCells())
        {
            TerrainIslands.TryGetArchipelago(gx, gz, out var cx, out var cz, out var halfLength, out var halfWidth,
                out var direction);
            if (halfWidth >= 0.5f * halfLength) continue;

            checkedCells++;
            var reach = 0.5f * halfLength;
            var along = TerrainIslands.FootprintWeight(gx, gz, cx + direction.X * reach, cz + direction.Y * reach);
            var across = TerrainIslands.FootprintWeight(gx, gz, cx - direction.Y * reach, cz + direction.X * reach);
            Assert.True(along > 0f, $"cell ({gx}, {gz}): nothing half a length along the chain's direction");
            Assert.True(along > across,
                $"cell ({gx}, {gz}): the chain reaches {along} along its direction, no more than {across} across it");
        }

        // Positive control: some archipelago is elongated enough to have a direction
        Assert.True(checkedCells > 0, "no accepted archipelago is narrower than half its length");
    }

    [Fact]
    public void IslandLandIsDryLandOfItsClimate()
    {
        var islandLand = 0;
        foreach (var s in Samples.Value)
        {
            if (!s.IsIslandLand) continue;
            islandLand++;
            Assert.True(s.Biome != Biome.Ocean, $"({s.X}, {s.Z}): island land classifies as ocean");
            var height = (int)MathF.Floor(TerrainGenerator.LandHeight(s.X, s.Z, out _, out _, out _, out _));
            Assert.True(height > WorldGenerationConfig.SeaLevel,
                $"({s.X}, {s.Z}): island land stands at {height}, not above the sea");
        }

        // Positive control: island land was found to check
        Assert.True(islandLand > 0, "no scanned column is island land");
    }

    /// <summary>Every archipelago cell that rolls a candidate over the scanned box.</summary>
    private static IEnumerable<(int Gx, int Gz)> ArchipelagoCells()
    {
        var cellBlocks = WorldGenerationConfig.ArchipelagoCellBlocks;
        var min = (int)MathF.Floor(-ScanHalfExtent / (float)cellBlocks);
        var max = (int)MathF.Floor(ScanHalfExtent / (float)cellBlocks);
        for (var gx = min; gx <= max; gx++)
        for (var gz = min; gz <= max; gz++)
            if (TerrainIslands.TryGetArchipelago(gx, gz, out _, out _, out _, out _, out _))
                yield return (gx, gz);
    }
}
