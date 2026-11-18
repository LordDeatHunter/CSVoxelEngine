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
        var column = ColumnCache.Get(0, 0);
        var band = FoliageGenerator.WriteBand(column);
        var beforeAfter = new Dictionary<Stage, ushort[]>();
        var timer = new WorldgenStageTimer
        {
            AfterMark = stage =>
            {
                if (stage is Stage.Flowers or Stage.Foliage)
                    beforeAfter[stage] = (ushort[])ChunkBlocks.DenseScratch.Clone();
            },
        };

        Compare(new Vector3(0, 0, 0), admitted: true, timer);
        Compare(new Vector3(0, -1, 0), admitted: false, null);

        var baseIndex = Chunk.Idx(2, 12, 0);
        var topIndex = Chunk.Idx(2, 13, 0);
        Assert.Equal(ContentRegistry.RequireBlock("tall_fern").Id, beforeAfter[Stage.Foliage][baseIndex]);
        Assert.Equal(ContentRegistry.RequireBlock("tall_fern_top").Id, beforeAfter[Stage.Foliage][topIndex]);
        Assert.NotEqual(beforeAfter[Stage.Flowers][baseIndex], beforeAfter[Stage.Foliage][baseIndex]);
        Assert.NotEqual(beforeAfter[Stage.Flowers][topIndex], beforeAfter[Stage.Foliage][topIndex]);
        output.WriteLine("S4a fixed Forest: real Foliage stage changed both natural tall-fern cells; four full fills compared");

        void Compare(Vector3 position, bool admitted, WorldgenStageTimer? stageTimer)
        {
            var gated = new Chunk(null!, position, generateBlockData: false);
            ChunkTerrainFill.GenerateBlockData(gated, stageTimer, stampGates: true);
            var ungated = new Chunk(null!, position, generateBlockData: false);
            ChunkTerrainFill.GenerateBlockData(ungated, stampGates: false);
            Assert.Equal(admitted, band.Reaches(gated.LowerBoundY));
            Assert.True(gated.Storage.ToDense().AsSpan().SequenceEqual(ungated.Storage.ToDense()));
            Assert.Equal(gated.StateArray, ungated.StateArray);
            Assert.Equal(gated.FluidFrontier, ungated.FluidFrontier);
            Assert.Equal(gated.SolidCount, ungated.SolidCount);
            Assert.Equal(gated.SurfaceMinY, ungated.SurfaceMinY);
            Assert.Equal(gated.IsBlockDataReady, ungated.IsBlockDataReady);
        }
    }
}
