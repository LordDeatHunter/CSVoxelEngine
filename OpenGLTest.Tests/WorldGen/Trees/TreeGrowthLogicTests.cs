using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Logics;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Trees;

/// <summary>
/// Shape rules of the umbrella (stone pine), baobab and cactus growth logics, plus the wide-trunk
/// gate: the bounds the logics enforce through zeroed weights (trunk tops, limb reach, climbing
/// limbs never turning back out, the umbrella's unforked bare limbs on distinct sides with bare
/// wood under their pads, the cactus's level arm runs turning straight up into prongs that stand
/// clear of the stem and below its top) and the crown shapes they are for. Species are
/// picked by their logic's name, never by species name, and nothing pins a height, a count or a
/// JSON number.
/// </summary>
public sealed class TreeGrowthLogicTests
{
    private static readonly (int X, int Y, int Z, int Seed)[] Samples =
    [
        (0, 1, 0, 11), (37, 1, -12, 222), (-200, 1, 91, 3333), (5, 1, 5, 44444),
        (640, 1, -310, 5), (-77, 1, -77, 66), (1024, 1, 2048, 777), (-512, 1, 300, 8888),
    ];

    public TreeGrowthLogicTests() => TestContent.EnsureLoaded();

    /// <summary>A flat world whose ground top is y = 0, so a root stands at y = 1.</summary>
    private static VirtualTreeSpace Flat() => new((_, _) => 0);

    private static IEnumerable<TreeSpecies> WithLogic(string logic)
    {
        var found = TreeSpeciesRegistry.All.Where(s => s.Logic.Name == logic).ToList();
        Assert.NotEmpty(found);
        return found;
    }

    private static IEnumerable<(TreeGraph Graph, List<TreeCell> Cells, int WoodCount)> Grown(TreeSpecies species)
    {
        foreach (var (x, y, z, seed) in Samples)
        {
            var space = Flat();
            var graph = TreeGrower.GrowToMaturity(species, x, y, z, space, seed);
            var cells = TreeRasterizer.Rasterize(graph, species, space, out var woodCount);
            yield return (graph, cells, woodCount);
        }
    }

    private static List<int> TrunkChain(TreeGraph graph)
    {
        var chain = new List<int>();
        TreeRasterizer.TrunkChain(graph, chain);
        return chain;
    }

    private static int Chebyshev(TreeGraph graph, int i) =>
        Math.Max(Math.Abs(graph[i].X - graph.RootX), Math.Abs(graph[i].Z - graph.RootZ));

    [Fact]
    public void UmbrellaCrownsSpreadWiderThanTheyAreTall()
    {
        foreach (var species in WithLogic("umbrella"))
        foreach (var (graph, cells, _) in Grown(species))
        {
            var crown = cells.Where(c => c.Y >= graph.RootY + species.LowestBranchHeight).ToList();
            Assert.NotEmpty(crown);
            var xSpan = crown.Max(c => c.X) - crown.Min(c => c.X);
            var zSpan = crown.Max(c => c.Z) - crown.Min(c => c.Z);
            var ySpan = crown.Max(c => c.Y) - crown.Min(c => c.Y);
            Assert.True(Math.Max(xSpan, zSpan) > ySpan,
                $"{species.Name} at ({graph.RootX}, {graph.RootZ}): crown {xSpan}x{zSpan} wide, {ySpan} tall");
        }
    }

    [Fact]
    public void UmbrellaTrunksStopClimbingAboveTheCrownBase()
    {
        foreach (var species in WithLogic("umbrella"))
        foreach (var (graph, _, _) in Grown(species))
        {
            var chain = TrunkChain(graph);
            var top = graph[chain[^1]].Y - graph.RootY;
            Assert.True(top <= species.LowestBranchHeight + UmbrellaGrowthLogic.CrownLift,
                $"{species.Name}: trunk climbs to {top}");
        }
    }

    /// <summary>The limb sides of a trunk node: a bit per side holding a non-feature child.</summary>
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

    /// <summary>The node's non-feature children.</summary>
    private static List<int> GrowthChildren(TreeGraph graph, int node)
    {
        var children = new List<int>();
        for (var c = graph[node].FirstChild; c >= 0; c = graph[c].NextSibling)
        {
            if (!graph[c].IsFeature)
                children.Add(c);
        }
        return children;
    }

    [Fact]
    public void UmbrellaLimbsNeverForkNearTheTrunk()
    {
        foreach (var species in WithLogic("umbrella"))
        {
            var bareNodes = 0;
            foreach (var (graph, _, _) in Grown(species))
            {
                var trunk = TrunkChain(graph).ToHashSet();
                for (var i = 0; i < graph.Count; i++)
                {
                    var n = graph[i];
                    if (n.IsFeature || trunk.Contains(i) || Chebyshev(graph, i) >= UmbrellaGrowthLogic.BareReach)
                        continue;
                    bareNodes++;
                    var where = $"{species.Name} at ({graph.RootX}, {graph.RootZ}): node {i} " +
                                $"({n.X - graph.RootX}, {n.Y - graph.RootY}, {n.Z - graph.RootZ})";
                    Assert.True(GrowthChildren(graph, i).Count <= 1, $"{where} forks inside the bare zone");
                    Assert.True((n.X == graph.RootX) != (n.Z == graph.RootZ), $"{where} turned off its axis");
                }
            }
            Assert.True(bareNodes > 0, $"{species.Name} grew no limb out of the trunk");
        }
    }

    [Fact]
    public void UmbrellaLimbsShowBareWoodUnderTheirPads()
    {
        foreach (var species in WithLogic("umbrella"))
        foreach (var (graph, cells, woodCount) in Grown(species))
        {
            var leaves = new HashSet<long>();
            for (var i = woodCount; i < cells.Count; i++)
                leaves.Add(TreeCellKey.Pack(cells[i].X, cells[i].Y, cells[i].Z));
            Assert.NotEmpty(leaves);

            bool IsBare(int node)
            {
                var n = graph[node];
                for (var dir = 0; dir < TreeDirections.Count; dir++)
                {
                    if (leaves.Contains(TreeCellKey.Pack(n.X + TreeDirections.Dx[dir], n.Y + TreeDirections.Dy[dir],
                            n.Z + TreeDirections.Dz[dir])))
                        return false;
                }
                return true;
            }

            var where = $"{species.Name} at ({graph.RootX}, {graph.RootZ})";
            var trunk = TrunkChain(graph);
            var limbs = 0;
            foreach (var t in trunk)
            {
                for (var dir = 0; dir < TreeDirections.Up; dir++)
                {
                    var limb = graph.ChildIn(t, dir);
                    if (limb < 0 || graph[limb].IsFeature)
                        continue;
                    limbs++;

                    // Walk the limb's single path out until it forks or ends: the first cell that
                    // touches a leaf must have bare wood before it.
                    var bare = 0;
                    for (var node = limb; ;)
                    {
                        if (!IsBare(node))
                        {
                            Assert.True(bare >= 1,
                                $"{where}: the limb leaving trunk height {graph[t].Y - graph.RootY} on side {dir} " +
                                "touches leaves at its first cell");
                            break;
                        }
                        bare++;
                        var next = GrowthChildren(graph, node);
                        if (next.Count != 1)
                            break;
                        node = next[0];
                    }
                }
            }
            Assert.True(limbs > 0, $"{where} grew no limbs");

            var lowestTip = int.MaxValue;
            for (var i = 0; i < graph.Count; i++)
            {
                if (LeafCluster.IsLiveTip(graph, species, i))
                    lowestTip = Math.Min(lowestTip, graph[i].Y);
            }
            Assert.True(lowestTip < int.MaxValue, $"{where} has no live tip");
            foreach (var t in trunk)
            {
                if (graph[t].Y < lowestTip - 1)
                    Assert.True(IsBare(t), $"{where}: the trunk at height {graph[t].Y - graph.RootY} touches leaves");
            }
        }
    }

    [Fact]
    public void UmbrellaTrunkNodesCarryAtMostTheirLimbs()
    {
        foreach (var species in WithLogic("umbrella"))
        foreach (var (graph, _, _) in Grown(species))
        {
            var used = 0;
            foreach (var t in TrunkChain(graph))
            {
                var sides = LimbSides(graph, t);
                var where = $"{species.Name} at ({graph.RootX}, {graph.RootZ}): trunk height {graph[t].Y - graph.RootY}";
                Assert.True(System.Numerics.BitOperations.PopCount((uint)sides) <= UmbrellaGrowthLogic.LimbsPerTrunkNode,
                    $"{where} carries more limbs than a trunk node may");
                Assert.True((used & sides) == 0, $"{where} stacks a limb over another one on the same side");
                used |= sides;
            }
        }
    }

    [Fact]
    public void BaobabLimbsStayWithinTheirReach()
    {
        foreach (var species in WithLogic("baobab"))
        foreach (var (graph, _, _) in Grown(species))
        {
            var limbs = 0;
            for (var i = 0; i < graph.Count; i++)
            {
                if (graph[i].IsFeature)
                    continue;
                var d = Chebyshev(graph, i);
                if (d > 0)
                    limbs++;
                Assert.True(d <= BaobabGrowthLogic.LimbReach, $"{species.Name}: node {i} is {d} from the trunk column");
            }
            Assert.True(limbs > 0, $"{species.Name} grew no limbs");
        }
    }

    [Fact]
    public void BaobabLimbsNeverTurnBackOutOnceTheyClimb()
    {
        foreach (var species in WithLogic("baobab"))
        {
            var climbing = 0;
            foreach (var (graph, _, _) in Grown(species))
            {
                var trunk = TrunkChain(graph).ToHashSet();
                for (var i = 0; i < graph.Count; i++)
                {
                    var n = graph[i];
                    if (n.IsFeature || trunk.Contains(i) || n.ParentDir != TreeDirections.Down)
                        continue;
                    climbing++;
                    for (var dir = 0; dir < TreeDirections.Up; dir++)
                        Assert.True(graph.ChildIn(i, dir) < 0, $"{species.Name}: climbing limb node {i} turns out");
                }
            }
            Assert.True(climbing > 0, $"{species.Name} grew no climbing limb");
        }
    }

    [Fact]
    public void BaobabTrunksStopAtTheCrown()
    {
        foreach (var species in WithLogic("baobab"))
        foreach (var (graph, _, _) in Grown(species))
        {
            var chain = TrunkChain(graph);
            var top = graph[chain[^1]].Y - graph.RootY;
            Assert.True(top <= species.LowestBranchHeight + BaobabGrowthLogic.CrownLift,
                $"{species.Name}: trunk climbs to {top}");
        }
    }

    /// <summary>One cactus arm: the stem node it leaves, its side and its nodes in growth order.</summary>
    private readonly record struct CactusArm(int StemNode, int Side, List<int> Nodes);

    /// <summary>
    /// Every arm of a cactus: each non-feature side child of a stem node, walked outward. An arm
    /// never forks, which the walk asserts, so its nodes are a single path.
    /// </summary>
    private static List<CactusArm> CactusArms(TreeGraph graph, TreeSpecies species, List<int> stem)
    {
        var arms = new List<CactusArm>();
        foreach (var stemNode in stem)
        {
            for (var side = 0; side < TreeDirections.Up; side++)
            {
                var start = graph.ChildIn(stemNode, side);
                if (start < 0 || graph[start].IsFeature)
                    continue;

                var nodes = new List<int> { start };
                for (var node = start;;)
                {
                    var next = -1;
                    for (var c = graph[node].FirstChild; c >= 0; c = graph[c].NextSibling)
                    {
                        if (graph[c].IsFeature)
                            continue;
                        Assert.True(next < 0, $"{species.Name} at ({graph.RootX}, {graph.RootZ}): arm node {node} forks");
                        next = c;
                    }
                    if (next < 0)
                        break;
                    nodes.Add(next);
                    node = next;
                }
                arms.Add(new CactusArm(stemNode, side, nodes));
            }
        }
        return arms;
    }

    [Fact]
    public void CactusArmsRunLevelThenClimbParallelToTheStem()
    {
        foreach (var species in WithLogic("cactus"))
        {
            var longestProng = 0;
            foreach (var (graph, _, _) in Grown(species))
            {
                var stem = TrunkChain(graph);
                var where = $"{species.Name} at ({graph.RootX}, {graph.RootZ})";
                foreach (var arm in CactusArms(graph, species, stem))
                {
                    // The growth direction of a node is the opposite of the way back to its parent.
                    var level = 0;
                    while (level < arm.Nodes.Count
                           && TreeDirections.Opposite(graph[arm.Nodes[level]].ParentDir) == arm.Side)
                        level++;
                    for (var i = level; i < arm.Nodes.Count; i++)
                    {
                        Assert.True(graph[arm.Nodes[i]].ParentDir == TreeDirections.Down,
                            $"{where}: arm cell {i} of side {arm.Side} neither runs level along its side nor climbs");
                    }

                    var prong = arm.Nodes.Count - level;
                    Assert.True(level <= CactusGrowthLogic.ArmRunMax, $"{where}: a level run of {level} cells");
                    if (prong == 0)
                        continue;
                    Assert.True(level >= CactusGrowthLogic.ArmRunMin,
                        $"{where}: an arm turns up after only {level} level cells");
                    longestProng = Math.Max(longestProng, prong);

                    var turn = graph[arm.Nodes[level - 1]];
                    for (var i = level; i < arm.Nodes.Count; i++)
                    {
                        var cell = graph[arm.Nodes[i]];
                        Assert.True(cell.X == turn.X && cell.Z == turn.Z, $"{where}: a prong leaves its column");
                        foreach (var s in stem)
                        {
                            var d = Math.Abs(cell.X - graph[s].X) + Math.Abs(cell.Y - graph[s].Y)
                                    + Math.Abs(cell.Z - graph[s].Z);
                            Assert.True(d > 1, $"{where}: a prong cell touches the stem");
                        }
                    }
                }
            }
            Assert.True(longestProng >= CactusGrowthLogic.MinProng,
                $"{species.Name}: no sampled prong reaches {CactusGrowthLogic.MinProng} cells (longest {longestProng})");
        }
    }

    [Fact]
    public void CactusArmsLeaveAtDistinctSidesAndSpacedHeights()
    {
        foreach (var species in WithLogic("cactus"))
        {
            var multiArmed = 0;
            foreach (var (graph, _, _) in Grown(species))
            {
                var arms = CactusArms(graph, species, TrunkChain(graph));
                if (arms.Count > 1)
                    multiArmed++;
                for (var a = 0; a < arms.Count; a++)
                for (var b = a + 1; b < arms.Count; b++)
                {
                    var where = $"{species.Name} at ({graph.RootX}, {graph.RootZ})";
                    Assert.True(arms[a].Side != arms[b].Side, $"{where}: two arms leave on side {arms[a].Side}");
                    var gap = Math.Abs(graph[arms[a].StemNode].Y - graph[arms[b].StemNode].Y);
                    Assert.True(gap >= CactusGrowthLogic.ArmSpacing, $"{where}: two arms {gap} stem cells apart");
                }
            }
            Assert.True(multiArmed > 0, $"{species.Name}: no sampled cactus grew a second arm");
        }
    }

    [Fact]
    public void CactusProngsStayBelowTheStem()
    {
        foreach (var species in WithLogic("cactus"))
        foreach (var (graph, _, _) in Grown(species))
        {
            var stem = TrunkChain(graph);
            var stemTop = graph[stem[^1]].Y;
            foreach (var arm in CactusArms(graph, species, stem))
            {
                var top = graph[arm.Nodes[^1]].Y;
                Assert.True(top < stemTop,
                    $"{species.Name} at ({graph.RootX}, {graph.RootZ}): an arm reaches y {top}, the stem tops out at {stemTop}");
            }
        }
    }

    [Fact]
    public void EveryWideSpeciesGrowsShellsAtMaturity()
    {
        var wide = TreeSpeciesRegistry.All.Where(s => TreeRasterizer.BlockRadius(s.MaxPx) >= 1).ToList();
        Assert.NotEmpty(wide);
        foreach (var species in wide)
        {
            var shells = 0;
            foreach (var (_, cells, woodCount) in Grown(species))
            {
                for (var i = 0; i < woodCount; i++)
                {
                    if (WoodState.IsShell(cells[i].State))
                        shells++;
                }
            }
            Assert.True(shells > 0, $"{species.Name} can grow wide but never grew a shell");
        }
    }
}
