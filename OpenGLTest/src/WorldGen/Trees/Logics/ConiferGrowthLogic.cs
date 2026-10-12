using System;

namespace VoxelEngine.WorldGen.Trees.Logics;

/// <summary>
/// Conifer growth: a straight leader with short whorled side branches. The trunk may only fork
/// at whorl heights (every <see cref="WhorlSpacing"/> cells from the species'
/// <c>lowestBranchHeight</c>); a whorl branch runs straight out, and leaving the trunk cuts the
/// signal's remaining energy, so branches shorten with height and the tree comes out conical.
/// Stateless.
/// </summary>
internal sealed class ConiferGrowthLogic : IGrowthLogic
{
    /// <summary>Share of the signal energy a tree may lose to its per-tree roll (height variety).</summary>
    private const float Variation = 0.4f;

    /// <summary>Height step between two whorls of side branches.</summary>
    private const int WhorlSpacing = 2;

    /// <summary>Divisor applied to the remaining energy when a signal turns out of the trunk.</summary>
    private const int SideEnergyDivisor = 3;

    /// <inheritdoc />
    public string Name => "conifer";

    /// <inheritdoc />
    public int TreeEnergy(TreeSpecies species, SplitMix64 rng) =>
        Math.Max(1, species.SignalEnergy - rng.NextInt(0, (int)(species.SignalEnergy * Variation) + 1));

    /// <inheritdoc />
    public void AdjustWeights(in GrowthSignal signal, Span<float> weights)
    {
        if (signal.IsInTrunk)
        {
            int lowest = signal.Species.LowestBranchHeight;
            bool whorl = signal.Height >= lowest && (signal.Height - lowest) % WhorlSpacing == 0;
            if (!whorl)
            {
                for (int dir = 0; dir < TreeDirections.Up; dir++)
                    weights[dir] = 0f;
            }
            return;
        }

        // A whorl branch runs straight out: no climbing, no bending sideways.
        weights[TreeDirections.Up] = 0f;
        for (int dir = 0; dir < TreeDirections.Up; dir++)
        {
            if (dir != signal.IncomingDir)
                weights[dir] = 0f;
        }
    }

    /// <inheritdoc />
    public int EnergyAfterTurn(in GrowthSignal signal, int chosenDir, int energy) =>
        signal.IsInTrunk ? Math.Max(1, energy / SideEnergyDivisor) : energy;
}
