namespace silkgltriangle;

/// <summary>
/// A tree feature that only reserves its name in <see cref="TreeFeatureRegistry"/> so species
/// JSON naming it validates; the real post-growth stage replaces it in phase 2.
/// </summary>
internal sealed class PlaceholderTreeFeature(string name) : ITreeFeature
{
    /// <inheritdoc />
    public string Name => name;
}
