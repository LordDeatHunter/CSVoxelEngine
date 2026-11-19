using System;
using System.Collections.Generic;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Ocean;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Ocean;

/// <summary>Opt-in fixed-column OCOR metadata candidates, never observed placed coral.</summary>
[Collection("Worldgen seed")]
public sealed class OceanCoralMoundMetadataDiagnostic(ITestOutputHelper output)
{
    private const string OptInVariable = "VOXEL_S6B_CORAL_MOUND_METADATA";
    // OceanHabitats.CoralSiteSalt (OCOR). Duplicated only to predict its private roll;
    // the production writer and this diagnostic both salt Seed with these bytes.
    private const int CoralSiteSalt = 0x4F434F52;

    // First of each group has a frozen 130-key raw-climate preflight row. The five
    // adjacent columns have no accepted climate, height or biome observation yet.
    private static readonly (int Cx, int Cz, string Provenance)[] Columns =
    [
        (-172, 260, "raw warmth 1, Ocean weight .7010949; metadata unobserved"),
        (-173, 260, "fixed -X neighbor; metadata unobserved"),
        (-172, 261, "fixed +Z neighbor; metadata unobserved"),
        (-196, 244, "raw warmth .9530894, Ocean weight .727316; metadata unobserved"),
        (-197, 244, "fixed -X neighbor; metadata unobserved"),
        (-196, 245, "fixed +Z neighbor; metadata unobserved"),
        (-164, 236, "raw warmth .7991052, Ocean weight .5480648; metadata unobserved"),
        (-164, 237, "fixed +Z neighbor; metadata unobserved"),
    ];

    private static readonly (int Dx, int Dz)[] Supports =
    [
        (0, 0), (-1, 0), (1, 0), (0, -1), (0, 1),
    ];

    [OptInCoralMoundMetadataFact]
    public void EightFrozenColumnsReportOCORMetadataCandidatesOnly()
    {
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;
        var applied = false;
        var ownGets = 0;
        var relevanceChecks = 0;
        var cornerReads = 0;
        var admittedColumns = 0;
        var inspectedSites = 0;
        var rowsWritten = 0;
        var centerArrayReads = 0;
        var supportIndexAttempts = 0;
        var supportArrayReads = 0;
        var classifyAttempts = 0;
        var internalSamples = 0;
        var explicitSamples = 0;
        var selectAttempts = 0;
        var hashAttempts = 0;
        var unit0Attempts = 0;
        var unit1Attempts = 0;
        var supportedCoral = 0;
        var chancePassed = 0;
        var candidates = 0;
        string? firstCandidate = null;
        var reasons = new Dictionary<string, int>();

        try
        {
            TestContent.EnsureLoaded();
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            applied = true;
            var minFloor = OceanHabitats.WriteBand().Min;

            foreach (var (cx, cz, provenance) in Columns)
            {
                ownGets++; // Count before a cold ColumnCache factory can enter.
                var column = ColumnCache.Get(cx, cz);
                output.WriteLine($"S6B_MOUND_COLUMN key=({cx},{cz}) provenance={provenance} "
                    + $"minH={column.MinHeight} maxH={column.MaxHeight} maxWater={column.MaxWaterLevel}");
                relevanceChecks++;
                if (!OceanHabitatField.HasRelevantOcean(column))
                {
                    output.WriteLine($"S6B_MOUND_COLUMN_MISS key=({cx},{cz}) reason=no-relevant-ocean-floor "
                        + "corners=unread sites=uninspected blocks=unknown state=unknown");
                    continue;
                }

                admittedColumns++;
                cornerReads++; // ReadGeneratedCorners attempts four ColumnCache.Get calls.
                var field = OceanHabitatField.ReadGeneratedCorners(cx, cz);
                for (var x = 0; x < Chunk.Size; x++)
                for (var z = 0; z < Chunk.Size; z++)
                {
                    inspectedSites++;
                    var index = (x << Chunk.SizeShift) | z;
                    centerArrayReads += 5;
                    var h = column.Heights[index];
                    var bed = column.Bed[index];
                    var water = column.WaterLevel[index];
                    var biome = column.Biomes[index];
                    var dirtDepth = column.DirtDepth[index];
                    var depth = WorldGenerationConfig.SeaLevel - h;
                    var localX = x + 0.5f;
                    var localZ = z + 0.5f;
                    classifyAttempts++;
                    if (biome == Biome.Ocean && water == WorldGenerationConfig.SeaLevel && depth >= 3)
                        internalSamples++; // Classify takes its own Sample on this path.
                    var habitat = field.Classify(true, biome == Biome.Ocean, water, h, localX, localZ);
                    explicitSamples++;
                    var warmth = field.Sample(localX, localZ);
                    selectAttempts++;
                    var material = OceanFloorMaterials.Select(habitat, warmth, depth);
                    var worldX = cx * Chunk.Size + x;
                    var worldZ = cz * Chunk.Size + z;
                    var chunkY = MathUtils.FloorDiv(h, Chunk.Size);
                    var localY = h - chunkY * Chunk.Size;
                    var reason = "center-not-coral";
                    var supportChecked = 0;
                    float? roll = null;
                    string color = "unselected";

                    if (material == OceanFloorMaterial.Coral)
                    {
                        reason = x <= 0 || x >= Chunk.Size - 1 || z <= 0 || z >= Chunk.Size - 1
                            ? "cross-outside-own-XZ"
                            : h < minFloor || localY + 3 >= Chunk.Size
                              || h + 3 > WorldGenerationConfig.SeaLevel - 2
                                ? "floor-band-or-own-Y-or-submerged-top"
                                : "supports-pass";

                        if (reason == "supports-pass")
                        {
                            foreach (var (dx, dz) in Supports)
                            {
                                supportIndexAttempts++;
                                supportChecked++;
                                var sx = x + dx;
                                var sz = z + dz;
                                var si = (sx << Chunk.SizeShift) | sz;
                                supportArrayReads += 3;
                                var sh = column.Heights[si];
                                var sb = column.Biomes[si];
                                var sw = column.WaterLevel[si];
                                if (sh != h) { reason = "support-height-mismatch"; break; }
                                if (sb != Biome.Ocean || sw != WorldGenerationConfig.SeaLevel)
                                { reason = "support-ocean-water-mismatch"; break; }

                                var sDepth = WorldGenerationConfig.SeaLevel - sh;
                                classifyAttempts++;
                                if (sDepth >= 3) internalSamples++;
                                var supportHabitat = field.Classify(true, true, sw, sh,
                                    sx + 0.5f, sz + 0.5f);
                                explicitSamples++;
                                var supportWarmth = field.Sample(sx + 0.5f, sz + 0.5f);
                                selectAttempts++;
                                if (OceanFloorMaterials.Select(supportHabitat, supportWarmth, sDepth)
                                    == OceanFloorMaterial.Coral) continue;
                                reason = "support-not-coral";
                                break;
                            }
                        }

                        if (reason == "supports-pass")
                        {
                            supportedCoral++;
                            hashAttempts++;
                            var hash = SplitMix64.Hash(worldX, worldZ,
                                WorldGenerationConfig.Seed ^ CoralSiteSalt);
                            unit0Attempts++;
                            var chance = SplitMix64.Unit(hash, 0);
                            roll = chance;
                            if (chance >= WorldFeatureConfig.OceanCoralMoundChance)
                                reason = "OCOR-chance-rejected";
                            else
                            {
                                chancePassed++;
                                unit1Attempts++;
                                color = SplitMix64.Unit(hash, 1) < 0.5f ? "coral_rose" : "coral_blue";
                                reason = "metadata-candidate-only";
                                candidates++;
                                firstCandidate ??= $"({worldX},{h},{worldZ}) key=({cx},{cz}) color={color}";
                            }
                        }
                    }

                    reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                    output.WriteLine($"S6B_MOUND_ROW key=({cx},{cz}) world=({worldX},{h},{worldZ}) "
                        + $"local=({x},{localY},{z}) chunkY={chunkY} h={h} bed={bed} "
                        + $"water={water} biome={biome} dirtDepth={dirtDepth} "
                        + $"corners=({field.C00:R},{field.C10:R},{field.C01:R},{field.C11:R}) "
                        + $"warmth={warmth:R} habitat={habitat} material={material} depth={depth} "
                        + $"supportChecked={supportChecked}/5 OCORroll={roll?.ToString("R") ?? "unread"} "
                        + $"predictedColor={color} reason={reason} "
                        + "actualBlocks=unknown actualState=unknown actualClearance=unknown");
                    rowsWritten++;
                }
            }

            output.WriteLine($"S6B_MOUND_RESULT candidates={candidates} supportedCoral={supportedCoral} "
                + $"chancePassed={chancePassed} first={firstCandidate ?? "none"} "
                + "actualPlacement=unknown actualVisibility=unknown");
            Assert.True(candidates > 0,
                "No OCOR-qualified metadata candidate in the frozen eight columns; retain all miss rows.");
        }
        finally
        {
            try
            {
                output.WriteLine($"S6B_MOUND_LEDGER seed=1337 profile=Dynamic "
                    + $"ownGets={ownGets}/8 relevanceChecks={relevanceChecks}/8 "
                    + $"cornerReads={cornerReads}/8 directGetUpper={ownGets + 4 * cornerReads}/40 "
                    + $"admittedColumns={admittedColumns}/8 inspectedSites={inspectedSites}/2048 "
                    + $"rowsWritten={rowsWritten}/2048 centerArrayReads={centerArrayReads}/10240 "
                    + $"supportIndexAttempts={supportIndexAttempts}/10240 "
                    + $"supportArrayReads={supportArrayReads}/30720 "
                    + $"classifyAttempts={classifyAttempts}/12288 internalSamples={internalSamples}/12288 "
                    + $"explicitSamples={explicitSamples}/12288 totalSamples={internalSamples + explicitSamples}/24576 "
                    + $"selectAttempts={selectAttempts}/12288 hashAttempts={hashAttempts}/2048 "
                    + $"unit0Attempts={unit0Attempts}/2048 unit1Attempts={unit1Attempts}/2048 "
                    + $"supportedCoral={supportedCoral} chancePassed={chancePassed} candidates={candidates}");
                foreach (var reason in reasons)
                    output.WriteLine($"S6B_MOUND_REASON {reason.Key}={reason.Value}");
            }
            finally
            {
                if (applied) WorldSeed.Apply(previousSeed, previousProfile);
            }
        }
    }

    /// <summary>Skip before content, seed, cache, climate or hash work by default.</summary>
    public sealed class OptInCoralMoundMetadataFactAttribute : FactAttribute
    {
        public OptInCoralMoundMetadataFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 for the fixed eight-column OCOR metadata cohort.";
        }
    }
}
