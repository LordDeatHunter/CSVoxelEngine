using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Config;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Items;
using VoxelEngine.Networking;
using VoxelEngine.Saves;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Chests;
using VoxelEngine.UI.Chest;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Chests;

/// <summary>
/// Chests: what moves between an inventory and a chest never appears or disappears, tools and gear keep their
/// components through a chest, a ruin chest rolls the same contents however it is first used, the block entity spills
/// when it goes, and the contents survive a save and the wire.
/// </summary>
public sealed class ChestTests
{
    public ChestTests() => TestContent.EnsureLoaded();

    private static byte Flint => ContentRegistry.RequireItem("flint").Id;
    private static byte Coal => ContentRegistry.RequireItem("coal").Id;
    private static byte Wooden => ContentRegistry.RequireBlock("wooden_chest").Id;
    private static byte RuinSurface => ContentRegistry.RequireBlock("ruin_chest_surface").Id;
    private static ChestProfile Profile(byte id) => BlockType.ById[id]!.Chest!;

    private static GearItem Cap => ItemRegistry.All.OfType<GearItem>().First(g => g.SourceName == "bronze_cap");

    private static (ChunkManager Manager, ChestManager Chests, DropManager Drops) World(byte chestBlock = 0)
    {
        var (manager, center) = MeshTestWorld.WalledArea(Vector3.Zero);
        if (chestBlock != 0) center.SetBlockRaw(4, 4, 4, chestBlock, 0);
        return (manager, new ChestManager(), new DropManager());
    }

    [Fact]
    public void TheChestProfilesComeFromTheBlockJson()
    {
        Assert.Equal(18, Profile(Wooden).Slots);
        Assert.Null(Profile(Wooden).LootTable);
        Assert.Equal("ruin_surface", Profile(RuinSurface).LootTable);
        Assert.True(Profile(ContentRegistry.RequireBlock("iron_chest").Id).Slots
                    > Profile(ContentRegistry.RequireBlock("bronze_chest").Id).Slots);
    }

    [Fact]
    public void DepositingMovesExactlyWhatWentAndNeverMoreThanHeld()
    {
        var inventory = Inventory.Empty();
        inventory.Add(Flint, 10);
        var chest = new ChestState(4);

        Assert.Equal(7, ChestTransfer.Deposit(inventory, chest, Flint, 7));
        Assert.Equal(3, inventory.CountFor(Flint));
        Assert.Equal(7, chest.CountOf(Flint));

        Assert.Equal(3, ChestTransfer.Deposit(inventory, chest, Flint, 99));
        Assert.Equal(0, inventory.CountFor(Flint));
        Assert.Equal(10, chest.CountOf(Flint));
        Assert.Equal(0, ChestTransfer.Deposit(inventory, chest, Flint, 1));
    }

    [Fact]
    public void AStackFillsItsSlotThenSpillsIntoTheNextAndAFullChestRefuses()
    {
        var chest = new ChestState(2);
        var placed = ChestTransfer.Place(chest, Flint, ChestConfig.MaxStack + 5);
        Assert.Equal(ChestConfig.MaxStack + 5, placed);
        Assert.Equal(ChestConfig.MaxStack, chest[0].Count);
        Assert.Equal(5, chest[1].Count);

        Assert.Equal(0, ChestTransfer.Place(chest, Coal, 1) );
        var full = new ChestState(1);
        ChestTransfer.Place(full, Flint, ChestConfig.MaxStack);
        Assert.Equal(0, ChestTransfer.Place(full, Flint, 3));
    }

    [Fact]
    public void WithdrawingStopsAtTheInventorysRoomAndTheRestStaysInTheChest()
    {
        var inventory = Inventory.Empty();
        inventory.Add(ContentRegistry.RequireItem("flint").Id, Inventory.MiscCapacity - 2);
        var chest = new ChestState(2);
        ChestTransfer.Place(chest, Coal, 10);

        var moved = ChestTransfer.Withdraw(inventory, chest, 0, 0);

        Assert.Equal(2, moved);
        Assert.Equal(8, chest.CountOf(Coal));
        Assert.Equal(2, inventory.CountFor(Coal));
        Assert.Equal(0, ChestTransfer.Withdraw(inventory, chest, 0, 0));
        Assert.Equal(8, chest.CountOf(Coal));
    }

    [Fact]
    public void ARandomRunOfMovesNeverCreatesOrLosesAnyUnit()
    {
        var rng = new Random(7);
        var inventory = Inventory.Empty();
        inventory.Add(Flint, 40);
        inventory.Add(Coal, 40);
        var chest = new ChestState(3);
        var ids = new[] { Flint, Coal };
        for (var i = 0; i < 400; i++)
        {
            var id = ids[rng.Next(2)];
            var move = rng.Next(3) switch
            {
                0 => ChestMove.DepositStack(id, rng.Next(0, 30)),
                1 => ChestMove.Withdraw(rng.Next(-1, 5), rng.Next(0, 30)),
                _ => ChestMove.DepositStack(id, 1),
            };
            ChestAccess.Apply(inventory, chest, move);
            foreach (var each in ids)
                Assert.Equal(40, inventory.CountFor(each) + chest.CountOf(each));
        }
    }

    [Fact]
    public void AToolOrGearKeepsEveryComponentThroughAChestAndGetsANewInstance()
    {
        var inventory = Inventory.Empty();
        var rolled = GearRoller.Roll(Cap, Rarity.Epic, 31);
        Assert.True(inventory.AddRolledGear(Cap.Id, rolled));
        var original = inventory.GearEntries()[0].InstanceId;
        var chest = new ChestState(3);

        Assert.True(ChestTransfer.DepositInstance(inventory, chest, original));
        Assert.Empty(inventory.GearEntries());
        Assert.Equal(1, chest[0].Count);
        Assert.NotNull(chest[0].Instance);

        Assert.Equal(1, ChestTransfer.Withdraw(inventory, chest, 0, 0));
        var back = inventory.GetToolStack(inventory.GearEntries()[0].InstanceId)!;
        Assert.Equal(rolled.Modifiers, back.Get<ModifiersComponent>());
        Assert.Equal(rolled.Name, back.ResolvedName);
        Assert.True(chest.IsEmpty);
    }

    [Fact]
    public void ADepositedWornPieceComesOffAndAFullGearPoolKeepsTheChestCopy()
    {
        var inventory = Inventory.Empty();
        inventory.Add(Cap.Id);
        var instance = inventory.GearEntries()[0].InstanceId;
        inventory.Equipment.TryEquip((int)GearSlot.Head, instance);
        var chest = new ChestState(2);

        Assert.True(ChestTransfer.DepositInstance(inventory, chest, instance));
        Assert.False(inventory.Equipment.IsEquipped(instance));

        while (inventory.Add(Cap.Id)) { }
        Assert.Equal(0, ChestTransfer.Withdraw(inventory, chest, 0, 0));
        Assert.False(chest[0].IsEmpty, "a refused withdraw leaves the piece in the chest");
    }

    [Fact]
    public void ARuinChestRollsTheSameContentsWhenOpenedAsWhenBrokenUnopened()
    {
        var opened = World(RuinSurface);
        var chest = opened.Chests.Open(opened.Manager, 4, 4, 4)!;
        var shown = Enumerable.Range(0, chest.SlotCount).Select(i => chest[i]).Where(s => !s.IsEmpty)
            .SelectMany(s => Enumerable.Repeat(s.Id, s.Count)).OrderBy(i => i).ToList();
        Assert.NotEmpty(shown);

        var broken = World(RuinSurface);
        broken.Chests.NotifyBlockRemoved(4, 4, 4, Profile(RuinSurface), 0, broken.Drops);
        var spilled = broken.Drops.Drops.Select(d => d.BlockId).OrderBy(i => i).ToList();

        Assert.Equal(shown, spilled);
    }

    [Fact]
    public void ARuinChestIsAPureFunctionOfItsCellAndTheWorldSeed()
    {
        var profile = Profile(RuinSurface);
        string Describe(ChestState c) => string.Join(",", Enumerable.Range(0, c.SlotCount)
            .Select(i => c[i]).Where(s => !s.IsEmpty).Select(s => $"{s.Id}x{s.Count}:{s.Instance?.ResolvedName}"));

        Assert.Equal(Describe(ChestLoot.Materialize(profile, 0, 10, 20, 30, 1)), Describe(ChestLoot.Materialize(profile, 0, 10, 20, 30, 1)));
        var contents = new HashSet<string>();
        for (var x = 0; x < 40; x++) contents.Add(Describe(ChestLoot.Materialize(profile, 0, x, 20, 30, 1)));
        Assert.True(contents.Count > 10, "different cells hold different loot");
        Assert.NotEqual(ChestLoot.SeedFor(1, 2, 3, 100), ChestLoot.SeedFor(1, 2, 3, 101));
    }

    [Fact]
    public void AnOrdinaryChestStartsEmptyAndANeverUsedChestHoldsNoState()
    {
        var world = World(Wooden);
        Assert.Equal(0, world.Chests.Count);
        Assert.True(world.Chests.Open(world.Manager, 4, 4, 4)!.IsEmpty);
        Assert.Equal(1, world.Chests.Count);
        Assert.Null(world.Chests.Open(world.Manager, 5, 5, 5));
    }

    [Fact]
    public void ABrokenChestSpillsWhatItHoldsAndForgetsTheCell()
    {
        var world = World(Wooden);
        var chest = world.Chests.Open(world.Manager, 4, 4, 4)!;
        ChestTransfer.Place(chest, Flint, 5);

        world.Chests.NotifyBlockRemoved(4, 4, 4, Profile(Wooden), 0, world.Drops);

        Assert.Equal(5, world.Drops.Drops.Count(d => d.BlockId == Flint));
        Assert.Null(world.Chests.Peek(4, 4, 4));
    }

    [Fact]
    public void AChestThatStoppedBeingOneWithoutABreakSpillsOnTheNextSweep()
    {
        var world = World(Wooden);
        var chest = world.Chests.Open(world.Manager, 4, 4, 4)!;
        ChestTransfer.Place(chest, Coal, 3);
        world.Manager.ChunkAt(4, 4, 4)!.SetBlockRaw(4, 4, 4, 0, 0);

        world.Chests.Update(world.Manager, world.Drops);

        Assert.Equal(3, world.Drops.Drops.Count(d => d.BlockId == Coal));
        Assert.Equal(0, world.Chests.Count);
    }

    [Fact]
    public void ContentsSurviveASaveAndTheWireWithEveryComponent()
    {
        var chest = new ChestState(6);
        ChestTransfer.Place(chest, Flint, 12);
        var rolled = GearRoller.Roll(Cap, Rarity.Rare, 77);
        var piece = new ItemStack(0, Cap);
        piece.Set(rolled.Modifiers);
        piece.Rename(rolled.Name);
        chest.Set(2, new ChestSlot(Cap.Id, 1, piece));

        var data = ChestSaveCodec.Export(1, 2, 3, chest);
        var restored = ChestSaveCodec.Import(data, [])!;
        Assert.Equal(chest.SlotCount, restored.SlotCount);
        Assert.Equal(12, restored.CountOf(Flint));
        Assert.Equal(rolled.Modifiers, restored[2].Instance!.Get<ModifiersComponent>());

        using var writer = new PacketWriter(NetMessage.ChestContents);
        ChestNetCodec.WriteOpenContents(writer, 1, 2, 3, chest);
        using var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.ChestContents, reader.Message);
        var read = ChestNetCodec.ReadContents(reader)!.Value;
        Assert.Equal((1, 2, 3), read.Cell);
        Assert.Equal(12, read.Contents.CountOf(Flint));
        Assert.Equal(rolled.Name, read.Contents[2].Instance!.ResolvedName);
    }

    [Fact]
    public void TheManagerRestoresWhatItCapturedAndASavedSlotOutOfRangeIsIgnored()
    {
        var world = World(Wooden);
        ChestTransfer.Place(world.Chests.Open(world.Manager, 4, 4, 4)!, Coal, 9);
        var saved = world.Chests.Capture();

        var again = new ChestManager();
        Assert.Equal(0, again.Restore(saved));
        Assert.Equal(9, again.Peek(4, 4, 4)!.CountOf(Coal));

        saved[0].Slots!.Add(new StationSaveData.ChestSlotData { Slot = 500, Name = "item/flint", Count = 3 });
        saved[0].Cell = [1, 2];
        var third = new ChestManager();
        Assert.Equal(1, third.Restore(saved));
        Assert.Equal(0, third.Count);
    }

    [Fact]
    public void TheWireRefusesAnUnknownMoveKindAndClampsAnAbsurdCount()
    {
        using var writer = new PacketWriter(NetMessage.ChestMove);
        writer.Write((byte)9);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        using var bad = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.ChestMove, bad.Message);
        Assert.Throws<System.IO.InvalidDataException>(() => ChestNetCodec.ReadMove(bad));

        using var big = new PacketWriter(NetMessage.ChestMove);
        ChestNetCodec.WriteMove(big, ChestMove.Withdraw(1, int.MaxValue));
        using var read = new PacketReader(big.ToArray());
        Assert.Equal(NetMessage.ChestMove, read.Message);
        Assert.Equal(ChestConfig.MaxStack, ChestNetCodec.ReadMove(read).Count);
    }

    [Fact]
    public void AccessNeedsAChestInReachAndAnUnknownDepositKindMovesNothing()
    {
        var world = World(Wooden);
        var near = new Vector3(4.5f, 5f, 6f);
        Assert.NotNull(ChestAccess.TryOpen(world.Chests, world.Manager, near, 4, 4, 4, 10f));
        Assert.Null(ChestAccess.TryOpen(world.Chests, world.Manager, near + new Vector3(0, 0, 40), 4, 4, 4, 10f));

        var inventory = Inventory.Empty();
        inventory.Add(Flint, 3);
        var chest = new ChestState(2);
        Assert.Equal(0, ChestAccess.Apply(inventory, chest, new ChestMove((ChestMoveKind)7)));
        Assert.Equal(0, ChestAccess.Apply(inventory, chest, ChestMove.DepositStack(0, 1)));
        Assert.Equal(3, inventory.CountFor(Flint));
    }

    [Fact]
    public void ThePanelListsOnlyTheMatchingNonEmptySlotsInSlotOrder()
    {
        var chest = new ChestState(6);
        ChestTransfer.Place(chest, Flint, 3);
        ChestTransfer.Place(chest, Coal, 4);
        var view = new ChestView();
        view.Show((0, 0, 0), "Chest", chest);

        Assert.Equal([0, 1], view.Rows());
        view.Search.Text.Set("coal");
        Assert.Equal([1], view.Rows());
        view.Search.Text.Set("zzz");
        Assert.Empty(view.Rows());

        view.Search.Clear();
        chest.Set(0, ChestSlot.Empty);
        Assert.Equal([1], view.Rows());
        view.Close();
        Assert.Empty(view.Rows());
    }

    [Fact]
    public void ChestCardHitTestsEqualTheDrawnRows()
    {
        var screen = new Vector2(1920f, 1080f);
        const int rowCount = 6;
        for (var row = 0; row < rowCount; row++)
        {
            var rect = ChestCardLayout.RowRect(row, screen, 0f);
            Assert.Equal(row, ChestCardLayout.RowAt(rect.Center, screen, 0f, rowCount));
        }
        Assert.Null(ChestCardLayout.RowAt(ChestCardLayout.RowRect(rowCount, screen, 0f).Center, screen, 0f, rowCount));
        var card = ChestCardLayout.Card(screen);
        Assert.True(card.Right <= screen.X);
        Assert.True(card.Left >= InventoryPanelLayout.PanelLeft + InventoryPanelLayout.PanelWidth(screen));
    }
}
