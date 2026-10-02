using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Rules of graph felling (<see cref="TopplingTree.Collect"/>): what falls is the wood that loses
/// every path to an anchor (<see cref="TreeSupport"/>), so on a thin tree cutting the root takes
/// every wood cell, cutting a limb takes exactly that limb, detached wood is never pulled along
/// and drops alone, and the canopy that comes with the wood hugs it; a wide trunk falls only once
/// a whole slice is cut, and only real ground anchors its shells. Every worldgen species is grown on a flat
/// <see cref="VirtualTreeSpace"/> and stamped into a headless <see cref="ChunkManager"/>, so the
/// facts read the same wood and leaf state bytes worldgen writes.
/// </summary>
public sealed class TreeFellingTests
{
    private const int Seed = 4242;
    private const int RootX = 8, RootY = 1, RootZ = 8;

    public TreeFellingTests() => TestContent.EnsureLoaded();

    private static (int X, int Y, int Z) Key(Vector3 cell) => ((int)cell.X, (int)cell.Y, (int)cell.Z);

    /// <summary>A free cell face-adjacent to some wood cell of the tree, off the leaves.</summary>
    private static (int X, int Y, int Z) FreeCellBesideWood(StampedTree tree)
    {
        foreach (var (x, y, z) in tree.Wood.Keys.OrderBy(k => k.Y).ThenBy(k => k.X).ThenBy(k => k.Z))
            for (var dir = 0; dir < TreeDirections.Count; dir++)
            {
                var cell = (x + TreeDirections.Dx[dir], y + TreeDirections.Dy[dir], z + TreeDirections.Dz[dir]);
                if (cell.Item2 >= RootY && !tree.Wood.ContainsKey(cell) && !tree.Leaves.ContainsKey(cell))
                    return cell;
            }

        throw new InvalidOperationException($"{tree.Species.Name} has no free cell beside its wood");
    }

    private static List<TopplingTree.FellBlock> CollectAt(StampedTree tree, Vector3 cell)
    {
        var (id, state) = tree.Read(Key(cell));
        return TopplingTree.Collect(tree.Chunks, cell, id, state);
    }

    [Fact]
    public void CuttingTheRootTakesEveryWoodCell()
    {
        foreach (var species in StampedTree.WorldgenSpecies)
        {
            var tree = StampedTree.Stamp(species, Seed, RootX, RootY, RootZ);
            var detached = FreeCellBesideWood(tree);
            tree.Write(detached, species.WoodId, WoodState.Detached);

            var collected = CollectAt(tree, tree.CellOf(TreeGraph.Root));
            var wood = collected.Where(b => b.Id == species.WoodId).Select(b => Key(b.Cell)).ToHashSet();

            Assert.True(tree.Wood.Keys.ToHashSet().SetEquals(wood),
                $"{species.Name}: cutting the root collected {wood.Count} wood cells of {tree.Wood.Count}");
            Assert.DoesNotContain(detached, wood);
            foreach (var block in collected.Where(b => b.Id == species.WoodId))
                Assert.Equal(tree.Wood[Key(block.Cell)].State, block.State);
        }
    }

    [Fact]
    public void CuttingALimbTakesOnlyItsSubtree()
    {
        var checkedAny = false;
        foreach (var species in StampedTree.WorldgenSpecies)
        {
            var tree = StampedTree.Stamp(species, Seed, RootX, RootY, RootZ);
            var limb = StampedTree.PickLimb(tree.Graph);
            if (limb < 0)
                continue;
            checkedAny = true;

            var expected = StampedTree.SubtreeOf(tree.Graph, limb)
                .Select(i => Key(tree.CellOf(i)))
                .ToHashSet();
            Assert.True(expected.Count < tree.Wood.Count, $"{species.Name}: the picked limb is the whole tree");

            var collected = CollectAt(tree, tree.CellOf(limb));
            var wood = collected.Where(b => b.Id == species.WoodId).Select(b => Key(b.Cell)).ToHashSet();
            Assert.True(expected.SetEquals(wood),
                $"{species.Name}: cutting node {limb} collected {wood.Count} wood cells, its subtree holds {expected.Count}");

            // Collect only reads: every wood cell of the tree still stands as it grew
            foreach (var (cell, (id, state)) in tree.Wood)
                Assert.Equal((id, state), tree.Read(cell));
        }

        Assert.True(checkedAny, "no worldgen species grew a limb with children");
    }

    [Fact]
    public void CollectedLeavesHugTheFallingWood()
    {
        var sawLeaves = false;
        foreach (var species in StampedTree.WorldgenSpecies)
        {
            var tree = StampedTree.Stamp(species, Seed, RootX, RootY, RootZ);
            var cuts = new List<int> { TreeGraph.Root };
            var limb = StampedTree.PickLimb(tree.Graph);
            if (limb >= 0)
                cuts.Add(limb);

            foreach (var cut in cuts)
            {
                var collected = CollectAt(tree, tree.CellOf(cut));
                var wood = collected.Where(b => BlockType.IsTreeWood(b.Id)).Select(b => Key(b.Cell)).ToList();
                Assert.All(wood, cell => Assert.True(tree.Wood.ContainsKey(cell),
                    $"{species.Name}: collected wood at {cell} is not part of the tree"));

                foreach (var block in collected.Where(b => !BlockType.IsTreeWood(b.Id)))
                {
                    Assert.True(TreeGenerator.IsLeaf(block.Id), $"{species.Name}: collected a non-leaf {block.Id}");
                    var (lx, ly, lz) = Key(block.Cell);
                    Assert.Contains(wood, w =>
                        Math.Max(Math.Abs(w.X - lx), Math.Max(Math.Abs(w.Y - ly), Math.Abs(w.Z - lz))) <= 2);
                    Assert.Equal(tree.Leaves[(lx, ly, lz)].State, block.State);
                    sawLeaves = true;
                }
            }
        }

        Assert.True(sawLeaves, "no fell collected a single leaf");
    }

    [Fact]
    public void DetachedWoodDropsAlone()
    {
        foreach (var species in StampedTree.WorldgenSpecies)
        {
            var tree = StampedTree.Stamp(species, Seed, RootX, RootY, RootZ);
            var detached = FreeCellBesideWood(tree);
            tree.Write(detached, species.WoodId, WoodState.Detached);

            // A tree-wood neighbour whose parent pointer lands on the detached cell still does
            // not make it a tree: the detached cell never fells anything
            var (dxn, dyn, dzn) = detached;
            var pointer = (dxn, dyn + 1, dzn);
            if (!tree.Wood.ContainsKey(pointer) && !tree.Leaves.ContainsKey(pointer))
                tree.Write(pointer, species.WoodId, WoodState.Encode(4, TreeDirections.Down, false));

            var cell = new Vector3(dxn, dyn, dzn);
            var collected = TopplingTree.Collect(tree.Chunks, cell, species.WoodId, WoodState.Detached);
            Assert.Equal(new[] { new TopplingTree.FellBlock(cell, species.WoodId, WoodState.Detached) }, collected);
        }
    }

    [Fact]
    public void TheFelledMeshIsCentredOnItsPivot()
    {
        // A crown whose outlying cell pulls the minimum corner more than a chunk from the far
        // side: the cells around the trunk column must still be meshed, the outlier is the one
        // that falls outside the scratch chunk
        var leaves = ContentRegistry.RequireBlock("oak_leaves");
        var state = LeafState.Encode(1, false);
        var pivot = new Vector3(100.5f, 40, 100.5f);
        var near = new Vector3(100 + Chunk.Size / 2 - 2, 41, 100);
        var outlier = new Vector3(100 - Chunk.Size + 2, 41, 100);
        List<TopplingTree.FellBlock> blocks =
        [
            new(new Vector3(100, 40, 100), leaves.Id, state),
            new(near, leaves.Id, state),
            new(outlier, leaves.Id, state),
        ];

        var mesh = TopplingTree.BuildMeshData(blocks, pivot);
        var positions = mesh.Vertices.Concat(mesh.TranslucentVertices).Select(v => v.Position + pivot).ToList();

        bool Meshed(Vector3 cell) => positions.Any(p =>
            p.X >= cell.X - 1e-4f && p.X <= cell.X + 1 + 1e-4f &&
            p.Y >= cell.Y - 1e-4f && p.Y <= cell.Y + 1 + 1e-4f &&
            p.Z >= cell.Z - 1e-4f && p.Z <= cell.Z + 1 + 1e-4f);

        Assert.True(Meshed(blocks[0].Cell), "the trunk-column cell is missing from the felled mesh");
        Assert.True(Meshed(near), "a cell half a chunk from the pivot is missing from the felled mesh");
        Assert.False(Meshed(outlier), "a cell beyond half a chunk on the other side was meshed");
    }

    /// <summary>A wide-trunk species stamped and loaded with real ground (rock) filled into every
    /// free cell one below the root across the tree's footprint, so its root-level shells stand
    /// on ground exactly as they would in the world.</summary>
    private static StampedTree StampWideGrounded(TrunkShape shape)
    {
        var tree = StampedTree.Stamp(WideTreeSpecies.Default(shape), Seed, RootX, RootY, RootZ);
        tree.LoadAround(TreeRasterizer.MaxShellRadius + 2);
        var (min, max) = (tree.Min, tree.Max);
        for (var x = min.X - 1; x <= max.X + 1; x++)
        for (var z = min.Z - 1; z <= max.Z + 1; z++)
        {
            var cell = (x, RootY - 1, z);
            if (!tree.Wood.ContainsKey(cell) && !tree.Leaves.ContainsKey(cell))
                tree.Write(cell, TerrainGenerator.RockBlock, 0);
        }

        Assert.Contains(tree.Wood.Values, w => WoodState.IsShell(w.State));
        return tree;
    }

    /// <summary>
    /// Picks the lowest trunk slice above the root that is ringed by shells, with the slices
    /// below and above it ringed too, and an on-axis shell offset standing in all three, then
    /// cuts every other wood cell of the slice one at a time (each must fell nothing but
    /// itself) and clears it. Returns the slice height and the one cell left standing in it.
    /// </summary>
    private static (int SliceY, (int X, int Y, int Z) Kept) CutSliceButOneShell(StampedTree tree)
    {
        var chain = new List<int>();
        TreeRasterizer.TrunkChain(tree.Graph, chain);
        for (var i = 1; i + 1 < chain.Count; i++)
        {
            var core = tree.Graph[chain[i]];
            if (TreeRasterizer.BlockRadius(core.ThicknessPx) < 1
                || TreeRasterizer.BlockRadius(tree.Graph[chain[i + 1]].ThicknessPx) < 1)
                continue;

            for (var dir = 0; dir < TreeDirections.Count; dir++)
            {
                if (TreeDirections.IsVertical(dir))
                    continue;
                var (ox, oz) = (core.X + TreeDirections.Dx[dir], core.Z + TreeDirections.Dz[dir]);
                var column = Enumerable.Range(core.Y - 1, 3).Select(y => (ox, y, oz)).ToList();
                if (!column.All(c => tree.Wood.TryGetValue(c, out var w) && WoodState.IsShell(w.State)))
                    continue;

                var kept = column[1];
                var slice = tree.Wood.Keys.Where(k => k.Y == core.Y && k != kept)
                    .OrderBy(k => k.X).ThenBy(k => k.Z).ToList();
                Assert.Contains((core.X, core.Y, core.Z), slice);
                foreach (var cell in slice)
                {
                    var collected = CollectAt(tree, new Vector3(cell.X, cell.Y, cell.Z));
                    Assert.True(collected.Count == 1,
                        $"{tree.Species.Name}: cutting {cell} of a slice still holding {kept} felled {collected.Count} blocks");
                    tree.Write(cell, 0, 0);
                }

                return (core.Y, kept);
            }
        }

        throw new InvalidOperationException($"{tree.Species.Name} has no shell-ringed slice above its root");
    }

    [Fact]
    public void CuttingPartOfAWideSliceFellsNothing()
    {
        foreach (var shape in Enum.GetValues<TrunkShape>())
        {
            var tree = StampWideGrounded(shape);
            var (sliceY, kept) = CutSliceButOneShell(tree);

            // Everything above the slice still stands where it grew
            foreach (var (cell, wood) in tree.Wood.Where(kv => kv.Key.Y > sliceY))
                Assert.Equal(wood, tree.Read(cell));
            Assert.Equal(tree.Wood[kept], tree.Read(kept));
        }
    }

    [Fact]
    public void CuttingAWholeSliceFellsEverythingAbove()
    {
        foreach (var shape in Enum.GetValues<TrunkShape>())
        {
            var tree = StampWideGrounded(shape);
            var (sliceY, kept) = CutSliceButOneShell(tree);

            var collected = CollectAt(tree, new Vector3(kept.X, kept.Y, kept.Z));
            var wood = collected.Where(b => BlockType.IsTreeWood(b.Id)).Select(b => Key(b.Cell)).ToHashSet();
            var expected = tree.Wood.Keys.Where(k => k.Y > sliceY).Append(kept).ToHashSet();

            Assert.True(expected.SetEquals(wood),
                $"{tree.Species.Name}: the last cut of slice {sliceY} felled {wood.Count} wood cells, {expected.Count} stood on it");
            foreach (var block in collected.Where(b => BlockType.IsTreeWood(b.Id)))
                Assert.Equal(tree.Wood[Key(block.Cell)].State, block.State);
        }
    }

    [Fact]
    public void CuttingTheRootCoreOfAWideTrunkLeavesItStanding()
    {
        foreach (var shape in Enum.GetValues<TrunkShape>())
        {
            var tree = StampWideGrounded(shape);
            Assert.True(TreeRasterizer.BlockRadius(tree.Graph[TreeGraph.Root].ThicknessPx) >= 1,
                $"{tree.Species.Name}: the root core grew no shells");

            var collected = CollectAt(tree, tree.CellOf(TreeGraph.Root));
            var lifted = collected.Where(b => BlockType.IsTreeWood(b.Id) && b.Cell.Y > RootY).ToList();
            Assert.True(lifted.Count == 0,
                $"{tree.Species.Name}: cutting the root core felled {lifted.Count} wood cells above it");
        }
    }

    [Fact]
    public void OnlyRealGroundAnchorsAShell()
    {
        var oak = WideTreeSpecies.LeafyBase;
        var otherWood = TreeSpeciesRegistry.All.First(s => s.WoodId != oak.WoodId).WoodId;
        // A lone ring stack whose parent (the core) is gone: only what it stands on can hold it
        var shell = WoodState.Encode(WoodState.FullCube, 2, true);
        (byte Id, byte State, TreeSupportResult Expected)[] grounds =
        [
            (oak.LeavesId, LeafState.Encode(1, false), TreeSupportResult.Unsupported),
            (otherWood, WoodState.Encode(WoodState.FullCube, WoodState.ParentRoot, false), TreeSupportResult.Unsupported),
            (oak.WoodId, WoodState.Encode(WoodState.FullCube, WoodState.ParentRoot, false), TreeSupportResult.Unsupported),
            (TerrainGenerator.RockBlock, 0, TreeSupportResult.Supported),
        ];

        foreach (var (groundId, groundState, expected) in grounds)
        {
            var chunks = new ChunkManager(null!);
            var stack = Enumerable.Range(10, 3).Select(y => (8, y, 8)).ToList();
            StampedTree.LoadAround(chunks, stack, 2);
            foreach (var cell in stack)
                StampedTree.WriteCell(chunks, cell, oak.WoodId, shell);
            StampedTree.WriteCell(chunks, (8, 9, 8), groundId, groundState);

            var support = new TreeSupport(WorldGenerationConfig.TreeFellWoodCap);
            var result = support.Search(chunks.TryGetCell, 8, 12, 8,
                hasExcluded: false, 0, 0, 0, unloadedIsUnknown: true);
            Assert.True(expected == result, $"a shell stack on block {groundId} searched {result}, expected {expected}");
            if (result == TreeSupportResult.Unsupported)
                Assert.Equal(stack.ToHashSet(), support.Component.Select(c => (c.X, c.Y, c.Z)).ToHashSet());
        }
    }
}
