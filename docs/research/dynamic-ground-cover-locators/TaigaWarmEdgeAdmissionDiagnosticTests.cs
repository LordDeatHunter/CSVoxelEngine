using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>
/// Opt-in research diagnostic for the fixed seed-1337 warm edge observed by the earlier
/// Taiga transect. This source is outside the default test project and is not a regression test.
/// </summary>
[Collection("Worldgen seed")]
public sealed class TaigaWarmEdgeAdmissionDiagnosticTests(ITestOutputHelper output)
{
    [Fact]
    public void FixedWarmEdgeReportsEveryPureGateAndOneConditionalMetadataColumn()
    {
        TestContent.EnsureLoaded();
        var fern = ContentRegistry.RequireBlock("fern").Id;
        var grassTuft = FoliageGenerator.GrassTuftBlock;
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
        var snowCalls = 0;
        var tuftCalls = 0;
        var variantCalls = 0;
        var pureCandidates = 0;
        var columnCalls = 0;
        var metadataReads = 0;
        var altitudeColdCalls = 0;
        var depthCalls = 0;
        var coreCalls = 0;
        var beachCalls = 0;
        var heightWaterCalls = 0;
        var bedWaterCalls = 0;
        var clearanceCalls = 0;
        var floorDivCalls = 0;
        (int X, int Z)? firstCandidate = null;

        try
        {
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            for (var i = 1; i <= 31; i++)
            {
                pointVisits++;
                var x = -7192 + i;
                var z = 8808 + i;

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
                snowCalls++;
                var climateHasSnow = SnowGenerator.HasSnow(x, z, cold);

                var checkedVariant = false;
                var tuft = false;
                var candidateName = "NOT_CHECKED";
                if (flora == Biome.Taiga && !climateHasSnow)
                {
                    tuftCalls++;
                    tuft = FoliageGenerator.TryGetGrassTuft(x, z);
                    variantCalls++;
                    var candidate = DynamicGroundCover.CandidateAt(x, z, flora);
                    checkedVariant = true;
                    candidateName = candidate == fern ? "fern"
                        : candidate == grassTuft ? "tall_grass" : $"unexpected-id-{candidate}";
                }

                var pureCandidate = flora == Biome.Taiga && !climateHasSnow && tuft
                    && candidateName == "fern";
                if (pureCandidate)
                {
                    pureCandidates++;
                    firstCandidate ??= (x, z);
                }
                output.WriteLine($"TaigaWarmEdge i={i} x={x} z={z} base={baseBiome} ocean={ocean:R} "
                    + $"temperature={temperature:R} humidity={humidity:R} cold={cold:R} dry={dry:R} "
                    + $"warm={warm:R} semiDry={semiDry:R} jungle={jungle:R} ecotone={ecotone:R} "
                    + $"shift={shift:R} shiftedCold={shiftedCold:R} shiftedDry={shiftedDry:R} "
                    + $"shiftedWarm={shiftedWarm:R} shiftedSemiDry={shiftedSemiDry:R} "
                    + $"shiftedJungle={shiftedJungle:R} classified={classified} flora={flora} "
                    + $"land={baseBiome != Biome.Ocean && ocean < WorldGenerationConfig.OceanShoreWeight} "
                    + $"coldGate={shiftedCold >= WorldGenerationConfig.ColdBiomeMinCold} "
                    + $"taigaDryGate={shiftedDry < 0.5f} climateHasSnow={climateHasSnow} "
                    + $"tuft={tuft} variantChecked={checkedVariant} variant={candidateName} "
                    + $"pureCandidate={pureCandidate}; altitude/ground/air/final stamp UNOBSERVED");
                rowsLogged++;
            }

            // All 31 rows are reported before spending the sole optional metadata read.
            if (firstCandidate is { } point)
            {
                floorDivCalls++;
                var cx = MathUtils.FloorDiv(point.X, Chunk.Size);
                floorDivCalls++;
                var cz = MathUtils.FloorDiv(point.Z, Chunk.Size);
                columnCalls++;
                var column = ColumnCache.Get(cx, cz);
                var lx = point.X - cx * Chunk.Size;
                var lz = point.Z - cz * Chunk.Size;
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
                coreCalls++;
                var core = TerrainErg.Intersects(point.X, point.Z, point.X, point.Z);
                beachCalls++;
                var beach = TerrainGenerator.IsBeachHeight(height);
                heightWaterCalls++;
                var surfaceWater = TerrainGenerator.IsWaterColumn(point.X, point.Z, height);
                bedWaterCalls++;
                var groundWet = TerrainGenerator.IsWaterColumn(point.X, point.Z, bed);
                clearanceCalls++;
                var clearance = SurfaceClearance.Allows(height, 1);
                floorDivCalls++;
                var localY = height + 1 - MathUtils.FloorDiv(height + 1, Chunk.Size) * Chunk.Size;
                // Only the fill's likely material, never a claim about actual cave/feature edits.
                var predictedGrass = stored == Biome.Taiga && dirtDepth != 0 && !beach && !groundWet;
                output.WriteLine($"TaigaWarmEdge firstPure x={point.X} z={point.Z} cx={cx} cz={cz} "
                    + $"height={height} stored={stored} dirtDepth={dirtDepth} bed={bed} "
                    + $"waterLevel={waterLevel} coldAtHeight={coldAtHeight:R} snowDepth={snowDepth} "
                    + $"core={core} beach={beach} surfaceWater={surfaceWater} groundWet={groundWet} "
                    + $"clearance={clearance} localY={localY} predictedGrass={predictedGrass}; "
                    + "actual ground/air/occupancy/final stamp UNOBSERVED (no fill)");
            }
            else
                output.WriteLine("TaigaWarmEdge no complete pure Taiga/no-snow/tuft/fern candidate; columnCalls=0");

            Assert.Equal(31, pointVisits);
            Assert.Equal(31, rowsLogged);
            Assert.Equal(31, floraCalls);
            Assert.Equal(31, biomeWeightsCalls);
            Assert.Equal(31, climateCalls);
            Assert.Equal(31, coldCalls);
            Assert.Equal(31, dryCalls);
            Assert.Equal(31, warmCalls);
            Assert.Equal(31, semiDryCalls);
            Assert.Equal(31, jungleCalls);
            Assert.Equal(31, ecotoneCalls);
            Assert.Equal(31, snowCalls);
            Assert.InRange(classifyCalls, 0, 31);
            Assert.InRange(columnCalls, 0, 1);
            Assert.InRange(tuftCalls, 0, 31);
            Assert.InRange(variantCalls, 0, 31);
            Assert.Equal(tuftCalls, variantCalls);
            Assert.Equal(columnCalls * 5, metadataReads);
        }
        finally
        {
            output.WriteLine($"TaigaWarmEdge partial ledger pointVisits={pointVisits}/31 rowsLogged={rowsLogged}/31 "
                + $"floraCalls={floraCalls}/31 biomeWeightsCalls={biomeWeightsCalls}/31 climateCalls={climateCalls}/31 "
                + $"coldCalls={coldCalls}/31 dryCalls={dryCalls}/31 warmCalls={warmCalls}/31 "
                + $"semiDryCalls={semiDryCalls}/31 jungleCalls={jungleCalls}/31 ecotoneCalls={ecotoneCalls}/31 "
                + $"classifyCalls={classifyCalls}/31 snowCalls={snowCalls}/31 tuftCalls={tuftCalls}/31 "
                + $"variantCalls={variantCalls}/31 pureCandidates={pureCandidates} columnCalls={columnCalls}/1 "
                + $"metadataReads={metadataReads}/5 altitudeColdCalls={altitudeColdCalls}/1 depthCalls={depthCalls}/1 "
                + $"coreCalls={coreCalls}/1 beachCalls={beachCalls}/1 heightWaterCalls={heightWaterCalls}/1 "
                + $"bedWaterCalls={bedWaterCalls}/1 clearanceCalls={clearanceCalls}/1 floorDivCalls={floorDivCalls}/3");
            WorldSeed.Apply(WorldSeed.Default);
        }
    }
}
