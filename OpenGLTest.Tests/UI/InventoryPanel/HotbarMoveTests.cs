using System.Linq;
using VoxelEngine.Networking;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.UI.InventoryPanel;

/// <summary>
/// A hotbar slot dragged onto another moves what it shows there and swaps the destination's contents back into its own
/// slot, the items stay in their pools, and a move or clear that would change nothing is refused, on the client and in the server's replay alike.
/// </summary>
public sealed class HotbarMoveTests
{
    public HotbarMoveTests() => TestContent.EnsureLoaded();

    [Fact]
    public void AMoveSwapsTheDestinationsContentsBackIntoTheSource()
    {
        var inventory = Inventory.Empty();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var bucket = ContentRegistry.RequireItem("bucket").Id;
        Assert.True(inventory.Add(stone, 3, autoEquip: false));
        Assert.True(inventory.Add(bucket, 1, autoEquip: false));
        inventory.EquipStackable(0, stone);
        inventory.EquipTool(5, inventory.ToolEntries().Single().InstanceId);

        Assert.True(inventory.MoveSlot(0, 5));
        Assert.Equal(stone, inventory.IdAt(5));
        Assert.Equal(bucket, inventory.IdAt(0));
        Assert.Single(inventory.ToolEntries());
    }

    [Fact]
    public void AMoveOntoAnEmptySlotLeavesTheSourceEmpty()
    {
        var inventory = Inventory.Empty();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        Assert.True(inventory.Add(stone, 3, autoEquip: false));
        inventory.EquipStackable(0, stone);

        Assert.True(inventory.MoveSlot(0, 4));
        Assert.Equal(stone, inventory.IdAt(4));
        Assert.Equal(0, inventory.IdAt(0));
    }

    [Fact]
    public void AMoveOntoItsOwnSlotOrFromAnEmptyOneChangesNothing()
    {
        var inventory = Inventory.Empty();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        Assert.True(inventory.Add(stone, 3, autoEquip: false));
        inventory.EquipStackable(0, stone);

        Assert.False(inventory.MoveSlot(0, 0));
        Assert.False(inventory.MoveSlot(2, 3));
        Assert.Equal(stone, inventory.IdAt(0));
        Assert.Equal(0, inventory.IdAt(3));
    }

    [Fact]
    public void TheServerReplaysAMoveAndAClearOnlyWithinTheHotbar()
    {
        var inventory = Inventory.Empty();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        Assert.True(inventory.Add(stone, 3, autoEquip: false));
        inventory.EquipStackable(1, stone);

        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.MoveSlot(1, Inventory.SlotCount), creative: false));
        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.ClearSlot(-1), creative: false));
        Assert.Equal(stone, inventory.IdAt(1));

        Assert.True(InventoryIntentRules.Apply(inventory, InventoryIntent.MoveSlot(1, 6), creative: false));
        Assert.Equal(stone, inventory.IdAt(6));
        Assert.True(InventoryIntentRules.Apply(inventory, InventoryIntent.ClearSlot(6), creative: false));
        Assert.Equal(0, inventory.IdAt(6));
    }
}
