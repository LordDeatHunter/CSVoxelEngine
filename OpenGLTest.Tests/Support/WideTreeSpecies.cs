using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Features;
using VoxelEngine.WorldGen.Trees.Shape;

namespace VoxelEngine.Tests.Support;

/// <summary>
/// Synthetic wide-trunk species for the core/shell rules: a registered species copied property
/// for property with a larger <see cref="TreeSpecies.MaxPx"/>, its own girth profile, trunk shape
/// and rim, so its trunk chain grows past 16 px and rasterises shells. The species is never
/// registered; its block ids are the base species' own. Tests assert relations only, never the
/// numbers chosen here.
/// </summary>
internal static class WideTreeSpecies
{
    /// <summary>
    /// <paramref name="baseSpecies"/> with the thickness fields overridden. When
    /// <paramref name="features"/> holds a buttress, <see cref="TreeSpecies.LowestBranchHeight"/>
    /// is raised to at least 1, which the compiler requires of buttress species.
    /// </summary>
    public static TreeSpecies From(TreeSpecies baseSpecies, int maxPx, IReadOnlyList<(float, float)> girth,
        TrunkShape shape, TrunkRim rim = TrunkRim.Block, IReadOnlyList<TreeFeatureSpec>? features = null)
    {
        var feats = features ?? baseSpecies.Features;
        var buttress = false;
        for (var i = 0; i < feats.Count; i++)
            buttress |= feats[i].Feature.Name == "buttress";

        return new TreeSpecies
        {
            Name = baseSpecies.Name + "_wide_" + shape.ToString().ToLowerInvariant(),
            WoodId = baseSpecies.WoodId,
            LeavesId = baseSpecies.LeavesId,
            Logic = baseSpecies.Logic,
            SignalEnergy = baseSpecies.SignalEnergy,
            UpProbability = baseSpecies.UpProbability,
            LowestBranchHeight = buttress ? Math.Max(1, baseSpecies.LowestBranchHeight) : baseSpecies.LowestBranchHeight,
            MaturityStepsMin = baseSpecies.MaturityStepsMin,
            MaturityStepsMax = baseSpecies.MaturityStepsMax,
            TipPx = baseSpecies.TipPx,
            PipeExponent = baseSpecies.PipeExponent,
            MaxPx = maxPx,
            GirthProfile = girth,
            TrunkShape = shape,
            Rim = rim,
            LeafShape = baseSpecies.LeafShape,
            LeafRadius = baseSpecies.LeafRadius,
            LeafHydration = baseSpecies.LeafHydration,
            TipMaxPx = baseSpecies.TipMaxPx,
            LeafMinRadius = baseSpecies.LeafMinRadius,
            LeafFullRadiusPx = baseSpecies.LeafFullRadiusPx,
            Features = feats,
            CrownHeightMin = baseSpecies.CrownHeightMin,
            CrownHeightMax = baseSpecies.CrownHeightMax,
            CrownRadius = baseSpecies.CrownRadius,
            LodProfile = baseSpecies.LodProfile,
            Worldgen = baseSpecies.Worldgen,
        };
    }

    /// <summary>
    /// <paramref name="baseSpecies"/> copied field for field with only the crown-scaling knobs
    /// (<see cref="TreeSpecies.LeafMinRadius"/>, <see cref="TreeSpecies.LeafFullRadiusPx"/>) replaced.
    /// Never registered; its block ids are the base species' own.
    /// </summary>
    internal static TreeSpecies WithLeafScaling(TreeSpecies baseSpecies, int minRadius, int fullRadiusPx) => new()
    {
        Name = baseSpecies.Name + "_scaled",
        WoodId = baseSpecies.WoodId,
        LeavesId = baseSpecies.LeavesId,
        Logic = baseSpecies.Logic,
        SignalEnergy = baseSpecies.SignalEnergy,
        UpProbability = baseSpecies.UpProbability,
        LowestBranchHeight = baseSpecies.LowestBranchHeight,
        MaturityStepsMin = baseSpecies.MaturityStepsMin,
        MaturityStepsMax = baseSpecies.MaturityStepsMax,
        TipPx = baseSpecies.TipPx,
        PipeExponent = baseSpecies.PipeExponent,
        MaxPx = baseSpecies.MaxPx,
        GirthProfile = baseSpecies.GirthProfile,
        TrunkShape = baseSpecies.TrunkShape,
        Rim = baseSpecies.Rim,
        LeafShape = baseSpecies.LeafShape,
        LeafRadius = baseSpecies.LeafRadius,
        LeafHydration = baseSpecies.LeafHydration,
        TipMaxPx = baseSpecies.TipMaxPx,
        LeafMinRadius = minRadius,
        LeafFullRadiusPx = fullRadiusPx,
        Features = baseSpecies.Features,
        CrownHeightMin = baseSpecies.CrownHeightMin,
        CrownHeightMax = baseSpecies.CrownHeightMax,
        CrownRadius = baseSpecies.CrownRadius,
        LodProfile = baseSpecies.LodProfile,
        Worldgen = baseSpecies.Worldgen,
    };

    /// <summary>The first leafy worldgen species (ordinal name order).</summary>
    public static TreeSpecies LeafyBase => StampedTree.WorldgenSpecies.First(s => s.HasLeaves);

    /// <summary>A strong base girth: the trunk foot many times the pipe value, tapering to 1.</summary>
    public static readonly IReadOnlyList<(float, float)> StrongGirth = [(0f, 16f), (0.5f, 6f), (1f, 1f)];

    /// <summary>The leafy base species made wide with <see cref="StrongGirth"/> at the compiler's trunk cap.</summary>
    public static TreeSpecies Default(TrunkShape shape, TrunkRim rim = TrunkRim.Block,
        IReadOnlyList<TreeFeatureSpec>? features = null) =>
        From(LeafyBase, TreeSpeciesCompiler.MaxTrunkPx, StrongGirth, shape, rim, features);

    /// <summary><see cref="Default"/> for every trunk shape.</summary>
    public static IEnumerable<TreeSpecies> AllShapes => Enum.GetValues<TrunkShape>().Select(s => Default(s));
}
