using System.Collections.Generic;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>Fixed snow-aware point observations before choosing real Dynamic flora fill columns.</summary>
[Collection("Worldgen seed")]
public sealed class DynamicSnowAwarePlantLocatorTests(ITestOutputHelper output)
{
    // The six remaining Jungle hits of the completed seed-1337 coarse grid, in recorded order.
    private static readonly (int X, int Z)[] JungleCenters =
    [
        (-11992, -2392), (4008, 808), (-8792, 10408),
        (7208, -7192), (10408, -5592), (12008, -3992),
    ];

    [Fact]
    public void FixedJungleCentersAndWarmTaigaBoundaryReportSnowFreeMetadata()
    {
        TestContent.EnsureLoaded();
        var pointVisits = 0;
        var jungleColumnAttempts = 0;
        var taigaColumnAttempts = 0;
        var floraCalls = 0;
        var climateCalls = 0;
        var coldOfCalls = 0;
        var hasSnowCalls = 0;
        var depthCalls = 0;
        var coldWeightCalls = 0;
        var ergCalls = 0;
        var beachCalls = 0;
        var waterCalls = 0;
        var clearanceCalls = 0;
        var floorDivCalls = 0;
        var heightReads = 0;
        var storedBiomeReads = 0;
        var jungleAccepted = 0;
        var taigaPureHits = 0;
        var taigaAccepted = 0;
        var taigaDeferred = 0;
        var reasons = new Dictionary<string, int>();
        try
        {
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            foreach (var (x, z) in JungleCenters)
            {
                pointVisits++;
                floorDivCalls += 2;
                var cx = MathUtils.FloorDiv(x, Chunk.Size);
                var cz = MathUtils.FloorDiv(z, Chunk.Size);
                jungleColumnAttempts++;
                var column = ColumnCache.Get(cx, cz);
                var lx = x - cx * Chunk.Size;
                var lz = z - cz * Chunk.Size;
                var ci = (lx << Chunk.SizeShift) | lz;
                heightReads++;
                var height = column.Heights[ci];
                storedBiomeReads++;
                var stored = column.Biomes[ci];
                floraCalls++;
                var flora = TerrainGenerator.FloraBiomeAt(x, z);
                var checks = CheckMetadata(x, z, height);
                var reason = stored != Biome.Jungle || flora != Biome.Jungle ? "jungle-biome"
                    : checks.SnowDepth != 0 ? "jungle-snow"
                    : checks.InCore ? "jungle-core"
                    : checks.Beach ? "jungle-beach"
                    : checks.Water ? "jungle-water"
                    : !checks.Clearance ? "jungle-clearance"
                    : !checks.GroundInChunk ? "jungle-ground-band" : "jungle-accepted";
                Count(reason);
                if (reason == "jungle-accepted") jungleAccepted++;
                output.WriteLine($"S4a snowpoint Jungle x={x} z={z} cx={cx} cz={cz} height={height} stored={stored} flora={flora} coldAtHeight={checks.ColdWeight:R} snowDepth={checks.SnowDepth} core={checks.InCore} beach={checks.Beach} water={checks.Water} clearance={checks.Clearance} groundInChunk={checks.GroundInChunk} result={reason}; soil/air/writes unobserved");
            }

            // Local 15 x 15 boundary grid around the warmest recorded coarse Taiga center.
            for (var xi = 0; xi < 15; xi++)
            for (var zi = 0; zi < 15; zi++)
            {
                pointVisits++;
                var x = -7640 + 64 * xi;
                var z = 8360 + 64 * zi;
                floraCalls++;
                var flora = TerrainGenerator.FloraBiomeAt(x, z);
                climateCalls++;
                BiomeClassifier.SampleClimate(x, z, out var temperature, out var humidity);
                coldOfCalls++;
                var climateCold = BiomeClassifier.ColdOf(temperature);
                hasSnowCalls++;
                var climateSnow = SnowGenerator.HasSnow(x, z, climateCold);
                if (flora != Biome.Taiga)
                {
                    Count("taiga-flora-mismatch");
                    continue;
                }
                if (climateSnow)
                {
                    Count("taiga-climate-snow");
                    continue;
                }
                taigaPureHits++;
                output.WriteLine($"S4a snowpoint pure Taiga x={x} z={z} flora={flora} temperature={temperature:R} humidity={humidity:R} climateCold={climateCold:R} climateHasSnow={climateSnow}");
                if (taigaColumnAttempts >= 4)
                {
                    taigaDeferred++;
                    Count("taiga-height-unobserved-budget");
                    output.WriteLine($"S4a snowpoint deferred Taiga x={x} z={z} height=UNOBSERVED");
                    continue;
                }
                floorDivCalls += 2;
                var cx = MathUtils.FloorDiv(x, Chunk.Size);
                var cz = MathUtils.FloorDiv(z, Chunk.Size);
                taigaColumnAttempts++;
                var column = ColumnCache.Get(cx, cz);
                var lx = x - cx * Chunk.Size;
                var lz = z - cz * Chunk.Size;
                var ci = (lx << Chunk.SizeShift) | lz;
                heightReads++;
                var height = column.Heights[ci];
                storedBiomeReads++;
                var stored = column.Biomes[ci];
                var checks = CheckMetadata(x, z, height);
                var reason = stored != Biome.Taiga ? "taiga-stored-biome"
                    : checks.SnowDepth != 0 ? "taiga-height-snow"
                    : checks.InCore ? "taiga-core"
                    : checks.Beach ? "taiga-beach"
                    : checks.Water ? "taiga-water"
                    : !checks.Clearance ? "taiga-clearance"
                    : !checks.GroundInChunk ? "taiga-ground-band" : "taiga-accepted";
                Count(reason);
                if (reason == "taiga-accepted") taigaAccepted++;
                output.WriteLine($"S4a snowpoint Taiga x={x} z={z} cx={cx} cz={cz} height={height} stored={stored} flora={flora} temperature={temperature:R} humidity={humidity:R} climateCold={climateCold:R} coldAtHeight={checks.ColdWeight:R} snowDepth={checks.SnowDepth} core={checks.InCore} beach={checks.Beach} water={checks.Water} clearance={checks.Clearance} groundInChunk={checks.GroundInChunk} result={reason}; soil/air/writes unobserved");
            }
            Assert.True(jungleAccepted > 0, "six frozen Jungle centers have no snow-free eligible metadata; no wider search authorized");
            Assert.True(taigaAccepted > 0, "fixed warm-edge Taiga grid has no snow-free eligible metadata within four height reads; no wider search authorized");
        }
        finally
        {
            foreach (var reason in reasons)
                output.WriteLine($"S4a snowpoint reason-{reason.Key}={reason.Value}");
            output.WriteLine($"S4a snowpoint partial ledger points={pointVisits}/231 jungleColumnAttempts={jungleColumnAttempts}/6 taigaColumnAttempts={taigaColumnAttempts}/4 floraCalls={floraCalls}/231 climateCalls={climateCalls}/225 coldOfCalls={coldOfCalls}/225 hasSnowCalls={hasSnowCalls}/225 depthCalls={depthCalls}/10 coldWeightCalls={coldWeightCalls}/10 ergCalls={ergCalls}/10 beachCalls={beachCalls}/10 waterCalls={waterCalls}/10 clearanceCalls={clearanceCalls}/10 floorDivCalls={floorDivCalls}/30 heightReads={heightReads}/10 storedBiomeReads={storedBiomeReads}/10");
            output.WriteLine($"S4a snowpoint totals jungleAccepted={jungleAccepted} taigaPureHits={taigaPureHits} taigaAccepted={taigaAccepted} taigaHeightDeferred={taigaDeferred}");
            WorldSeed.Apply(WorldSeed.Default);
        }

        void Count(string reason) => reasons[reason] = reasons.GetValueOrDefault(reason) + 1;

        (float ColdWeight, int SnowDepth, bool InCore, bool Beach, bool Water, bool Clearance,
            bool GroundInChunk) CheckMetadata(int x, int z, int height)
        {
            coldWeightCalls++;
            var cold = SnowGenerator.ColdWeightAt(x, z, height);
            depthCalls++;
            var snow = SnowGenerator.DepthAt(x, z, height);
            ergCalls++;
            var core = TerrainErg.Intersects(x, z, x, z);
            beachCalls++;
            var beach = TerrainGenerator.IsBeachHeight(height);
            waterCalls++;
            var water = TerrainGenerator.IsWaterColumn(x, z, height);
            clearanceCalls++;
            var clearance = SurfaceClearance.Allows(height, 1);
            floorDivCalls++;
            var baseY = height + 1;
            var groundInChunk = baseY - MathUtils.FloorDiv(baseY, Chunk.Size) * Chunk.Size != 0;
            return (cold, snow, core, beach, water, clearance, groundInChunk);
        }
    }
}
