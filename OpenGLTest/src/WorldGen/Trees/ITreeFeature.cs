namespace silkgltriangle;

/// <summary>
/// A post-growth stage a tree species' <c>features[]</c> names (roots, buttress). Phase 2 of the
/// tree rework adds the stage itself; for now the interface only carries the name the species
/// JSON is validated against. Implementations must hold no mutable state, because the
/// generation worker threads will call them concurrently.
/// </summary>
public interface ITreeFeature
{
    /// <summary>The name a species' feature <c>type</c> refers to.</summary>
    string Name { get; }
}
