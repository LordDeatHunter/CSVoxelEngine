using System;
using System.IO;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Trees;

/// <summary>Opt-in batch observation of two previously admitted natural Jungle roots.</summary>
[Collection("Worldgen seed")]
public sealed class DynamicJungleBatchPairTests
{
    private const string OptInVariable = "VOXEL_S4B_BATCH_PAIR";

    [OptInBatchPairFact]
    public void FrozenNaturalJunglePairSharesBatchAndGiantEmergesAboveNeighbor()
    {
        const int giantX = 4004;
        const int giantZ = 811;
        const int ordinaryX = 4002;
        const int ordinaryZ = 802;

        using var log = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        try
        {
            TestContent.EnsureLoaded();
            var giant = TreeSpeciesRegistry.Require("jungle_giant");
            var ordinary = TreeSpeciesRegistry.Require("jungle_tree");

            Assert.Equal((62, 12), (TreeGenerator.CellOf(giantX), TreeGenerator.CellOf(giantZ)));
            Assert.Equal((62, 12), (TreeGenerator.CellOf(ordinaryX), TreeGenerator.CellOf(ordinaryZ)));

            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            Assert.True(TreeGenerator.TryGetTree(giantX, giantZ, out var admittedGiant, out var giantBaseY));
            Assert.Same(giant, admittedGiant);
            Assert.Equal(32, giantBaseY);
            Assert.True(TreeGenerator.TryGetTree(ordinaryX, ordinaryZ, out var admittedOrdinary, out var ordinaryBaseY));
            Assert.Same(ordinary, admittedOrdinary);
            Assert.Equal(33, ordinaryBaseY);

            var growsBefore = TreeBatches.GrowsOnThisThread;
            var giantShape = TreeBatches.BatchShapeOf(giantX, giantZ);
            var growsAfterCold = TreeBatches.GrowsOnThisThread;
            Assert.True(growsAfterCold > growsBefore, "The first lookup must build the cold cell batch.");
            var ordinaryShape = TreeBatches.BatchShapeOf(ordinaryX, ordinaryZ);
            Assert.Equal(growsAfterCold, TreeBatches.GrowsOnThisThread);

            var giantLeafTop = CheckAndReport(log, giantShape, giant, giantX, giantBaseY, giantZ);
            var ordinaryLeafTop = CheckAndReport(log, ordinaryShape, ordinary, ordinaryX, ordinaryBaseY, ordinaryZ);
            log.WriteLine($"S4B_BATCH_PAIR cell=62,12 giantLeafTop={giantLeafTop} ordinaryLeafTop={ordinaryLeafTop} growsBefore={growsBefore} growsAfterCold={growsAfterCold} growsAfterHit={TreeBatches.GrowsOnThisThread}");
            Assert.True(giantLeafTop > ordinaryLeafTop,
                $"Observed giant batch crown {giantLeafTop} must exceed its natural ordinary neighbor {ordinaryLeafTop}.");
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    private static int CheckAndReport(StreamWriter log, TreeBatches.TreeShape shape, TreeSpecies species,
        int x, int baseY, int z)
    {
        Assert.Same(species, shape.Species);
        Assert.Equal((x, baseY, z), (shape.X, shape.BaseY, shape.Z));
        Assert.True(shape.WoodCount > 0);
        Assert.True(shape.WoodCount < shape.Cells.Length);

        var reach = TreeRasterizer.ReachOf(species);
        var woodTop = int.MinValue;
        var leafTop = int.MinValue;
        for (var i = 0; i < shape.Cells.Length; i++)
        {
            var cell = shape.Cells[i];
            if (i < shape.WoodCount)
            {
                Assert.Equal(species.WoodId, cell.Block);
                Assert.False(WoodState.IsDetached(cell.State));
                woodTop = Math.Max(woodTop, cell.Y);
            }
            else
            {
                Assert.Equal(species.LeavesId, cell.Block);
                Assert.False(LeafState.IsPersistent(cell.State));
                Assert.Equal((byte)0, (byte)(cell.State & ~(LeafState.HydrationMask | LeafState.PersistentBit)));
                leafTop = Math.Max(leafTop, cell.Y);
            }

            Assert.InRange(cell.X, x - reach.Horizontal, x + reach.Horizontal);
            Assert.InRange(cell.Y, baseY - reach.Down, baseY + reach.Up);
            Assert.InRange(cell.Z, z - reach.Horizontal, z + reach.Horizontal);
            Assert.InRange(cell.Y, shape.MinY, shape.MaxY);
        }

        log.WriteLine($"S4B_BATCH_SHAPE species={species.Name} root={x},{baseY},{z} cells={shape.Cells.Length} wood={shape.WoodCount} leaves={shape.Cells.Length - shape.WoodCount} woodTop={woodTop} leafTop={leafTop} minY={shape.MinY} maxY={shape.MaxY} reach={reach.Horizontal},{reach.Up},{reach.Down}");
        return leafTop;
    }

    /// <summary>Ordinary test runs discover this fact as skipped until the fixed observation is granted.</summary>
    public sealed class OptInBatchPairFactAttribute : FactAttribute
    {
        public OptInBatchPairFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 for the one-cell natural batch observation.";
        }
    }
}
