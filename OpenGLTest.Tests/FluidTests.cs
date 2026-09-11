using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class FluidTests
{
    public FluidTests() => TestContent.EnsureLoaded();

    [Fact]
    public void SourceIsFullHeightAndFallingIsToo()
    {
        Assert.True(FluidState.IsSource(FluidState.Source));
        Assert.Equal(8f / 9f, FluidState.SurfaceHeight(FluidState.Source), precision: 5);
        Assert.Equal(1f, FluidState.SurfaceHeight(FluidState.Falling), precision: 5);
        Assert.Equal(0, FluidState.Level(FluidState.Source));
        Assert.Equal(8, FluidState.Amount(FluidState.Falling));
        Assert.Equal(5, FluidState.Amount(FluidState.Flowing(3)));
    }

    // The whole chunk floored: with no hole in slope-search range every direction
    // ties and water spreads evenly, and unloaded neighbours read as walls.
    // A smaller floor has open edges the search correctly prefers (Minecraft getSpread).
    private static ChunkManager FullFloorBasin(out byte water)
    {
        TestContent.EnsureLoaded();
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        manager.Chunks[Vector3.Zero] = chunk;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        water = ContentRegistry.RequireBlock("water").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, 0, z, stone, 0);
        return manager;
    }

    [Fact]
    public void SourceSpreadsEvenlyWithNoHoleNearby()
    {
        var manager = FullFloorBasin(out var water);
        var fluids = new FluidManager();
        var drops = new DropManager();
        manager.Chunks[Vector3.Zero].SetBlockRaw(8, 1, 8, water, FluidState.Source);
        fluids.ScheduleAround(8, 1, 8);

        fluids.Update(1f, manager, drops);

        Assert.True(manager.TryGetCell(8, 1, 8, out var kept, out var keptState));
        Assert.Equal(water, kept);
        Assert.True(FluidState.IsSource(keptState));
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            Assert.True(manager.TryGetCell(8 + dx, 1, 8 + dz, out var spread, out _));
            Assert.Equal(water, spread);
        }
    }

    [Fact]
    public void SourceFallsDownward()
    {
        var manager = FullFloorBasin(out var water);
        var fluids = new FluidManager();
        var drops = new DropManager();
        manager.Chunks[Vector3.Zero].SetBlockRaw(1, 5, 1, water, FluidState.Source);
        fluids.ScheduleAround(1, 5, 1);

        fluids.Update(1f, manager, drops);

        Assert.True(manager.TryGetCell(1, 4, 1, out var below, out var state));
        Assert.Equal(water, below);
        Assert.True(FluidState.IsFalling(state));
    }

    // A source sitting on the last column of a chunk, with its +X neighbour in a loaded
    // second chunk: the spread has to read across the border, which is the tick's chunk
    // cache's miss-then-hit path (every other fluid test lives in a single chunk).
    [Fact]
    public void SourceSpreadsAcrossAChunkBorder()
    {
        var manager = new ChunkManager(null!);
        var water = ContentRegistry.RequireBlock("water").Id;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var cx = 0; cx <= 1; cx++)
        {
            var chunk = new Chunk(null!, new Vector3(cx, 0, 0), generateBlockData: false);
            chunk.IsBlockDataReady = true;
            manager.Chunks[new Vector3(cx, 0, 0)] = chunk;
            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
                chunk.SetBlockRaw(x, 0, z, stone, 0);
        }

        var fluids = new FluidManager();
        var drops = new DropManager();
        manager.Chunks[Vector3.Zero].SetBlockRaw(Chunk.SizeMask, 1, 8, water, FluidState.Source);
        fluids.ScheduleAround(Chunk.SizeMask, 1, 8);

        fluids.Update(1f, manager, drops);

        Assert.True(manager.TryGetCell(Chunk.Size, 1, 8, out var spread, out _));
        Assert.Equal(water, spread);
    }

    [Fact]
    public void WakeLoadedChunksHonoursThePerFrameCap()
    {
        var manager = new ChunkManager(null!);
        const int extra = 3;
        // Chunk y=10 sits above sea level, so the wake skips the border scan and stays cheap
        for (var i = 0; i < WorldGenerationConfig.MaxWakeChunksPerFrame + extra; i++)
            manager.QueueChunk(new Vector3(i, 10, 0));

        new FluidManager().WakeLoadedChunks(manager);

        var remaining = 0;
        while (manager.TryDequeueLoaded(out _)) remaining++;
        Assert.Equal(extra, remaining);
    }
}
