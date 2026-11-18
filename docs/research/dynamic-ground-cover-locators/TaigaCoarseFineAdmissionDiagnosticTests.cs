using System.Collections.Generic;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>
/// Opt-in, fixed coarse-to-fine Taiga admission observation outside default tests;
/// a zero-candidate result is a valid bounded observation, not a feature regression.
/// </summary>
[Collection("Worldgen seed")]
public sealed class TaigaCoarseFineAdmissionDiagnosticTests(ITestOutputHelper output)
{
    private const int CoarseStart = -64;
    private const int CoarseEnd = 64;
    private const int FineStart = -32;
    private const int FineEnd = 32;
    private const int CoarseBudget = 129 * 129; // 16641
    private const int FinePerCenter = 65 * 65; // 4225
    private const int FineBudget = 8 * FinePerCenter; // 33800
    private const int ColumnBudget = 8;

    [Fact]
    public void FixedCoarseThenFineGridReportsPublicAdmission()
    {
        var coarseVisits = 0;
        var coarseRowsLogged = 0;
        var coarseClimateCalls = 0;
        var coarseColdCalls = 0;
        var coarseBiomeWeightsCalls = 0;
        var coarseEligible = 0;
        var coarseExcluded = 0;
        var coarseExclusionChecks = 0;
        var coarseSpacingComparisons = 0;
        var pointVisits = 0;
        var rowsLogged = 0;
        var floraCalls = 0;
        var biomeWeightsCalls = 0;
        var climateCalls = 0;
        var coldCalls = 0;
        var dryCalls = 0;
        var warmCalls = 0;
        var semiDryCalls = 0;
        var jungleCalls = 0;
        var ecotoneCalls = 0;
        var classifyCalls = 0;
        var packNoiseCalls = 0;
        var snowCalls = 0;
        var tuftCalls = 0;
        var variantCalls = 0;
        var localNoiseFactoryCalls = 0;
        var explanatoryCoreCalls = 0;
        var explanatoryOasisCalls = 0;
        var explanatoryPatchCalls = 0;
        var explanatoryClimateCalls = 0;
        var explanatoryDensityCalls = 0;
        var pureCandidates = 0;
        var taigaSnowRejects = 0;
        var shiftedColdRejects = 0;
        var columnCalls = 0;
        var metadataReads = 0;
        var altitudeColdCalls = 0;
        var depthCalls = 0;
        var beachCalls = 0;
        var heightWaterCalls = 0;
        var bedWaterCalls = 0;
        var clearanceCalls = 0;
        var selectionFloorDivCalls = 0;
        var columnDedupComparisons = 0;
        var verticalFloorDivCalls = 0;
        var centers = new List<(int X, int Z)>();
        var selected = new List<(int X, int Z, int Cx, int Cz, bool Core)>();

        try
        {
            TestContent.EnsureLoaded();
            var fern = ContentRegistry.RequireBlock("fern").Id;
            var grassTuft = FoliageGenerator.GrassTuftBlock;
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);

            // Stage A: fixed coarse grid. These scalar gates are necessary for a
            // snow-free cold land candidate, not proof of Taiga or tuft admission.
            for (var i = CoarseStart; i <= CoarseEnd; i++)
            for (var j = CoarseStart; j <= CoarseEnd; j++)
            {
                coarseVisits++;
                var x = 256 * i;
                var z = 256 * j;
                coarseClimateCalls++;
                BiomeClassifier.SampleClimate(x, z, out var temperature, out var humidity);
                coarseColdCalls++;
                var cold = BiomeClassifier.ColdOf(temperature);
                coarseBiomeWeightsCalls++;
                var baseBiome = TerrainGenerator.BiomeWeights(x, z, out var ocean, out _);
                var land = baseBiome != Biome.Ocean && ocean < WorldGenerationConfig.OceanShoreWeight;
                var coldBand = cold >= WorldGenerationConfig.ColdBiomeMinCold
                    - WorldGenerationConfig.DynamicEcotoneBand
                    && cold < WorldGenerationConfig.SnowFullColdThreshold;
                var eligible = land && coldBand;
                if (eligible) coarseEligible++;

                // The closed +/-64 refinement square may not intersect the
                // already tested nearby rectangle, including its boundary.
                coarseExclusionChecks++;
                var intersectsRejected = x + 64 >= -7323 && x - 64 <= -7069
                    && z + 64 >= 8761 && z - 64 <= 8887;
                if (eligible && intersectsRejected) coarseExcluded++;
                var separated = true;
                if (eligible && !intersectsRejected)
                    foreach (var center in centers)
                    {
                        coarseSpacingComparisons++;
                        if (Math.Max(Math.Abs(x - center.X), Math.Abs(z - center.Z)) < 512)
                        {
                            separated = false;
                            break;
                        }
                    }
                var selectedCenter = eligible && !intersectsRejected && separated
                    && centers.Count < ColumnBudget;
                if (selectedCenter) centers.Add((x, z));
                var reason = !land ? baseBiome == Biome.Ocean ? "ocean-base" : "ocean-shore"
                    : cold < WorldGenerationConfig.ColdBiomeMinCold - WorldGenerationConfig.DynamicEcotoneBand
                    ? "cold-low" : cold >= WorldGenerationConfig.SnowFullColdThreshold
                    ? "cold-full-snow" : intersectsRejected ? "rejected-rectangle"
                    : !separated ? "center-separation" : !selectedCenter ? "center-cap" : "selected-center";
                output.WriteLine($"TaigaCoarseFine coarse={coarseVisits} i={i} j={j} x={x} z={z} "
                    + $"temperature={temperature:R} humidity={humidity:R} cold={cold:R} "
                    + $"base={baseBiome} ocean={ocean:R} land={land} coldBand={coldBand} "
                    + $"eligible={eligible} intersectsRejected={intersectsRejected} "
                    + $"separated={separated} selectedCenter={selectedCenter} reason={reason}");
                coarseRowsLogged++;
            }

            // Stage B begins only after all coarse rows. One local object mirrors
            // the existing patch-noise factory without reading its private field.
            localNoiseFactoryCalls++;
            var sameFactoryPatchNoise = TerrainGenerator.MakeNoise(63, WorldFeatureConfig.GrassPatchFrequency);

            foreach (var center in centers)
            for (var u = FineStart; u <= FineEnd; u++)
            for (var v = FineStart; v <= FineEnd; v++)
            {
                pointVisits++;
                var x = center.X + 2 * u;
                var z = center.Z + 2 * v;

                floraCalls++;
                var flora = TerrainGenerator.FloraBiomeAt(x, z);
                biomeWeightsCalls++;
                var baseBiome = TerrainGenerator.BiomeWeights(x, z, out var ocean, out _);
                climateCalls++;
                BiomeClassifier.SampleClimate(x, z, out var temperature, out var humidity);
                coldCalls++;
                var cold = BiomeClassifier.ColdOf(temperature);
                dryCalls++;
                var dry = BiomeClassifier.DryOf(humidity);
                warmCalls++;
                var warm = BiomeClassifier.WarmOf(temperature);
                semiDryCalls++;
                var semiDry = BiomeClassifier.SemiDryOf(humidity);
                jungleCalls++;
                var jungle = BiomeClassifier.JungleOf(temperature, humidity);
                ecotoneCalls++;
                var ecotone = TerrainNoise.EcotoneNoise.GetNoise(x, z);
                var shift = ecotone * WorldGenerationConfig.DynamicEcotoneBand;
                var shiftedCold = cold + shift;
                var shiftedDry = dry + shift;
                var shiftedWarm = warm + shift;
                var shiftedSemiDry = semiDry + shift;
                var shiftedJungle = jungle + shift;
                var classified = baseBiome;
                if (baseBiome != Biome.Ocean)
                {
                    classifyCalls++;
                    classified = BiomeClassifier.Classify(ocean, shiftedCold, shiftedWarm,
                        shiftedDry, shiftedSemiDry, shiftedJungle);
                }

                float? packNoise = null;
                float? packThreshold = null;
                if (cold > WorldGenerationConfig.SnowColdThreshold
                    && cold < WorldGenerationConfig.SnowFullColdThreshold)
                {
                    packNoiseCalls++;
                    packNoise = 0.5f + 0.5f * TerrainNoise.PackIceNoise.GetNoise(x + 4096f, z - 4096f);
                    packThreshold = MathUtils.SmoothStep(WorldGenerationConfig.SnowColdThreshold,
                        WorldGenerationConfig.SnowFullColdThreshold, cold);
                }
                snowCalls++;
                var climateHasSnow = SnowGenerator.HasSnow(x, z, cold);

                var tuft = false;
                var candidateName = "NOT_CHECKED";
                bool? explanatoryCore = null;
                bool? explanatoryOasis = null;
                float? sameFactoryPatch = null;
                float? explanatoryWetness = null;
                float? explanatoryDensity = null;
                var tuftExplanation = "NOT_CHECKED";
                if (flora == Biome.Taiga && !climateHasSnow)
                {
                    tuftCalls++;
                    tuft = FoliageGenerator.TryGetGrassTuft(x, z);
                    variantCalls++;
                    var candidate = DynamicGroundCover.CandidateAt(x, z, flora);
                    candidateName = candidate == fern ? "fern"
                        : candidate == grassTuft ? "tall_grass" : $"unexpected-id-{candidate}";
                    explanatoryCoreCalls++;
                    explanatoryCore = TerrainErg.Intersects(x, z, x, z);
                    explanatoryOasisCalls++;
                    explanatoryOasis = OasisGenerator.IsDisc(x, z);
                    explanatoryPatchCalls++;
                    sameFactoryPatch = sameFactoryPatchNoise.GetNoise(x, z);
                    explanatoryClimateCalls++;
                    TerrainGenerator.Climate(x, z, out _, out var wetness);
                    explanatoryWetness = wetness;
                    explanatoryDensityCalls++;
                    explanatoryDensity = FoliageGenerator.GrassDensityAt(wetness);
                    // The hash is private and is not cloned: a remaining density rejection
                    // is a source inference from these gates and the actual public result.
                    tuftExplanation = explanatoryCore == true ? "core"
                        : explanatoryOasis == true ? "oasis-public-check"
                        : sameFactoryPatch.Value <= WorldFeatureConfig.GrassPatchThreshold ? "same-factory-patch"
                        : tuft ? "public-admitted" : "density-inferred";
                }

                var pureCandidate = flora == Biome.Taiga && !climateHasSnow && tuft
                    && candidateName == "fern";
                var selectedForMetadata = false;
                var reason = baseBiome == Biome.Ocean || ocean >= WorldGenerationConfig.OceanShoreWeight
                    ? "ocean" : shiftedCold < WorldGenerationConfig.ColdBiomeMinCold
                    ? "shifted-cold" : shiftedDry >= 0.5f ? "shifted-dry"
                    : flora != Biome.Taiga ? "other-flora" : climateHasSnow ? "climate-snow"
                    : !tuft ? "no-tuft" : !pureCandidate ? "other-variant" : "pure-fern";
                if (reason == "shifted-cold") shiftedColdRejects++;
                if (reason == "climate-snow") taigaSnowRejects++;
                if (pureCandidate)
                {
                    pureCandidates++;
                    // Deduplicate by negative-safe arithmetic only; no column/terrain read.
                    selectionFloorDivCalls++;
                    var cx = MathUtils.FloorDiv(x, Chunk.Size);
                    selectionFloorDivCalls++;
                    var cz = MathUtils.FloorDiv(z, Chunk.Size);
                    var alreadySelected = false;
                    foreach (var point in selected)
                    {
                        columnDedupComparisons++;
                        if (point.Cx != cx || point.Cz != cz) continue;
                        alreadySelected = true;
                        break;
                    }
                    if (!alreadySelected && selected.Count < ColumnBudget)
                    {
                        selected.Add((x, z, cx, cz, explanatoryCore.GetValueOrDefault()));
                        selectedForMetadata = true;
                    }
                }

                output.WriteLine($"TaigaCoarseFine fine={pointVisits} centerX={center.X} centerZ={center.Z} "
                    + $"u={u} v={v} x={x} z={z} "
                    + $"base={baseBiome} ocean={ocean:R} temperature={temperature:R} "
                    + $"humidity={humidity:R} cold={cold:R} dry={dry:R} warm={warm:R} "
                    + $"semiDry={semiDry:R} jungle={jungle:R} ecotone={ecotone:R} shift={shift:R} "
                    + $"shiftedCold={shiftedCold:R} shiftedDry={shiftedDry:R} "
                    + $"shiftedWarm={shiftedWarm:R} shiftedSemiDry={shiftedSemiDry:R} "
                    + $"shiftedJungle={shiftedJungle:R} classified={classified} flora={flora} "
                    + $"coldGate={shiftedCold >= WorldGenerationConfig.ColdBiomeMinCold} "
                    + $"taigaDryGate={shiftedDry < 0.5f} "
                    + $"packNoise={(packNoise.HasValue ? packNoise.Value.ToString("R") : "NA")} "
                    + $"packSnowThreshold={(packThreshold.HasValue ? packThreshold.Value.ToString("R") : "NA")} "
                    + $"climateHasSnow={climateHasSnow} tuft={tuft} variant={candidateName} "
                    + $"explanatoryCore={(explanatoryCore.HasValue ? explanatoryCore.Value.ToString() : "NA")} "
                    + $"explanatoryOasis={(explanatoryOasis.HasValue ? explanatoryOasis.Value.ToString() : "NA")} "
                    + $"sameFactoryPatch={(sameFactoryPatch.HasValue ? sameFactoryPatch.Value.ToString("R") : "NA")} "
                    + $"patchThreshold={WorldFeatureConfig.GrassPatchThreshold:R} "
                    + $"explanatoryWetness={(explanatoryWetness.HasValue ? explanatoryWetness.Value.ToString("R") : "NA")} "
                    + $"explanatoryDensity={(explanatoryDensity.HasValue ? explanatoryDensity.Value.ToString("R") : "NA")} "
                    + $"tuftExplanation={tuftExplanation} "
                    + $"pureCandidate={pureCandidate} selectedForMetadata={selectedForMetadata} "
                    + $"reason={reason}; "
                    + "altitude/ground/air/final stamp UNOBSERVED");
                rowsLogged++;
            }

            // Stage C: all coarse and fine rows precede optional metadata.
            foreach (var point in selected)
            {
                columnCalls++;
                var column = ColumnCache.Get(point.Cx, point.Cz);
                var lx = point.X - point.Cx * Chunk.Size;
                var lz = point.Z - point.Cz * Chunk.Size;
                var index = (lx << Chunk.SizeShift) | lz;
                metadataReads++;
                var height = column.Heights[index];
                metadataReads++;
                var stored = column.Biomes[index];
                metadataReads++;
                var dirtDepth = column.DirtDepth[index];
                metadataReads++;
                var bed = column.Bed[index];
                metadataReads++;
                var waterLevel = column.WaterLevel[index];
                altitudeColdCalls++;
                var coldAtHeight = SnowGenerator.ColdWeightAt(point.X, point.Z, height);
                depthCalls++;
                var snowDepth = SnowGenerator.DepthAt(point.X, point.Z, height);
                var core = point.Core; // Reuse the row's explanatory core read.
                beachCalls++;
                var beach = TerrainGenerator.IsBeachHeight(height);
                heightWaterCalls++;
                var surfaceWater = TerrainGenerator.IsWaterColumn(point.X, point.Z, height);
                bedWaterCalls++;
                var groundWet = TerrainGenerator.IsWaterColumn(point.X, point.Z, bed);
                clearanceCalls++;
                var clearance = SurfaceClearance.Allows(height, 1);
                verticalFloorDivCalls++;
                var localY = height + 1 - MathUtils.FloorDiv(height + 1, Chunk.Size) * Chunk.Size;
                var predictedGrass = stored == Biome.Taiga && dirtDepth != 0 && !beach && !groundWet;
                output.WriteLine($"TaigaCoarseFine selectedPure x={point.X} z={point.Z} cx={point.Cx} cz={point.Cz} "
                    + $"height={height} stored={stored} dirtDepth={dirtDepth} bed={bed} "
                    + $"waterLevel={waterLevel} coldAtHeight={coldAtHeight:R} snowDepth={snowDepth} "
                    + $"core={core} beach={beach} surfaceWater={surfaceWater} groundWet={groundWet} "
                    + $"clearance={clearance} localY={localY} predictedGrass={predictedGrass}; "
                    + "actual ground/air/occupancy/final stamp UNOBSERVED (no fill)");
            }
            if (selected.Count == 0)
                output.WriteLine("TaigaCoarseFine no complete pure Taiga/no-snow/tuft/fern candidate; columnCalls=0");

            Assert.Equal(CoarseBudget, coarseVisits);
            Assert.Equal(CoarseBudget, coarseRowsLogged);
            Assert.Equal(CoarseBudget, coarseClimateCalls);
            Assert.Equal(CoarseBudget, coarseColdCalls);
            Assert.Equal(CoarseBudget, coarseBiomeWeightsCalls);
            Assert.Equal(CoarseBudget, coarseExclusionChecks);
            Assert.InRange(coarseSpacingComparisons, 0, CoarseBudget * ColumnBudget);
            Assert.InRange(centers.Count, 0, ColumnBudget);
            Assert.Equal(FinePerCenter * centers.Count, pointVisits);
            Assert.Equal(pointVisits, rowsLogged);
            Assert.Equal(pointVisits, floraCalls);
            Assert.Equal(pointVisits, biomeWeightsCalls);
            Assert.Equal(pointVisits, climateCalls);
            Assert.Equal(pointVisits, coldCalls);
            Assert.Equal(pointVisits, dryCalls);
            Assert.Equal(pointVisits, warmCalls);
            Assert.Equal(pointVisits, semiDryCalls);
            Assert.Equal(pointVisits, jungleCalls);
            Assert.Equal(pointVisits, ecotoneCalls);
            Assert.Equal(pointVisits, snowCalls);
            Assert.InRange(classifyCalls, 0, pointVisits);
            Assert.InRange(packNoiseCalls, 0, pointVisits);
            Assert.InRange(tuftCalls, 0, pointVisits);
            Assert.Equal(tuftCalls, variantCalls);
            Assert.Equal(1, localNoiseFactoryCalls);
            Assert.Equal(tuftCalls, explanatoryCoreCalls);
            Assert.Equal(tuftCalls, explanatoryOasisCalls);
            Assert.Equal(tuftCalls, explanatoryPatchCalls);
            Assert.Equal(tuftCalls, explanatoryClimateCalls);
            Assert.Equal(tuftCalls, explanatoryDensityCalls);
            Assert.InRange(columnCalls, 0, ColumnBudget);
            Assert.Equal(selected.Count, columnCalls);
            Assert.Equal(5 * columnCalls, metadataReads);
            Assert.Equal(2 * pureCandidates, selectionFloorDivCalls);
            Assert.InRange(columnDedupComparisons, 0, FineBudget * ColumnBudget);
            Assert.Equal(columnCalls, verticalFloorDivCalls);
        }
        finally
        {
            output.WriteLine($"TaigaCoarseFine partial ledger coarseVisits={coarseVisits}/{CoarseBudget} "
                + $"coarseRows={coarseRowsLogged}/{CoarseBudget} coarseClimateCalls={coarseClimateCalls}/{CoarseBudget} "
                + $"coarseColdCalls={coarseColdCalls}/{CoarseBudget} "
                + $"coarseBiomeWeightsCalls={coarseBiomeWeightsCalls}/{CoarseBudget} "
                + $"coarseEligible={coarseEligible} coarseExcluded={coarseExcluded} "
                + $"coarseExclusionChecks={coarseExclusionChecks}/{CoarseBudget} "
                + $"coarseSpacingComparisons={coarseSpacingComparisons}/{CoarseBudget * ColumnBudget} "
                + $"centers={centers.Count}/{ColumnBudget} fineVisits={pointVisits}/{FineBudget} "
                + $"fineRows={rowsLogged}/{FineBudget} floraCalls={floraCalls}/{FineBudget} "
                + $"biomeWeightsCalls={biomeWeightsCalls}/{FineBudget} climateCalls={climateCalls}/{FineBudget} "
                + $"coldCalls={coldCalls}/{FineBudget} dryCalls={dryCalls}/{FineBudget} "
                + $"warmCalls={warmCalls}/{FineBudget} semiDryCalls={semiDryCalls}/{FineBudget} "
                + $"jungleCalls={jungleCalls}/{FineBudget} ecotoneCalls={ecotoneCalls}/{FineBudget} "
                + $"classifyCalls={classifyCalls}/{FineBudget} packNoiseCalls={packNoiseCalls}/{FineBudget} "
                + $"snowCalls={snowCalls}/{FineBudget} tuftCalls={tuftCalls}/{FineBudget} "
                + $"variantCalls={variantCalls}/{FineBudget} pureCandidates={pureCandidates} "
                + $"localNoiseFactoryCalls={localNoiseFactoryCalls}/1 "
                + $"explanatoryCoreCalls={explanatoryCoreCalls}/{FineBudget} "
                + $"explanatoryOasisCalls={explanatoryOasisCalls}/{FineBudget} "
                + $"explanatoryPatchCalls={explanatoryPatchCalls}/{FineBudget} "
                + $"explanatoryClimateCalls={explanatoryClimateCalls}/{FineBudget} "
                + $"explanatoryDensityCalls={explanatoryDensityCalls}/{FineBudget} "
                + $"shiftedColdRejects={shiftedColdRejects} taigaSnowRejects={taigaSnowRejects} "
                + $"selectedColumns={selected.Count}/{ColumnBudget} "
                + $"columnCalls={columnCalls}/{ColumnBudget} metadataReads={metadataReads}/{5 * ColumnBudget} "
                + $"altitudeColdCalls={altitudeColdCalls}/{ColumnBudget} depthCalls={depthCalls}/{ColumnBudget} "
                + $"beachCalls={beachCalls}/{ColumnBudget} "
                + $"heightWaterCalls={heightWaterCalls}/{ColumnBudget} bedWaterCalls={bedWaterCalls}/{ColumnBudget} "
                + $"clearanceCalls={clearanceCalls}/{ColumnBudget} "
                + $"selectionFloorDivCalls={selectionFloorDivCalls}/{2 * FineBudget} "
                + $"columnDedupComparisons={columnDedupComparisons}/{FineBudget * ColumnBudget} "
                + $"verticalFloorDivCalls={verticalFloorDivCalls}/{ColumnBudget}");
            WorldSeed.Apply(WorldSeed.Default);
        }
    }
}
