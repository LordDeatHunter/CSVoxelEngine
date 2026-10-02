using System;

namespace silkgltriangle;

/// <summary>
/// A growth logic that reserves its name in <see cref="GrowthLogicRegistry"/> so species JSON
/// naming it validates, and grows with the neutral default: the full signal energy, the
/// grower's base weights untouched and no energy lost on a turn. Umbrella and baobab keep it
/// until their real logics arrive in phase 7.
/// </summary>
internal sealed class PlaceholderGrowthLogic(string name) : IGrowthLogic
{
    /// <inheritdoc />
    public string Name => name;

    /// <inheritdoc />
    public int TreeEnergy(TreeSpecies species, SplitMix64 rng) => species.SignalEnergy;

    /// <inheritdoc />
    public void AdjustWeights(in GrowthSignal signal, Span<float> weights)
    {
    }

    /// <inheritdoc />
    public int EnergyAfterTurn(in GrowthSignal signal, int chosenDir, int energy) => energy;
}
