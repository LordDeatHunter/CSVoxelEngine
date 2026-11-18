using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>
/// Opt-in, fixed locally aligned Taiga admission observation. Outside the default test project;
/// a zero-candidate result is a valid bounded observation, not a feature regression.
/// </summary>
[Collection("Worldgen seed")]
public sealed class TaigaAlignedAdmissionDiagnosticTests(ITestOutputHelper output)
{
    private const int NormalStart = -12;
    private const int NormalEnd = 12;
    private const int TangentStart = -64;
    private const int TangentEnd = 64;
    private const int PointBudget = (NormalEnd - NormalStart + 1) * (TangentEnd - TangentStart + 1); // 3225

    [Fact]
    public void FixedAlignedGridReportsPureGatesAndAtMostOneMetadataColumn()
    {
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
        var pureCandidates = 0;
        var taigaSnowRejects = 0;
        var shiftedColdRejects = 0;
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
            TestContent.EnsureLoaded();
            var fern = ContentRegistry.RequireBlock("fern").Id;
            var grassTuft = FoliageGenerator.GrassTuftBlock;
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);

            // The retained local cold gradient is mostly along z: (10,-1) is its
            // approximate isocold tangent. This is a fixed sample, not a global isotherm.
            for (var normal = NormalStart; normal <= NormalEnd; normal++)
            for (var tangent = TangentStart; tangent <= TangentEnd; tangent++)
            {
                pointVisits++;
                var x = -7195 + 10 * tangent;
                var z = 8824 + normal - tangent;

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
                if (flora == Biome.Taiga && !climateHasSnow)
                {
                    tuftCalls++;
                    tuft = FoliageGenerator.TryGetGrassTuft(x, z);
                    variantCalls++;
                    var candidate = DynamicGroundCover.CandidateAt(x, z, flora);
                    candidateName = candidate == fern ? "fern"
                        : candidate == grassTuft ? "tall_grass" : $"unexpected-id-{candidate}";
                }

                var pureCandidate = flora == Biome.Taiga && !climateHasSnow && tuft
                    && candidateName == "fern";
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
                    firstCandidate ??= (x, z);
                }

                output.WriteLine($"TaigaAligned point={pointVisits} normal={normal} tangent={tangent} "
                    + $"x={x} z={z} base={baseBiome} ocean={ocean:R} temperature={temperature:R} "
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
                    + $"pureCandidate={pureCandidate} reason={reason}; "
                    + "altitude/ground/air/final stamp UNOBSERVED");
                rowsLogged++;
            }

            // No adaptive second point. All 3225 pure rows are emitted before one optional column.
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
                var predictedGrass = stored == Biome.Taiga && dirtDepth != 0 && !beach && !groundWet;
                output.WriteLine($"TaigaAligned firstPure x={point.X} z={point.Z} cx={cx} cz={cz} "
                    + $"height={height} stored={stored} dirtDepth={dirtDepth} bed={bed} "
                    + $"waterLevel={waterLevel} coldAtHeight={coldAtHeight:R} snowDepth={snowDepth} "
                    + $"core={core} beach={beach} surfaceWater={surfaceWater} groundWet={groundWet} "
                    + $"clearance={clearance} localY={localY} predictedGrass={predictedGrass}; "
                    + "actual ground/air/occupancy/final stamp UNOBSERVED (no fill)");
            }
            else
                output.WriteLine("TaigaAligned no complete pure Taiga/no-snow/tuft/fern candidate; columnCalls=0");

            Assert.Equal(PointBudget, pointVisits);
            Assert.Equal(PointBudget, rowsLogged);
            Assert.Equal(PointBudget, floraCalls);
            Assert.Equal(PointBudget, biomeWeightsCalls);
            Assert.Equal(PointBudget, climateCalls);
            Assert.Equal(PointBudget, coldCalls);
            Assert.Equal(PointBudget, dryCalls);
            Assert.Equal(PointBudget, warmCalls);
            Assert.Equal(PointBudget, semiDryCalls);
            Assert.Equal(PointBudget, jungleCalls);
            Assert.Equal(PointBudget, ecotoneCalls);
            Assert.Equal(PointBudget, snowCalls);
            Assert.InRange(classifyCalls, 0, PointBudget);
            Assert.InRange(packNoiseCalls, 0, PointBudget);
            Assert.InRange(tuftCalls, 0, PointBudget);
            Assert.Equal(tuftCalls, variantCalls);
            Assert.InRange(columnCalls, 0, 1);
            Assert.Equal(5 * columnCalls, metadataReads);
        }
        finally
        {
            output.WriteLine($"TaigaAligned partial ledger points={pointVisits}/{PointBudget} "
                + $"rowsLogged={rowsLogged}/{PointBudget} floraCalls={floraCalls}/{PointBudget} "
                + $"biomeWeightsCalls={biomeWeightsCalls}/{PointBudget} climateCalls={climateCalls}/{PointBudget} "
                + $"coldCalls={coldCalls}/{PointBudget} dryCalls={dryCalls}/{PointBudget} "
                + $"warmCalls={warmCalls}/{PointBudget} semiDryCalls={semiDryCalls}/{PointBudget} "
                + $"jungleCalls={jungleCalls}/{PointBudget} ecotoneCalls={ecotoneCalls}/{PointBudget} "
                + $"classifyCalls={classifyCalls}/{PointBudget} packNoiseCalls={packNoiseCalls}/{PointBudget} "
                + $"snowCalls={snowCalls}/{PointBudget} tuftCalls={tuftCalls}/{PointBudget} "
                + $"variantCalls={variantCalls}/{PointBudget} pureCandidates={pureCandidates} "
                + $"shiftedColdRejects={shiftedColdRejects} taigaSnowRejects={taigaSnowRejects} "
                + $"columnCalls={columnCalls}/1 metadataReads={metadataReads}/5 "
                + $"altitudeColdCalls={altitudeColdCalls}/1 depthCalls={depthCalls}/1 "
                + $"coreCalls={coreCalls}/1 beachCalls={beachCalls}/1 "
                + $"heightWaterCalls={heightWaterCalls}/1 bedWaterCalls={bedWaterCalls}/1 "
                + $"clearanceCalls={clearanceCalls}/1 floorDivCalls={floorDivCalls}/3");
            WorldSeed.Apply(WorldSeed.Default);
        }
    }
}
