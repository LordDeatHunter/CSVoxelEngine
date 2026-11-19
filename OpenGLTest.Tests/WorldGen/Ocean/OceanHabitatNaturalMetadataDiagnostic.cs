using System;
using VoxelEngine.Chunks;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Ocean;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Ocean;

/// <summary>Opt-in fixed-column metadata candidates for later real ocean habitat fills.</summary>
[Collection("Worldgen seed")]
public sealed class OceanHabitatNaturalMetadataDiagnostic(ITestOutputHelper output)
{
    private const string OptInVariable = "VOXEL_S6B_HABITAT_METADATA";

    // Historical S1a seed-1337 data found unfrozen open ocean at (-3000,3904) and a wet
    // shelf at (-3000,4000). Current S6b biome, height, and warmth remain unobserved.
    // The two adjacent keys are unobserved proposals; the last S6a key is a cold control.
    private static readonly (int Cx, int Cz, string Provenance)[] Columns =
    [
        (-188, 244, "S1a historical open unfrozen ocean (-3000,3904), float h=-45.10973; current metadata unobserved"),
        (-188, 245, "fixed +16Z neighbor (-3000,3920); wholly unobserved"),
        (-188, 246, "fixed +32Z neighbor (-3000,3936); wholly unobserved"),
        (-188, 250, "S1a historical wet unfrozen shelf (-3000,4000), float h=-3.3933728, oceanWeight=.4638545; stored Ocean unproven"),
        (-74, -188, "S6a accepted rock (-1179,-2996), h=-119, Ocean/Abyss, corners 0"),
    ];
    private static readonly (int Dx, int Dz)[] MoundSupports =
    [
        (0, 0), (-1, 0), (1, 0), (0, -1), (0, 1),
    ];

    [OptInHabitatMetadataFact]
    public void FiveFrozenOceanColumnsYieldMetadataOnlyHabitatCandidates()
    {
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;
        var applied = false;
        var ownGetAttempts = 0;
        var relevanceChecks = 0;
        var cornerFieldAttempts = 0;
        var admittedColumns = 0;
        var inspectedSites = 0;
        var rowsWritten = 0;
        var classifyAttempts = 0;
        var warmthSampleAttempts = 0;
        var selectAttempts = 0;
        var floorCandidates = 0;
        var moundMetadataCandidates = 0;
        var supportReads = 0;
        var habitatCounts = new int[6];
        var materialCounts = new int[4];
        var firstMaterial = new string?[4];

        try
        {
            TestContent.EnsureLoaded();
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            applied = true;
            var minFloor = OceanHabitats.WriteBand().Min;

            foreach (var (cx, cz, provenance) in Columns)
            {
                ownGetAttempts++; // Count the attempt before a cold/evicted factory can throw.
                var column = ColumnCache.Get(cx, cz);
                output.WriteLine($"S6B_COLUMN key=({cx},{cz}) provenance={provenance}");
                relevanceChecks++;
                if (!OceanHabitatField.HasRelevantOcean(column))
                {
                    output.WriteLine($"S6B_COLUMN_MISS key=({cx},{cz}) reason=no-relevant-ocean-floor");
                    continue;
                }

                admittedColumns++;
                cornerFieldAttempts++; // Four derived Get calls at most; partial on exception.
                var field = OceanHabitatField.ReadGeneratedCorners(cx, cz);
                for (var x = 0; x < Chunk.Size; x++)
                for (var z = 0; z < Chunk.Size; z++)
                {
                    inspectedSites++;
                    var index = (x << Chunk.SizeShift) | z;
                    var height = column.Heights[index];
                    var bed = column.Bed[index];
                    var water = column.WaterLevel[index];
                    var biome = column.Biomes[index];
                    var localX = x + 0.5f;
                    var localZ = z + 0.5f;
                    classifyAttempts++;
                    var habitat = field.Classify(true, biome == Biome.Ocean, water,
                        height, localX, localZ);
                    warmthSampleAttempts++;
                    var warmth = field.Sample(localX, localZ);
                    selectAttempts++;
                    var material = OceanFloorMaterials.Select(habitat, warmth,
                        WorldGenerationConfig.SeaLevel - height);
                    habitatCounts[(int)habitat]++;
                    materialCounts[(int)material]++;
                    var floorChunkY = MathUtils.FloorDiv(height, Chunk.Size);
                    var localY = height - floorChunkY * Chunk.Size;
                    var floorAndAboveFit = height >= minFloor
                        && height + 1 < WorldGenerationConfig.SeaLevel
                        && localY + 1 < Chunk.Size;
                    var visibleCandidate = material != OceanFloorMaterial.None && floorAndAboveFit;
                    var moundMetadata = visibleCandidate && material == OceanFloorMaterial.Coral
                        && MoundMetadataFits(column, x, z, height, localY, ref supportReads);
                    var worldX = cx * Chunk.Size + x;
                    var worldZ = cz * Chunk.Size + z;
                    var reason = habitat == OceanHabitat.None ? "habitat-none-admission"
                        : material == OceanFloorMaterial.None ? "material-none"
                        : !floorAndAboveFit ? "floor-band-or-own-Y-boundary"
                        : "metadata-candidate-only";
                    output.WriteLine($"S6B_ROW key=({cx},{cz}) world=({worldX},{worldZ}) "
                        + $"h={height} bed={bed} water={water} biome={biome} "
                        + $"corners=({field.C00},{field.C10},{field.C01},{field.C11}) "
                        + $"warmth={warmth} habitat={habitat} material={material} "
                        + $"floorChunkY={floorChunkY} localY={localY} floorAboveFit={floorAndAboveFit} "
                        + $"moundMetadata={moundMetadata} reason={reason}");
                    rowsWritten++;
                    if (!visibleCandidate) continue;
                    floorCandidates++;
                    firstMaterial[(int)material] ??= $"({worldX},{worldZ}) h={height} key=({cx},{cz})";
                    if (moundMetadata) moundMetadataCandidates++;
                }
            }

            Assert.True(floorCandidates > 0,
                "No metadata-eligible floor among the five frozen columns; retain the full miss ledger.");
        }
        finally
        {
            try
            {
                output.WriteLine($"S6B_LEDGER seed=1337 profile=Dynamic ownGetAttempts={ownGetAttempts}/5 "
                    + $"cornerFieldAttempts={cornerFieldAttempts}/5 derivedCornerGetUpper={4 * cornerFieldAttempts}/20 "
                    + $"relevanceChecks={relevanceChecks}/5 admittedColumns={admittedColumns}/5 "
                    + $"inspectedSites={inspectedSites}/1280 classifyAttempts={classifyAttempts}/1280 "
                    + $"rowsWritten={rowsWritten}/1280 "
                    + $"warmthSampleAttempts={warmthSampleAttempts}/1280 "
                    + $"derivedBilinearUpper={classifyAttempts + warmthSampleAttempts}/2560 "
                    + $"selectAttempts={selectAttempts}/1280 "
                    + $"supportReads={supportReads}/6400 floorCandidates={floorCandidates} "
                    + $"moundMetadataCandidates={moundMetadataCandidates}");
                for (var i = 0; i < habitatCounts.Length; i++)
                    output.WriteLine($"S6B_HABITAT {((OceanHabitat)i)}={habitatCounts[i]}");
                for (var i = 0; i < materialCounts.Length; i++)
                    output.WriteLine($"S6B_MATERIAL {((OceanFloorMaterial)i)}={materialCounts[i]} "
                        + $"first={firstMaterial[i] ?? "none"}");
            }
            finally
            {
                if (applied) WorldSeed.Apply(previousSeed, previousProfile);
            }
        }
    }

    private static bool MoundMetadataFits(ColumnData column, int x, int z, int height,
        int localY, ref int supportReads)
    {
        if (x <= 0 || x >= Chunk.Size - 1 || z <= 0 || z >= Chunk.Size - 1
            || localY + 3 >= Chunk.Size
            || height + 3 > WorldGenerationConfig.SeaLevel - 2) return false;

        // Only the five own-column-array supports. Actual block IDs/state/water and
        // the OCOR candidate roll are intentionally unknown until a separate fill grant.
        foreach (var (dx, dz) in MoundSupports)
        {
            supportReads++;
            var index = ((x + dx) << Chunk.SizeShift) | (z + dz);
            if (column.Heights[index] != height || column.Biomes[index] != Biome.Ocean
                || column.WaterLevel[index] != WorldGenerationConfig.SeaLevel)
                return false;
        }
        return true;
    }

    /// <summary>Default discovery and execution skip before content or worldgen initialization.</summary>
    public sealed class OptInHabitatMetadataFactAttribute : FactAttribute
    {
        public OptInHabitatMetadataFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 for the five frozen metadata-only ocean columns.";
        }
    }
}
