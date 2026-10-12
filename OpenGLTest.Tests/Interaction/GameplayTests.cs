using System.Numerics;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

public sealed class GameplayTests
{
    public GameplayTests() => TestContent.EnsureLoaded();

    private static ChunkManager SingleChunk(out Chunk chunk, out byte stone)
    {
        var manager = new ChunkManager(null!);
        chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        manager.Chunks[Vector3.Zero] = chunk;
        stone = ContentRegistry.RequireBlock("stone").Id;
        return manager;
    }

    [Fact]
    public void RaycastHitsWallWithFaceNormal()
    {
        var manager = SingleChunk(out _, out var stone);
        manager.Chunks[Vector3.Zero].SetBlockRaw(2, 0, 0, stone, 0);
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(0.5f, 0.5f, 0.5f), yaw: 0f, pitch: 0f);

        var hit = camera.Raycast(10f, manager, out var hitPos, out var normal);

        Assert.True(hit);
        Assert.Equal(new Vector3(2, 0, 0), hitPos);
        Assert.Equal(new Vector3(-1, 0, 0), normal);
    }

    [Fact]
    public void RaycastMissesEmptySky()
    {
        var manager = SingleChunk(out _, out _);
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(0.5f, 0.5f, 0.5f), yaw: 0f, pitch: 89f);

        Assert.False(camera.Raycast(10f, manager, out _, out _));
    }

    [Fact]
    public void WaterIsTargetableOnlyWhenAskedFor()
    {
        var manager = SingleChunk(out _, out _);
        var water = ContentRegistry.RequireBlock("water").Id;
        manager.Chunks[Vector3.Zero].SetBlockRaw(2, 0, 0, water, FluidState.Source);
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(0.5f, 0.5f, 0.5f), yaw: 0f, pitch: 0f);

        Assert.False(camera.Raycast(10f, manager, out _, out _));
        Assert.True(camera.Raycast(10f, manager, out var hitPos, out _, includeFluids: true));
        Assert.Equal(new Vector3(2, 0, 0), hitPos);
    }

    [Fact]
    public void InventoryAddAndConsumeRoundtrip()
    {
        var inventory = new Inventory();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        inventory.Select(0);
        inventory.EquipStackable(0, stone);

        var before = inventory.SelectedCount;
        Assert.True(inventory.Add(stone));
        Assert.Equal(before + 1, inventory.SelectedCount);
        Assert.True(inventory.ConsumeSelected());
        Assert.Equal(before, inventory.SelectedCount);

        inventory.Select(1);
        Assert.False(inventory.ConsumeSelected());
    }

    [Fact]
    public void InventoryMorphSwapsBucketForWaterBucketInPlace()
    {
        var inventory = new Inventory();
        var bucket = ContentRegistry.RequireItem("bucket").Id;
        var filled = ContentRegistry.RequireItem("water_bucket").Id;
        var instance = Assert.Single(inventory.ToolEntries(), t => t.ItemId == bucket).InstanceId;
        inventory.EquipTool(0, instance);

        Assert.True(inventory.MorphTool(instance, filled));
        Assert.Equal(filled, inventory.SelectedBlockId);
    }

    [Fact]
    public void InventoryScrollWrapsAround()
    {
        var inventory = new Inventory();
        inventory.Select(0);
        inventory.Scroll(1);
        Assert.Equal(Inventory.SlotCount - 1, inventory.SelectedIndex);
        inventory.Scroll(-1);
        Assert.Equal(0, inventory.SelectedIndex);
    }

    [Fact]
    public void ClearingABlockZeroesItsFluidState()
    {
        var manager = SingleChunk(out var chunk, out _);
        var water = ContentRegistry.RequireBlock("water").Id;
        chunk.SetBlockRaw(1, 1, 1, water, FluidState.Flowing(3));

        var removed = chunk.ClearBlockAt(new Vector3(1, 1, 1));

        Assert.Equal(water, removed);
        Assert.Equal(BlockType.Air, chunk.GetLocalBlock(1, 1, 1));
        Assert.Equal(0, chunk.GetLocalState(1, 1, 1));
        Assert.True(manager.TryGetCell(1, 1, 1, out _, out var state));
        Assert.Equal(0, state);
    }
}
