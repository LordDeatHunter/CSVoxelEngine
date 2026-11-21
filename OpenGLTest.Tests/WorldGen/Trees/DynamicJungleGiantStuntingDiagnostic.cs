using System;
using System.IO;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Trees;

/// <summary>Opt-in diagnosis of the fixed giant root that was short in a natural batch.</summary>
[Collection("Worldgen seed")]
public sealed class DynamicJungleGiantStuntingDiagnostic
{
    private const string OptInVariable = "VOXEL_S4B_GIANT_STUNTING_DIAGNOSTIC";

    [OptInStuntingFact]
    [Trait("Purpose", "S4bFrozenRootDiagnostic")]
    public void ReportsFrozenGiantTerrainSoloAndConditionalFlatCrown()
    {
        const int x = 4004;
        const int z = 811;
        const int baseY = 32;
        var explicitRoots = 0;
        var shapeCalls = 0;
        var flatGrows = 0;
        var flatRasters = 0;
        using var log = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };

        try
        {
            TestContent.EnsureLoaded();
            var giant = TreeSpeciesRegistry.Require("jungle_giant");
            var ordinary = TreeSpeciesRegistry.Require("jungle_tree");
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);

            explicitRoots++;
            var admitted = TreeGenerator.TryGetTree(x, z, out var selected, out var selectedBaseY);
            log.WriteLine($"S4B_STUNT_ADMISSION root={x},{z} admitted={admitted} species={(admitted ? selected.Name : "-")} baseY={selectedBaseY}");
            Assert.True(admitted);
            Assert.Same(giant, selected);
            Assert.Equal(baseY, selectedBaseY);

            var growsBefore = TreeBatches.GrowsOnThisThread;
            shapeCalls++;
            var solo = TreeBatches.ShapeOf(x, z);
            var growsAfter = TreeBatches.GrowsOnThisThread;
            var soloLeafTop = ReportShape(log, "terrain-solo", solo, giant, x, baseY, z);
            log.WriteLine($"S4B_STUNT_SOLO_GROWS before={growsBefore} after={growsAfter} delta={growsAfter - growsBefore}");

            Assert.Equal(growsBefore + 1, growsAfter);
            CheckShape(solo, giant, x, baseY, z);
            var woodAboveCap = HasWoodAt(solo, x, 39, z);
            var woodTop = WoodTop(solo);
            Assert.True(woodAboveCap && woodTop > 38,
                "Terrain solo must grow wood through (4004,39,811); otherwise the recorded batch-occupancy explanation is contradicted.");

            var ordinaryReach = TreeRasterizer.ReachOf(ordinary);
            var threshold = Math.Max(50, baseY + ordinaryReach.Up);
            log.WriteLine($"S4B_STUNT_THRESHOLD archivedOrdinaryLeafTop=50 ordinaryReachUp={ordinaryReach.Up} baseY={baseY} threshold={threshold} terrainSoloLeafTop={soloLeafTop} above={soloLeafTop > threshold}");
            if (soloLeafTop > threshold)
            {
                log.WriteLine("S4B_STUNT_FLAT skipped=terrain-solo-above-threshold");
                return;
            }

            var flatSpace = new VirtualTreeSpace(static (_, _) => 31);
            flatGrows++;
            var graph = TreeGrower.GrowToMaturity(giant, x, baseY, z, flatSpace);
            flatRasters++;
            var flatCells = TreeRasterizer.Rasterize(graph, giant, flatSpace, out var flatWoodCount).ToArray();
            var flat = TreeBatches.TreeShape.Build(giant, x, baseY, z, flatCells, flatWoodCount);
            var flatLeafTop = ReportShape(log, "flat-unoccupied", flat, giant, x, baseY, z);
            CheckShape(flat, giant, x, baseY, z);
            log.WriteLine($"S4B_STUNT_FLAT skipped=false leafTop={flatLeafTop} threshold={threshold} above={flatLeafTop > threshold}");
        }
        finally
        {
            try
            {
                log.WriteLine($"S4B_STUNT_WORK explicitRoots={explicitRoots} shapeCalls={shapeCalls} flatGrows={flatGrows} flatRasters={flatRasters}");
            }
            finally
            {
                WorldSeed.Apply(WorldSeed.Default);
            }
        }
    }

    private static int ReportShape(StreamWriter log, string kind, TreeBatches.TreeShape shape,
        TreeSpecies species, int x, int baseY, int z)
    {
        var woodTop = WoodTop(shape);
        var leafTop = int.MinValue;
        for (var i = shape.WoodCount; i < shape.Cells.Length; i++)
            leafTop = Math.Max(leafTop, shape.Cells[i].Y);

        var reach = TreeRasterizer.ReachOf(species);
        log.WriteLine($"S4B_STUNT_SHAPE kind={kind} species={shape.Species.Name} root={shape.X},{shape.BaseY},{shape.Z} expectedRoot={x},{baseY},{z} cells={shape.Cells.Length} wood={shape.WoodCount} leaves={shape.Cells.Length - shape.WoodCount} woodTop={woodTop} leafTop={leafTop} minY={shape.MinY} maxY={shape.MaxY} reach={reach.Horizontal},{reach.Up},{reach.Down} woodAt4004_39_811={HasWoodAt(shape, x, 39, z)}");
        return leafTop;
    }

    private static int WoodTop(TreeBatches.TreeShape shape)
    {
        var top = int.MinValue;
        for (var i = 0; i < shape.WoodCount; i++)
            top = Math.Max(top, shape.Cells[i].Y);
        return top;
    }

    private static bool HasWoodAt(TreeBatches.TreeShape shape, int x, int y, int z)
    {
        for (var i = 0; i < shape.WoodCount; i++)
            if (shape.Cells[i].X == x && shape.Cells[i].Y == y && shape.Cells[i].Z == z)
                return true;
        return false;
    }

    private static void CheckShape(TreeBatches.TreeShape shape, TreeSpecies species, int x, int baseY, int z)
    {
        Assert.Same(species, shape.Species);
        Assert.Equal((x, baseY, z), (shape.X, shape.BaseY, shape.Z));
        Assert.True(shape.WoodCount > 0);
        Assert.True(shape.WoodCount < shape.Cells.Length);
        var reach = TreeRasterizer.ReachOf(species);
        for (var i = 0; i < shape.Cells.Length; i++)
        {
            var cell = shape.Cells[i];
            if (i < shape.WoodCount)
            {
                Assert.Equal(species.WoodId, cell.Block);
                Assert.False(WoodState.IsDetached(cell.State));
            }
            else
            {
                Assert.Equal(species.LeavesId, cell.Block);
                Assert.False(LeafState.IsPersistent(cell.State));
                Assert.Equal((byte)0, (byte)(cell.State & ~(LeafState.HydrationMask | LeafState.PersistentBit)));
            }

            Assert.InRange(cell.X, x - reach.Horizontal, x + reach.Horizontal);
            Assert.InRange(cell.Y, baseY - reach.Down, baseY + reach.Up);
            Assert.InRange(cell.Z, z - reach.Horizontal, z + reach.Horizontal);
            Assert.InRange(cell.Y, shape.MinY, shape.MaxY);
        }
    }

    /// <summary>Default discovery exposes a skip until this specific diagnostic is requested.</summary>
    public sealed class OptInStuntingFactAttribute : FactAttribute
    {
        public OptInStuntingFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 to observe the fixed giant root.";
        }
    }
}
