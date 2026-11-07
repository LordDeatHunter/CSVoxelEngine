using System.Linq;
using VoxelEngine.Config;
using VoxelEngine.Items;
using VoxelEngine.Networking;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Player;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Items;

/// <summary>
/// The gear rules: what fits where, that worn gear follows the pool, that the pools keep their own caps, that
/// equipment survives a save and a wire round trip and that what is worn changes the stats it should.
/// </summary>
public sealed class GearTests
{
    public GearTests() => TestContent.EnsureLoaded();

    private static GearItem GearFor(GearSlot slot, int skip = 0) =>
        ItemRegistry.All.OfType<GearItem>().Where(g => g.Info.Slot == slot).Skip(skip).First();

    private static int Own(Inventory inventory, GearItem gear)
    {
        Assert.True(inventory.Add(gear.Id));
        return inventory.GearEntries().Last(e => e.ItemId == gear.Id).InstanceId;
    }

    [Fact]
    public void GearStoresInItsOwnPoolAndNeverTheTools()
    {
        var gear = GearFor(GearSlot.Head);
        Assert.Equal(InventoryCategory.Gear, Inventory.CategoryOf(gear.Id));

        var inventory = Inventory.Empty();
        Own(inventory, gear);

        Assert.Equal(1, inventory.UsedOf(InventoryCategory.Gear));
        Assert.Equal(0, inventory.UsedOf(InventoryCategory.Tools));
        Assert.Empty(inventory.ToolEntries());
    }

    [Fact]
    public void AFullToolsPoolStillTakesGearAndTheReverse()
    {
        var inventory = Inventory.Empty();
        var tool = ItemRegistry.All.First(i => Inventory.CategoryOf(i.Id) == InventoryCategory.Tools && i is not StaffItem);
        while (inventory.Add(tool.Id)) { }
        Assert.False(inventory.CanAccept(tool.Id));
        Assert.True(inventory.CanAccept(GearFor(GearSlot.Chest).Id));

        var gear = GearFor(GearSlot.Chest);
        while (inventory.Add(gear.Id)) { }
        Assert.Equal(Inventory.GearCapacity, inventory.UsedOf(InventoryCategory.Gear));
        Assert.False(inventory.CanAccept(gear.Id));
        Assert.Equal(Inventory.ToolCapacity, inventory.UsedOf(InventoryCategory.Tools));
    }

    [Fact]
    public void APieceFitsOnlyASlotOfItsOwnKind()
    {
        var inventory = Inventory.Empty();
        var head = Own(inventory, GearFor(GearSlot.Head));

        Assert.True(inventory.Equipment.TryEquip((int)GearSlot.Head, head));
        Assert.False(inventory.Equipment.TryEquip((int)GearSlot.Chest, head));
        Assert.False(inventory.Equipment.TryEquip(GearConfig.ArmorSlotCount, head));
        Assert.Equal(head, inventory.Equipment.InstanceAt((int)GearSlot.Head));
        Assert.Equal(0, inventory.Equipment.InstanceAt((int)GearSlot.Chest));
    }

    [Fact]
    public void AnyAccessorySlotTakesAnAccessoryAndNoneTakesArmor()
    {
        var inventory = Inventory.Empty();
        var ring = Own(inventory, GearFor(GearSlot.Accessory));
        var cap = Own(inventory, GearFor(GearSlot.Head));

        for (var slot = GearConfig.ArmorSlotCount; slot < GearConfig.EquipmentSlotCount; slot++)
            Assert.True(inventory.Equipment.TryEquip(slot, ring));
        Assert.Equal(GearConfig.EquipmentSlotCount - 1, inventory.Equipment.SlotOf(ring));
        Assert.False(inventory.Equipment.TryEquip(GearConfig.ArmorSlotCount, cap));
    }

    [Fact]
    public void EquippingARefusedThingChangesNothing()
    {
        var inventory = Inventory.Empty();
        var tool = ItemRegistry.All.First(i => Inventory.CategoryOf(i.Id) == InventoryCategory.Tools && i is not StaffItem);
        inventory.Add(tool.Id);
        var toolInstance = inventory.ToolEntries()[0].InstanceId;
        var version = inventory.Equipment.Version;

        Assert.False(inventory.Equipment.TryEquip(0, toolInstance));
        Assert.False(inventory.Equipment.TryEquip(0, 9999));
        Assert.False(inventory.Equipment.TryEquip(-1, toolInstance));
        Assert.Equal(version, inventory.Equipment.Version);
    }

    [Fact]
    public void WornGearFollowsThePoolSoARemovedPieceComesOff()
    {
        var inventory = Inventory.Empty();
        var cap = Own(inventory, GearFor(GearSlot.Head));
        inventory.Equipment.TryEquip((int)GearSlot.Head, cap);

        Assert.True(inventory.RemoveTool(cap));

        Assert.False(inventory.Equipment.IsEquipped(cap));
        Assert.Equal(GearStats.None, inventory.Equipment.Stats);
    }

    [Fact]
    public void GearNeverGoesOnTheHotbar()
    {
        var inventory = Inventory.Empty();
        var cap = Own(inventory, GearFor(GearSlot.Head));

        Assert.False(inventory.EquipTool(0, cap));
        Assert.Equal(0, inventory.ToolInstanceAt(0));
    }

    [Fact]
    public void ReplacingAWornPieceKeepsTheOldOneOwned()
    {
        var inventory = Inventory.Empty();
        var first = Own(inventory, GearFor(GearSlot.Head, 0));
        var second = Own(inventory, GearFor(GearSlot.Head, 1));
        inventory.Equipment.TryEquip((int)GearSlot.Head, first);

        inventory.Equipment.TryEquip((int)GearSlot.Head, second);

        Assert.Equal(second, inventory.Equipment.InstanceAt((int)GearSlot.Head));
        Assert.False(inventory.Equipment.IsEquipped(first));
        Assert.Equal(2, inventory.UsedOf(InventoryCategory.Gear));
    }

    [Fact]
    public void StatsAreTheSumOfWhatIsWornAndClampedToTheGameLimits()
    {
        var inventory = Inventory.Empty();
        var pieces = ItemRegistry.All.OfType<GearItem>().Where(g => g.Info.Slot != GearSlot.Accessory)
            .GroupBy(g => g.Info.Slot).Select(group => group.First()).ToList();
        foreach (var piece in pieces)
            inventory.Equipment.TryEquip((int)piece.Info.Slot, Own(inventory, piece));

        var expected = pieces.Sum(p => p.Info.Modifiers.Where(m => m.Stat == GearStat.Armor).Sum(m => (int)m.Value));
        Assert.Equal(System.Math.Min(expected, GearConfig.ArmorCap), inventory.Equipment.Stats.Armor);

        var huge = GearStats.None.With(GearStat.MaxHealth, 9999f).With(GearStat.MoveSpeed, 50f).With(GearStat.Armor, 999f);
        var clamped = huge.Clamped();
        Assert.Equal(GearConfig.MaxHealthBonusCap, clamped.MaxHealth);
        Assert.Equal(GearConfig.ArmorCap, clamped.Armor);
        Assert.True(clamped.MoveSpeed <= GearConfig.FactorBonusCap);
    }

    [Fact]
    public void EquipmentSurvivesASaveRoundTripAndDropsWhatNoLongerFits()
    {
        var a = Inventory.Empty();
        var cap = Own(a, GearFor(GearSlot.Head));
        var chest = Own(a, GearFor(GearSlot.Chest));
        a.Equipment.TryEquip((int)GearSlot.Head, cap);
        a.Equipment.TryEquip((int)GearSlot.Chest, chest);

        var b = Inventory.Empty();
        b.Import(a.Export());
        Assert.Equal(cap, b.Equipment.InstanceAt((int)GearSlot.Head));
        Assert.Equal(chest, b.Equipment.InstanceAt((int)GearSlot.Chest));
        Assert.Equal(a.Equipment.Stats, b.Equipment.Stats);

        // A save that names the chest piece in the head slot, and a piece it never owned, restores neither
        var data = a.Export();
        data.Equipped = [chest, 9999, 0, 0, 0, 0, 0, 0, 0];
        var c = Inventory.Empty();
        c.Import(data);
        Assert.Equal(0, c.Equipment.InstanceAt(0));
        Assert.Equal(0, c.Equipment.InstanceAt(1));
    }

    [Fact]
    public void ASaveWithNoEquipmentReadsAsNothingWorn()
    {
        var a = Inventory.Empty();
        Own(a, GearFor(GearSlot.Head));
        var data = a.Export();
        Assert.Null(data.Equipped);

        var b = Inventory.Empty();
        b.Equipment.TryEquip(0, Own(b, GearFor(GearSlot.Head)));
        b.Import(data);
        Assert.Equal(GearStats.None, b.Equipment.Stats);
    }

    [Fact]
    public void GearIntentsRoundTripAndTheServerRulesJudgeThem()
    {
        foreach (var intent in new[] { InventoryIntent.EquipGear(2, 31), InventoryIntent.UnequipGear(6) })
        {
            using var writer = new PacketWriter(NetMessage.InventoryRequest);
            InventoryIntentCodec.Write(writer, intent);
            using var reader = new PacketReader(writer.ToArray());
            Assert.Equal(NetMessage.InventoryRequest, reader.Message);
            Assert.Equal(intent, InventoryIntentCodec.Read(reader));
        }

        var inventory = Inventory.Empty();
        var cap = Own(inventory, GearFor(GearSlot.Head));
        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.EquipGear((int)GearSlot.Chest, cap), false));
        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.EquipGear((int)GearSlot.Head, 4242), false));
        Assert.True(InventoryIntentRules.Apply(inventory, InventoryIntent.EquipGear((int)GearSlot.Head, cap), false));
        Assert.True(inventory.Equipment.IsEquipped(cap));
        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.UnequipGear((int)GearSlot.Chest), false));
        Assert.True(InventoryIntentRules.Apply(inventory, InventoryIntent.UnequipGear((int)GearSlot.Head), false));
        Assert.False(inventory.Equipment.IsEquipped(cap));
    }

    [Fact]
    public void MaxHealthFollowsGearWithoutHealingAndShrinksTheHealthWithIt()
    {
        var health = new PlayerHealth();
        health.TakeDamage(30);
        var before = health.Health;

        health.SetMaxBonus(50);
        Assert.Equal(PlayerHealth.MaxHealth + 50, health.Max);
        Assert.Equal(before, health.Health);

        health.Restore(health.Max);
        health.SetMaxBonus(0);
        Assert.Equal(PlayerHealth.MaxHealth, health.Health);
    }

    [Fact]
    public void ManaMaxAndRegenFollowGear()
    {
        var plain = new PlayerMana();
        var geared = new PlayerMana();
        geared.SetGear(40, 2f);
        Assert.Equal(plain.Max + 40, geared.Max);

        plain.TrySpend(plain.Max, creative: false);
        geared.TrySpend(geared.Max, creative: false);
        plain.Regen(5f);
        geared.Regen(5f);
        Assert.True(geared.Current / geared.Max > plain.Current / plain.Max);

        geared.SetGear(0, 1f);
        Assert.True(geared.Current <= geared.Max);
    }
}
