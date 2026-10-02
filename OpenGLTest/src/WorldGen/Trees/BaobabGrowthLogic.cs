using System;

namespace silkgltriangle;

/// <summary>
/// Baobab growth: a short, massive trunk that forks eagerly (<see cref="ForkBoost"/>) from the
/// species' <c>lowestBranchHeight</c> and stops climbing <see cref="CrownLift"/> cells above it,
/// then a crown of limbs that step out at most <see cref="LimbReach"/> cells from the trunk
/// column (Chebyshev) and, once they climb, only climb, so the crown is a bundle of upright
/// stubby branches over the wide trunk. The bounds are enforced by zeroed weights, never by the
/// energy accounting. Only lowers, zeroes or multiplies the grower's weights: it never raises a
/// weight the grower zeroed (the trunk's sides below <c>lowestBranchHeight</c>, Down), which keeps
/// <see cref="TreeGraph.FromWorld"/>'s root-level buttress rule exact. Stateless.
/// </summary>
internal sealed class BaobabGrowthLogic : IGrowthLogic
{
    /// <summary>Share of the signal energy a tree may lose to its per-tree roll (height variety).</summary>
    private const float Variation = 0.2f;

    /// <summary>
    /// Cells the trunk may climb above <c>lowestBranchHeight</c>: a trunk node at that height or
    /// higher grows no further up, so the trunk chain tops out there.
    /// </summary>
    internal const int CrownLift = 1;

    /// <summary>Multiplier on the trunk's side weights where it may fork.</summary>
    internal const float ForkBoost = 2f;

    /// <summary>Largest Chebyshev horizontal distance of a limb node from the trunk column.</summary>
    internal const int LimbReach = 3;

    /// <summary>Multiplier on a level limb's up weight, so limbs turn upward soon.</summary>
    internal const float ClimbBoost = 2f;

    /// <inheritdoc />
    public string Name => "baobab";

    /// <inheritdoc />
    public int TreeEnergy(TreeSpecies species, SplitMix64 rng) =>
        Math.Max(1, species.SignalEnergy - rng.NextInt(0, (int)(species.SignalEnergy * Variation) + 1));

    /// <inheritdoc />
    public void AdjustWeights(in GrowthSignal signal, Span<float> weights)
    {
        var lowest = signal.Species.LowestBranchHeight;
        if (signal.IsInTrunk)
        {
            if (signal.Height >= lowest)
            {
                for (var dir = 0; dir < TreeDirections.Up; dir++)
                    weights[dir] *= ForkBoost;
            }
            if (signal.Height >= lowest + CrownLift)
                weights[TreeDirections.Up] = 0f;
            return;
        }

        if (signal.IncomingDir == TreeDirections.Up)
        {
            // A climbing limb only climbs: it never turns back out.
            for (var dir = 0; dir < TreeDirections.Up; dir++)
                weights[dir] = 0f;
            return;
        }

        // A level limb may not step past the reach, and leans upward.
        var rootX = signal.Graph.RootX;
        var rootZ = signal.Graph.RootZ;
        for (var dir = 0; dir < TreeDirections.Up; dir++)
        {
            var dx = Math.Abs(signal.NodeX + TreeDirections.Dx[dir] - rootX);
            var dz = Math.Abs(signal.NodeZ + TreeDirections.Dz[dir] - rootZ);
            if (Math.Max(dx, dz) > LimbReach)
                weights[dir] = 0f;
        }
        weights[TreeDirections.Up] *= ClimbBoost;
    }

    /// <inheritdoc />
    public int EnergyAfterTurn(in GrowthSignal signal, int chosenDir, int energy) => energy;
}
