using System.Collections.Generic;
using VoxelEngine.WorldGen.Trees.Features;
using VoxelEngine.WorldGen.Trees.Logics;
using VoxelEngine.WorldGen.Trees.Shape;

namespace VoxelEngine.WorldGen.Trees;

/// <summary>
/// One compiled, immutable tree species (<c>assets/data/trees/&lt;name&gt;.json</c> through
/// <see cref="TreeSpeciesCompiler"/>): resolved block ids, the growth logic and features from
/// their registries, and validated numbers with defaults applied.
/// </summary>
public sealed class TreeSpecies
{
    /// <summary>The species key: its file name.</summary>
    public required string Name { get; init; }

    /// <summary>The wood block id.</summary>
    public required byte WoodId { get; init; }

    /// <summary>The leaves block id, 0 for a leafless species.</summary>
    public required byte LeavesId { get; init; }

    /// <summary>Whether the species grows leaves; the leaf fields below are meaningful only then.</summary>
    public bool HasLeaves => LeavesId != 0;

    /// <summary>The direction chooser <c>growth.logic</c> named.</summary>
    public required IGrowthLogic Logic { get; init; }

    /// <summary>Energy a growth signal starts with.</summary>
    public required int SignalEnergy { get; init; }

    /// <summary>Weight of growing straight up.</summary>
    public required int UpProbability { get; init; }

    /// <summary>Height above the root below which no branch forks.</summary>
    public required int LowestBranchHeight { get; init; }

    /// <summary>Fewest growth steps to maturity.</summary>
    public required int MaturityStepsMin { get; init; }

    /// <summary>Most growth steps to maturity.</summary>
    public required int MaturityStepsMax { get; init; }

    /// <summary>Branch tip diameter in pixels.</summary>
    public required int TipPx { get; init; }

    /// <summary>Pipe-model exponent combining child diameters.</summary>
    public required float PipeExponent { get; init; }

    /// <summary>Largest diameter in pixels.</summary>
    public required int MaxPx { get; init; }

    /// <summary>Girth multiplier along the trunk, heights strictly increasing from 0.</summary>
    public required IReadOnlyList<(float Height01, float Multiplier)> GirthProfile { get; init; }

    /// <summary>Cross-section of the wood.</summary>
    public required TrunkShape TrunkShape { get; init; }

    /// <summary>How a wide trunk's rim meets the cell grid.</summary>
    public required TrunkRim Rim { get; init; }

    /// <summary>Leaf cluster volume (meaningful only when <see cref="HasLeaves"/>).</summary>
    public required LeafClusterShape LeafShape { get; init; }

    /// <summary>Leaf cluster radius in blocks (meaningful only when <see cref="HasLeaves"/>).</summary>
    public required int LeafRadius { get; init; }

    /// <summary>Hydration a fresh leaf starts with (meaningful only when <see cref="HasLeaves"/>).</summary>
    public required int LeafHydration { get; init; }

    /// <summary>Largest branch diameter still growing a cluster (meaningful only when <see cref="HasLeaves"/>).</summary>
    public required int TipMaxPx { get; init; }

    /// <summary>
    /// Cluster radius of a tree whose root is 1 px thick; the radius grows with the root diameter up to
    /// <see cref="LeafRadius"/> (meaningful only when <see cref="HasLeaves"/>).
    /// </summary>
    public required int LeafMinRadius { get; init; }

    /// <summary>
    /// Root diameter (pixels) at which the cluster reaches <see cref="LeafRadius"/> (meaningful only
    /// when <see cref="HasLeaves"/>).
    /// </summary>
    public required int LeafFullRadiusPx { get; init; }

    /// <summary>Post-growth features, each type at most once.</summary>
    public required IReadOnlyList<TreeFeatureSpec> Features { get; init; }

    /// <summary>Lowest far-LOD crown height.</summary>
    public required int CrownHeightMin { get; init; }

    /// <summary>Highest far-LOD crown height.</summary>
    public required int CrownHeightMax { get; init; }

    /// <summary>Far-LOD crown radius.</summary>
    public required int CrownRadius { get; init; }

    /// <summary>Far-LOD crown silhouette.</summary>
    public required LodCrownProfile LodProfile { get; init; }

    /// <summary>Worldgen placement, null when the species never generates naturally.</summary>
    public required TreeWorldgenRules? Worldgen { get; init; }
}
