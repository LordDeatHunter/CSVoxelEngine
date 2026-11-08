using System;
using System.Linq;
using VoxelEngine.Config;
using VoxelEngine.Items;
using VoxelEngine.Player;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Player;

/// <summary>What a body is drawn wearing: the visible armor pieces of its equipment, and what a peer may claim over the wire.</summary>
public sealed class ArmorLookTests
{
    public ArmorLookTests() => TestContent.EnsureLoaded();

    private static byte Id(string item) => ContentRegistry.RequireItem(item).Id;

    [Fact]
    public void AWornVisiblePieceShowsInItsSlotAndAnAccessoryNever()
    {
        var inventory = Inventory.Empty();
        Assert.True(inventory.Add(Id("bronze_cap")));
        Assert.True(inventory.Add(Id("reserve_charm")));
        foreach (var entry in inventory.GearEntries().ToList())
            Assert.True(inventory.Equipment.TryEquipAuto(entry.InstanceId));

        var look = ArmorLook.From(inventory.Equipment);

        Assert.Equal(Id("bronze_cap"), look.PieceAt((int)GearSlot.Head));
        Assert.Equal(0, look.PieceAt((int)GearSlot.Chest));
        Assert.False(look.IsNone);
    }

    [Fact]
    public void ThePiecesSurviveTheWireAndAPeerCannotPutAPieceInTheWrongSlot()
    {
        var wire = new byte[GearConfig.ArmorSlotCount];
        wire[(int)GearSlot.Feet] = Id("bronze_boots");
        wire[(int)GearSlot.Head] = Id("bronze_boots");
        wire[(int)GearSlot.Chest] = Id("flint");

        var look = ArmorLook.FromWire(wire);

        Assert.Equal(Id("bronze_boots"), look.PieceAt((int)GearSlot.Feet));
        Assert.Equal(0, look.PieceAt((int)GearSlot.Head));
        Assert.Equal(0, look.PieceAt((int)GearSlot.Chest));
        var back = new byte[GearConfig.ArmorSlotCount];
        look.CopyTo(back);
        Assert.True(ArmorLook.FromWire(back).Equals(look));
    }

    [Fact]
    public void NothingWornIsTheSharedEmptyLook()
    {
        Assert.Same(ArmorLook.None, ArmorLook.From(Inventory.Empty().Equipment));
        Assert.Same(ArmorLook.None, ArmorLook.FromWire(new byte[GearConfig.ArmorSlotCount]));
    }

    [Fact]
    public void EveryArmorSlotCoversSomePartOfTheRig()
    {
        for (var slot = 0; slot < GearConfig.ArmorSlotCount; slot++)
            Assert.NotEmpty(ArmorLook.PartsOf(slot));
    }
}
