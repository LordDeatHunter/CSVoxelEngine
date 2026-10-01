namespace silkgltriangle;

/// <summary>
/// A growth logic that only reserves its name in <see cref="GrowthLogicRegistry"/> so species
/// JSON naming it validates; the real direction chooser replaces it in phase 2.
/// </summary>
internal sealed class PlaceholderGrowthLogic(string name) : IGrowthLogic
{
    /// <inheritdoc />
    public string Name => name;
}
