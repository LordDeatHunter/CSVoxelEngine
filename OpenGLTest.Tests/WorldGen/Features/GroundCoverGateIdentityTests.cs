using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using Xunit;
using Xunit.Abstractions;
using Stage = VoxelEngine.WorldGen.WorldgenStageTimer.Stage;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>Real filled Forest column proving the Dynamic foliage gate preserves a natural tall fern.</summary>
[Collection("Worldgen seed")]
public sealed class GroundCoverGateIdentityTests(ITestOutputHelper output) : IDisposable
{
    public void Dispose() => WorldSeed.Apply(WorldSeed.Default);

    [Fact]
    public void FixedForestFoliageGatePreservesTheFullChunkAndWritesTheNaturalPair()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
        var columnGetAttempts = 0;
        var fillAttempts = 0;
        var pairComparisonAttempts = 0;
        var snapshotArrays = 0;
        var snapshotCells = 0;
        var beforeAfter = new Dictionary<Stage, ushort[]>();
        try
        {
            columnGetAttempts++;
            var column = ColumnCache.Get(0, 0);
            var band = FoliageGenerator.WriteBand(column);
            var timer = new WorldgenStageTimer
            {
                AfterMark = stage =>
                {
                    if (stage is not (Stage.Flowers or Stage.Foliage)) return;
                    snapshotArrays++;
                    snapshotCells += ChunkBlocks.DenseScratch.Length;
                    beforeAfter[stage] = (ushort[])ChunkBlocks.DenseScratch.Clone();
                },
            };

            Compare(new Vector3(0, 0, 0), admitted: true, timer);
            Compare(new Vector3(0, 1, 0), admitted: false, null);

            var baseIndex = Chunk.Idx(2, 12, 0);
            var topIndex = Chunk.Idx(2, 13, 0);
            Assert.Equal(ContentRegistry.RequireBlock("tall_fern").Id, beforeAfter[Stage.Foliage][baseIndex]);
            Assert.Equal(ContentRegistry.RequireBlock("tall_fern_top").Id, beforeAfter[Stage.Foliage][topIndex]);
            Assert.NotEqual(beforeAfter[Stage.Flowers][baseIndex], beforeAfter[Stage.Foliage][baseIndex]);
            Assert.NotEqual(beforeAfter[Stage.Flowers][topIndex], beforeAfter[Stage.Foliage][topIndex]);
            output.WriteLine("S4a fixed Forest: real Foliage stage changed both natural tall-fern cells");

            void Compare(Vector3 position, bool admitted, WorldgenStageTimer? stageTimer)
            {
                var gated = new Chunk(null!, position, generateBlockData: false);
                output.WriteLine($"S4a Foliage band=[{band.Min},{band.Max}] chunk={position} worldY0={gated.LowerBoundY} expectedAdmission={admitted}");
                Assert.Equal(admitted, band.Reaches(gated.LowerBoundY));
                fillAttempts++;
                ChunkTerrainFill.GenerateBlockData(gated, stageTimer, stampGates: true);
                var ungated = new Chunk(null!, position, generateBlockData: false);
                fillAttempts++;
                ChunkTerrainFill.GenerateBlockData(ungated, stampGates: false);
                pairComparisonAttempts++;
                Assert.True(gated.IsBlockDataReady && ungated.IsBlockDataReady);
                Assert.True(gated.Storage.ToDense().AsSpan().SequenceEqual(ungated.Storage.ToDense()));
                Assert.Equal(gated.StateArray, ungated.StateArray);
                Assert.Equal(gated.FluidFrontier, ungated.FluidFrontier);
                Assert.Equal(gated.SolidCount, ungated.SolidCount);
                Assert.Equal(gated.SurfaceMinY, ungated.SurfaceMinY);
                Assert.Equal(gated.IsBlockDataReady, ungated.IsBlockDataReady);
            }
        }
        finally
        {
            output.WriteLine($"S4a gate partial ledger columnGetAttempts={columnGetAttempts} fillAttempts={fillAttempts} pairComparisonAttempts={pairComparisonAttempts} snapshotArrays={snapshotArrays} snapshotCells={snapshotCells}");
        }
    }
}
