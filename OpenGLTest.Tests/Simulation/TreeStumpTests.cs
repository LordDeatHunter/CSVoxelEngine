using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.Simulation;

/// <summary>
/// Rules of what a felled trunk leaves (<see cref="TreeStump.TryFind"/>): a cut above the root
/// of a wood that names a stump finds the root, with every trunk cell between it and the cut
/// folded into the fell; the root itself, a limb and a limb leaves nothing.
/// Every worldgen species is grown on a flat virtual space and stamped into a headless
/// <see cref="VoxelEngine.Chunks.ChunkManager"/> like the felling tests.
/// </summary>
public sealed class TreeStumpTests
{
    private const int Seed = 4242;
    private const int RootX = 8, RootY = 1, RootZ = 8;

    public TreeStumpTests() => TestContent.EnsureLoaded();

    private static bool Find(StampedTree tree, int node, out Vector3 root, out List<(Vector3 Cell, ushort Id, byte State)> between)
    {
        var cell = tree.CellOf(node);
        var (id, state) = tree.Read(((int)cell.X, (int)cell.Y, (int)cell.Z));
        return TreeStump.TryFind(tree.Chunks, cell, id, state, out root, out _, out between);
    }

    [Fact]
    public void ACutAboveTheRootLeavesTheRootAndFoldsTheTrunkBetween()
    {
        var checkedAny = false;
        foreach (var species in StampedTree.WorldgenSpecies.Where(s => BlockType.ById[s.WoodId]!.StumpId != 0))
        {
            var tree = StampedTree.Stamp(species, Seed, RootX, RootY, RootZ);
            var trunk = new List<int>();
            TreeRasterizer.TrunkChain(tree.Graph, trunk);
            if (trunk.Count < 3) continue;
            checkedAny = true;

            for (var i = 1; i < trunk.Count; i++)
            {
                Assert.True(Find(tree, trunk[i], out var root, out var between), $"{species.Name}: cut at trunk cell {i}");
                Assert.Equal(tree.CellOf(TreeGraph.Root), root);
                Assert.Equal(i - 1, between.Count);
            }
        }

        Assert.True(checkedAny, "no stump-leaving species grew a trunk of three cells");
    }

    [Fact]
    public void CuttingTheRootLeavesNoStump()
    {
        foreach (var species in StampedTree.WorldgenSpecies.Where(s => BlockType.ById[s.WoodId]!.StumpId != 0))
        {
            var tree = StampedTree.Stamp(species, Seed, RootX, RootY, RootZ);
            Assert.False(Find(tree, TreeGraph.Root, out _, out _), species.Name);
        }
    }

    [Fact]
    public void ALimbLeavesNoStump()
    {
        var checkedAny = false;
        foreach (var species in StampedTree.WorldgenSpecies.Where(s => BlockType.ById[s.WoodId]!.StumpId != 0))
        {
            var tree = StampedTree.Stamp(species, Seed, RootX, RootY, RootZ);
            var limb = StampedTree.PickLimb(tree.Graph);
            if (limb < 0) continue;
            checkedAny = true;

            var trunk = new List<int>();
            TreeRasterizer.TrunkChain(tree.Graph, trunk);
            if (trunk.Contains(limb)) continue;
            Assert.False(Find(tree, limb, out _, out _), species.Name);
        }

        Assert.True(checkedAny, "no species grew a limb");
    }

    [Fact]
    public void EveryStumpLinkNamesAStumpBlock()
    {
        foreach (var block in BlockType.ById.Where(b => b is { StumpId: not 0 }))
            Assert.True(BlockType.ById[block!.StumpId]!.IsStump, block.Name);
    }
}
