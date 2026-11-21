using System;
using VoxelEngine.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>Fixed coarse climate grid for locating later real Jungle and Taiga fill controls.</summary>
[Collection("Worldgen seed")]
public sealed class DynamicClimateLocatorTests(ITestOutputHelper output) : IDisposable
{
    public void Dispose() => WorldSeed.Apply(WorldSeed.Default);

    [Fact]
    public void FixedCoarseGridReportsDitheredJungleAndTaigaCandidates()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
        var floraReads = 0;
        var pureReads = 0;
        var climateReads = 0;
        var jungleHits = 0;
        var taigaHits = 0;
        try
        {
            // Exactly 16 x 16 top-level samples; this locates climate, not plants or dry ground.
            for (var xi = 0; xi < 16; xi++)
            for (var zi = 0; zi < 16; zi++)
            {
                var x = -11992 + 1600 * xi;
                var z = -11992 + 1600 * zi;
                floraReads++;
                var flora = TerrainGenerator.FloraBiomeAt(x, z);
                pureReads++;
                var pure = TerrainGenerator.BiomeAt(x, z);
                climateReads++;
                BiomeClassifier.SampleClimate(x, z, out var temperature, out var humidity);
                if (flora is not (Biome.Jungle or Biome.Taiga)) continue;
                if (flora == Biome.Jungle) jungleHits++;
                else taigaHits++;
                output.WriteLine($"S4a coarse seed=1337 x={x} z={z} flora={flora} pure={pure} temperature={temperature:R} humidity={humidity:R}");
            }
            Assert.True(jungleHits > 0, "fixed coarse grid missed dithered Jungle");
            Assert.True(taigaHits > 0, "fixed coarse grid missed dithered Taiga");
        }
        finally
        {
            output.WriteLine($"S4a coarse ledger floraReads={floraReads}/256 pureReads={pureReads}/256 climateReads={climateReads}/256 jungleHits={jungleHits} taigaHits={taigaHits}");
        }
    }
}
