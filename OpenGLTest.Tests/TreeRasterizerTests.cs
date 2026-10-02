using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Rules of <see cref="TreeRasterizer"/> and <see cref="LeafCluster"/>: output order, the leaf
/// hydration rule, obstacles, parent codes that decode onto wood, and the core/shell layout of a
/// wide trunk. Every shipped species is checked through <see cref="TreeSpeciesRegistry.All"/>,
/// the shell rules also through the synthetic <see cref="WideTreeSpecies"/>; nothing pins a count
/// or a JSON number.
/// </summary>
public sealed class TreeRasterizerTests
{
    private const int Seed = 1234;

    private static readonly (int X, int Y, int Z)[] Positions =
        [(0, 1, 0), (37, 1, -12), (-200, 1, 91), (5, 1, 5)];

    public TreeRasterizerTests() => TestContent.EnsureLoaded();

    /// <summary>A flat world whose ground top is y = 0, so a root stands at y = 1.</summary>
    private static VirtualTreeSpace Flat() => new((_, _) => 0);

    private static TreeGraph Grow(TreeSpecies species, (int X, int Y, int Z) pos, ITreeSpace space) =>
        TreeGrower.GrowToMaturity(species, pos.X, pos.Y, pos.Z, space, Seed);

    private static long Key(int x, int y, int z) => TreeCellKey.Pack(x, y, z);

    /// <summary>Every shipped species plus the synthetic wide ones, so the shell rules are exercised.</summary>
    private static IEnumerable<TreeSpecies> SpeciesWithWide => TreeSpeciesRegistry.All.Concat(WideTreeSpecies.AllShapes);

    [Fact]
    public void WoodComesBeforeFeaturesBeforeLeaves()
    {
        var shells = 0;
        foreach (var species in SpeciesWithWide)
        foreach (var pos in Positions)
        {
            // Carve a column under the base so species with roots really grow feature nodes.
            var space = new VirtualTreeSpace((_, _) => 0, (x, y, z) => x == pos.X && z == pos.Z && y < pos.Y);
            var graph = Grow(species, pos, space);
            var cells = TreeRasterizer.Rasterize(graph, species, space, out var woodCount);
            Assert.InRange(woodCount, graph.Count, cells.Count);

            // Nodes in index order, growth nodes before feature nodes
            var seenFeature = false;
            for (var i = 0; i < graph.Count; i++)
            {
                var c = cells[i];
                Assert.Equal(species.WoodId, c.Block);
                Assert.Equal(i, graph.NodeAt(c.X, c.Y, c.Z));
                Assert.False(WoodState.IsShell(c.State));
                if (graph[i].IsFeature)
                    seenFeature = true;
                else
                    Assert.False(seenFeature, $"{species.Name}: growth node {i} after a feature node");
            }

            // Then the shells, which are wood but never graph nodes
            for (var i = graph.Count; i < woodCount; i++)
            {
                var c = cells[i];
                Assert.Equal(species.WoodId, c.Block);
                Assert.True(WoodState.IsShell(c.State));
                Assert.Equal(-1, graph.NodeAt(c.X, c.Y, c.Z));
                shells++;
            }

            // Then the leaves
            for (var i = woodCount; i < cells.Count; i++)
            {
                Assert.True(species.HasLeaves);
                Assert.Equal(species.LeavesId, cells[i].Block);
            }
        }
        Assert.True(shells > 0, "no species emitted a shell, so the shell section went untested");
    }

    [Fact]
    public void EveryLeafIsWithinHydrationOfALiveTip()
    {
        foreach (var species in TreeSpeciesRegistry.All)
        foreach (var pos in Positions)
        {
            var space = Flat();
            var graph = Grow(species, pos, space);
            var cells = TreeRasterizer.Rasterize(graph, species, space);

            foreach (var c in cells)
            {
                if (c.Block == species.WoodId)
                    continue;
                Assert.True(species.HasLeaves, $"{species.Name} is leafless but emitted {c}");

                var h = LeafState.Hydration(c.State);
                Assert.InRange(h, 1, LeafState.MaxHydration);

                var expected = int.MinValue;
                var nearest = int.MaxValue;
                for (var t = 0; t < graph.Count; t++)
                {
                    if (!LeafCluster.IsLiveTip(graph, species, t))
                        continue;
                    var tip = graph[t];
                    int dx = c.X - tip.X, dy = c.Y - tip.Y, dz = c.Z - tip.Z;
                    var distance = LeafCluster.Distance(dx, dy, dz);
                    nearest = Math.Min(nearest, distance);
                    if (LeafCluster.InShape(species.LeafShape, species.LeafRadius, dx, dy, dz))
                        expected = Math.Max(expected, species.LeafHydration - distance);
                }

                Assert.Equal(expected, h);
                Assert.True(expected >= 1);
                Assert.Equal(h, LeafCluster.HydrationAt(graph, species, c.X, c.Y, c.Z));
                Assert.True(nearest <= species.LeafHydration - h);
            }
        }
    }

    [Fact]
    public void LeavesNeverOverwriteWoodTerrainOrOccupiedCells()
    {
        foreach (var species in TreeSpeciesRegistry.All)
        foreach (var pos in Positions)
        {
            var rootX = pos.X;
            int Height(int x, int z) => x >= rootX + 2 ? 20 : 0;
            var space = new VirtualTreeSpace(Height);
            for (var y = 1; y <= 16; y++)
            {
                space.Occupy(pos.X - 1, y, pos.Z);
                space.Occupy(pos.X, y, pos.Z - 2);
            }

            var cells = TreeRasterizer.Rasterize(Grow(species, pos, space), species, space);
            var wood = new HashSet<long>();
            foreach (var c in cells)
            {
                if (c.Block == species.WoodId)
                    wood.Add(Key(c.X, c.Y, c.Z));
            }

            var leaves = new HashSet<long>();
            foreach (var c in cells)
            {
                if (c.Block == species.WoodId)
                    continue;
                var key = Key(c.X, c.Y, c.Z);
                Assert.True(leaves.Add(key), $"{species.Name} emitted the leaf {c} twice");
                Assert.DoesNotContain(key, wood);
                Assert.False(space.IsOccupied(c.X, c.Y, c.Z), $"{species.Name} put a leaf in an occupied cell at {c}");
                Assert.True(c.Y > Height(c.X, c.Z), $"{species.Name} put a leaf in terrain at {c}");
            }
        }
    }

    [Fact]
    public void EveryParentCodeDecodesOntoAWoodCell()
    {
        foreach (var species in SpeciesWithWide)
        foreach (var pos in Positions)
        {
            var space = new VirtualTreeSpace((_, _) => 0, (x, y, z) => x == pos.X && z == pos.Z && y < pos.Y);
            var cells = TreeRasterizer.Rasterize(Grow(species, pos, space), species, space);

            var wood = new HashSet<long>();
            foreach (var c in cells)
            {
                if (c.Block == species.WoodId)
                    wood.Add(Key(c.X, c.Y, c.Z));
            }

            var roots = 0;
            foreach (var c in cells)
            {
                if (c.Block != species.WoodId)
                    continue;
                Assert.False(WoodState.IsDetached(c.State));
                if (WoodState.IsRoot(c.State))
                    roots++;
                if (WoodState.TryParentOffset(c.State, out var dx, out var dy, out var dz))
                    Assert.Contains(Key(c.X + dx, c.Y + dy, c.Z + dz), wood);
            }
            Assert.Equal(1, roots);
        }
    }

    [Fact]
    public void EveryLiveTipInOpenAirWearsLeaves()
    {
        foreach (var species in TreeSpeciesRegistry.All)
        {
            if (!species.HasLeaves || species.LeafHydration < 2)
                continue;

            foreach (var pos in Positions)
            {
                var space = Flat();
                var graph = Grow(species, pos, space);
                var leaves = new HashSet<long>();
                // A wide trunk's shells are wood, never leaf room: a tip hemmed in by them is not in open air.
                var shells = new HashSet<long>();
                foreach (var c in TreeRasterizer.Rasterize(graph, species, space))
                {
                    if (c.Block == species.WoodId && WoodState.IsShell(c.State))
                        shells.Add(Key(c.X, c.Y, c.Z));
                    else if (c.Block == species.LeavesId)
                        leaves.Add(Key(c.X, c.Y, c.Z));
                }

                for (var t = 0; t < graph.Count; t++)
                {
                    if (!LeafCluster.IsLiveTip(graph, species, t))
                        continue;
                    var tip = graph[t];
                    var free = 0;
                    var leafy = 0;
                    for (var d = 0; d < TreeDirections.Count; d++)
                    {
                        int x = tip.X + TreeDirections.Dx[d], y = tip.Y + TreeDirections.Dy[d], z = tip.Z + TreeDirections.Dz[d];
                        if (graph.Occupies(x, y, z) || !space.IsFree(x, y, z) || shells.Contains(Key(x, y, z)))
                            continue;
                        free++;
                        if (leaves.Contains(Key(x, y, z)))
                            leafy++;
                    }
                    if (free > 0)
                        Assert.True(leafy > 0, $"{species.Name}: live tip {t} at ({tip.X}, {tip.Y}, {tip.Z}) has no leaves");
                }
            }
        }
    }

    /// <summary>The wood cells of a rasterisation by cell key.</summary>
    private static Dictionary<long, TreeCell> WoodByKey(List<TreeCell> cells, int woodCount)
    {
        var wood = new Dictionary<long, TreeCell>();
        for (var i = 0; i < woodCount; i++)
            wood[Key(cells[i].X, cells[i].Y, cells[i].Z)] = cells[i];
        return wood;
    }

    /// <summary>
    /// Follows a shell's parent codes through the output wood until a trunk-chain node: returns
    /// that node and fills <paramref name="visited"/> with every cell on the way (the shell first,
    /// the core excluded), or -1 when the chain leaves the wood or never reaches the trunk.
    /// </summary>
    private static int CoreOf(TreeGraph graph, HashSet<int> trunk, Dictionary<long, TreeCell> wood, TreeCell shell,
        List<TreeCell> visited)
    {
        visited.Clear();
        var cur = shell;
        for (var guard = 0; guard <= wood.Count; guard++)
        {
            var node = graph.NodeAt(cur.X, cur.Y, cur.Z);
            if (node >= 0 && trunk.Contains(node))
                return node;
            visited.Add(cur);
            if (!WoodState.TryParentOffset(cur.State, out var dx, out var dy, out var dz)
                || !wood.TryGetValue(Key(cur.X + dx, cur.Y + dy, cur.Z + dz), out var next))
                return -1;
            cur = next;
        }
        return -1;
    }

    private static HashSet<int> TrunkSet(TreeGraph graph)
    {
        var chain = new List<int>();
        TreeRasterizer.TrunkChain(graph, chain);
        return chain.ToHashSet();
    }

    [Fact]
    public void ShellsAppearOnlyAroundWideTrunkNodes()
    {
        var shells = 0;
        var visited = new List<TreeCell>();
        foreach (var species in SpeciesWithWide)
        foreach (var pos in Positions)
        {
            var space = Flat();
            var graph = Grow(species, pos, space);
            var cells = TreeRasterizer.Rasterize(graph, species, space, out var woodCount);
            var wood = WoodByKey(cells, woodCount);
            var trunk = TrunkSet(graph);

            for (var i = 0; i < woodCount; i++)
            {
                var c = cells[i];
                if (!WoodState.IsShell(c.State))
                    continue;
                Assert.True(TreeRasterizer.BlockRadius(species.MaxPx) >= 1,
                    $"{species.Name} can never pass one block yet emitted the shell {c}");
                shells++;

                var core = CoreOf(graph, trunk, wood, c, visited);
                Assert.True(core >= 0, $"{species.Name}: shell {c} chains to no trunk-chain node");
                Assert.Equal(c.Y, graph[core].Y);
                Assert.True(TreeRasterizer.BlockRadius(graph[core].ThicknessPx) >= 1,
                    $"{species.Name}: node {core} carries shells at a zero block radius");

                // Anything between the shell and its core is another shell or a full-cube fin at that Y
                foreach (var v in visited)
                {
                    Assert.Equal(c.Y, v.Y);
                    var node = graph.NodeAt(v.X, v.Y, v.Z);
                    if (node >= 0)
                    {
                        Assert.True(graph[node].IsFeature);
                        Assert.Equal(WoodState.FullCube, WoodState.Thickness(v.State));
                    }
                    else
                    {
                        Assert.True(WoodState.IsShell(v.State));
                    }
                }
            }
        }
        Assert.True(shells > 0, "no wide species emitted a shell");
    }

    [Fact]
    public void EveryShellChainsToItsCoreInsideTheFootprint()
    {
        var shells = 0;
        var visited = new List<TreeCell>();
        foreach (var species in WideTreeSpecies.AllShapes)
        foreach (var pos in Positions)
        {
            var space = Flat();
            var graph = Grow(species, pos, space);
            var cells = TreeRasterizer.Rasterize(graph, species, space, out var woodCount);
            var wood = WoodByKey(cells, woodCount);
            var trunk = TrunkSet(graph);

            for (var i = graph.Count; i < woodCount; i++)
            {
                var c = cells[i];
                shells++;
                var core = CoreOf(graph, trunk, wood, c, visited);
                Assert.True(core >= 0, $"{species.Name}: shell {c} chains to no core");
                var n = graph[core];
                Assert.Equal(n.Y, c.Y);
                Assert.Equal(Math.Abs(c.X - n.X) + Math.Abs(c.Z - n.Z), visited.Count);

                var r = TreeRasterizer.BlockRadius(n.ThicknessPx);
                foreach (var v in visited)
                    Assert.True(TreeRasterizer.InFootprint(species.TrunkShape, r, v.X - n.X, v.Z - n.Z),
                        $"{species.Name}: {v} lies outside the radius-{r} footprint of core {core}");
            }
        }
        Assert.True(shells > 0, "no wide species emitted a shell");
    }

    [Fact]
    public void ShellParentPointsOneStepTowardTheCore()
    {
        foreach (var shape in Enum.GetValues<TrunkShape>())
        for (var r = 1; r <= TreeRasterizer.MaxShellRadius; r++)
        {
            var offsets = TreeRasterizer.ShellOffsets(shape, r).ToArray();
            Assert.NotEmpty(offsets);
            var seen = new HashSet<(int, int)>();
            foreach (var o in offsets)
            {
                Assert.Equal(o.ParentDir, TreeRasterizer.ShellParentDir(o.Dx, o.Dz));
                Assert.Equal(0, TreeDirections.Dy[o.ParentDir]);
                int px = o.Dx + TreeDirections.Dx[o.ParentDir], pz = o.Dz + TreeDirections.Dz[o.ParentDir];
                Assert.Equal(Math.Abs(o.Dx) + Math.Abs(o.Dz) - 1, Math.Abs(px) + Math.Abs(pz));

                // The parent is the core or an offset listed earlier (parent before child)
                Assert.True((px == 0 && pz == 0) || seen.Contains((px, pz)),
                    $"{shape} r={r}: parent ({px}, {pz}) of ({o.Dx}, {o.Dz}) is not listed before it");
                Assert.True(seen.Add((o.Dx, o.Dz)), $"{shape} r={r}: offset ({o.Dx}, {o.Dz}) listed twice");
            }
        }
        Assert.True(TreeRasterizer.ShellOffsets(TrunkShape.Circle, 0).IsEmpty);
    }

    [Fact]
    public void CircleFitsInsideSquareAndIsRotationSymmetric()
    {
        for (var r = 1; r <= TreeRasterizer.MaxShellRadius; r++)
        {
            var circle = TreeRasterizer.ShellOffsets(TrunkShape.Circle, r).ToArray().Select(o => (o.Dx, o.Dz)).ToHashSet();
            var square = TreeRasterizer.ShellOffsets(TrunkShape.Square, r).ToArray().Select(o => (o.Dx, o.Dz)).ToHashSet();

            Assert.Subset(square, circle);
            foreach (var (dx, dz) in circle)
            {
                Assert.Contains((-dz, dx), circle);
                Assert.Contains((-dx, dz), circle);
                Assert.Contains((dx, -dz), circle);
                Assert.True(TreeRasterizer.InFootprint(TrunkShape.Circle, r, dx, dz));
            }

            var box = new HashSet<(int, int)>();
            for (var dx = -r; dx <= r; dx++)
            for (var dz = -r; dz <= r; dz++)
                if (dx != 0 || dz != 0)
                    box.Add((dx, dz));
            Assert.True(box.SetEquals(square), $"square r={r} is not the full box minus the core");

            if (r > 1)
            {
                var smaller = TreeRasterizer.ShellOffsets(TrunkShape.Circle, r - 1).ToArray().Select(o => (o.Dx, o.Dz));
                Assert.Subset(circle, smaller.ToHashSet());
            }
        }
    }

    [Fact]
    public void FootprintNeverExceedsTheDiameter()
    {
        var previous = 0;
        for (var d = 0f; d <= TreeSpeciesCompiler.MaxTrunkPx * 2; d += 0.25f)
        {
            var r = TreeRasterizer.BlockRadius(d);
            Assert.True((2 * r + 1) * WoodState.PixelsPerBlock <= MathF.Max(WoodState.PixelsPerBlock, d),
                $"a {d} px trunk gets a {2 * r + 1}-block footprint");
            Assert.True(r >= previous, $"BlockRadius drops at {d} px");
            previous = r;
        }

        Assert.True(TreeRasterizer.MaxShellRadius > 0);
        Assert.Equal(TreeRasterizer.MaxShellRadius, TreeRasterizer.BlockRadius(TreeSpeciesCompiler.MaxTrunkPx));
        for (var r = 1; r <= TreeRasterizer.MaxShellRadius; r++)
        {
            var min = TreeRasterizer.MinDiameterPx(r);
            Assert.Equal(r, TreeRasterizer.BlockRadius(min));
            Assert.Equal(r - 1, TreeRasterizer.BlockRadius(min - 0.25f));
        }
    }

    [Fact]
    public void ShellsNeverLandOnNodesLeavesOrBlockedCells()
    {
        var shells = 0;
        var blockedInFootprint = 0;
        foreach (var species in WideTreeSpecies.AllShapes)
        foreach (var pos in Positions)
        {
            var space = Flat();
            for (var y = pos.Y; y <= pos.Y + 12; y++)
            {
                space.Occupy(pos.X + 1, y, pos.Z + 1);
                space.Occupy(pos.X - 1, y, pos.Z);
                space.Occupy(pos.X, y, pos.Z + 2);
            }

            var graph = Grow(species, pos, space);
            var cells = TreeRasterizer.Rasterize(graph, species, space, out var woodCount);

            var all = new HashSet<long>();
            var wood = new HashSet<long>();
            foreach (var c in cells)
            {
                var key = Key(c.X, c.Y, c.Z);
                Assert.True(all.Add(key), $"{species.Name} emitted the cell {c} twice");
                if (c.Block == species.WoodId)
                    wood.Add(key);
            }

            for (var i = graph.Count; i < woodCount; i++)
            {
                var c = cells[i];
                shells++;
                Assert.False(space.IsOccupied(c.X, c.Y, c.Z), $"{species.Name} put a shell in an occupied cell at {c}");
                Assert.True(WoodState.TryParentOffset(c.State, out var dx, out var dy, out var dz));
                Assert.Contains(Key(c.X + dx, c.Y + dy, c.Z + dz), wood);
            }

            for (var node = TreeGraph.Root; node >= 0; node = graph.ChildIn(node, TreeDirections.Up))
            {
                var n = graph[node];
                var r = TreeRasterizer.BlockRadius(n.ThicknessPx);
                if (r <= 0)
                    continue;
                foreach (var o in TreeRasterizer.ShellOffsets(species.TrunkShape, r))
                {
                    if (space.IsOccupied(n.X + o.Dx, n.Y, n.Z + o.Dz))
                        blockedInFootprint++;
                }
            }
        }
        Assert.True(shells > 0, "no wide species emitted a shell");
        Assert.True(blockedInFootprint > 0, "no occupied cell fell inside a wide footprint, so blocking went untested");
    }

    /// <summary>
    /// A copy of <paramref name="graph"/> with the same nodes, edges and diameters under different
    /// indices: breadth-first from the root, each node's children taken in reverse order.
    /// </summary>
    private static TreeGraph Permuted(TreeGraph graph, out bool reordered)
    {
        var root = graph[TreeGraph.Root];
        var copy = new TreeGraph(root.X, root.Y, root.Z, root.ThicknessPx);
        copy.SetThickness(TreeGraph.Root, root.PipePx, root.ThicknessPx);
        reordered = false;

        var queue = new List<int> { TreeGraph.Root };
        var children = new List<int>();
        for (var head = 0; head < queue.Count; head++)
        {
            var original = graph[queue[head]];
            var at = copy.NodeAt(original.X, original.Y, original.Z);
            children.Clear();
            for (var c = original.FirstChild; c >= 0; c = graph[c].NextSibling)
                children.Add(c);
            children.Reverse();
            foreach (var c in children)
            {
                var n = graph[c];
                var added = copy.AddChild(at, TreeDirections.Opposite(n.ParentDir), n.ThicknessPx, n.IsFeature);
                copy.SetThickness(added, n.PipePx, n.ThicknessPx);
                reordered |= added != c;
                queue.Add(c);
            }
        }

        Assert.Equal(graph.Count, copy.Count);
        return copy;
    }

    [Fact]
    public void ShellEmissionIgnoresNodeOrder()
    {
        var shells = 0;
        var anyReordered = false;
        foreach (var species in WideTreeSpecies.AllShapes)
        foreach (var pos in Positions)
        {
            var space = Flat();
            var graph = Grow(species, pos, space);
            var copy = Permuted(graph, out var reordered);
            anyReordered |= reordered;

            var a = TreeRasterizer.Rasterize(graph, species, space, out var woodA);
            var b = TreeRasterizer.Rasterize(copy, species, space, out var woodB);
            var shellsA = a.Take(woodA).Where(c => WoodState.IsShell(c.State)).ToHashSet();
            var shellsB = b.Take(woodB).Where(c => WoodState.IsShell(c.State)).ToHashSet();
            Assert.True(shellsA.SetEquals(shellsB), $"{species.Name} at {pos}: shells depend on node order");
            shells += shellsA.Count;
        }
        Assert.True(shells > 0, "no wide species emitted a shell");
        Assert.True(anyReordered, "the permuted copies kept every index, so order independence went untested");
    }
}
