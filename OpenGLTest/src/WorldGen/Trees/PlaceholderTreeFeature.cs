namespace silkgltriangle;

/// <summary>
/// A tree feature that reserves its name in <see cref="TreeFeatureRegistry"/> so species JSON
/// naming it validates, and does nothing when applied. Buttress keeps it until its real stage
/// arrives in phase 7.
/// </summary>
internal sealed class PlaceholderTreeFeature(string name) : ITreeFeature
{
    /// <inheritdoc />
    public string Name => name;

    /// <inheritdoc />
    public void Apply(TreeGraph graph, TreeSpecies species, ITreeSpace space, int depth, SplitMix64 rng)
    {
    }
}
