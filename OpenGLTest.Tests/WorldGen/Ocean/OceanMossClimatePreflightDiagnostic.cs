using System;
using System.Collections.Generic;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Ocean;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Ocean;

/// <summary>Opt-in raw-climate candidates, never generated-column or placed-moss proof.</summary>
[Collection("Worldgen seed")]
public sealed class OceanMossClimatePreflightDiagnostic(ITestOutputHelper output)
{
    private const string OptInVariable = "VOXEL_S6B_MOSS_CLIMATE_PREFLIGHT";

    [OptInMossClimatePreflightFact]
    public void Frozen130ChunkKeysReportPreliminaryMossClimateOnly()
    {
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;
        var applied = false;
        var keysAttempted = 0;
        var cornerClimateAttempts = 0;
        var rawBiomeAttempts = 0;
        var centerSampleAttempts = 0;
        var rowsWritten = 0;
        var moderateOceanCount = 0;
        var warmOceanCount = 0;
        (int Cx, int Cz)? firstModerate = null;
        (int Cx, int Cz)? firstWarm = null;
        var biomeCounts = new Dictionary<Biome, int>();

        try
        {
            TestContent.EnsureLoaded();
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            applied = true;

            // Frozen coarse grids, X offset outer and Z offset inner. The last two
            // are static warm-reef neighbors, not fallback coordinates after a miss.
            ReadOnlySpan<int> offsets = [-32, -24, -16, -8, 0, 8, 16, 24];
            foreach (var dx in offsets)
            foreach (var dz in offsets)
                Visit("warm-anchor-grid", -188 + dx, 244 + dz);
            foreach (var dx in offsets)
            foreach (var dz in offsets)
                Visit("cold-anchor-grid", -74 + dx, -188 + dz);
            Visit("warm-anchor-neighbor-1", -188, 243);
            Visit("warm-anchor-neighbor-2", -188, 242);

            output.WriteLine($"S6B_MOSS_PREFLIGHT_HIST total={rowsWritten}/130 "
                + $"moderateOcean={moderateOceanCount} warmOcean={warmOceanCount} "
                + $"firstModerate={firstModerate?.ToString() ?? "none"} "
                + $"firstWarm={firstWarm?.ToString() ?? "none"}");
            foreach (var biome in Enum.GetValues<Biome>())
                output.WriteLine($"S6B_MOSS_PREFLIGHT_BIOME biome={biome} "
                    + $"count={biomeCounts.GetValueOrDefault(biome)}");

            Assert.Equal(130, rowsWritten);
            Assert.True(moderateOceanCount > 0,
                "The frozen raw-ocean grid must contain a preliminary moderate-warmth candidate.");
            Assert.True(warmOceanCount > 0,
                "The frozen raw-ocean grid must contain a preliminary warm deep-glow candidate.");

            void Visit(string family, int cx, int cz)
            {
                keysAttempted++; // Before the first potentially failing climate entry.
                var x0 = cx * Chunk.Size;
                var z0 = cz * Chunk.Size;
                cornerClimateAttempts++;
                TerrainGenerator.Climate(x0, z0, out var w00, out var wet00);
                cornerClimateAttempts++;
                TerrainGenerator.Climate(x0 + Chunk.Size, z0, out var w10, out var wet10);
                cornerClimateAttempts++;
                TerrainGenerator.Climate(x0, z0 + Chunk.Size, out var w01, out var wet01);
                cornerClimateAttempts++;
                TerrainGenerator.Climate(x0 + Chunk.Size, z0 + Chunk.Size, out var w11, out var wet11);
                var field = new OceanHabitatField(w00, w10, w01, w11);
                centerSampleAttempts++;
                var warmth = field.Sample(8.5f, 8.5f);
                var centerX = x0 + 8;
                var centerZ = z0 + 8;
                rawBiomeAttempts++;
                var rawBiome = TerrainGenerator.BiomeWeights(centerX, centerZ,
                    out var oceanWeight, out var desertWeight);
                var moderate = rawBiome == Biome.Ocean && warmth > 0.42f && warmth < 0.58f;
                var warm = rawBiome == Biome.Ocean
                    && warmth >= WorldFeatureConfig.OceanGlowMossMinWarmth;
                if (moderate)
                {
                    moderateOceanCount++;
                    firstModerate ??= (cx, cz);
                }
                if (warm)
                {
                    warmOceanCount++;
                    firstWarm ??= (cx, cz);
                }
                biomeCounts[rawBiome] = biomeCounts.GetValueOrDefault(rawBiome) + 1;
                output.WriteLine($"S6B_MOSS_PREFLIGHT_ROW family={family} key=({cx},{cz}) "
                    + $"center=({centerX},{centerZ}) "
                    + $"corner00=({w00:R},{wet00:R}) corner10=({w10:R},{wet10:R}) "
                    + $"corner01=({w01:R},{wet01:R}) corner11=({w11:R},{wet11:R}) "
                    + $"centerWarmth={warmth:R} rawBiome={rawBiome} "
                    + $"rawOceanWeight={oceanWeight:R} rawDesertWeight={desertWeight:R} "
                    + $"PRELIMINARY_moderateOcean={moderate} PRELIMINARY_warmOcean={warm} "
                    + "height=unknown water=unknown storedBiome=unknown material=unknown");
                rowsWritten++;
            }
        }
        finally
        {
            try
            {
                output.WriteLine($"S6B_MOSS_PREFLIGHT_LEDGER seed=1337 profile=Dynamic "
                    + $"keysAttempted={keysAttempted}/130 "
                    + $"cornerClimateAttempts={cornerClimateAttempts}/520 "
                    + $"rawBiomeAttempts={rawBiomeAttempts}/130 "
                    + $"centerSampleAttempts={centerSampleAttempts}/130 "
                    + $"rowsWritten={rowsWritten}/130 "
                    + $"moderateOcean={moderateOceanCount} warmOcean={warmOceanCount}");
            }
            finally
            {
                if (applied) WorldSeed.Apply(previousSeed, previousProfile);
            }
        }
    }

    /// <summary>Skip before content, seed, climate, noise or column work by default.</summary>
    public sealed class OptInMossClimatePreflightFactAttribute : FactAttribute
    {
        public OptInMossClimatePreflightFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 for the frozen raw-climate-only preflight.";
        }
    }
}
