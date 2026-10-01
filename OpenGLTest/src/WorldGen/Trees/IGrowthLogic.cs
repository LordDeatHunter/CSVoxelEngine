namespace silkgltriangle;

/// <summary>
/// The direction chooser a tree species' <c>growth.logic</c> names (oak, conifer, cactus, ...).
/// Phase 2 of the tree rework adds the growth-pulse methods; for now the interface only carries
/// the name the species JSON is validated against. Implementations must hold no mutable state,
/// because the generation worker threads will call them concurrently.
/// </summary>
public interface IGrowthLogic
{
    /// <summary>The name a species' <c>growth.logic</c> refers to.</summary>
    string Name { get; }
}
