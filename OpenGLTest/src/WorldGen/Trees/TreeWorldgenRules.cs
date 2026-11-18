using System.Collections.Generic;

namespace VoxelEngine.WorldGen.Trees;

/// <summary>
/// Where worldgen plants a <see cref="TreeSpecies"/>: the biomes it grows in and the per-column
/// placement rules. Immutable; a species with no rules never generates naturally.
/// </summary>
public sealed class TreeWorldgenRules
{
    /// <summary>World profiles in which this species may generate.</summary>
    public required IReadOnlyList<WorldGenProfile> Profiles { get; init; }

    /// <summary>The biomes the species grows in; non-empty, no duplicates.</summary>
    public required IReadOnlyList<Biome> Biomes { get; init; }

    /// <summary>Whether the species is planted only inside an oasis disc (an oasis palm), never
    /// by the ordinary biome placement.</summary>
    public bool Oasis { get; init; }

    /// <summary>Chance per candidate column, 0..1.</summary>
    public required float Density { get; init; }

    /// <summary>Dynamic-only density, leaving the Legacy density unchanged.</summary>
    public required float DynamicDensity { get; init; }

    /// <summary>Positive relative weight in the Dynamic biome species choice.</summary>
    public required float DynamicWeight { get; init; }

    /// <summary>Minimum gap to another trunk in blocks.</summary>
    public required int Spacing { get; init; }

    /// <summary>Largest height step across the footprint.</summary>
    public required int MaxSlope { get; init; }

    /// <summary>Soil depth the column needs.</summary>
    public required int MinSoil { get; init; }

    /// <summary>Lowest ground height the species grows on.</summary>
    public required int MinGround { get; init; }

    /// <summary>Highest ground height the species grows on.</summary>
    public required int MaxGround { get; init; }

    /// <summary>
    /// Share of <see cref="TerrainGenerator.ForestOpenness"/> thinning the species takes, 0..1:
    /// 1 thins it like the broadleaf forest, 0 ignores open regions entirely.
    /// </summary>
    public float Openness { get; init; } = 1f;

    /// <summary>Whether the species keeps out of the spawn clearing (<see cref="WorldGenerationConfig.TreeSpawnClearRadius"/>).</summary>
    public bool SpawnClear { get; init; } = true;

    /// <summary>
    /// The block worldgen lays as this species' fallen (hollow) log on the floor of the biomes it
    /// is the tree of, a facing model block whose state byte holds the log's axis; 0 means none.
    /// </summary>
    public ushort FallenLogId { get; init; }

    /// <summary>Whether the species grows in <paramref name="biome"/>.</summary>
    public bool GrowsIn(Biome biome)
    {
        for (var i = 0; i < Biomes.Count; i++)
            if (Biomes[i] == biome) return true;
        return false;
    }

    /// <summary>Whether this immutable rule admits the selected world profile.</summary>
    public bool Allows(WorldGenProfile profile)
    {
        for (var i = 0; i < Profiles.Count; i++)
            if (Profiles[i] == profile) return true;
        return false;
    }

    /// <summary>The placement density for the selected profile.</summary>
    public float DensityFor(WorldGenProfile profile) =>
        profile == WorldGenProfile.Dynamic ? DynamicDensity : Density;
}
