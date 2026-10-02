using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Rules of graph felling (<see cref="TopplingTree.Collect"/>): what falls is the cut cell's
/// subtree over the tree graph's child edges, so cutting the root takes every wood cell, cutting
/// a limb takes exactly that limb, detached wood is never pulled along and drops alone, and the
/// canopy that comes with the wood hugs it. Every worldgen species is grown on a flat
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
}
