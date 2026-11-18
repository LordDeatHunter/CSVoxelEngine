using System;
using System.Linq;
using VoxelEngine.Chunks;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Trees;

/// <summary>The profile tree tables, deterministic species roll and distinct emergent ownership.</summary>
[Collection("Worldgen shape cache")]
public sealed class DynamicTreeProfileTests
{
    public DynamicTreeProfileTests() => TestContent.EnsureLoaded();

    [Fact]
    public void DynamicGiantHasDistinctRuntimeOwnersAndFitsTheSharedBatchCell()
    {
        var ordinary = TreeSpeciesRegistry.Require("jungle_tree");
        var giant = TreeSpeciesRegistry.Require("jungle_giant");
        Assert.False(giant.Worldgen!.Allows(WorldGenProfile.Legacy));
        Assert.True(giant.Worldgen.Allows(WorldGenProfile.Dynamic));
        Assert.NotEqual(ordinary.WoodId, giant.WoodId);
        Assert.NotEqual(ordinary.LeavesId, giant.LeavesId);
        Assert.Same(giant, TreeSpeciesRegistry.ForBlock(giant.WoodId));
        Assert.Same(giant, TreeSpeciesRegistry.ForBlock(giant.LeavesId));
        Assert.True(TreeGenerator.IsLeaf(giant.LeavesId));
        foreach (var id in new[] { giant.WoodId, giant.LeavesId })
        {
            var name = SaveNames.NameOf(id);
            Assert.NotNull(name);
            Assert.True(SaveNames.TryResolve(name, out var restored));
            Assert.Equal(id, restored);
        }
        Assert.True(2 * TreeRasterizer.ReachOf(giant).Horizontal <= WorldGenerationConfig.TreeCellBlocks);

        try
        {
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Legacy);
            Assert.Same(ordinary, TreeGenerator.SpeciesByBiome[(int)Biome.Jungle]);
            var legacyReach = TreeGenerator.ReachHorizontal;
            var legacyLeaves = TreeGenerator.LeavesBlock;
            Assert.Equal(legacyReach, TreeGenerator.FellPoisonRadiusFor(ordinary.WoodId));
            Assert.True(TreeGenerator.FellPoisonRadiusFor(giant.WoodId)
                >= TreeRasterizer.ReachOf(giant).Horizontal);
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Dynamic);
            Assert.True(TreeGenerator.ReachHorizontal >= TreeRasterizer.ReachOf(giant).Horizontal);
            Assert.True(TreeGenerator.ReachHorizontal > legacyReach);
            Assert.Equal(legacyLeaves, TreeGenerator.LeavesBlock);
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Legacy);
            Assert.Equal(legacyReach, TreeGenerator.ReachHorizontal);
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    [Fact]
    public void DynamicRootChoiceIsDeterministicAndSelectsBothEligibleJungleSpecies()
    {
        var ordinary = TreeSpeciesRegistry.Require("jungle_tree");
        var giant = TreeSpeciesRegistry.Require("jungle_giant");
        var choices = new[] { giant, ordinary }.OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();
        var first = new TreeSpecies[128];
        try
        {
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            for (var i = 0; i < first.Length; i++)
                first[i] = TreeGenerator.ChooseDynamicSpecies(choices, -160 + i * 7, 320 - i * 11);
            Assert.Contains(ordinary, first);
            Assert.Contains(giant, first);

            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            for (var i = 0; i < first.Length; i++)
                Assert.Same(first[i], TreeGenerator.ChooseDynamicSpecies(choices, -160 + i * 7, 320 - i * 11));
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    [Fact]
    public void EmergentGiantGrowsAboveTheOrdinaryJungleCanopyOnFlatGround()
    {
        var ordinary = TreeSpeciesRegistry.Require("jungle_tree");
        var giant = TreeSpeciesRegistry.Require("jungle_giant");
        var observedEmergence = false;
        foreach (var (x, z) in new[] { (0, 0), (37, -12), (-200, 91), (5, 5) })
        {
            var ordinaryGraph = TreeGrower.GrowToMaturity(ordinary, x, 1, z, new VirtualTreeSpace((_, _) => 0), 1337);
            var giantGraph = TreeGrower.GrowToMaturity(giant, x, 1, z, new VirtualTreeSpace((_, _) => 0), 1337);
            observedEmergence |= giantGraph.MaxY > ordinaryGraph.MaxY;
            Assert.True(giantGraph.MaxY <= 1 + TreeRasterizer.ReachOf(giant).Up);
        }
        Assert.True(observedEmergence, "the new giant never rose over an ordinary Jungle tree at the sampled roots");
    }

    [Fact]
    public void DynamicFixedJungleRootUsesTheSameSpeciesInSoloAndBatchGrowth()
    {
        try
        {
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            // S4a established these as snow-free Jungle columns. This is one fixed observation,
            // not an adaptive search or a claim that either contains a natural giant.
            foreach (var (cx, cz) in new[] { (250, 50), (-550, 650) })
            for (var lx = 0; lx < Chunk.Size; lx++)
            for (var lz = 0; lz < Chunk.Size; lz++)
            {
                var x = cx * Chunk.Size + lx;
                var z = cz * Chunk.Size + lz;
                if (!TreeGenerator.TryGetTree(x, z, out var species, out var baseY)) continue;
                var solo = TreeBatches.ShapeOf(x, z);
                var batch = TreeBatches.BatchShapeOf(x, z);
                Assert.Same(species, solo.Species);
                Assert.Same(species, batch.Species);
                Assert.Equal(baseY, solo.BaseY);
                Assert.Equal(baseY, batch.BaseY);
                return;
            }
            Assert.Fail("the fixed S4a Jungle columns contain no Dynamic tree root");
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
    }
}
