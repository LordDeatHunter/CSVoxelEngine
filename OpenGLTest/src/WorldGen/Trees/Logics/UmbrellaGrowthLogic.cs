using System;

namespace VoxelEngine.WorldGen.Trees.Logics;

/// <summary>
/// Umbrella (stone pine) growth: a tall bare trunk that stops climbing
/// <see cref="CrownLift"/> cells above the species' <c>lowestBranchHeight</c> and splits there
/// into a few bare limbs that spread out and up into the flat crown: at most
/// <see cref="LimbsPerTrunkNode"/> per trunk node, one per side over the whole trunk (a limb
/// stacked over another would have its rise blocked and be buried in the upper one's pads), and
/// the nodes below the trunk's top share only what the top leaves over, so the top always forks
/// instead of ending as a leaf-capped stub over the limbs.
/// <para>
/// Inside the <b>bare zone</b>, fewer than <see cref="BareReach"/> cells (Chebyshev) from the
/// trunk column, a limb neither forks nor turns aside: it runs straight out along the axis it left
/// the trunk on (<see cref="StraightBonus"/>) and climbs at nearly every step it may
/// (<see cref="RiseWeight"/>, never twice in a row), a rising staircase that lifts the pads clear
/// of the fork, so the only childless nodes, and with them the leaf pads, sit out at the
/// limb ends and the wood between the trunk and the pads stays bare. Beyond the zone a limb runs
/// level and outward as before, favouring straight runs and stepping up only now and then
/// (<see cref="ClimbWeight"/>, never twice in a row), and never turns back into the zone, so the
/// crown comes out flat and wider than it is tall.
/// </para>
/// <para>
/// Only lowers or zeroes the grower's weights and adds to weights that are already open: it never
/// raises a weight the grower zeroed (the trunk's sides below <c>lowestBranchHeight</c>, Down),
/// which keeps <see cref="TreeGraph.FromWorld"/>'s root-level feature rule exact. Stateless.
/// </para>
/// </summary>
internal sealed class UmbrellaGrowthLogic : IGrowthLogic
{
    /// <summary>
    /// Share of the signal energy a tree may lose to its per-tree roll (height variety). Small,
    /// because the bare zone spends a fixed share of every limb's energy before the crown starts.
    /// </summary>
    private const float Variation = 0.1f;

    /// <summary>
    /// Cells the trunk may climb above <c>lowestBranchHeight</c>: a trunk node at that height or
    /// higher grows no further up, so the trunk chain tops out there.
    /// </summary>
    internal const int CrownLift = 2;

    /// <summary>Weight added to a limb's own travel direction, so limbs run straight out.</summary>
    internal const float StraightBonus = 1.5f;

    /// <summary>Up weight of a level limb step: limbs climb rarely and never twice in a row.</summary>
    internal const float ClimbWeight = 0.35f;

    /// <summary>
    /// Chebyshev cells from the trunk column inside which a limb neither forks nor turns aside,
    /// so the limb stays one bare run of wood out to its pads.
    /// </summary>
    internal const int BareReach = 5;

    /// <summary>
    /// Up weight of a bare-zone limb step, far above the straight run's, so a limb rises at nearly
    /// every step it may (never twice in a row) and its pads stand clear above the fork.
    /// </summary>
    internal const float RiseWeight = 40.0f;

    /// <summary>Most limbs (non-feature side children) one trunk node carries.</summary>
    internal const int LimbsPerTrunkNode = 2;

    /// <inheritdoc />
    public string Name => "umbrella";

    /// <inheritdoc />
    public int TreeEnergy(TreeSpecies species, SplitMix64 rng) =>
        Math.Max(1, species.SignalEnergy - rng.NextInt(0, (int)(species.SignalEnergy * Variation) + 1));

    /// <inheritdoc />
    public void AdjustWeights(in GrowthSignal signal, Span<float> weights)
    {
        var graph = signal.Graph;
        if (signal.IsInTrunk)
        {
            if (signal.Height >= signal.Species.LowestBranchHeight + CrownLift)
                weights[TreeDirections.Up] = 0f;

            // Below the crown base the grower already closed every side.
            if (signal.Height < signal.Species.LowestBranchHeight)
                return;

            // The limb cap: a trunk node full of limbs only lets pulses follow the ones it has.
            var limbs = LimbSides(graph, signal.Node);
            var full = BitCount(limbs) >= LimbsPerTrunkNode;

            // One limb per side over the whole trunk: a limb stacked over another on the same
            // side would block its rise and bury it in the upper limb's pads. The nodes below the
            // trunk's top level share what the top leaves over, so the top always has sides left
            // and never ends as a bare, leaf-capped stub over the fork.
            var isTop = signal.Height >= signal.Species.LowestBranchHeight + CrownLift;
            var usedElsewhere = 0;
            var belowTop = 0;
            for (var node = TreeGraph.Root; node >= 0; node = graph.ChildIn(node, TreeDirections.Up))
            {
                var sides = LimbSides(graph, node);
                if (node != signal.Node)
                    usedElsewhere |= sides;
                if (graph[node].Y - graph.RootY < signal.Species.LowestBranchHeight + CrownLift)
                    belowTop += BitCount(sides);
            }
            // TreeDirections.Up is also the number of sides (they are directions 0..3).
            if (!isTop && belowTop >= TreeDirections.Up - LimbsPerTrunkNode)
                full = true;

            for (var dir = 0; dir < TreeDirections.Up; dir++)
            {
                if ((limbs & (1 << dir)) == 0 && (full || (usedElsewhere & (1 << dir)) != 0))
                    weights[dir] = 0f;
            }
            return;
        }

        var offX = signal.NodeX - graph.RootX;
        var offZ = signal.NodeZ - graph.RootZ;
        var reach = Math.Max(Math.Abs(offX), Math.Abs(offZ));
        if (reach < BareReach)
        {
            AdjustBare(in signal, weights, offX, offZ);
            return;
        }

        if (TreeDirections.IsSide(signal.IncomingDir))
            weights[signal.IncomingDir] += StraightBonus;
        weights[TreeDirections.Up] = TreeDirections.IsVertical(signal.IncomingDir)
            ? 0f
            : MathF.Min(weights[TreeDirections.Up], ClimbWeight);

        // Outer limbs never fold back into the bare zone, so it keeps no tips and no pads.
        for (var dir = 0; dir < TreeDirections.Up; dir++)
        {
            var nx = offX + TreeDirections.Dx[dir];
            var nz = offZ + TreeDirections.Dz[dir];
            if (Math.Max(Math.Abs(nx), Math.Abs(nz)) < BareReach)
                weights[dir] = 0f;
        }
    }

    /// <summary>
    /// A bare-zone limb node: follow its one existing child, or keep running straight out along the
    /// axis the node lies on from the trunk column, climbing whenever it did not just climb.
    /// </summary>
    private static void AdjustBare(in GrowthSignal signal, Span<float> weights, int offX, int offZ)
    {
        var graph = signal.Graph;

        // No forks: a node that already continues only lets pulses follow that continuation.
        var childDir = -1;
        for (var dir = 0; dir < TreeDirections.Count && childDir < 0; dir++)
        {
            var child = graph.ChildIn(signal.Node, dir);
            if (child >= 0 && !graph[child].IsFeature)
                childDir = dir;
        }
        if (childDir >= 0)
        {
            for (var dir = 0; dir < TreeDirections.Count; dir++)
            {
                if (dir != childDir)
                    weights[dir] = 0f;
            }
            return;
        }

        // The heading: the side pointing away from the trunk column along the node's one offset
        // axis. A limb leaves the trunk along a side and only ever follows that side or rises, so
        // its offset always lies on one axis; -1 (no side open) guards anything else.
        var heading = offZ == 0 && offX != 0 ? SideOf(offX > 0 ? 1 : -1, 0)
            : offX == 0 && offZ != 0 ? SideOf(0, offZ > 0 ? 1 : -1)
            : -1;
        for (var dir = 0; dir < TreeDirections.Up; dir++)
        {
            if (dir != heading)
                weights[dir] = 0f;
        }
        if (heading >= 0 && weights[heading] > 0f)
            weights[heading] += StraightBonus;
        // Off the trunk the grower never zeroes Up, so setting an open Up to the rise weight
        // raises no weight the grower closed.
        weights[TreeDirections.Up] = TreeDirections.IsVertical(signal.IncomingDir) || !(weights[TreeDirections.Up] > 0f)
            ? 0f
            : RiseWeight;
    }

    /// <summary>Bit mask of the sides on which the node carries a limb (a non-feature side child).</summary>
    private static int LimbSides(TreeGraph graph, int node)
    {
        var mask = 0;
        for (var dir = 0; dir < TreeDirections.Up; dir++)
        {
            var child = graph.ChildIn(node, dir);
            if (child >= 0 && !graph[child].IsFeature)
                mask |= 1 << dir;
        }
        return mask;
    }

    private static int BitCount(int mask) => System.Numerics.BitOperations.PopCount((uint)mask);

    /// <summary>The side direction with the given unit X/Z step.</summary>
    private static int SideOf(int dx, int dz)
    {
        for (var dir = 0; dir < TreeDirections.Up; dir++)
        {
            if (TreeDirections.Dx[dir] == dx && TreeDirections.Dz[dir] == dz)
                return dir;
        }
        return -1;
    }

    /// <inheritdoc />
    public int EnergyAfterTurn(in GrowthSignal signal, int chosenDir, int energy) => energy;
}
