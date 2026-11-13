using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.Simulation;

/// <summary>
/// Rules of what a felled trunk leaves (<see cref="TreeStump.TryFind"/>): breaking the root of a wood that names a stump
/// leaves it; a cut higher up on the trunk and a limb leave nothing.
/// Every worldgen species is grown on a flat virtual space and stamped into a headless
/// <see cref="VoxelEngine.Chunks.ChunkManager"/> like the felling tests.
/// </summary>
public sealed class TreeStumpTests
{
    private const int Seed = 4242;
    private const int RootX = 8, RootY = 1, RootZ = 8;

    public TreeStumpTests() => TestContent.EnsureLoaded();

    private static bool Find(StampedTree tree, int node)
    {
        var cell = tree.CellOf(node);
        var (id, state) = tree.Read(((int)cell.X, (int)cell.Y, (int)cell.Z));
        return TreeStump.TryFind(id, state, out _);
    }

    [Fact]
    public void BreakingTheRootLeavesAStump()
    {
        foreach (var species in StampedTree.WorldgenSpecies.Where(s => BlockType.ById[s.WoodId]!.StumpId != 0))
        {
            var tree = StampedTree.Stamp(species, Seed, RootX, RootY, RootZ);
            Assert.True(Find(tree, TreeGraph.Root), species.Name);
        }
    }

    [Fact]
    public void ACutHigherOnTheTrunkLeavesNoStump()
    {
        var checkedAny = false;
        foreach (var species in StampedTree.WorldgenSpecies.Where(s => BlockType.ById[s.WoodId]!.StumpId != 0))
        {
            var tree = StampedTree.Stamp(species, Seed, RootX, RootY, RootZ);
            var trunk = new List<int>();
            TreeRasterizer.TrunkChain(tree.Graph, trunk);
            for (var i = 1; i < trunk.Count; i++)
            {
                checkedAny = true;
                Assert.False(Find(tree, trunk[i]), $"{species.Name}: cut at trunk cell {i}");
            }
        }

        Assert.True(checkedAny, "no stump-leaving species grew a trunk");
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
            Assert.False(Find(tree, limb), species.Name);
        }

        Assert.True(checkedAny, "no species grew a limb");
    }

    [Fact]
    public void EveryLogAStumpTakesCanBeSplitIntoSomething()
    {
        var yielded = new List<ushort>();
        foreach (var block in BlockType.ById.Where(b => b is { StumpId: not 0 }))
        {
            Assert.NotNull(block!.SplitLoot);
            yielded.Clear();
            block.SplitLoot!.Roll(new VoxelEngine.Content.Loot.LootContext(0, 0, 0), yielded);
            Assert.NotEmpty(yielded);
            Assert.DoesNotContain(block.Id, yielded);
        }
    }

    [Fact]
    public void EveryStumpLinkNamesAStumpBlock()
    {
        foreach (var block in BlockType.ById.Where(b => b is { StumpId: not 0 }))
            Assert.True(BlockType.ById[block!.StumpId]!.IsStump, block.Name);
    }
}
