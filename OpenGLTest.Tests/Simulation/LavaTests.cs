using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class LavaTests
{
    public LavaTests() => TestContent.EnsureLoaded();

    private static ChunkManager FloorBasin()
    {
        TestContent.EnsureLoaded();
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        manager.Chunks[Vector3.Zero] = chunk;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, 0, z, stone, 0);
        return manager;
    }

    // Update() runs at most MaxFluidTicksPerFrame ticks per call and drops the rest, so
    // multi-tick scenarios pump one tick at a time instead of passing a large delta.
    private static void Pump(FluidManager fluids, int ticks, ChunkManager manager, DropManager drops)
    {
        for (var i = 0; i < ticks; i++)
            fluids.Update(WorldGenerationConfig.FluidTickSeconds, manager, drops);
    }

    [Fact]
    public void LavaSitsOutEveryOtherTick()
    {
        var manager = FloorBasin();
        var lava = ContentRegistry.RequireBlock("lava").Id;
        var water = ContentRegistry.RequireBlock("water").Id;
        var fluids = new FluidManager();
        var drops = new DropManager();
        manager.Chunks[Vector3.Zero].SetBlockRaw(4, 1, 8, lava, FluidState.Source);
        manager.Chunks[Vector3.Zero].SetBlockRaw(12, 1, 8, water, FluidState.Source);
        fluids.ScheduleAround(4, 1, 8);
        fluids.ScheduleAround(12, 1, 8);

        fluids.Update(WorldGenerationConfig.FluidTickSeconds, manager, drops);

        Assert.True(manager.TryGetCell(5, 1, 8, out var lavaNeighbour, out _));
        Assert.NotEqual(lava, lavaNeighbour);
        Assert.True(manager.TryGetCell(13, 1, 8, out var waterNeighbour, out _));
        Assert.Equal(water, waterNeighbour);
    }

    [Fact]
    public void LavaStopsThreeBlocksFromItsSource()
    {
        var manager = FloorBasin();
        var lava = ContentRegistry.RequireBlock("lava").Id;
        var fluids = new FluidManager();
        var drops = new DropManager();
        manager.Chunks[Vector3.Zero].SetBlockRaw(8, 1, 8, lava, FluidState.Source);
        fluids.ScheduleAround(8, 1, 8);

        Pump(fluids, 8, manager, drops);

        Assert.True(manager.TryGetCell(11, 1, 8, out var edge, out var edgeState));
        Assert.Equal(lava, edge);
        Assert.Equal(3, FluidState.Level(edgeState));
        Assert.True(manager.TryGetCell(12, 1, 8, out var beyond, out _));
        Assert.NotEqual(lava, beyond);
    }

    [Fact]
    public void LavaNeverFormsNewSources()
    {
        var manager = FloorBasin();
        var lava = ContentRegistry.RequireBlock("lava").Id;
        var fluids = new FluidManager();
        var drops = new DropManager();
        manager.Chunks[Vector3.Zero].SetBlockRaw(2, 1, 8, lava, FluidState.Source);
        manager.Chunks[Vector3.Zero].SetBlockRaw(4, 1, 8, lava, FluidState.Source);
        fluids.ScheduleAround(2, 1, 8);
        fluids.ScheduleAround(4, 1, 8);

        Pump(fluids, 8, manager, drops);

        Assert.True(manager.TryGetCell(3, 1, 8, out var middle, out var middleState));
        Assert.Equal(lava, middle);
        Assert.False(FluidState.IsSource(middleState));
    }

    [Fact]
    public void AdjacentLavaHardensWhileWaterSurvives()
    {
        var manager = FloorBasin();
        var lava = ContentRegistry.RequireBlock("lava").Id;
        var water = ContentRegistry.RequireBlock("water").Id;
        var cobble = ContentRegistry.RequireBlock("cobblestone").Id;
        var fluids = new FluidManager();
        var drops = new DropManager();
        manager.Chunks[Vector3.Zero].SetBlockRaw(8, 1, 8, lava, FluidState.Source);
        manager.Chunks[Vector3.Zero].SetBlockRaw(9, 1, 8, water, FluidState.Source);
        fluids.ScheduleAround(8, 1, 8);
        fluids.ScheduleAround(9, 1, 8);

        fluids.Update(1f, manager, drops);

        Assert.True(manager.TryGetCell(8, 1, 8, out var hardened, out _));
        Assert.Equal(cobble, hardened);
        Assert.True(manager.TryGetCell(9, 1, 8, out var survived, out var survivedState));
        Assert.Equal(water, survived);
        Assert.True(FluidState.IsSource(survivedState));
    }

    [Fact]
    public void LavaFlowingIntoWaterArrivesHardened()
    {
        var manager = FloorBasin();
        var lava = ContentRegistry.RequireBlock("lava").Id;
        var water = ContentRegistry.RequireBlock("water").Id;
        var cobble = ContentRegistry.RequireBlock("cobblestone").Id;
        var fluids = new FluidManager();
        var drops = new DropManager();
        manager.Chunks[Vector3.Zero].SetBlockRaw(6, 1, 8, lava, FluidState.Source);
        manager.Chunks[Vector3.Zero].SetBlockRaw(8, 1, 8, water, FluidState.Source);
        fluids.ScheduleAround(6, 1, 8);
        fluids.ScheduleAround(8, 1, 8);

        fluids.Update(1f, manager, drops);

        Assert.True(manager.TryGetCell(7, 1, 8, out var middle, out _));
        Assert.Equal(cobble, middle);
    }

    [Fact]
    public void PocketsStayDeepAndBuried()
    {
        Assert.False(LavaGenerator.HasLava(0, 0, 0, 50));
        Assert.False(LavaGenerator.HasLava(0, WorldGenerationConfig.LavaMaxY + 1, 0, 50));
        Assert.False(LavaGenerator.HasLava(0, WorldGenerationConfig.LavaMaxY, 0, WorldGenerationConfig.LavaMaxY + 1));
    }

    [Fact]
    public void PocketsExistSomewhereDeep()
    {
        var found = false;
        for (var x = -64; x < 64 && !found; x++)
        for (var z = -64; z < 64 && !found; z++)
            found = LavaGenerator.HasLava(x, WorldGenerationConfig.LavaMaxY, z, 50);
        Assert.True(found);
    }

    [Fact]
    public void StampPondsLavaOnCaveFloors()
    {
        var lava = ContentRegistry.RequireBlock("lava").Id;
        var stone = ContentRegistry.RequireBlock("stone").Id;

        var wx = 0;
        var wz = 0;
        var found = false;
        for (var x = -64; x < 64 && !found; x++)
        for (var z = -64; z < 64 && !found; z++)
        {
            if (LavaGenerator.HasLava(x, WorldGenerationConfig.LavaMaxY, z, 50))
            {
                wx = x;
                wz = z;
                found = true;
            }
        }

        Assert.True(found);
        var worldX0 = wx & ~(Chunk.Size - 1);
        var worldZ0 = wz & ~(Chunk.Size - 1);
        var worldY0 = WorldGenerationConfig.LavaMaxY & ~(Chunk.Size - 1);
        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        var heights = new int[Chunk.Size * Chunk.Size];
        for (var i = 0; i < heights.Length; i++) heights[i] = 50;
        var column = new ColumnData(heights, new byte[heights.Length], new float[heights.Length], new float[heights.Length]);

        var lx = wx - worldX0;
        var lz = wz - worldZ0;
        var ly = WorldGenerationConfig.LavaMaxY - worldY0;
        blocks[(lx << (2 * Chunk.SizeShift)) | ((ly - 1) << Chunk.SizeShift) | lz] = stone;

        LavaGenerator.Stamp(blocks, column, worldX0, worldY0, worldZ0);

        Assert.Equal(lava, blocks[(lx << (2 * Chunk.SizeShift)) | (ly << Chunk.SizeShift) | lz]);
    }
}
