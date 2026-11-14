using System.Linq;
using VoxelEngine.Networking;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Simulation.Lanterns;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.UI.InventoryPanel;

/// <summary>
/// The offhand slot beside the hotbar: while a use runs for it the selected-slot readers address it, it shares the
/// hotbar's pools and clearing rules, it survives a save and a server's replay, and what it holds gives light.
/// </summary>
public sealed class OffhandTests
{
    public OffhandTests() => TestContent.EnsureLoaded();

    private static ushort Stone => ContentRegistry.RequireBlock("stone").Id;
    private static ushort Torch => ContentRegistry.RequireBlock("torch").Id;

    private static Inventory WithStoneSelectedAndTorchOffhand()
    {
        var inventory = Inventory.Empty();
        Assert.True(inventory.Add(Stone, 5, autoEquip: false));
        Assert.True(inventory.Add(Torch, 3, autoEquip: false));
        inventory.EquipStackable(0, Stone);
        inventory.Select(0);
        inventory.EquipStackable(Inventory.OffhandIndex, Torch);
        return inventory;
    }

    [Fact]
    public void TheSelectedReadersAddressTheOffhandOnlyWhileItsScopeIsOpen()
    {
        var inventory = WithStoneSelectedAndTorchOffhand();
        Assert.Equal(Stone, inventory.SelectedBlockId);

        using (inventory.UseHand(offhand: true))
        {
            Assert.Equal(Torch, inventory.SelectedBlockId);
            Assert.Equal(3, inventory.SelectedCount);
        }

        Assert.Equal(Stone, inventory.SelectedBlockId);
        Assert.False(inventory.OffhandActive);
    }

    [Fact]
    public void SpendingInTheOffhandScopeSpendsTheOffhandsStackAndClearsItWhenItRunsOut()
    {
        var inventory = WithStoneSelectedAndTorchOffhand();
        using (inventory.UseHand(offhand: true))
            for (var i = 0; i < 3; i++)
                Assert.True(inventory.ConsumeSelected());

        Assert.Equal(5, inventory.CountFor(Stone));
        Assert.Equal(0, inventory.CountFor(Torch));
        Assert.True(inventory.Offhand.IsEmpty);
    }

    [Fact]
    public void SwappingHandsExchangesTheSelectedSlotAndTheOffhand()
    {
        var inventory = WithStoneSelectedAndTorchOffhand();

        Assert.True(inventory.SwapHands());
        Assert.Equal(Torch, inventory.SelectedBlockId);
        Assert.Equal(Stone, inventory.OffhandId);

        inventory.ClearSlot(inventory.SelectedIndex);
        inventory.ClearSlot(Inventory.OffhandIndex);
        Assert.False(inventory.SwapHands());
    }

    [Fact]
    public void AMoveBetweenAHotbarSlotAndTheOffhandSwapsThem()
    {
        var inventory = WithStoneSelectedAndTorchOffhand();

        Assert.True(inventory.MoveSlot(0, Inventory.OffhandIndex));
        Assert.Equal(Torch, inventory.IdAt(0));
        Assert.Equal(Stone, inventory.OffhandId);
        Assert.False(inventory.MoveSlot(Inventory.OffhandIndex, Inventory.OffhandIndex));
    }

    [Fact]
    public void ADroppedToolLeavesNoGhostInTheOffhand()
    {
        var inventory = Inventory.Empty();
        var bucket = ContentRegistry.RequireItem("bucket").Id;
        Assert.True(inventory.Add(bucket, 1, autoEquip: false));
        var instance = inventory.ToolEntries().Single().InstanceId;
        Assert.True(inventory.EquipTool(Inventory.OffhandIndex, instance));
        Assert.Equal(bucket, inventory.OffhandId);

        Assert.True(inventory.RemoveTool(instance));
        Assert.True(inventory.Offhand.IsEmpty);
    }

    [Fact]
    public void TheOffhandSurvivesASaveAndAnOldSaveLoadsWithoutOne()
    {
        var a = WithStoneSelectedAndTorchOffhand();
        var b = Inventory.Empty();
        b.Import(a.Export());
        Assert.Equal(Torch, b.OffhandId);

        var data = a.Export();
        data.Offhand = null;
        b.Import(data);
        Assert.True(b.Offhand.IsEmpty);
    }

    [Fact]
    public void TheServerAppliesOffhandIntentsLikeHotbarOnesAndRefusesGear()
    {
        var inventory = Inventory.Empty();
        Assert.True(inventory.Add(Torch, 2, autoEquip: false));

        Assert.True(InventoryIntentRules.Apply(inventory, InventoryIntent.EquipStackable(Inventory.OffhandIndex, Torch), creative: false));
        Assert.Equal(Torch, inventory.OffhandId);
        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.EquipStackable(Inventory.OffhandIndex + 1, Torch), creative: false));
        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.Select(Inventory.OffhandIndex), creative: false));

        Assert.True(InventoryIntentRules.Apply(inventory, InventoryIntent.SwapHands(), creative: false));
        Assert.True(inventory.Offhand.IsEmpty);
        Assert.Equal(Torch, inventory.SelectedBlockId);

        Assert.True(InventoryIntentRules.Apply(inventory, InventoryIntent.ClearSlot(inventory.SelectedIndex), creative: false));
        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.SwapHands(), creative: false));
    }

    [Fact]
    public void TheSwapIntentRoundTripsThroughTheWire()
    {
        using var writer = new PacketWriter(NetMessage.InventoryRequest);
        InventoryIntentCodec.Write(writer, InventoryIntent.SwapHands());
        using var reader = new PacketReader(writer.ToArray());
        _ = reader.Message;
        var read = InventoryIntentCodec.Read(reader);
        Assert.Equal(NetInventoryAction.SwapHands, read.Action);
    }

    [Fact]
    public void AnItemInEitherHandGivesLightAndOnlyTheHandHoldingItDoes()
    {
        var inventory = WithStoneSelectedAndTorchOffhand();

        Assert.True(LanternManager.Emits(inventory));
        Assert.True(LanternManager.EmitsFrom(inventory, offhand: true));
        Assert.False(LanternManager.EmitsFrom(inventory, offhand: false));

        inventory.ClearSlot(Inventory.OffhandIndex);
        Assert.False(LanternManager.Emits(inventory));
    }
}
