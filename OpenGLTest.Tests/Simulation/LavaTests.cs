using System.Numerics;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features.Underground;
using Xunit;

namespace VoxelEngine.Tests.Simulation;

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
            fluids.Update(SimulationConfig.FluidTickSeconds, manager, drops);
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

        fluids.Update(SimulationConfig.FluidTickSeconds, manager, drops);

        Assert.True(manager.TryGetCell(5, 1, 8, out var lavaNeighbor, out _));
        Assert.NotEqual(lava, lavaNeighbor);
        Assert.True(manager.TryGetCell(13, 1, 8, out var waterNeighbor, out _));
        Assert.Equal(water, waterNeighbor);
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
    public void StampPondsSparseLavaOnBuriedCaveFloors()
    {
        var site = CaveFluidSources.SiteAt(0, -2, 0);
        for (var cx = 1; (site.Y & Chunk.SizeMask) == 0; cx++) site = CaveFluidSources.SiteAt(cx, -2, 0);
        Assert.True(LavaGenerator.HasLava(site.X, site.Y, site.Z, 50));
        Assert.False(LavaGenerator.HasLava(site.X, site.Y, site.Z, site.Y + 1));
        var x0 = site.X & ~Chunk.SizeMask;
        var y0 = site.Y & ~Chunk.SizeMask;
        var z0 = site.Z & ~Chunk.SizeMask;
        var heights = new int[Chunk.Size * Chunk.Size];
        System.Array.Fill(heights, 50);
        var column = new ColumnData(heights, new byte[heights.Length], new float[heights.Length], new float[heights.Length]);
        var blocks = new ushort[Chunk.BlockCount];
        var ly = site.Y - y0;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            blocks[Chunk.Idx(x, ly - 1, z)] = ContentRegistry.RequireBlock("stone").Id;
        LavaGenerator.Stamp(blocks, column, x0, y0, z0);
        Assert.Equal(LavaGenerator.LavaBlock, blocks[Chunk.Idx(site.X - x0, ly, site.Z - z0)]);
        Assert.InRange(System.Linq.Enumerable.Count(blocks, b => b == LavaGenerator.LavaBlock), 1, 2);
    }
}
