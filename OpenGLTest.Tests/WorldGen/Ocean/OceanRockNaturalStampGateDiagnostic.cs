using System;
using System.Numerics;
using VoxelEngine.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Ocean;

/// <summary>Opt-in, fixed natural rock comparison of the normal gated and ungated chunk fills.</summary>
[Collection("Worldgen seed")]
public sealed class OceanRockNaturalStampGateDiagnostic(ITestOutputHelper output)
{
    private const string OptInVariable = "VOXEL_S6A_NATURAL_ROCK_GATE";

    [OptInGateFact]
    public void FrozenNaturalRockChunkMatchesWithStampGatesOnAndOff()
    {
        const int cx = -74, cy = -8, cz = -188;
        const int lx = 5, lz = 13; // (-1179, -2995) in the frozen chunk.
        const int floor = -120;
        var fills = 0;
        var blockComparisons = 0;
        var stateComparisons = 0;
        var frontierComparisons = 0;
        var witnessCellReads = 0;
        var applied = false;
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;

        try
        {
            TestContent.EnsureLoaded();
            WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
            applied = true;
            var key = new Vector3(cx, cy, cz);
            var gated = new Chunk(null!, key, generateBlockData: false);
            fills++;
            ChunkTerrainFill.GenerateBlockData(gated, stampGates: true, generateLakes: true);
            var ungated = new Chunk(null!, key, generateBlockData: false);
            fills++;
            ChunkTerrainFill.GenerateBlockData(ungated, stampGates: false, generateLakes: true);

            var gatedBlocks = gated.Storage.ToDense();
            var ungatedBlocks = ungated.Storage.ToDense();
            Assert.Equal(Chunk.Size * Chunk.Size * Chunk.Size, gatedBlocks.Length);
            Assert.Equal(gatedBlocks.Length, ungatedBlocks.Length);
            for (var i = 0; i < gatedBlocks.Length; i++)
            {
                blockComparisons++;
                Assert.True(gatedBlocks[i] == ungatedBlocks[i], $"block mismatch at dense index {i}");
            }

            // Match StampGateTests' presence semantics; also compare the effective zero state of
            // every cell so the result records what a reader sees when StateArray is absent.
            var gatedState = gated.StateArray;
            var ungatedState = ungated.StateArray;
            Assert.Equal(gatedState is null, ungatedState is null);
            for (var i = 0; i < gatedBlocks.Length; i++)
            {
                stateComparisons++;
                Assert.Equal(gatedState?[i] ?? (byte)0, ungatedState?[i] ?? (byte)0);
            }

            Assert.Equal(gated.SolidCount, ungated.SolidCount);
            Assert.Equal(gated.SurfaceMinY, ungated.SurfaceMinY);
            Assert.Equal(gated.IsBlockDataReady, ungated.IsBlockDataReady);
            Assert.True(gated.IsBlockDataReady);
            var gatedFrontier = gated.FluidFrontier;
            var ungatedFrontier = ungated.FluidFrontier;
            Assert.Equal(gatedFrontier is null, ungatedFrontier is null);
            if (gatedFrontier is not null && ungatedFrontier is not null)
            {
                Assert.Equal(gatedFrontier.Length, ungatedFrontier.Length);
                for (var i = 0; i < gatedFrontier.Length; i++)
                {
                    frontierComparisons++;
                    Assert.Equal(gatedFrontier[i], ungatedFrontier[i]);
                }
            }

            foreach (var chunk in new[] { gated, ungated })
            {
                Assert.Equal(TerrainGenerator.CharredStoneBlock, ReadBlock(chunk, floor + 1));
                Assert.Equal((byte)0, ReadState(chunk, floor + 1));
                Assert.Equal(TerrainGenerator.CharredStoneBlock, ReadBlock(chunk, floor + 2));
                Assert.Equal((byte)0, ReadState(chunk, floor + 2));
                Assert.Equal(TerrainGenerator.SeaBlock, ReadBlock(chunk, floor + 3));
                Assert.Equal((byte)0, ReadState(chunk, floor + 3));
            }
            output.WriteLine($"S6a gate witness seed=1337 profile=Dynamic key=({cx},{cy},{cz}) world=(-1179,-2995) floor={floor} rockY=-119,-118 capY=-117 sourceWaterState=0 gatedAndUngated=true");
            output.WriteLine($"S6a metadata solid={gated.SolidCount} surfaceMinY={gated.SurfaceMinY} ready={gated.IsBlockDataReady} statePresent={gatedState is not null} frontierPresent={gatedFrontier is not null} frontierLength={gatedFrontier?.Length ?? 0}");
        }
        finally
        {
            try
            {
                output.WriteLine($"S6a partial gate ledger seed=1337 profile=Dynamic key=({cx},{cy},{cz}) attemptedNormalFills={fills}/2 blockComparisons={blockComparisons}/4096 stateComparisons={stateComparisons}/4096 frontierComparisons={frontierComparisons} witnessCellReads={witnessCellReads}/12");
            }
            finally
            {
                if (applied) WorldSeed.Apply(previousSeed, previousProfile);
            }
        }

        ushort ReadBlock(Chunk chunk, int worldY)
        {
            witnessCellReads++;
            return chunk.GetLocalBlock(lx, worldY - cy * Chunk.Size, lz);
        }

        byte ReadState(Chunk chunk, int worldY)
        {
            witnessCellReads++;
            return chunk.GetLocalState(lx, worldY - cy * Chunk.Size, lz);
        }
    }

    /// <summary>Discovery skips the two full fills unless explicitly requested.</summary>
    public sealed class OptInGateFactAttribute : FactAttribute
    {
        public OptInGateFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
                Skip = $"Set {OptInVariable}=1 for the frozen natural rock stamp-gate comparison.";
        }
    }
}
