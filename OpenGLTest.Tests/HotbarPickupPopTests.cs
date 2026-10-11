using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The hotbar's pickup pop reads each slot's (item, count): only a rise of the same item pops,
/// never a fall or a change of item, and a pop always settles back to exactly the rest scale. The
/// inventory cases cover a singleplayer pickup and a client's snapshot import alike.
/// </summary>
public sealed class HotbarPickupPopTests
{
    private const float Dt = 1f / 60f;

    [Fact]
    public void ARisingCountOfTheSameItemStartsThePop()
    {
        var pops = new HotbarPickupPop();
        Assert.False(pops.Observe(2, 5, 10));
        Assert.True(pops.Observe(2, 5, 11));

        pops.Advance(WorldGenerationConfig.HotbarPopSeconds * 0.25f);
        Assert.True(pops.ScaleAt(2) > 1f);
        // Only the slot that rose pops
        Assert.Equal(1f, pops.ScaleAt(3));
    }

    [Fact]
    public void AFallingCountDoesNotPop()
    {
        var pops = new HotbarPickupPop();
        pops.Observe(0, 5, 10);
        Assert.False(pops.Observe(0, 5, 9));
        pops.Advance(Dt);
        Assert.Equal(1f, pops.ScaleAt(0));
    }

    [Fact]
    public void AnItemChangeInTheSlotDoesNotPop()
    {
        var pops = new HotbarPickupPop();
        pops.Observe(0, 5, 10);
        // A different item with a bigger count (an equip) is not a pickup
        Assert.False(pops.Observe(0, 6, 40));
        // Nor is an empty slot gaining an item
        pops.Observe(1, 0, 0);
        Assert.False(pops.Observe(1, 7, 3));
    }

    [Fact]
    public void TheFirstLookAtASlotNeverPops()
    {
        var pops = new HotbarPickupPop();
        Assert.False(pops.Observe(4, 5, 64));
        Assert.Equal(1f, pops.ScaleAt(4));
    }

    [Fact]
    public void ABrokenToolsSlotFlashesAndFadesOut()
    {
        var pops = new HotbarPickupPop();
        Assert.Equal(0f, pops.FlashAt(3));
        pops.Flash(3);
        Assert.Equal(1f, pops.FlashAt(3));
        Assert.Equal(0f, pops.FlashAt(4));

        pops.Advance(WorldGenerationConfig.HotbarFlashSeconds * 0.5f);
        var half = pops.FlashAt(3);
        Assert.True(half > 0f && half < 1f, $"a half-flashed slot read {half}");

        pops.Advance(WorldGenerationConfig.HotbarFlashSeconds);
        Assert.Equal(0f, pops.FlashAt(3));
        // An out-of-range slot never flashes and never throws
        pops.Flash(-1);
        pops.Flash(Inventory.SlotCount + 5);
        Assert.Equal(0f, pops.FlashAt(Inventory.SlotCount + 5));
    }

    [Fact]
    public void APopReturnsToExactlyTheRestScale()
    {
        var pops = new HotbarPickupPop();
        pops.Observe(0, 5, 1);
        pops.Observe(0, 5, 2);

        var grew = false;
        var frames = (int)(WorldGenerationConfig.HotbarPopSeconds / Dt) + 5;
        for (var i = 0; i < frames; i++)
        {
            pops.Advance(Dt);
            grew |= pops.ScaleAt(0) > 1f;
        }
        Assert.True(grew);
        Assert.Equal(1f, pops.ScaleAt(0));
    }

    [Fact]
    public void APickupPopsTheSlotButAPlacementOrAnEquipDoesNot()
    {
        TestContent.EnsureLoaded();
        var blocks = BlockType.Types.Values.Where(t => t.HasItem && !t.Fluid).Take(2).ToArray();
        var inventory = Inventory.Empty();
        var pops = new HotbarPickupPop();

        // The first find auto-equips into the empty slot: a new item there, not a pop
        inventory.Add(blocks[0].Id, 5);
        Assert.False(Updated(pops, inventory));

        // More of it is a pickup
        inventory.Add(blocks[0].Id, 3);
        Assert.True(Updated(pops, inventory));

        // Placing one spends from the slot
        Assert.True(inventory.ConsumeSelected());
        Assert.False(Updated(pops, inventory));

        // Equipping another item over the slot changes what it shows
        inventory.Add(blocks[1].Id, 50);
        Updated(pops, inventory);
        inventory.EquipStackable(inventory.SelectedIndex, blocks[1].Id);
        Assert.False(Updated(pops, inventory));
    }

    [Fact]
    public void AClientSnapshotWithMoreOfAnItemPopsItsSlot()
    {
        TestContent.EnsureLoaded();
        var block = BlockType.Types.Values.First(t => t.HasItem && !t.Fluid);
        var server = Inventory.Empty();
        server.Add(block.Id, 4);

        // The client's mirror only ever imports the server's snapshots
        var client = Inventory.Empty();
        client.Import(server.Export());
        var pops = new HotbarPickupPop();
        Assert.False(Updated(pops, client));

        server.Add(block.Id, 2);
        client.Import(server.Export());
        Assert.True(Updated(pops, client));

        server.ConsumeSelected();
        client.Import(server.Export());
        Assert.False(Updated(pops, client));
    }

    // One frame of the HUD: whether any slot started a pop.
    private static bool Updated(HotbarPickupPop pops, Inventory inventory)
    {
        var any = false;
        pops.Advance(Dt);
        for (var i = 0; i < Inventory.SlotCount; i++)
            any |= pops.Observe(i, inventory.IdAt(i), inventory.CountAt(i));
        return any;
    }
}
