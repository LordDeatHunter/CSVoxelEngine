namespace silkgltriangle;

/// <summary>
/// A post-growth stage a tree species' <c>features[]</c> names (roots, buttress), run by
/// <see cref="TreeGrower.GrowToMaturity(TreeSpecies, int, int, int, ITreeSpace, int)"/> after the
/// final thickening. Implementations must hold no mutable state, because the generation worker
/// threads call them concurrently.
/// </summary>
public interface ITreeFeature
{
    /// <summary>The name a species' feature <c>type</c> refers to.</summary>
    string Name { get; }

    /// <summary>
    /// Applies the feature to a grown graph. It may append nodes with <c>feature: true</c>, and
    /// must only write into cells <paramref name="space"/> reports free and the graph does not
    /// already hold. <paramref name="depth"/> is the species' feature depth in blocks.
    /// </summary>
    void Apply(TreeGraph graph, TreeSpecies species, ITreeSpace space, int depth, SplitMix64 rng);
}
