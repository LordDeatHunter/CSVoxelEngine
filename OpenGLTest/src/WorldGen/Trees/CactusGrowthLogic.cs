using System;

namespace silkgltriangle;

/// <summary>
/// Cactus growth: a stem plus arms that bend upward. An arm's first cell leaves the stem
/// horizontally and every later cell climbs; a stem node carries at most one arm, so no two arms
/// leave the stem at the same height, and leaving the stem halves the remaining energy so arms stay shorter than
/// the stem. Cacti have no leaves (the species' <c>leafCluster</c> is absent), so nothing here
/// deals with them. Stateless.
/// </summary>
internal sealed class CactusGrowthLogic : IGrowthLogic
{
    /// <summary>Share of the signal energy a tree may lose to its per-tree roll (height variety).</summary>
    private const float Variation = 0.2f;

    /// <inheritdoc />
    public string Name => "cactus";

    /// <inheritdoc />
    public int TreeEnergy(TreeSpecies species, SplitMix64 rng) =>
        Math.Max(1, species.SignalEnergy - rng.NextInt(0, (int)(species.SignalEnergy * Variation) + 1));

    /// <inheritdoc />
    public void AdjustWeights(in GrowthSignal signal, Span<float> weights)
    {
        if (signal.IsInTrunk)
        {
            // One arm per stem node: once a node carries an arm, its other sides stay closed, so
            // an arm never gets a sibling at the same height. The existing arm stays open so a
            // later pulse can follow it out and keep it climbing.
            bool hasArm = false;
            for (int dir = 0; dir < TreeDirections.Up; dir++)
            {
                if (signal.Graph.ChildIn(signal.Node, dir) >= 0)
                    hasArm = true;
            }
            if (hasArm)
            {
                for (int dir = 0; dir < TreeDirections.Up; dir++)
                {
                    if (signal.Graph.ChildIn(signal.Node, dir) < 0)
                        weights[dir] = 0f;
                }
            }
            return;
        }

        // Out of the stem: the arm's first cell was the horizontal one, every later one climbs.
        for (int dir = 0; dir < TreeDirections.Up; dir++)
            weights[dir] = 0f;
        weights[TreeDirections.Up] = 1f;
    }

    /// <inheritdoc />
    public int EnergyAfterTurn(in GrowthSignal signal, int chosenDir, int energy) =>
        signal.IsInTrunk ? Math.Max(1, energy / 2) : energy;
}
