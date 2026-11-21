using System;
using VoxelEngine.Chunks;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Ocean;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Ocean;

/// <summary>Opt-in generated metadata candidates, never observed moss blocks or water state.</summary>
[Collection("Worldgen seed")]
public sealed class OceanMossNaturalMetadataDiagnostic(ITestOutputHelper output)
{
    private const string OptInVariable = "VOXEL_S6B_MOSS_METADATA";

    // The first, fifth through eighth keys have raw-climate preflight rows. The three
    // adjacent keys are frozen proposals; their climate and generated metadata are unknown.
    private static readonly (int Cx, int Cz, string Provenance)[] Columns =
    [
        (-188, 220, "preflight moderate; lowest Ocean weight among 16, center (-3000,3528)"),
        (-189, 220, "fixed -X16 neighbor; unobserved"),
        (-187, 220, "fixed +X16 neighbor; unobserved"),
        (-188, 219, "fixed -Z16 neighbor; unobserved"),
        (-220, 228, "preflight warm, center (-3512,3656)"),
        (-220, 236, "preflight warm, center (-3512,3784)"),
        (-212, 244, "preflight warm, center (-3384,3912)"),
        (-164, 220, "preflight moderate/warm overlap, center (-2616,3528)"),
    ];

    [OptInMossMetadataFact]
    public void EightFrozenColumnsReportContainedMossMetadataAndSaltPrediction()
    {
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;
        var applied = false;
        var ownGetAttempts = 0;
        var relevanceChecks = 0;
        var cornerFieldAttempts = 0;
        var admittedColumns = 0;
        var inspectedSites = 0;
        var classifyAttempts = 0;
        var warmthSampleAttempts = 0;
        var selectAttempts = 0;
        var saltPredictionAttempts = 0;
        var rowsWritten = 0;
        var rawSeaMoss = 0;
        var rawGlowMoss = 0;
        var containedSeaMoss = 0;
        var containedGlowMoss = 0;
        var nonSaltSeaMoss = 0;
        var nonSaltGlowMoss = 0;
        string? firstSeaMoss = null;
        string? firstGlowMoss = null;

        try
        {
            TestContent.EnsureLoaded();
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            applied = true;
            var minFloor = OceanHabitats.WriteBand().Min;

            foreach (var (cx, cz, provenance) in Columns)
            {
                ownGetAttempts++; // Count before a potentially cold/evicted factory.
                var column = ColumnCache.Get(cx, cz);
                output.WriteLine($"S6B_MOSS_METADATA_COLUMN key=({cx},{cz}) provenance={provenance}");
                relevanceChecks++;
                if (!OceanHabitatField.HasRelevantOcean(column))
                {
                    output.WriteLine($"S6B_MOSS_METADATA_COLUMN_MISS key=({cx},{cz}) reason=no-relevant-ocean-floor");
                    continue;
                }

                admittedColumns++;
                cornerFieldAttempts++; // ReadGeneratedCorners may perform four cache Get calls.
                var field = OceanHabitatField.ReadGeneratedCorners(cx, cz);
                for (var x = 0; x < Chunk.Size; x++)
                for (var z = 0; z < Chunk.Size; z++)
                {
                    inspectedSites++;
                    var i = (x << Chunk.SizeShift) | z;
                    var h = column.Heights[i];
                    var bed = column.Bed[i];
                    var water = column.WaterLevel[i];
                    var biome = column.Biomes[i];
                    var dirtDepth = column.DirtDepth[i];
                    var localX = x + 0.5f;
                    var localZ = z + 0.5f;
                    classifyAttempts++;
                    var habitat = field.Classify(true, biome == Biome.Ocean, water, h,
                        localX, localZ);
                    warmthSampleAttempts++;
                    var warmth = field.Sample(localX, localZ);
                    selectAttempts++;
                    var material = OceanFloorMaterials.Select(habitat, warmth,
                        WorldGenerationConfig.SeaLevel - h);
                    var floorChunkY = MathUtils.FloorDiv(h, Chunk.Size);
                    var localY = h - floorChunkY * Chunk.Size;
                    var contained = h >= minFloor && h + 1 < WorldGenerationConfig.SeaLevel
                        && localY + 1 < Chunk.Size;
                    var expectedHost = h >= WorldGenerationConfig.SeaLevel - WorldGenerationConfig.OceanSandDepth
                        ? "sand" : h >= WorldGenerationConfig.SeaLevel - WorldGenerationConfig.OceanGravelDepth
                            ? "gravel" : "stone";
                    var moss = material is OceanFloorMaterial.SeaMoss or OceanFloorMaterial.GlowMoss;
                    var predictedSalt = false;
                    var saltChecked = false;
                    var worldX = cx * Chunk.Size + x;
                    var worldZ = cz * Chunk.Size + z;
                    if (moss && contained && expectedHost == "stone"
                        && WorldGenerationConfig.RockSaltEnabled)
                    {
                        saltPredictionAttempts++; // Before static init/noise entry.
                        predictedSalt = RockSaltGenerator.HasSalt(worldX, h, worldZ,
                            biome, h, dirtDepth);
                        saltChecked = true;
                    }

                    var raw = moss;
                    var nonSalt = raw && contained && !predictedSalt;
                    if (material == OceanFloorMaterial.SeaMoss)
                    {
                        rawSeaMoss++;
                        if (contained) containedSeaMoss++;
                        if (nonSalt)
                        {
                            nonSaltSeaMoss++;
                            firstSeaMoss ??= $"({worldX},{worldZ}) h={h} key=({cx},{cz})";
                        }
                    }
                    if (material == OceanFloorMaterial.GlowMoss)
                    {
                        rawGlowMoss++;
                        if (contained) containedGlowMoss++;
                        if (nonSalt)
                        {
                            nonSaltGlowMoss++;
                            firstGlowMoss ??= $"({worldX},{worldZ}) h={h} key=({cx},{cz})";
                        }
                    }

                    var reason = material == OceanFloorMaterial.None ? "material-none"
                        : !moss ? "other-material"
                        : !contained ? "floor-band-or-own-Y-boundary"
                        : predictedSalt ? "predicted-rock-salt-owner"
                        : "contained-non-salt-prediction-only";
                    output.WriteLine($"S6B_MOSS_METADATA_ROW key=({cx},{cz}) world=({worldX},{worldZ}) "
                        + $"h={h} bed={bed} water={water} dirtDepth={dirtDepth} biome={biome} "
                        + $"corners=({field.C00:R},{field.C10:R},{field.C01:R},{field.C11:R}) "
                        + $"warmth={warmth:R} habitat={habitat} material={material} "
                        + $"expectedHost={expectedHost} saltChecked={saltChecked} predictedSalt={predictedSalt} "
                        + $"floorChunkY={floorChunkY} localY={localY} contained={contained} "
                        + $"rawMoss={raw} nonSaltPrediction={nonSalt} reason={reason} "
                        + "actualFloor=unknown actualAbove=unknown blockState=unknown");
                    rowsWritten++;
                }
            }

            output.WriteLine($"S6B_MOSS_METADATA_HIST rawSeaMoss={rawSeaMoss} rawGlowMoss={rawGlowMoss} "
                + $"containedSeaMoss={containedSeaMoss} containedGlowMoss={containedGlowMoss} "
                + $"nonSaltSeaMoss={nonSaltSeaMoss} nonSaltGlowMoss={nonSaltGlowMoss} "
                + $"firstSeaMoss={firstSeaMoss ?? "none"} firstGlowMoss={firstGlowMoss ?? "none"}");
            Assert.True(nonSaltSeaMoss > 0 && nonSaltGlowMoss > 0,
                "The eight frozen columns must contain both moss metadata candidates; retain full miss rows.");
        }
        finally
        {
            try
            {
                output.WriteLine($"S6B_MOSS_METADATA_LEDGER seed=1337 profile=Dynamic "
                    + $"ownGetAttempts={ownGetAttempts}/8 relevanceChecks={relevanceChecks}/8 "
                    + $"cornerFieldAttempts={cornerFieldAttempts}/8 "
                    + $"derivedCornerGetUpper={4 * cornerFieldAttempts}/32 "
                    + $"admittedColumns={admittedColumns}/8 inspectedSites={inspectedSites}/2048 "
                    + $"classifyAttempts={classifyAttempts}/2048 "
                    + $"warmthSampleAttempts={warmthSampleAttempts}/2048 "
                    + $"derivedBilinearUpper={classifyAttempts + warmthSampleAttempts}/4096 "
                    + $"selectAttempts={selectAttempts}/2048 "
                    + $"saltPredictionAttempts={saltPredictionAttempts}/2048 rowsWritten={rowsWritten}/2048 "
                    + $"rawSeaMoss={rawSeaMoss} rawGlowMoss={rawGlowMoss} "
                    + $"containedSeaMoss={containedSeaMoss} containedGlowMoss={containedGlowMoss} "
                    + $"nonSaltSeaMoss={nonSaltSeaMoss} nonSaltGlowMoss={nonSaltGlowMoss}");
            }
            finally
            {
                if (applied) WorldSeed.Apply(previousSeed, previousProfile);
            }
        }
    }

    /// <summary>Skips before content, seed, column, climate or patch-noise work by default.</summary>
    public sealed class OptInMossMetadataFactAttribute : FactAttribute
    {
        public OptInMossMetadataFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 for eight frozen generated moss metadata columns.";
        }
    }
}
