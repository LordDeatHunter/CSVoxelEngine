using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Trees;

/// <summary>
/// Rules of <see cref="TreeGraph.FromWorld"/>: rebuilding a stamped tree's graph from its wood
/// state bytes gives back the stamped topology and thickness, a cut or detached branch is never
/// followed, nodes below the root load as feature nodes, the floor keeps a rebuilt tree from
/// thinning, a wide trunk's shells are never nodes yet its girth survives the round trip, and a
/// walk that reaches an unloaded chunk or starts off a root refuses.
/// </summary>
public sealed class TreeGraphFromWorldTests
{
    private const int Seed = 4242;
    private const int RootX = 8, RootY = 1, RootZ = 8;

    public TreeGraphFromWorldTests() => TestContent.EnsureLoaded();

    private static TreeGraph? Rebuild(StampedTree tree) =>
        TreeGraph.FromWorld(tree.Chunks.TryGetCell, tree.Species, RootX, RootY, RootZ);

    private static (int X, int Y, int Z) CellOf(TreeGraph graph, int i)
    {
        var n = graph[i];
        return (n.X, n.Y, n.Z);
    }

    private static HashSet<(int X, int Y, int Z)> CellsOf(TreeGraph graph) =>
        Enumerable.Range(0, graph.Count).Select(i => CellOf(graph, i)).ToHashSet();

    private static StampedTree StampLoaded(TreeSpecies species)
    {
        var tree = StampedTree.Stamp(species, Seed, RootX, RootY, RootZ);
        tree.LoadAround(1);
        return tree;
    }

    [Fact]
    public void FromWorldRoundTripsAStampedTree()
    {
        foreach (var species in StampedTree.WorldgenSpecies)
        {
            var tree = StampLoaded(species);
            var graph = Rebuild(tree);
            Assert.NotNull(graph);

            Assert.True(CellsOf(graph).SetEquals(tree.NodeWood),
                $"{species.Name}: rebuilt {graph.Count} cells, the stamp holds {tree.NodeWood.Count()} node wood cells");

            for (var i = 0; i < graph.Count; i++)
            {
                var cell = CellOf(graph, i);
                var stamped = tree.Wood[cell].State;
                Assert.Equal(WoodState.Thickness(stamped), TreeRasterizer.ThicknessCode(graph[i].ThicknessPx));
                if (i == TreeGraph.Root)
                    continue;

                Assert.True(graph[i].Parent < i, $"{species.Name}: node {i} indexes below its parent");
                Assert.True(WoodState.TryParentOffset(stamped, out var dx, out var dy, out var dz));
                Assert.Equal((cell.X + dx, cell.Y + dy, cell.Z + dz), CellOf(graph, graph[i].Parent));
            }
        }
    }

    private static StampedTree StampWideLoaded(TreeSpecies species)
    {
        var tree = StampedTree.Stamp(species, Seed, RootX, RootY, RootZ);
        // The girth reads reach the shell radius out from each core.
        tree.LoadAround(TreeRasterizer.MaxShellRadius + 1);
        Assert.True(tree.Wood.Values.Any(w => WoodState.IsShell(w.State)),
            $"{species.Name}: the wide species grew no shell");
        return tree;
    }

    [Fact]
    public void FromWorldRoundTripsAWideTrunk()
    {
        var trunk = new List<int>();
        foreach (var species in WideTreeSpecies.AllShapes)
        {
            var tree = StampWideLoaded(species);
            var graph = Rebuild(tree);
            Assert.NotNull(graph);
            TreeGrower.Thicken(graph, species);

            var cells = TreeRasterizer.Rasterize(graph, species, new VirtualTreeSpace((_, _) => RootY - 1), out var woodCount);
            var wood = new Dictionary<(int X, int Y, int Z), (byte Id, byte State)>();
            for (var i = 0; i < woodCount; i++)
                wood[(cells[i].X, cells[i].Y, cells[i].Z)] = (cells[i].Block, cells[i].State);

            Assert.Equal(tree.Wood.Count, wood.Count);
            foreach (var (cell, stamped) in tree.Wood)
            {
                Assert.True(wood.TryGetValue(cell, out var rebuilt), $"{species.Name}: {cell} is missing after the round trip");
                Assert.Equal(stamped, rebuilt);
            }

            // The recovered floor never encodes thicker than the trunk grew, yet keeps its shell
            // radius wherever the core stands in shells; a wide core's floor (recovered from its
            // shells) never exceeds the grown float. A core whose whole ring is taken by limb
            // nodes has no shell to measure, so only the upper bound holds for it.
            TreeRasterizer.TrunkChain(graph, trunk);
            var wideCores = 0;
            foreach (var i in trunk)
            {
                var grown = tree.Graph[tree.Graph.NodeAt(graph[i].X, graph[i].Y, graph[i].Z)].ThicknessPx;
                var floor = graph[i].FloorPx;
                Assert.True(TreeRasterizer.ThicknessCode(floor) <= TreeRasterizer.ThicknessCode(grown),
                    $"{species.Name}: trunk node {i} floors at {floor} px above its grown {grown} px");
                var n = graph[i];
                var standsInShells = false;
                for (var dx = -1; dx <= 1; dx++)
                for (var dz = -1; dz <= 1; dz++)
                    standsInShells |= tree.Wood.TryGetValue((n.X + dx, n.Y, n.Z + dz), out var w) && WoodState.IsShell(w.State);
                if (!standsInShells)
                {
                    Assert.True(TreeRasterizer.BlockRadius(floor) <= TreeRasterizer.BlockRadius(grown),
                        $"{species.Name}: shell-less trunk node {i} floors wider than it grew");
                    continue;
                }
                Assert.Equal(TreeRasterizer.BlockRadius(grown), TreeRasterizer.BlockRadius(floor));
                if (TreeRasterizer.BlockRadius(grown) < 1)
                    continue;
                wideCores++;
                Assert.True(floor <= grown, $"{species.Name}: wide core {i} floors at {floor} px above its grown {grown} px");
            }
            Assert.True(wideCores > 0, $"{species.Name}: no trunk node grew wide");
        }
    }

    [Fact]
    public void ShellsAreNeverGraphNodes()
    {
        foreach (var species in WideTreeSpecies.AllShapes)
        {
            var tree = StampWideLoaded(species);
            var graph = Rebuild(tree);
            Assert.NotNull(graph);

            for (var i = 0; i < graph.Count; i++)
                Assert.False(WoodState.IsShell(tree.Read(CellOf(graph, i)).State),
                    $"{species.Name}: node {i} was rebuilt on a shell cell");
        }
    }

    [Fact]
    public void FromWorldThenThickenReproducesTheStampedState()
    {
        foreach (var species in StampedTree.WorldgenSpecies)
        {
            var tree = StampLoaded(species);
            var graph = Rebuild(tree);
            Assert.NotNull(graph);
            TreeGrower.Thicken(graph, species);

            for (var i = 0; i < graph.Count; i++)
                Assert.Equal(tree.Wood[CellOf(graph, i)].State, TreeRasterizer.WoodStateOf(in graph[i]));
        }
    }

    [Fact]
    public void ARebuiltCutTreeNeverThins()
    {
        var checkedAny = false;
        foreach (var species in StampedTree.WorldgenSpecies)
        {
            var tree = StampLoaded(species);
            var limb = StampedTree.PickLimb(tree.Graph);
            if (limb < 0)
                continue;
            checkedAny = true;

            tree.Write(CellOf(tree.Graph, limb), 0, 0);
            var graph = Rebuild(tree);
            Assert.NotNull(graph);
            TreeGrower.Thicken(graph, species);

            for (var i = 0; i < graph.Count; i++)
            {
                var stamped = WoodState.Thickness(tree.Wood[CellOf(graph, i)].State);
                Assert.True(TreeRasterizer.ThicknessCode(graph[i].ThicknessPx) >= stamped,
                    $"{species.Name}: node {i} thinned below its stamped code {stamped}");
            }
        }

        Assert.True(checkedAny, "no worldgen species grew a limb with children");
    }

    [Fact]
    public void ACutBranchStaysOutOfTheRebuiltGraph()
    {
        var checkedAny = false;
        foreach (var species in StampedTree.WorldgenSpecies)
        {
            var tree = StampLoaded(species);
            var limb = StampedTree.PickLimb(tree.Graph);
            if (limb < 0)
                continue;
            checkedAny = true;

            var subtree = StampedTree.SubtreeOf(tree.Graph, limb).Select(i => CellOf(tree.Graph, i)).ToHashSet();
            tree.Write(CellOf(tree.Graph, limb), 0, 0);
            var graph = Rebuild(tree);
            Assert.NotNull(graph);

            var rebuilt = CellsOf(graph);
            Assert.True(rebuilt.SetEquals(tree.Wood.Keys.Where(c => !subtree.Contains(c))),
                $"{species.Name}: the rebuilt graph is not the stamp minus the cut subtree");
        }

        Assert.True(checkedAny, "no worldgen species grew a limb with children");
    }

    [Fact]
    public void DetachedWoodIsNeverFollowed()
    {
        var checkedAny = false;
        foreach (var species in StampedTree.WorldgenSpecies)
        {
            var tree = StampLoaded(species);
            var limb = StampedTree.PickLimb(tree.Graph);
            if (limb < 0)
                continue;
            checkedAny = true;

            var cell = CellOf(tree.Graph, limb);
            var subtree = StampedTree.SubtreeOf(tree.Graph, limb).Select(i => CellOf(tree.Graph, i)).ToHashSet();
            var (id, state) = tree.Wood[cell];
            tree.Write(cell, id, WoodState.WithParent(state, WoodState.ParentDetached));

            var graph = Rebuild(tree);
            Assert.NotNull(graph);
            Assert.Empty(CellsOf(graph).Intersect(subtree));
        }

        Assert.True(checkedAny, "no worldgen species grew a limb with children");
    }

    [Fact]
    public void NodesBelowTheRootLoadAsFeatureNodes()
    {
        var species = StampedTree.WorldgenSpecies.First();
        var chunks = new ChunkManager(null!);
        var cells = new List<(int X, int Y, int Z)>();
        const int x = 5, y = 6, z = 5, reach = 3;

        StampedTree.WriteCell(chunks, (x, y, z), species.WoodId, WoodState.Encode(2, WoodState.ParentRoot, false));
        cells.Add((x, y, z));
        for (var d = 1; d <= reach; d++)
        {
            StampedTree.WriteCell(chunks, (x, y + d, z), species.WoodId, WoodState.Encode(1, TreeDirections.Down, false));
            StampedTree.WriteCell(chunks, (x, y - d, z), species.WoodId, WoodState.Encode(1, TreeDirections.Up, false));
            cells.Add((x, y + d, z));
            cells.Add((x, y - d, z));
        }
        StampedTree.LoadAround(chunks, cells, 1);

        var graph = TreeGraph.FromWorld(chunks.TryGetCell, species, x, y, z);
        Assert.NotNull(graph);
        Assert.True(CellsOf(graph).SetEquals(cells));
        for (var i = 0; i < graph.Count; i++)
            Assert.Equal(graph[i].Y < y, graph[i].IsFeature);
    }

    [Fact]
    public void AnUnloadedNeighborRefusesTheRebuild()
    {
        var species = StampedTree.WorldgenSpecies.First();
        var chunks = new ChunkManager(null!);
        const int x = Chunk.Size - 1, y = 8, z = 8;
        var cells = new List<(int X, int Y, int Z)> { (x, y, z), (x + 1, y, z) };
        StampedTree.WriteCell(chunks, (x, y, z), species.WoodId, WoodState.Encode(2, WoodState.ParentRoot, false));
        // The child across the +X border points -X at the root.
        var west = Enumerable.Range(0, TreeDirections.Count).First(d => TreeDirections.Dx[d] == -1);
        StampedTree.WriteCell(chunks, (x + 1, y, z), species.WoodId, WoodState.Encode(1, west, false));
        StampedTree.LoadAround(chunks, cells, 1);
        Assert.NotNull(TreeGraph.FromWorld(chunks.TryGetCell, species, x, y, z));

        Assert.True(chunks.Chunks.TryRemove(new Vector3(1, 0, 0), out _));
        Assert.Null(TreeGraph.FromWorld(chunks.TryGetCell, species, x, y, z));
    }

    [Fact]
    public void ANonRootCellRebuildsNothing()
    {
        var species = StampedTree.WorldgenSpecies.First();
        var tree = StampLoaded(species);
        var child = tree.Graph.ChildIn(TreeGraph.Root, TreeDirections.Up);
        Assert.True(child > 0, $"{species.Name} grew no trunk cell above its root");

        var (cx, cy, cz) = CellOf(tree.Graph, child);
        Assert.Null(TreeGraph.FromWorld(tree.Chunks.TryGetCell, species, cx, cy, cz));

        var air = tree.Wood.Keys.Concat(tree.Leaves.Keys).Max(c => c.Y) + 1;
        Assert.Null(TreeGraph.FromWorld(tree.Chunks.TryGetCell, species, RootX, air, RootZ));
    }
}
