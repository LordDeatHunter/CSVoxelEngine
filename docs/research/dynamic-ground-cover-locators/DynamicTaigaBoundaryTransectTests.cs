using System.Collections.Generic;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>Fixed cold-to-warm corridor between observed Taiga and Jungle endpoints.</summary>
[Collection("Worldgen seed")]
public sealed class DynamicTaigaBoundaryTransectTests(ITestOutputHelper output)
{
    [Fact]
    public void FixedTaigaToJungleTransectsReportSnowFreeTaigaMetadata()
    {
        TestContent.EnsureLoaded();
        var fern = ContentRegistry.RequireBlock("fern").Id;
        var points = 0;
        var floraCalls = 0;
        var climateCalls = 0;
        var coldOfCalls = 0;
        var hasSnowCalls = 0;
        var columnAttempts = 0;
        var heightReads = 0;
        var storedBiomeReads = 0;
        var depthCalls = 0;
        var coldWeightCalls = 0;
        var coreCalls = 0;
        var beachCalls = 0;
        var waterCalls = 0;
        var clearanceCalls = 0;
        var floorDivCalls = 0;
        var tuftCalls = 0;
        var variantCalls = 0;
        var pureHits = 0;
        var metadataCandidates = 0;
        var heightDeferred = 0;
        var selectedFerns = 0;
        var reasons = new Dictionary<string, int>();
        try
        {
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            for (var i = 0; i <= 25; i++)
            foreach (var offset in new[] { -32, 0, 32 })
            {
                points++;
                var x = -7192 - 64 * i + offset;
                var z = 8808 + 64 * i + offset;
                floraCalls++;
                var flora = TerrainGenerator.FloraBiomeAt(x, z);
                climateCalls++;
                BiomeClassifier.SampleClimate(x, z, out var temperature, out var humidity);
                coldOfCalls++;
                var climateCold = BiomeClassifier.ColdOf(temperature);
                hasSnowCalls++;
                var climateSnow = SnowGenerator.HasSnow(x, z, climateCold);
                var pureEligible = flora == Biome.Taiga && !climateSnow;
                output.WriteLine($"S4a Taiga transect point i={i} offset={offset} x={x} z={z} flora={flora} temperature={temperature:R} humidity={humidity:R} climateCold={climateCold:R} climateHasSnow={climateSnow} pureEligible={pureEligible}");
                if (!pureEligible)
                {
                    Count(flora == Biome.Taiga ? "climate-snow" : "flora-mismatch");
                    continue;
                }
                pureHits++;
                if (columnAttempts >= 4)
                {
                    heightDeferred++;
                    Count("height-unobserved-budget");
                    output.WriteLine($"S4a Taiga transect deferred x={x} z={z} height=UNOBSERVED");
                    continue;
                }

                floorDivCalls += 2;
                var cx = MathUtils.FloorDiv(x, Chunk.Size);
                var cz = MathUtils.FloorDiv(z, Chunk.Size);
                columnAttempts++;
                var column = ColumnCache.Get(cx, cz);
                var lx = x - cx * Chunk.Size;
                var lz = z - cz * Chunk.Size;
                var ci = (lx << Chunk.SizeShift) | lz;
                heightReads++;
                var height = column.Heights[ci];
                storedBiomeReads++;
                var stored = column.Biomes[ci];
                coldWeightCalls++;
                var coldAtHeight = SnowGenerator.ColdWeightAt(x, z, height);
                depthCalls++;
                var snowDepth = SnowGenerator.DepthAt(x, z, height);
                coreCalls++;
                var core = TerrainErg.Intersects(x, z, x, z);
                beachCalls++;
                var beach = TerrainGenerator.IsBeachHeight(height);
                waterCalls++;
                var water = TerrainGenerator.IsWaterColumn(x, z, height);
                clearanceCalls++;
                var clearance = SurfaceClearance.Allows(height, 1);
                floorDivCalls++;
                var localY = height + 1 - MathUtils.FloorDiv(height + 1, Chunk.Size) * Chunk.Size;
                var reason = stored != Biome.Taiga ? "stored-biome" : snowDepth != 0 ? "height-snow"
                    : core ? "core" : beach ? "beach" : water ? "water"
                    : !clearance ? "clearance" : localY == 0 ? "ground-band" : "metadata-accepted";
                Count(reason);
                var tuft = false;
                var selectedFern = false;
                if (reason == "metadata-accepted")
                {
                    metadataCandidates++;
                    tuftCalls++;
                    tuft = FoliageGenerator.TryGetGrassTuft(x, z);
                    if (tuft)
                    {
                        variantCalls++;
                        selectedFern = DynamicGroundCover.CandidateAt(x, z, stored) == fern;
                        if (selectedFern) selectedFerns++;
                    }
                }
                output.WriteLine($"S4a Taiga transect metadata x={x} z={z} cx={cx} cz={cz} height={height} stored={stored} flora={flora} coldAtHeight={coldAtHeight:R} snowDepth={snowDepth} core={core} beach={beach} water={water} clearance={clearance} localY={localY} result={reason} tuft={tuft} selectedFern={selectedFern}; ground/air/writes unobserved");
            }
            Assert.True(metadataCandidates > 0,
                "fixed cold-to-warm corridor has no snow-free Taiga metadata candidate; no wider search authorized");
        }
        finally
        {
            foreach (var reason in reasons)
                output.WriteLine($"S4a Taiga transect reason-{reason.Key}={reason.Value}");
            output.WriteLine($"S4a Taiga transect partial ledger points={points}/78 floraCalls={floraCalls}/78 climateCalls={climateCalls}/78 coldOfCalls={coldOfCalls}/78 hasSnowCalls={hasSnowCalls}/78 columnAttempts={columnAttempts}/4 heightReads={heightReads}/4 storedBiomeReads={storedBiomeReads}/4 depthCalls={depthCalls}/4 coldWeightCalls={coldWeightCalls}/4 coreCalls={coreCalls}/4 beachCalls={beachCalls}/4 waterCalls={waterCalls}/4 clearanceCalls={clearanceCalls}/4 floorDivCalls={floorDivCalls}/12 tuftCalls={tuftCalls}/4 variantCalls={variantCalls}/4");
            output.WriteLine($"S4a Taiga transect totals pureHits={pureHits} metadataCandidates={metadataCandidates} heightDeferred={heightDeferred} selectedFerns={selectedFerns}");
            WorldSeed.Apply(WorldSeed.Default);
        }

        void Count(string reason) => reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
    }
}
