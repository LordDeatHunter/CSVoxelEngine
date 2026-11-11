using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.WorldGen.Features.Underground;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Features.Underground;

public sealed class CaveLavaLakeTests
{
    public CaveLavaLakeTests() => TestContent.EnsureLoaded();

    [Fact]
    public void LakeOccurrenceRisesWithDepthAndNeverEntersTheBarrier()
    {
        var shallow = CaveLavaLakes.Chance(WorldGenerationConfig.LavaMaxY);
        var deep = CaveLavaLakes.Chance(WorldGenerationConfig.BarrierTopY + 32);
        Assert.True(deep > shallow);
        var previous = shallow;
        for (var y = WorldGenerationConfig.LavaMaxY; y > WorldGenerationConfig.BarrierTopY; y--)
        {
            var next = CaveLavaLakes.Chance(y);
            Assert.True(next >= previous && next <= 1f);
            previous = next;
        }
        for (var y0 = (WorldGenerationConfig.BarrierTopY & ~Chunk.SizeMask) - 32; y0 < WorldGenerationConfig.BarrierTopY; y0 += Chunk.Size)
            Assert.False(CaveLavaLakes.TrySite(16, y0, 16, out _, out _, out _));
    }

    [Fact]
    public void MissingCaveFloorRejectsRatherThanManufacturingFloatingRock()
    {
        var heights = Enumerable.Repeat(80, Chunk.Size * Chunk.Size).ToArray();
        var column = new ColumnData(heights, new byte[heights.Length], new float[heights.Length], new float[heights.Length]);
        var tested = false;
        for (var x0 = -128; x0 < 128; x0 += Chunk.Size)
        for (var y0 = -384; y0 < -64; y0 += Chunk.Size)
        for (var z0 = -128; z0 < 128; z0 += Chunk.Size)
        {
            if (!CaveLavaLakes.TrySite(x0, y0, z0, out _, out _, out _)) continue;
            var blocks = new ushort[Chunk.BlockCount];
            CaveLavaLakes.Stamp(blocks, column, x0, y0, z0);
            Assert.All(blocks, id => Assert.Equal(BlockType.Air, id));
            tested = true;
        }
        Assert.True(tested);
    }

    [Fact]
    public void ALeakingFloorAndFluidContactAreRejectedWithoutRepair()
    {
        var heights = Enumerable.Repeat(80, Chunk.Size * Chunk.Size).ToArray();
        var column = new ColumnData(heights, new byte[heights.Length], new float[heights.Length], new float[heights.Length]);
        for (var x0 = -128; x0 < 128; x0 += Chunk.Size)
        for (var y0 = -384; y0 < -64; y0 += Chunk.Size)
        for (var z0 = -128; z0 < 128; z0 += Chunk.Size)
        {
            if (!CaveLavaLakes.TrySite(x0, y0, z0, out var level, out _, out _)) continue;
            foreach (var obstruction in new[] { BlockType.Air, LavaGenerator.LavaBlock })
            {
                var blocks = new ushort[Chunk.BlockCount];
                for (var x = 0; x < Chunk.Size; x++)
                for (var z = 0; z < Chunk.Size; z++)
                for (var y = 0; y <= level; y++) blocks[Chunk.Idx(x, y, z)] = TerrainGenerator.RockBlock;
                for (var x = 0; x < Chunk.Size; x += 3)
                for (var z = 0; z < Chunk.Size; z += 3)
                for (var y = 0; y <= level; y++) blocks[Chunk.Idx(x, y, z)] = obstruction;
                var original = (ushort[])blocks.Clone();
                CaveLavaLakes.Stamp(blocks, column, x0, y0, z0);
                Assert.Equal(original, blocks);
            }
            return;
        }
        Assert.Fail("no candidate site found");
    }

    [Fact]
    public void AGeneratedLakeIsFullyFilledContainedAndNeedsNoFlowToFillIt()
    {
        var heights = Enumerable.Repeat(80, Chunk.Size * Chunk.Size).ToArray();
        var column = new ColumnData(heights, new byte[heights.Length], new float[heights.Length], new float[heights.Length]);
        var found = false;
        for (var x0 = -128; x0 < 128 && !found; x0 += Chunk.Size)
        for (var y0 = -384; y0 < -64 && !found; y0 += Chunk.Size)
        for (var z0 = -128; z0 < 128 && !found; z0 += Chunk.Size)
        {
            if (!CaveLavaLakes.TrySite(x0, y0, z0, out var level, out _, out _)) continue;
            var blocks = new ushort[Chunk.BlockCount];
            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
            for (var y = 0; y <= level; y++) blocks[Chunk.Idx(x, y, z)] = TerrainGenerator.RockBlock;
            var original = (ushort[])blocks.Clone();
            CaveLavaLakes.Stamp(blocks, column, x0, y0, z0);
            for (var i = 0; i < blocks.Length; i++)
                if (blocks[i] != original[i]) Assert.True(BlockType.IsSolid(original[i]), "pool added material to cave air");
            var lava = LavaGenerator.LavaBlock;
            Assert.True(blocks.Count(b => b == lava) > 2, "a lake became another isolated source");
            for (var x = 1; x < Chunk.Size - 1; x++)
            for (var y = 1; y < Chunk.Size - 1; y++)
            for (var z = 1; z < Chunk.Size - 1; z++)
            {
                if (blocks[Chunk.Idx(x, y, z)] != lava) continue;
                Assert.True(y <= level);
                Assert.NotEqual(BlockType.Air, blocks[Chunk.Idx(x, y - 1, z)]);
                Assert.NotEqual(BlockType.Air, blocks[Chunk.Idx(x - 1, y, z)]);
                Assert.NotEqual(BlockType.Air, blocks[Chunk.Idx(x + 1, y, z)]);
                Assert.NotEqual(BlockType.Air, blocks[Chunk.Idx(x, y, z - 1)]);
                Assert.NotEqual(BlockType.Air, blocks[Chunk.Idx(x, y, z + 1)]);
            }
            var chunk = new Chunk(null!, new Vector3(x0 / Chunk.Size, y0 / Chunk.Size, z0 / Chunk.Size), false);
            ChunkTerrainFill.ScanFluidFrontier(chunk, blocks, column, y0);
            Assert.Null(chunk.FluidFrontier);
            var manager = new ChunkManager(null!);
            chunk.IsBlockDataReady = true;
            manager.Chunks[chunk.Position] = chunk;
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++) chunk.SetBlockRaw(x, y, z, blocks[Chunk.Idx(x, y, z)], 0);
            var fluids = new FluidManager();
            fluids.ScheduleBox(chunk.Position * Chunk.Size, chunk.Position * Chunk.Size + new Vector3(Chunk.SizeMask));
            var drops = new DropManager();
            for (var tick = 0; tick < 32; tick++) fluids.Update(SimulationConfig.FluidTickSeconds, manager, drops);
            Assert.Equal(blocks, chunk.Storage.ToDense());
            Assert.All(chunk.StateArray ?? new byte[Chunk.BlockCount], state => Assert.Equal(FluidState.Source, state));
            var again = (ushort[])original.Clone();
            CaveLavaLakes.Stamp(again, column, x0, y0, z0);
            Assert.Equal(blocks, again);
            found = true;
        }
        Assert.True(found, "no lake site found");
    }
}
