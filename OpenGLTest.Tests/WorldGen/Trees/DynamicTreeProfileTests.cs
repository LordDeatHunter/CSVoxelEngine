using System;
using System.Linq;
using VoxelEngine.Saves;
using VoxelEngine.Simulation.LeafDecay;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Trees;

/// <summary>The profile tree tables, deterministic species roll and distinct emergent ownership.</summary>
[Collection("Worldgen seed")]
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
        Assert.Equal(4, new LeafDecayManager().ScheduleReach);
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
            Assert.Equal(25, legacyReach);
            Assert.Equal(25, TreeGenerator.ReachUp);
            Assert.Equal(2, TreeGenerator.ReachChunkRadius);
            Assert.Equal(10, TreeGenerator.ProxyReach);
            Assert.Equal(ordinary.LeavesId, TreeGenerator.LeavesBlock);
            Assert.NotEqual(giant.LeavesId, TreeGenerator.LeavesBlock);
            Assert.Equal(legacyReach, TreeGenerator.FellPoisonRadiusFor(ordinary.WoodId));
            Assert.True(TreeGenerator.FellPoisonRadiusFor(giant.WoodId)
                >= TreeRasterizer.ReachOf(giant).Horizontal);
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Dynamic);
            Assert.True(TreeGenerator.ReachHorizontal >= TreeRasterizer.ReachOf(giant).Horizontal);
            Assert.True(TreeGenerator.ReachHorizontal > legacyReach);
            Assert.Equal(ordinary.LeavesId, TreeGenerator.LeavesBlock);
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Legacy);
            Assert.Equal(legacyReach, TreeGenerator.ReachHorizontal);
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    [Fact]
    public void StockUnweightedRowsKeepTheirPrimaryAndJungleOptsIntoWeightedChoice()
    {
        var ordinary = TreeSpeciesRegistry.Require("jungle_tree");
        var giant = TreeSpeciesRegistry.Require("jungle_giant");
        foreach (var biome in Enum.GetValues<Biome>())
        {
            var row = TreeGenerator.DynamicRowFor(biome);
            if (biome == Biome.Jungle)
            {
                Assert.Equal(2, row.Count);
                Assert.Contains(ordinary, row);
                Assert.Contains(giant, row);
                continue;
            }
            var primary = TreeGenerator.SpeciesByBiome[(int)biome];
            if (primary == null) Assert.Empty(row);
            else Assert.Equal([primary], row);
        }
        Assert.Null(TreeSpeciesRegistry.Require("dead_tree").Worldgen!.DynamicWeight);
        Assert.NotNull(ordinary.Worldgen!.DynamicWeight);
        Assert.NotNull(giant.Worldgen!.DynamicWeight);
        Assert.Same(TreeSpeciesRegistry.Require("cactus"), TreeGenerator.DynamicSpeciesFor(Biome.Desert, 0, 0));
    }

    [Fact]
    public void RealJungleRowChoiceIsDeterministicAcrossProfileReset()
    {
        var ordinary = TreeSpeciesRegistry.Require("jungle_tree");
        var giant = TreeSpeciesRegistry.Require("jungle_giant");
        var first = new TreeSpecies[128];
        try
        {
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            for (var i = 0; i < first.Length; i++)
                first[i] = TreeGenerator.DynamicSpeciesFor(Biome.Jungle, -160 + i * 7, 320 - i * 11)!;
            Assert.Contains(ordinary, first);
            Assert.Contains(giant, first);

            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            for (var i = 0; i < first.Length; i++)
                Assert.Same(first[i], TreeGenerator.DynamicSpeciesFor(Biome.Jungle, -160 + i * 7, 320 - i * 11));
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
        foreach (var (x, z) in new[] { (0, 0), (37, -12), (-200, 91), (5, 5) })
        {
            var ordinarySpace = new VirtualTreeSpace((_, _) => 0);
            var giantSpace = new VirtualTreeSpace((_, _) => 0);
            var ordinaryGraph = TreeGrower.GrowToMaturity(ordinary, x, 1, z, ordinarySpace, 1337);
            var giantGraph = TreeGrower.GrowToMaturity(giant, x, 1, z, giantSpace, 1337);
            var ordinaryCells = TreeRasterizer.Rasterize(ordinaryGraph, ordinary, ordinarySpace);
            var giantCells = TreeRasterizer.Rasterize(giantGraph, giant, giantSpace);
            Assert.True(giantCells.Where(c => c.Block == giant.WoodId).Max(c => c.Y)
                > ordinaryCells.Where(c => c.Block == ordinary.WoodId).Max(c => c.Y));
            Assert.True(giantCells.Where(c => c.Block == giant.LeavesId).Max(c => c.Y)
                > ordinaryCells.Where(c => c.Block == ordinary.LeavesId).Max(c => c.Y));
            Assert.True(giantGraph.MaxY <= 1 + TreeRasterizer.ReachOf(giant).Up);
        }
    }
}
