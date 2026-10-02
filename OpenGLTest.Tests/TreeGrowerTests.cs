using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Rules of <see cref="TreeGrower"/>: determinism from position and seed, the parent chain, the
/// pipe model and girth, the reach bound, obstacles and the roots feature. Every shipped species
/// is checked through <see cref="TreeSpeciesRegistry.All"/>; nothing pins a height, a count or a
/// JSON number.
/// </summary>
public sealed class TreeGrowerTests
{
    private const int SeedA = 1234;
    private const int SeedB = 987654;

    private static readonly (int X, int Y, int Z)[] Positions =
        [(0, 1, 0), (37, 1, -12), (-200, 1, 91), (5, 1, 5)];

    public TreeGrowerTests() => TestContent.EnsureLoaded();

    /// <summary>A flat world whose ground top is y = 0, so a root stands at y = 1.</summary>
    private static VirtualTreeSpace Flat() => new((_, _) => 0);

    private static TreeGraph Grow(TreeSpecies species, (int X, int Y, int Z) pos, int seed, ITreeSpace? space = null) =>
        TreeGrower.GrowToMaturity(species, pos.X, pos.Y, pos.Z, space ?? Flat(), seed);

    /// <summary>The rasterisation with every cell made relative to the root.</summary>
    private static List<TreeCell> RelativeCells(TreeSpecies species, (int X, int Y, int Z) pos, int seed)
    {
        var space = Flat();
        var cells = TreeRasterizer.Rasterize(Grow(species, pos, seed, space), species, space);
        for (var i = 0; i < cells.Count; i++)
            cells[i] = cells[i] with { X = cells[i].X - pos.X, Y = cells[i].Y - pos.Y, Z = cells[i].Z - pos.Z };
        return cells;
    }

    private static void AssertParentsAtLeastChildren(TreeGraph graph)
    {
        for (var i = 1; i < graph.Count; i++)
        {
            var child = graph[i];
            var parent = graph[child.Parent];
            Assert.True(parent.PipePx >= child.PipePx, $"pipe of node {child.Parent} below its child {i}");
            Assert.True(parent.ThicknessPx >= child.ThicknessPx, $"thickness of node {child.Parent} below its child {i}");
        }
    }

    [Fact]
    public void SameSeedAndPositionGrowTheSameTree()
    {
        foreach (var species in TreeSpeciesRegistry.All)
        foreach (var pos in Positions)
        {
            var first = Flat();
            var second = Flat();
            var a = TreeRasterizer.Rasterize(Grow(species, pos, SeedA, first), species, first);
            var b = TreeRasterizer.Rasterize(Grow(species, pos, SeedA, second), species, second);
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public void DifferentPositionsOrSeedsGenerallyDiffer()
    {
        foreach (var species in TreeSpeciesRegistry.All)
        {
            var reference = RelativeCells(species, Positions[0], SeedA);
            var positionsDiffer = false;
            for (var p = 1; p < Positions.Length && !positionsDiffer; p++)
                positionsDiffer = !reference.SequenceEqual(RelativeCells(species, Positions[p], SeedA));
            Assert.True(positionsDiffer, $"{species.Name} grows the same shape at every position");

            var seedsDiffer = false;
            for (var p = 0; p < Positions.Length && !seedsDiffer; p++)
                seedsDiffer = !RelativeCells(species, Positions[p], SeedA)
                    .SequenceEqual(RelativeCells(species, Positions[p], SeedB));
            Assert.True(seedsDiffer, $"{species.Name} ignores the world seed");
        }
    }

    [Fact]
    public void EveryWoodCellReachesTheRootThroughFaceAdjacentCells()
    {
        foreach (var species in TreeSpeciesRegistry.All)
        foreach (var pos in Positions)
        {
            var graph = Grow(species, pos, SeedA);
            var roots = 0;
            for (var i = 0; i < graph.Count; i++)
            {
                var n = graph[i];
                if (n.Parent < 0)
                {
                    roots++;
                    continue;
                }
                Assert.True(n.Parent < i);
                Assert.True(graph.IsFaceAdjacent(i, n.Parent));

                var at = i;
                var steps = 0;
                while (graph[at].Parent >= 0 && steps <= graph.Count)
                {
                    at = graph[at].Parent;
                    steps++;
                }
                Assert.Equal(TreeGraph.Root, at);
                Assert.True(steps <= graph.Count);
            }
            Assert.Equal(1, roots);
            Assert.True(graph[TreeGraph.Root].Parent < 0);
        }
    }

    [Fact]
    public void AParentIsNeverThinnerThanAnyChild()
    {
        foreach (var species in TreeSpeciesRegistry.All)
        foreach (var pos in Positions)
        {
            var graph = Grow(species, pos, SeedA);
            AssertParentsAtLeastChildren(graph);
            for (var i = 0; i < graph.Count; i++)
            {
                var n = graph[i];
                Assert.InRange(n.ThicknessPx, species.TipPx, species.MaxPx);
                Assert.True(n.PipePx >= species.TipPx);
                Assert.InRange(WoodState.Thickness(TreeRasterizer.WoodStateOf(in n)), 0, WoodState.MaxThickness);
                if (n.Parent >= 0)
                    Assert.True(TreeRasterizer.ThicknessCode(graph[n.Parent].ThicknessPx)
                                >= TreeRasterizer.ThicknessCode(n.ThicknessPx));
            }
        }
    }

    [Fact]
    public void GirthIsAppliedOnceAndNeverSteersGrowth()
    {
        var wood = ContentRegistry.RequireBlock("oak_log").Id;
        var leaves = ContentRegistry.RequireBlock("oak_leaves").Id;
        byte? Resolve(string n) => n switch
        {
            "block/wood" => wood,
            "block/leaf" => leaves,
            _ => null,
        };
        TreeSpeciesDefinition Definition(float[][]? girth) => new()
        {
            Wood = "block/wood",
            Leaves = "block/leaf",
            Growth = new()
            {
                Logic = "oak",
                SignalEnergy = 12,
                UpProbability = 3,
                LowestBranchHeight = 3,
                MaturitySteps = [40, 60],
            },
            Thickness = new() { TipPx = 1, PipeExponent = 2f, MaxPx = 64, GirthProfile = girth },
            LeafCluster = new() { Radius = 2, Hydration = 3, TipMaxPx = 3 },
            Lod = new() { CrownHeight = [4, 8], CrownRadius = 2 },
        };

        var flat = TreeSpeciesCompiler.Compile("girth", Definition(null), "girth-flat.json", Resolve);
        var profiled = TreeSpeciesCompiler.Compile("girth", Definition([[0f, 2f], [1f, 1f]]), "girth-profiled.json", Resolve);

        foreach (var pos in Positions)
        {
            var a = Grow(flat, pos, SeedA);
            var b = Grow(profiled, pos, SeedA);
            Assert.Equal(a.Count, b.Count);
            for (var i = 0; i < a.Count; i++)
            {
                var na = a[i];
                var nb = b[i];
                Assert.Equal((na.X, na.Y, na.Z, na.Parent), (nb.X, nb.Y, nb.Z, nb.Parent));
                Assert.Equal(na.PipePx, nb.PipePx);
                Assert.True(nb.ThicknessPx >= na.ThicknessPx);
                Assert.True(nb.ThicknessPx <= MathF.Max(flat.TipPx, na.PipePx * 2f) + 1e-3f,
                    $"node {i}: girth compounded ({nb.ThicknessPx} from pipe {na.PipePx})");
            }
        }
    }

    [Fact]
    public void GrowthStaysWithinTheSpeciesReach()
    {
        foreach (var species in TreeSpeciesRegistry.All)
        foreach (var pos in Positions)
        {
            var space = Flat();
            var graph = Grow(species, pos, SeedA, space);
            var reach = TreeRasterizer.ReachOf(species);

            for (var i = 0; i < graph.Count; i++)
            {
                var n = graph[i];
                if (n.IsFeature)
                    continue;
                Assert.True(LeafCluster.Distance(n.X - pos.X, n.Y - pos.Y, n.Z - pos.Z) <= species.SignalEnergy);
                Assert.True(n.Y >= graph.RootY);
            }

            foreach (var c in TreeRasterizer.Rasterize(graph, species, space))
            {
                Assert.InRange(c.X - pos.X, -reach.Horizontal, reach.Horizontal);
                Assert.InRange(c.Z - pos.Z, -reach.Horizontal, reach.Horizontal);
                Assert.InRange(c.Y - pos.Y, -reach.Down, reach.Up);
            }
            Assert.True(graph.Count <= species.MaturityStepsMax + 1 + reach.Down);
        }
    }

    [Fact]
    public void APulseAddsAtMostOneNodeAndKeepsThicknessMonotone()
    {
        foreach (var species in TreeSpeciesRegistry.All)
        {
            var space = Flat();
            var graph = new TreeGraph(0, 1, 0, species.TipPx);
            var rng = new SplitMix64(42);
            for (var p = 0; p < 50; p++)
            {
                var before = graph.Count;
                var grew = TreeGrower.Pulse(graph, species, space, rng);
                var added = graph.Count - before;
                Assert.InRange(added, 0, 1);
                Assert.Equal(grew, added == 1);
                AssertParentsAtLeastChildren(graph);
            }
        }
    }

    [Fact]
    public void NoWoodInTerrainOrOccupiedCells()
    {
        foreach (var species in TreeSpeciesRegistry.All)
        foreach (var pos in Positions)
        {
            var rootX = pos.X;
            int Height(int x, int z) => x >= rootX + 2 ? 20 : 0;
            var space = new VirtualTreeSpace(Height);
            for (var y = 1; y <= 16; y++)
                space.Occupy(pos.X - 1, y, pos.Z);

            var cells = TreeRasterizer.Rasterize(Grow(species, pos, SeedA, space), species, space);
            foreach (var c in cells)
            {
                Assert.True(c.Y > Height(c.X, c.Z), $"{species.Name} wrote into terrain at {c}");
                Assert.False(space.IsOccupied(c.X, c.Y, c.Z), $"{species.Name} wrote into an occupied cell at {c}");
            }
        }
    }

    [Fact]
    public void RootsFillOnlyCarvedCellsBelowTheBase()
    {
        foreach (var species in TreeSpeciesRegistry.All)
        {
            var depth = 0;
            for (var f = 0; f < species.Features.Count; f++)
            {
                if (species.Features[f].Feature.Name == "roots")
                    depth = Math.Max(depth, species.Features[f].Depth);
            }
            if (depth == 0)
                continue;

            var pos = Positions[1];
            for (var carved = 0; carved <= depth + 2; carved++)
            {
                var k = carved;
                var space = k == 0
                    ? Flat()
                    : new VirtualTreeSpace((_, _) => 0,
                        (x, y, z) => x == pos.X && z == pos.Z && y < pos.Y && y >= pos.Y - k);
                var graph = Grow(species, pos, SeedA, space);
                var root = graph[TreeGraph.Root];

                var features = 0;
                var previous = TreeGraph.Root;
                for (var i = 0; i < graph.Count; i++)
                {
                    var n = graph[i];
                    if (!n.IsFeature)
                        continue;
                    features++;
                    Assert.Equal(previous, n.Parent);
                    Assert.Equal((pos.X, pos.Y - features, pos.Z), (n.X, n.Y, n.Z));
                    Assert.Equal(TreeDirections.Up, n.ParentDir);
                    Assert.True(WoodState.TryParentOffset(TreeRasterizer.WoodStateOf(in n), out var dx, out var dy, out var dz));
                    Assert.Equal((n.X, n.Y + 1, n.Z), (n.X + dx, n.Y + dy, n.Z + dz));
                    Assert.Equal(root.ThicknessPx, n.ThicknessPx);
                    Assert.False(LeafCluster.IsLiveTip(graph, species, i));
                    previous = i;
                }
                Assert.Equal(Math.Min(k, depth), features);
            }
        }
    }

    [Fact]
    public void EveryShippedSpeciesActuallyGrows()
    {
        foreach (var species in TreeSpeciesRegistry.All)
        foreach (var pos in Positions)
            Assert.True(Grow(species, pos, SeedA).Count > 1, $"{species.Name} did not grow at {pos}");
    }
}
