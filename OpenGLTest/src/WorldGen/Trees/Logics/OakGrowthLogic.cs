using System;

namespace VoxelEngine.WorldGen.Trees.Logics;

/// <summary>
/// Broadleaf crown growth. The trunk keeps the grower's base weights (it forks only above the
/// species' <c>lowestBranchHeight</c>); once a signal has left the trunk its limb runs level and
/// may step up one cell at a time but never twice in a row, so limbs stair outward and upward
/// into a dome whose leaf blobs form the crown. Stateless.
/// </summary>
internal sealed class OakGrowthLogic : IGrowthLogic
{
    /// <summary>Share of the signal energy a tree may lose to its per-tree roll (height variety).</summary>
    private const float Variation = 0.25f;

    /// <inheritdoc />
    public string Name => "oak";

    /// <inheritdoc />
    public int TreeEnergy(TreeSpecies species, SplitMix64 rng) =>
        Math.Max(1, species.SignalEnergy - rng.NextInt(0, (int)(species.SignalEnergy * Variation) + 1));

    /// <inheritdoc />
    public void AdjustWeights(in GrowthSignal signal, Span<float> weights)
    {
        if (signal.IsInTrunk)
            return;

        // A limb steps up at most one cell before it has to run level again.
        weights[TreeDirections.Up] = TreeDirections.IsVertical(signal.IncomingDir) ? 0f : 1f;
    }

    /// <inheritdoc />
    public int EnergyAfterTurn(in GrowthSignal signal, int chosenDir, int energy) => energy;
}
