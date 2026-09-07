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
}
