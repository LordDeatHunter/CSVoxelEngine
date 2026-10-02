using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Shape rules of the umbrella (stone pine) and baobab growth logics, plus the wide-trunk gate:
/// the bounds the logics enforce through zeroed weights (trunk tops, limb reach, climbing limbs
/// never turning back out) and the crown shapes they are for. Species are picked by their
/// logic's name, never by species name, and nothing pins a height, a count or a JSON number.
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
