using System.Linq;
using VoxelEngine.Items;
using VoxelEngine.Networking;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.UI.InventoryPanel;

/// <summary>
/// The [E] panel's right-click equip: one right-click wears gear or puts a tool or a stackable into the selected hotbar
/// slot, a second one undoes it, the intent it returns mirrors the change, and nothing the player does not own is
/// equipped.
/// </summary>
public sealed class RowEquipTests
{
    public RowEquipTests() => TestContent.EnsureLoaded();

    [Fact]
    public void ARightClickWearsGearAndASecondOneTakesItOff()
    {
        var inventory = Inventory.Empty();
        var gear = ItemRegistry.All.OfType<GearItem>().First();
        Assert.True(inventory.Add(gear.Id));
        var instance = inventory.GearEntries().Single().InstanceId;

        var wear = RowEquip.Apply(inventory, gear.Id, instance);
        Assert.True(inventory.Equipment.IsEquipped(instance));
        Assert.Equal(NetInventoryAction.EquipGear, wear?.Action);
        Assert.Equal(inventory.Equipment.SlotOf(instance), wear?.Slot);

        var takeOff = RowEquip.Apply(inventory, gear.Id, instance);
        Assert.False(inventory.Equipment.IsEquipped(instance));
        Assert.Equal(NetInventoryAction.UnequipGear, takeOff?.Action);
    }

    [Fact]
    public void ARightClickPutsAnOwnedStackableIntoTheSelectedSlotAndASecondOneClearsIt()
    {
        var inventory = Inventory.Empty();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        Assert.True(inventory.Add(stone, 3));
        inventory.Select(4);

        var equip = RowEquip.Apply(inventory, stone, 0);
        Assert.Equal(stone, inventory.IdAt(4));
        Assert.Equal(4, equip?.Slot);

        Assert.NotNull(RowEquip.Apply(inventory, stone, 0));
        Assert.Equal(0, inventory.IdAt(4));
    }

    [Fact]
    public void ARowNamingAToolKindEquipsItsOwnedInstance()
    {
        var inventory = Inventory.Empty();
        var bucket = ContentRegistry.RequireItem("bucket").Id;
        Assert.True(inventory.Add(bucket));
        inventory.Select(2);

        // A creative catalog row names the kind, not an instance
        Assert.NotNull(RowEquip.Apply(inventory, bucket, 0));
        Assert.Equal(inventory.ToolEntries().Single().InstanceId, inventory.ToolInstanceAt(2));
    }

    [Fact]
    public void NothingThePlayerDoesNotOwnIsEquipped()
    {
        var inventory = Inventory.Empty();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var bucket = ContentRegistry.RequireItem("bucket").Id;
        var gear = ItemRegistry.All.OfType<GearItem>().First();

        Assert.Null(RowEquip.Apply(inventory, stone, 0));
        Assert.Null(RowEquip.Apply(inventory, bucket, 0));
        Assert.Null(RowEquip.Apply(inventory, gear.Id, 0));
        Assert.Equal(0, inventory.IdAt(inventory.SelectedIndex));
        Assert.Empty(inventory.Equipment.Worn());
    }
}
