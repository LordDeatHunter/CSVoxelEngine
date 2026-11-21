using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Trees;

/// <summary>Opt-in normal fill and near/far canopy observation at a frozen batch-grown giant.</summary>
[Collection("Worldgen seed")]
public sealed class DynamicJungleGiantWitnessTests
{
    private const string OptInVariable = "VOXEL_S4B_GIANT_WITNESS";

    [OptInWitnessFact]
    [Trait("Purpose", "S4bFixedGiantStampAndCanopy")]
    public void FrozenNaturalGiantStampsAndKeepsItsCanopySpecies()
    {
        const int x = 4026;
        const int z = 778;
        const int expectedBaseY = 50;
        var phase = "setup";
        var directRootCalls = 0;
        var batchCalls = 0;
        var fillCount = 0;
        var grownColumns = 0;
        var proxyColumns = 0;
        var shapeCells = 0;
        var fillComparisons = 0;
        var fillMatches = 0;
        var fillRefused = 0;
        var grownSamples = 0;
        var proxySamples = 0;
        using var log = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };

        try
        {
            TestContent.EnsureLoaded();
            var giant = TreeSpeciesRegistry.Require("jungle_giant");
            var ordinary = TreeSpeciesRegistry.Require("jungle_tree");
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            phase = "admission";
            directRootCalls++;
            Assert.True(TreeGenerator.TryGetTree(x, z, out var selected, out var baseY));
            Assert.Same(giant, selected);
            Assert.Equal(expectedBaseY, baseY);

            phase = "batch";
            var growsBefore = TreeBatches.GrowsOnThisThread;
            batchCalls++;
            var batch = TreeBatches.BatchOf(TreeGenerator.CellOf(x), TreeGenerator.CellOf(z));
            TreeBatches.TreeShape? found = null;
            foreach (var candidate in batch.Trees)
            {
                if (candidate.X != x || candidate.Z != z) continue;
                Assert.Null(found);
                found = candidate;
            }
            Assert.NotNull(found);
            var shape = found!;
            Assert.Same(giant, shape.Species);
            Assert.Equal((x, baseY, z), (shape.X, shape.BaseY, shape.Z));
            Assert.True(shape.WoodCount > 0 && shape.WoodCount < shape.Cells.Length);
            var ordinaryUp = TreeRasterizer.ReachOf(ordinary).Up;
            var leafTop = int.MinValue;
            var rootWoodIndex = -1;
            var highestLeafIndex = -1;
            for (var i = 0; i < shape.Cells.Length; i++)
            {
                var cell = shape.Cells[i];
                shapeCells++;
                if (i < shape.WoodCount)
                {
                    Assert.Equal(giant.WoodId, cell.Block);
                    Assert.False(WoodState.IsDetached(cell.State));
                    if (cell.X == x && cell.Y == baseY && cell.Z == z) rootWoodIndex = i;
                    continue;
                }
                Assert.Equal(giant.LeavesId, cell.Block);
                Assert.False(LeafState.IsPersistent(cell.State));
                if (cell.Y > leafTop || cell.Y == leafTop && highestLeafIndex >= 0
                    && (cell.X, cell.Z, i).CompareTo((shape.Cells[highestLeafIndex].X,
                        shape.Cells[highestLeafIndex].Z, highestLeafIndex)) < 0)
                {
                    leafTop = cell.Y;
                    highestLeafIndex = i;
                }
            }
            Assert.True(rootWoodIndex >= 0 && highestLeafIndex >= 0);
            Assert.True(leafTop > baseY + ordinaryUp,
                $"Batch grown giant leaf {leafTop} must exceed ordinary reach {baseY + ordinaryUp}.");
            var root = shape.Cells[rootWoodIndex];
            var crown = shape.Cells[highestLeafIndex];
            var rootKey = (X: root.X >> Chunk.SizeShift, Y: root.Y >> Chunk.SizeShift,
                Z: root.Z >> Chunk.SizeShift);
            var crownKey = (X: crown.X >> Chunk.SizeShift, Y: crown.Y >> Chunk.SizeShift,
                Z: crown.Z >> Chunk.SizeShift);
            log.WriteLine($"S4B_WITNESS_SELECTED root={x},{baseY},{z} shapeCells={shape.Cells.Length} wood={shape.WoodCount} leafTop={leafTop} ordinaryBound={baseY + ordinaryUp} rootCell={root.X},{root.Y},{root.Z} crownCell={crown.X},{crown.Y},{crown.Z} rootChunk={rootKey} crownChunk={crownKey} grows={TreeBatches.GrowsOnThisThread - growsBefore}");

            phase = "normal-fills";
            var keys = new HashSet<(int X, int Y, int Z)> { rootKey, crownKey };
            foreach (var key in keys)
            {
                var chunk = new Chunk(null!, new Vector3(key.X, key.Y, key.Z), generateBlockData: false);
                fillCount++;
                ChunkTerrainFill.GenerateBlockData(chunk);
                foreach (var selectedCell in new[] { root, crown })
                {
                    if ((selectedCell.X >> Chunk.SizeShift, selectedCell.Y >> Chunk.SizeShift,
                            selectedCell.Z >> Chunk.SizeShift) != key) continue;
                    var selectedBlock = chunk.GetLocalBlock(selectedCell.X & Chunk.SizeMask,
                        selectedCell.Y & Chunk.SizeMask, selectedCell.Z & Chunk.SizeMask);
                    var selectedState = chunk.GetLocalState(selectedCell.X & Chunk.SizeMask,
                        selectedCell.Y & Chunk.SizeMask, selectedCell.Z & Chunk.SizeMask);
                    log.WriteLine($"S4B_WITNESS_FILL key={key} selected={selectedCell.X},{selectedCell.Y},{selectedCell.Z} expected={selectedCell.Block}:{selectedCell.State} actual={selectedBlock}:{selectedState}");
                    Assert.Equal(selectedCell.Block, selectedBlock);
                    Assert.Equal(selectedCell.State, selectedState);
                }
                foreach (var expected in shape.Cells)
                {
                    if ((expected.X >> Chunk.SizeShift, expected.Y >> Chunk.SizeShift,
                            expected.Z >> Chunk.SizeShift) != key) continue;
                    fillComparisons++;
                    var actualBlock = chunk.GetLocalBlock(expected.X & Chunk.SizeMask,
                        expected.Y & Chunk.SizeMask, expected.Z & Chunk.SizeMask);
                    var actualState = chunk.GetLocalState(expected.X & Chunk.SizeMask,
                        expected.Y & Chunk.SizeMask, expected.Z & Chunk.SizeMask);
                    if (actualBlock == expected.Block && actualState == expected.State) fillMatches++;
                    else fillRefused++;
                }
            }
            Assert.Equal(keys.Count, fillCount);
            Assert.True(fillMatches >= 2, "Both selected giant cells must survive the normal fill.");
            log.WriteLine($"S4B_WITNESS_FILL_COMPARE compared={fillComparisons} matched={fillMatches} refused={fillRefused}");

            phase = "grown-canopy";
            grownColumns++;
            var grown = TreeCanopy.GetCanopyColumns(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
            phase = "proxy-canopy";
            proxyColumns++;
            var proxy = TreeCanopy.GetProxyCanopyColumns(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
            Assert.Equal(Chunk.Size * Chunk.Size, grown.Tops.Length);
            Assert.Equal(grown.Tops.Length, proxy.Tops.Length);
            for (var i = 0; i < grown.Tops.Length; i++)
            {
                if (grown.LeafBlocks[i] == giant.LeavesId && grown.Tops[i] > 0) grownSamples++;
                if (proxy.LeafBlocks[i] == giant.LeavesId && proxy.Tops[i] > 0) proxySamples++;
            }
            log.WriteLine($"S4B_WITNESS_CANOPY chunk={x >> Chunk.SizeShift},{z >> Chunk.SizeShift} grownGiantOwned={grownSamples} proxyGiantOwned={proxySamples} proxyIsApproximation=true");
            Assert.True(grownSamples > 0, "The root column's batch canopy must expose giant leaves.");
            Assert.True(proxySamples > 0, "The root column's species proxy must expose giant leaves.");
        }
        finally
        {
            try
            {
                log.WriteLine($"S4B_WITNESS_LEDGER phase={phase} directRootCalls={directRootCalls} batchCalls={batchCalls} fills={fillCount} grownColumns={grownColumns} proxyColumns={proxyColumns} shapeCells={shapeCells} fillComparisons={fillComparisons} fillMatches={fillMatches} fillRefused={fillRefused} grownSamples={grownSamples} proxySamples={proxySamples} growsOnThread={TreeBatches.GrowsOnThisThread}");
            }
            finally
            {
                WorldSeed.Apply(WorldSeed.Default);
            }
        }
    }

    /// <summary>Default discovery skips this fixed natural witness.</summary>
    public sealed class OptInWitnessFactAttribute : FactAttribute
    {
        public OptInWitnessFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 to observe the fixed Jungle giant witness.";
        }
    }
}
