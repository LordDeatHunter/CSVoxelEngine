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

    [Fact]
    public void ADropPutsTheRowIntoThatSlotAndNeverTogglesItOff()
    {
        var inventory = Inventory.Empty();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var bucket = ContentRegistry.RequireItem("bucket").Id;
        Assert.True(inventory.Add(stone, 3, autoEquip: false));
        Assert.True(inventory.Add(bucket, autoEquip: false));
        var tool = inventory.ToolEntries().Single().InstanceId;

        var drop = RowEquip.DropOnHotbar(inventory, stone, 0, 6);
        Assert.Equal(stone, inventory.IdAt(6));
        Assert.Equal(6, drop?.Slot);
        Assert.NotNull(RowEquip.DropOnHotbar(inventory, bucket, tool, 2));
        Assert.Equal(tool, inventory.ToolInstanceAt(2));

        // Dropped again onto the slot that already shows it: nothing changes, nothing is sent
        Assert.Null(RowEquip.DropOnHotbar(inventory, stone, 0, 6));
        Assert.Null(RowEquip.DropOnHotbar(inventory, bucket, tool, 2));
        Assert.Equal(stone, inventory.IdAt(6));
        Assert.Equal(tool, inventory.ToolInstanceAt(2));
    }

    [Fact]
    public void GearDropsOnlyIntoAFittingEquipmentSlotNeverOntoTheHotbar()
    {
        var inventory = Inventory.Empty();
        var gear = ItemRegistry.All.OfType<GearItem>().First();
        Assert.True(inventory.Add(gear.Id));
        var instance = inventory.GearEntries().Single().InstanceId;
        var fits = Enumerable.Range(0, GearConfig.EquipmentSlotCount).First(s => GearSlots.Fits(gear.Info.Slot, s));
        var misfit = Enumerable.Range(0, GearConfig.EquipmentSlotCount).First(s => !GearSlots.Fits(gear.Info.Slot, s));

        Assert.Null(RowEquip.DropOnHotbar(inventory, gear.Id, instance, 0));
        Assert.Equal(0, inventory.IdAt(0));
        Assert.Null(RowEquip.DropOnEquipment(inventory, gear.Id, instance, misfit));
        Assert.False(inventory.Equipment.IsEquipped(instance));

        var wear = RowEquip.DropOnEquipment(inventory, gear.Id, instance, fits);
        Assert.Equal(NetInventoryAction.EquipGear, wear?.Action);
        Assert.Equal(fits, inventory.Equipment.SlotOf(instance));
        Assert.Null(RowEquip.DropOnEquipment(inventory, gear.Id, instance, fits));
    }

    [Fact]
    public void AGrantThatDoesNotAutoEquipStaysOffTheHotbarWhileAPickupLandsOnIt()
    {
        var inventory = Inventory.Empty();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var dirt = ContentRegistry.RequireBlock("dirt").Id;
        var bucket = ContentRegistry.RequireItem("bucket").Id;

        Assert.True(inventory.Add(stone, 5, autoEquip: false));
        Assert.True(inventory.Add(bucket, autoEquip: false));
        Assert.False(inventory.ContainsInHotbar(stone));
        Assert.False(inventory.ContainsInHotbar(bucket));

        Assert.True(inventory.Add(dirt));
        Assert.True(inventory.ContainsInHotbar(dirt));
    }

    [Fact]
    public void TheEquippedMarkReadsTheHotbarAndWhatIsWornForEveryKindOfRow()
    {
        var inventory = Inventory.Empty();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var bucket = ContentRegistry.RequireItem("bucket").Id;
        var gear = ItemRegistry.All.OfType<GearItem>().First();
        Assert.True(inventory.Add(stone, 2, autoEquip: false));
        Assert.True(inventory.Add(bucket, autoEquip: false));
        Assert.True(inventory.Add(gear.Id));
        var tool = inventory.ToolEntries().Single().InstanceId;
        var piece = inventory.GearEntries().Single().InstanceId;

        Assert.False(EquippedMark.Of(inventory, stone, 0).IsEquipped);
        Assert.False(EquippedMark.Of(inventory, bucket, tool).IsEquipped);
        Assert.False(EquippedMark.Of(inventory, gear.Id, piece).IsEquipped);

        inventory.EquipStackable(3, stone);
        inventory.EquipTool(9, tool);
        Assert.True(inventory.Equipment.TryEquipAuto(piece));

        // A tab's own rows name the instance; a catalog row or a search hit may name only the kind
        Assert.Equal(3, EquippedMark.Of(inventory, stone, 0).HotbarSlot);
        Assert.Equal(9, EquippedMark.Of(inventory, bucket, tool).HotbarSlot);
        Assert.Equal(9, EquippedMark.Of(inventory, bucket, 0).HotbarSlot);
        Assert.True(EquippedMark.Of(inventory, gear.Id, piece).Worn);
        Assert.True(EquippedMark.Of(inventory, gear.Id, 0).Worn);
        Assert.Equal("worn", EquippedMark.Of(inventory, gear.Id, 0).CountLabel("1"));
        Assert.Equal("0", EquippedMark.HotbarKey(9));
    }
}
