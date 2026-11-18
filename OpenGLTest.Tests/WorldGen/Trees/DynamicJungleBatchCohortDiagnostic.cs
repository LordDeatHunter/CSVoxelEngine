using System;
using System.IO;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Trees;

/// <summary>Opt-in observation of every returned tree in one frozen natural batch cell.</summary>
[Collection("Worldgen seed")]
public sealed class DynamicJungleBatchCohortDiagnostic
{
    private const string OptInVariable = "VOXEL_S4B_BATCH_COHORT_DIAGNOSTIC";

    [OptInCohortFact]
    [Trait("Purpose", "S4bFixedCellCohortDiagnostic")]
    public void ReportsEveryTreeAndEmergentGiantInFrozenBatchCell()
    {
        const int cellX = 62;
        const int cellZ = 12;
        var batchCalls = 0;
        var rows = 0;
        var cellScans = 0L;
        var giantRows = 0;
        var aboveBoundGiantRows = 0;
        var firstAbove = "none";
        var foundArchivedGiant = false;
        var foundArchivedOrdinary = false;
        using var log = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };

        try
        {
            TestContent.EnsureLoaded();
            var giant = TreeSpeciesRegistry.Require("jungle_giant");
            var ordinary = TreeSpeciesRegistry.Require("jungle_tree");
            var ordinaryUp = TreeRasterizer.ReachOf(ordinary).Up;
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);

            var growsBefore = TreeBatches.GrowsOnThisThread;
            batchCalls++;
            var batch = TreeBatches.BatchOf(cellX, cellZ);
            var growsAfter = TreeBatches.GrowsOnThisThread;
            log.WriteLine($"S4B_COHORT_BATCH cell={cellX},{cellZ} shapes={batch.Trees.Length} minY={batch.MinY} maxY={batch.MaxY} growsBefore={growsBefore} growsAfter={growsAfter} growDelta={growsAfter - growsBefore} ordinaryUp={ordinaryUp}");

            for (var index = 0; index < batch.Trees.Length; index++)
            {
                var shape = batch.Trees[index];
                var species = shape.Species;
                var reach = TreeRasterizer.ReachOf(species);
                var woodTop = int.MinValue;
                var leafTop = int.MinValue;
                var leafMinX = int.MaxValue;
                var leafMaxX = int.MinValue;
                var leafMinZ = int.MaxValue;
                var leafMaxZ = int.MinValue;
                string? invalid = null;

                for (var i = 0; i < shape.Cells.Length; i++)
                {
                    var c = shape.Cells[i];
                    cellScans++;
                    if (i < shape.WoodCount)
                    {
                        woodTop = Math.Max(woodTop, c.Y);
                        if (c.Block != species.WoodId || WoodState.IsDetached(c.State))
                            invalid ??= $"wood block/state at cell {i}";
                    }
                    else
                    {
                        leafTop = Math.Max(leafTop, c.Y);
                        leafMinX = Math.Min(leafMinX, c.X);
                        leafMaxX = Math.Max(leafMaxX, c.X);
                        leafMinZ = Math.Min(leafMinZ, c.Z);
                        leafMaxZ = Math.Max(leafMaxZ, c.Z);
                        if (c.Block != species.LeavesId || LeafState.IsPersistent(c.State)
                            || (c.State & ~(LeafState.HydrationMask | LeafState.PersistentBit)) != 0)
                            invalid ??= $"leaf block/state at cell {i}";
                    }

                    if (c.X < shape.X - reach.Horizontal || c.X > shape.X + reach.Horizontal
                        || c.Y < shape.BaseY - reach.Down || c.Y > shape.BaseY + reach.Up
                        || c.Z < shape.Z - reach.Horizontal || c.Z > shape.Z + reach.Horizontal
                        || c.Y < shape.MinY || c.Y > shape.MaxY)
                        invalid ??= $"source reach or shape bounds at cell {i}";
                }

                var isGiant = ReferenceEquals(species, giant);
                var archivedPair = shape.X == 4004 && shape.Z == 811 ? "giant"
                    : shape.X == 4002 && shape.Z == 802 ? "ordinary" : "none";
                var bound = shape.BaseY + ordinaryUp;
                var aboveBound = isGiant && leafTop > bound;
                var leafBox = leafTop == int.MinValue ? "none"
                    : $"{leafMinX},{leafMaxX},{leafMinZ},{leafMaxZ}";
                log.WriteLine($"S4B_COHORT_ROW index={index} species={species.Name} root={shape.X},{shape.BaseY},{shape.Z} cells={shape.Cells.Length} wood={shape.WoodCount} leaves={shape.Cells.Length - shape.WoodCount} woodTop={woodTop} leafTop={(leafTop == int.MinValue ? "none" : leafTop)} minY={shape.MinY} maxY={shape.MaxY} reach={reach.Horizontal},{reach.Up},{reach.Down} leafBoxXZ={leafBox} archivedPair={archivedPair} giantRelativeLeafTop={(isGiant && leafTop != int.MinValue ? (leafTop - shape.BaseY).ToString() : "none")} ordinaryUpBound={(isGiant ? bound.ToString() : "none")} giantAboveBound={aboveBound} invalid={invalid ?? "none"}");
                rows++;
                if (isGiant)
                {
                    giantRows++;
                    if (aboveBound)
                    {
                        aboveBoundGiantRows++;
                        if (firstAbove == "none")
                            firstAbove = $"{index}:{shape.X},{shape.BaseY},{shape.Z}:leafTop={leafTop}";
                    }
                }

                if (archivedPair == "giant")
                {
                    Assert.False(foundArchivedGiant, "Archived giant root must occur once.");
                    foundArchivedGiant = true;
                    Assert.Same(giant, species);
                    Assert.Equal(32, shape.BaseY);
                }
                else if (archivedPair == "ordinary")
                {
                    Assert.False(foundArchivedOrdinary, "Archived ordinary root must occur once.");
                    foundArchivedOrdinary = true;
                    Assert.Same(ordinary, species);
                    Assert.Equal(33, shape.BaseY);
                }

                Assert.Equal(cellX, TreeGenerator.CellOf(shape.X));
                Assert.Equal(cellZ, TreeGenerator.CellOf(shape.Z));
                Assert.True(shape.WoodCount > 0 && shape.WoodCount <= shape.Cells.Length);
                if (!species.HasLeaves)
                    Assert.Equal(int.MinValue, leafTop);
                Assert.Null(invalid);
            }

            Assert.True(foundArchivedGiant, "The archived giant root must remain in this batch.");
            Assert.True(foundArchivedOrdinary, "The archived ordinary root must remain in this batch.");
        }
        finally
        {
            try
            {
                log.WriteLine($"S4B_COHORT_SUMMARY batchCalls={batchCalls} rows={rows} cellScans={cellScans} giantRows={giantRows} aboveBoundGiantRows={aboveBoundGiantRows} firstAbove={firstAbove} foundArchivedGiant={foundArchivedGiant} foundArchivedOrdinary={foundArchivedOrdinary} zeroPositive={aboveBoundGiantRows == 0}");
            }
            finally
            {
                WorldSeed.Apply(WorldSeed.Default);
            }
        }
    }

    /// <summary>Default discovery skips this bounded, fixed-cell diagnostic.</summary>
    public sealed class OptInCohortFactAttribute : FactAttribute
    {
        public OptInCohortFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 to observe the fixed Jungle batch cell.";
        }
    }
}
