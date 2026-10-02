using System;

namespace silkgltriangle;

/// <summary>
/// Umbrella (stone pine) growth: a tall bare trunk that stops climbing
/// <see cref="CrownLift"/> cells above the species' <c>lowestBranchHeight</c>, topped by limbs
/// that run level and outward, favouring straight runs (<see cref="StraightBonus"/>) and stepping
/// up only now and then (<see cref="ClimbWeight"/>, never twice in a row), so the crown comes out
/// flat and wider than it is tall. Only lowers, zeroes or multiplies the grower's weights and adds
/// to a side the signal already travels: it never raises a weight the grower zeroed (the trunk's
/// sides below <c>lowestBranchHeight</c>, Down), which keeps
/// <see cref="TreeGraph.FromWorld"/>'s root-level feature rule exact. Stateless.
/// </summary>
internal sealed class UmbrellaGrowthLogic : IGrowthLogic
{
    /// <summary>Share of the signal energy a tree may lose to its per-tree roll (height variety).</summary>
    private const float Variation = 0.2f;

    /// <summary>
    /// Cells the trunk may climb above <c>lowestBranchHeight</c>: a trunk node at that height or
    /// higher grows no further up, so the trunk chain tops out there.
    /// </summary>
    internal const int CrownLift = 2;

    /// <summary>Weight added to a limb's own travel direction, so limbs run straight out.</summary>
    internal const float StraightBonus = 1.5f;

    /// <summary>Up weight of a level limb step: limbs climb rarely and never twice in a row.</summary>
    internal const float ClimbWeight = 0.35f;

    /// <inheritdoc />
    public string Name => "umbrella";

    /// <inheritdoc />
    public int TreeEnergy(TreeSpecies species, SplitMix64 rng) =>
        Math.Max(1, species.SignalEnergy - rng.NextInt(0, (int)(species.SignalEnergy * Variation) + 1));

    /// <inheritdoc />
    public void AdjustWeights(in GrowthSignal signal, Span<float> weights)
    {
        if (signal.IsInTrunk)
        {
            if (signal.Height >= signal.Species.LowestBranchHeight + CrownLift)
                weights[TreeDirections.Up] = 0f;
            return;
        }

        if (TreeDirections.IsSide(signal.IncomingDir))
            weights[signal.IncomingDir] += StraightBonus;
        weights[TreeDirections.Up] = TreeDirections.IsVertical(signal.IncomingDir) ? 0f : ClimbWeight;
    }

    /// <inheritdoc />
    public int EnergyAfterTurn(in GrowthSignal signal, int chosenDir, int energy) => energy;
}
