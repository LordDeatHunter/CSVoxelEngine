using System;

namespace silkgltriangle;

/// <summary>
/// Cactus growth: a saguaro "pitchfork". A straight stem throws arms that leave it horizontally,
/// run level for <see cref="ArmRun"/> cells (<see cref="ArmRunMin"/>..<see cref="ArmRunMax"/>),
/// then turn sharply straight up into a prong that runs parallel to the stem with a clear air gap
/// between them, and never climbs level with the stem's current top. A stem node carries at most
/// one arm, arms leave from distinct sides at least <see cref="ArmSpacing"/> stem cells apart, the
/// side opposite an existing arm is favoured (<see cref="OppositeArmBonus"/>), and an arm only
/// leaves where the signal's remaining energy covers its level run plus a <see cref="MinProng"/>
/// prong, so no arm ends as a stub. Turning costs nothing beyond the step itself; the energy the
/// signal has left where it leaves the stem is what bounds the prong.
/// <para>
/// The run length is a pure hash of the root position, the arm's height and its side, drawn
/// without touching the tree's random stream, so the stream order every other rule relies on is
/// unchanged. <see cref="GrowthSignal"/> carries no world seed, so a cactus rooted at a given
/// position gets the same run lengths in every world; that is accepted, since its height, energy
/// and arm sides still vary with the seed through the normal draws.
/// </para>
/// <para>
/// Only lowers or zeroes the grower's weights and adds to weights that are already open: it never
/// raises a weight the grower zeroed (the stem's sides below <c>lowestBranchHeight</c>, Down),
/// which keeps <see cref="TreeGraph.FromWorld"/>'s root-level feature rule exact. Cacti have no
/// leaves (the species' <c>leafCluster</c> is absent), so nothing here deals with them. Stateless.
/// </para>
/// </summary>
internal sealed class CactusGrowthLogic : IGrowthLogic
{
    /// <summary>Share of the signal energy a tree may lose to its per-tree roll (height variety).</summary>
    private const float Variation = 0.2f;

    /// <summary>Fewest level cells an arm runs out from the stem before it turns up.</summary>
    internal const int ArmRunMin = 2;

    /// <summary>Most level cells an arm runs out from the stem before it turns up.</summary>
    internal const int ArmRunMax = 3;

    /// <summary>Prong cells the remaining energy must cover before an arm may leave the stem.</summary>
    internal const int MinProng = 2;

    /// <summary>Least height difference, in stem cells, between two arms.</summary>
    internal const int ArmSpacing = 2;

    /// <summary>Weight added to the side opposite an existing arm, so pairs of arms balance.</summary>
    internal const float OppositeArmBonus = 4f;

    /// <inheritdoc />
    public string Name => "cactus";

    /// <summary>
    /// Level cells the arm leaving the stem of the cactus rooted at (<paramref name="rootX"/>,
    /// <paramref name="rootZ"/>) at height <paramref name="y"/> toward side <paramref name="dir"/>
    /// runs before it turns up: <see cref="ArmRunMin"/>..<see cref="ArmRunMax"/>, a pure function
    /// of the position with no random draw.
    /// </summary>
    internal static int ArmRun(int rootX, int rootZ, int y, int dir) =>
        ArmRunMin + (int)(SplitMix64.Hash(rootX * TreeDirections.Count + dir, rootZ, y)
                          % (ulong)(ArmRunMax - ArmRunMin + 1));

    /// <inheritdoc />
    public int TreeEnergy(TreeSpecies species, SplitMix64 rng) =>
        Math.Max(1, species.SignalEnergy - rng.NextInt(0, (int)(species.SignalEnergy * Variation) + 1));

    /// <inheritdoc />
    public void AdjustWeights(in GrowthSignal signal, Span<float> weights)
    {
        if (signal.IsInTrunk)
        {
            AdjustStem(in signal, weights);
            return;
        }

        var graph = signal.Graph;
        var run = Math.Abs(signal.NodeX - graph.RootX) + Math.Abs(signal.NodeZ - graph.RootZ);
        if (TreeDirections.IsSide(signal.IncomingDir)
            && run < ArmRun(graph.RootX, graph.RootZ, signal.NodeY, signal.IncomingDir))
        {
            // The level run: keep going straight out, no climbing yet.
            for (var dir = 0; dir < TreeDirections.Up; dir++)
            {
                if (dir != signal.IncomingDir)
                    weights[dir] = 0f;
            }
            weights[TreeDirections.Up] = 0f;
            return;
        }

        // The prong: straight up, parallel to the stem, and never level with the stem's top.
        for (var dir = 0; dir < TreeDirections.Up; dir++)
            weights[dir] = 0f;
        weights[TreeDirections.Up] = signal.NodeY + 1 >= StemTopY(graph)
            ? 0f
            : MathF.Min(weights[TreeDirections.Up], 1f);
    }

    /// <summary>The stem's rules: one arm per node, spaced and on distinct sides, energy-gated.</summary>
    private static void AdjustStem(in GrowthSignal signal, Span<float> weights)
    {
        var graph = signal.Graph;

        // One arm per stem node: once a node carries an arm, its other sides stay closed, so an
        // arm never gets a sibling at the same height. The existing arm stays open so a later
        // pulse can follow it out and keep it growing.
        if (ArmDirOf(graph, signal.Node) >= 0)
        {
            for (var dir = 0; dir < TreeDirections.Up; dir++)
            {
                if (graph.ChildIn(signal.Node, dir) < 0)
                    weights[dir] = 0f;
            }
            return;
        }

        // A new arm: no other arm within ArmSpacing - 1 stem cells, and never on a side another
        // arm already took.
        var usedSides = 0;
        var crowded = false;
        for (var node = TreeGraph.Root; node >= 0; node = graph.ChildIn(node, TreeDirections.Up))
        {
            var armDir = ArmDirOf(graph, node);
            if (armDir < 0)
                continue;
            usedSides |= 1 << armDir;
            if (Math.Abs(graph[node].Y - signal.NodeY) < ArmSpacing)
                crowded = true;
        }

        for (var dir = 0; dir < TreeDirections.Up; dir++)
        {
            // Every arm cell costs one step, the step out of the stem being the run's first, so
            // the energy left at this node must cover the whole level run plus a MinProng prong.
            if (crowded || (usedSides & (1 << dir)) != 0
                || signal.Energy < ArmRun(graph.RootX, graph.RootZ, signal.NodeY, dir) + MinProng)
                weights[dir] = 0f;
        }

        // Balance: favor the side opposite an existing arm, but only if it is still open.
        for (var dir = 0; dir < TreeDirections.Up; dir++)
        {
            var opposite = TreeDirections.Opposite(dir);
            if ((usedSides & (1 << dir)) != 0 && weights[opposite] > 0f)
                weights[opposite] += OppositeArmBonus;
        }
    }

    /// <summary>The side of the stem node's arm (a non-feature side child), or -1.</summary>
    private static int ArmDirOf(TreeGraph graph, int node)
    {
        for (var dir = 0; dir < TreeDirections.Up; dir++)
        {
            var child = graph.ChildIn(node, dir);
            if (child >= 0 && !graph[child].IsFeature)
                return dir;
        }
        return -1;
    }

    /// <summary>The Y of the stem's current top: the root, then straight up.</summary>
    private static int StemTopY(TreeGraph graph)
    {
        var node = TreeGraph.Root;
        for (var up = graph.ChildIn(node, TreeDirections.Up); up >= 0; up = graph.ChildIn(node, TreeDirections.Up))
            node = up;
        return graph[node].Y;
    }

    /// <inheritdoc />
    public int EnergyAfterTurn(in GrowthSignal signal, int chosenDir, int energy) => energy;
}
