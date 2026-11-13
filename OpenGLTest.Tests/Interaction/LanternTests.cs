using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Networking;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Saves;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Chests;
using VoxelEngine.Simulation.Lanterns;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.Lod;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.Interaction;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

/// <summary>Lantern conservation through the same inventory, click and edit paths the server uses.</summary>
[Collection("Worldgen seed")]
public sealed class LanternTests : IDisposable
{
    private readonly SupportTestWorld _test;
    private Inventory Inventory => _test.Player.Inventory;
    private World World => _test.World;
    private static ushort Lantern => ContentRegistry.RequireBlock("lantern").Id;
    private static ushort Coal => ContentRegistry.RequireItem("coal").Id;
    private static ushort Slime => ContentRegistry.RequireItem("slimeball").Id;
    private static ushort Everfuel => ContentRegistry.RequireItem("everfuel").Id;

    public LanternTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
        _test = new SupportTestWorld();
        Inventory.Import(new PlayerSaveData.InventoryData());
        _test.Load(4, _test.Top + 2, 4);
    }
    public void Dispose() => _test.Dispose();

    private ItemStack AcquireLantern()
    {
        Assert.True(Inventory.Add(Lantern));
        var instance = Inventory.ToolEntries().Where(r => r.ItemId == Lantern).Last().InstanceId;
        if (Inventory.SelectedToolInstance != instance) Inventory.EquipTool(Inventory.SelectedIndex, instance);
        return Inventory.GetToolStack(instance)!;
    }
    private LanternState Fuel(ItemStack stack, ushort id, int count)
    {
        Assert.True(Inventory.Add(id, count));
        var state = LanternState.Of(stack)!;
        Assert.Equal(count, state.Move(Inventory, ChestMove.DepositStack(id, count)));
        return state;
    }
    private bool Click(out RightClickOutcome outcome) => World.TryUseSelected(World.InteractionReach, _test.Player.Context, out outcome);
    private void AimAt(Vector3 cell) => _test.Player.Aim(new Vector3(cell.X + .5f, cell.Y + .5f, cell.Z + 2.5f), -Vector3.UnitZ);

    [Theory]
    [InlineData(false, 7)]
    [InlineData(false, 2)]
    [InlineData(false, -1)]
    [InlineData(true, 7)]
    [InlineData(true, 2)]
    [InlineData(true, -1)]
    public void GroundPickupEquipsOnlyThePreferredEmptySlotAndPreservesEachLantern(bool dropped, int target)
    {
        var stack = AcquireLantern();
        var fuel = Fuel(stack, Coal, 3);
        fuel.Advance(LanternState.Duration(Coal) * .4f);
        var remaining = fuel.Remaining;
        var cell = new Vector3(4, _test.Top, 4);
        Assert.True(World.TryPlaceSelectedBlockAt(_test.Player.Context, cell, Vector3.UnitY));
        var other = AcquireLantern();
        var otherFuel = Fuel(other, Slime, 2);
        Inventory.Add(_test.Stone, autoEquip: false);
        for (var i = 0; i < Inventory.SlotCount; i++) Inventory.EquipStackable(i, _test.Stone);
        Inventory.Select(7);
        if (target >= 0) Inventory.ClearSlot(2);
        if (target == 7) Inventory.ClearSlot(7);
        var before = Inventory.Hotbar.ToArray();
        if (dropped)
        {
            Assert.True(_test.Break(4, _test.Top + 1, 4));
            var drop = Assert.Single(World.Drops);
            _test.Player.Position = drop.Position + Vector3.UnitY * DropManager.CollectHeightOffset;
            for (var i = 0; i < 120 && World.Drops.Count > 0; i++) World.Pickup.Update(1d / 60);
            Assert.Empty(World.Drops);
            Assert.True(_test.Player.InventoryChanged);
        }
        else
        {
            AimAt(cell + Vector3.UnitY);
            _test.Player.Context.RemoteSneaking = true;
            Assert.True(Click(out var outcome));
            Assert.Equal(RightClickOutcome.LanternPickup, outcome);
            Assert.Null(World.Lanterns.Peek((4, _test.Top + 1, 4)));
        }
        var picked = Inventory.Tools.Last();
        Assert.NotEqual(other.InstanceId, picked.InstanceId);
        Assert.Equal(remaining, LanternState.Of(picked)!.Remaining);
        Assert.Equal(2, LanternState.Of(picked)!.Storage.CountOf(Coal));
        Assert.Equal(2, otherFuel.Storage.CountOf(Slime));
        Assert.Equal(7, Inventory.SelectedIndex); // Pickup does not switch the player's chosen slot.
        for (var i = 0; i < Inventory.SlotCount; i++)
            if (i == target) Assert.Equal(picked.InstanceId, Inventory.ToolInstanceAt(i));
            else Assert.Equal(before[i], Inventory.Hotbar[i]);
        var mirror = Inventory.Empty();
        mirror.Import(InventoryNetCodec.Decode(InventoryNetCodec.Encode(Inventory.Export())));
        Assert.Equal(Inventory.SelectedIndex, mirror.SelectedIndex);
        Assert.Equal(Inventory.Hotbar, mirror.Hotbar);
        Assert.Equal(remaining, LanternState.Of(mirror.GetToolStack(picked.InstanceId))!.Remaining);
    }

    [Fact]
    public void RefusedInstancePickupLeavesEquipmentAndBothFuelOwnersUntouched()
    {
        var stack = AcquireLantern();
        var fuel = Fuel(stack, Coal, 3);
        fuel.Advance(.5f);
        Inventory.Add(Lantern, Inventory.ToolCapacity, autoEquip: false);
        Inventory.Select(7);
        var before = InventoryNetCodec.Encode(Inventory.Export());
        Assert.False(DropPickup.Credit(Inventory, Lantern, stack));
        var drops = new DropManager();
        drops.SpawnInstance(stack, _test.Player.Position - Vector3.UnitY * DropManager.CollectHeightOffset,
            Vector3.Zero, delay: 0f);
        var pickup = new DropPickup(World, drops, _test.Manager, null, new VoxelEngine.Particles.FireParticleSystem(),
            serverMode: true, clientMode: false);
        pickup.Update(1d / 60);
        var refused = Assert.Single(drops.Drops);
        Assert.Equal(fuel.Remaining, LanternState.Of(refused.Instance)!.Remaining);
        Assert.False(_test.Player.InventoryChanged);
        Assert.Equal(before, InventoryNetCodec.Encode(Inventory.Export()));
        Assert.Equal(2, fuel.Storage.CountOf(Coal));
        Assert.True(fuel.Remaining > 0f);
    }

    [Fact]
    public void PayloadlessGroundPickupEquipsANewLanternEvenWhenAnotherIsOwned()
    {
        var other = AcquireLantern();
        var otherFuel = Fuel(other, Coal, 2);
        Inventory.Select(7);
        Assert.True(DropPickup.Credit(Inventory, Lantern, null));
        Assert.Equal(Lantern, Inventory.SelectedBlockId);
        Assert.NotEqual(other.InstanceId, Inventory.SelectedToolInstance);
        Assert.False(LanternState.Held(Inventory)!.HasFuel);
        Assert.Equal(2, otherFuel.Storage.CountOf(Coal));
    }

    [Fact]
    public void CardFuelUsesTheLiveInstanceFractionForEmptyPartialAndEverfuel()
    {
        var stack = AcquireLantern();
        var fuel = LanternState.Of(stack)!;
        var view = new VoxelEngine.UI.Chest.ChestView();
        foreach (var instance in new[] { 0, stack.InstanceId })
        {
            view.ShowLantern(default, instance, fuel);
            Assert.Same(fuel, view.LanternFuel);
            Assert.Equal(VoxelEngine.UI.Hud.ItemTooltip.Hover(Vector2.Zero, Lantern, stack, null).BarFraction,
                view.LanternFuel!.Fraction);
        }
        Fuel(stack, Coal, 1);
        fuel.Advance(LanternState.Duration(Coal) * .4f);
        Assert.InRange(view.LanternFuel!.Fraction, .59f, .61f);
        Fuel(stack, Everfuel, 1);
        Assert.True(view.LanternFuel.Endless);
        Assert.Equal(1f, view.LanternFuel.Fraction);
        var screen = new Vector2(1920, 1080);
        var bar = VoxelEngine.UI.Chest.ChestCardLayout.FuelBar(screen);
        Assert.True(bar.Top >= VoxelEngine.UI.Chest.ChestCardLayout.Title(screen).Bottom);
        Assert.True(bar.Bottom <= VoxelEngine.UI.Chest.ChestCardLayout.SearchBox(screen).Top);
    }

    [Fact]
    public void AuthoritativeCraftsConsumeIngredientsAndCreateDistinctEmptyLanterns()
    {
        var recipe = ContentRegistry.CraftingRecipes.Single(r => r.ResultId == Lantern);
        var members = recipe.Ingredients.Select(i => i.AcceptedIds[0]).ToArray();
        for (var i = 0; i < recipe.Ingredients.Length; i++)
            Inventory.Add(members[i], recipe.Ingredients[i].Count * 2);
        var intent = InventoryIntent.Craft(recipe.Name, 2, members);
        Assert.True(InventoryIntentRules.Apply(Inventory, intent, creative: false));
        var instances = Inventory.Tools.Where(t => t.DefinitionId == Lantern).ToArray();
        Assert.Equal(recipe.ResultCount * 2, instances.Length);
        Assert.Equal(instances.Length, instances.Select(t => t.InstanceId).Distinct().Count());
        Assert.All(instances, t => Assert.False(LanternState.Of(t)!.HasFuel));
        var fuel = Fuel(instances[0], Coal, 1);
        fuel.Advance(.1f);
        Assert.False(LanternState.Of(instances[1])!.HasFuel);
        Assert.False(InventoryIntentRules.Apply(Inventory, intent, creative: false));
        Assert.DoesNotContain(ContentRegistry.CraftingRecipes, r => r.ResultId == Everfuel);
    }

    [Fact]
    public void FuelIsConsumedOnDemandAndSlimeBurnsForOneQuarter()
    {
        var a = Fuel(AcquireLantern(), Coal, 2);
        var b = Fuel(AcquireLantern(), Slime, 2);
        a.Advance(.25f); b.Advance(.25f);
        Assert.Equal(LanternState.Duration(Coal), (b.Remaining + .25f) / LanternConfig.SlimeDurationShare, 4);
        Assert.Equal(1, a.Storage.CountOf(Coal));
        a.Advance(a.Remaining);
        Assert.Equal(1, a.Storage.CountOf(Coal)); // Exact exhaustion never pre-consumes the next lump.
        a.Advance(.5f);
        Assert.Equal(0, a.Storage.CountOf(Coal));
        Assert.Equal(LanternState.Duration(Coal) - .5f, a.Remaining, 4);
        a.Advance(a.Remaining + 1f);
        Assert.False(a.HasFuel);
        Assert.Equal(0f, a.Fraction);
    }

    [Fact]
    public void TaggedStorageTransfersConserveCountsAndRefuseNonFuelAndInstances()
    {
        var stack = AcquireLantern();
        var state = Fuel(stack, Coal, 3);
        var stone = _test.Stone;
        Inventory.Add(stone, 5);
        var total = Inventory.CountFor(Coal) + state.Storage.CountOf(Coal);
        Assert.Equal(0, state.Move(Inventory, ChestMove.DepositStack(stone, 2)));
        Assert.Equal(0, state.Move(Inventory, ChestMove.DepositInstance(stack.InstanceId)));
        Assert.Equal(2, state.Move(Inventory, ChestMove.Withdraw(0, 2)));
        Assert.Equal(total, Inventory.CountFor(Coal) + state.Storage.CountOf(Coal));
        Assert.Equal(0, state.Move(Inventory, ChestMove.Withdraw(LanternConfig.Slots, 0)));
        Assert.Equal(1, Inventory.CountFor(Lantern));
    }

    [Fact]
    public void EverfuelRequiresCreativeAcquisitionAndSuspendsBothFuelAndItemConsumption()
    {
        var state = Fuel(AcquireLantern(), Coal, 2);
        state.Advance(2f);
        var before = state.Remaining;
        Assert.False(InventoryIntentRules.Apply(Inventory, InventoryIntent.CreativeGrant(Everfuel, 1), creative: false));
        Assert.True(InventoryIntentRules.Apply(Inventory, InventoryIntent.CreativeGrant(Everfuel, 1), creative: true));
        Assert.Equal(1, state.Move(Inventory, ChestMove.DepositStack(Everfuel, 1)));
        state.Advance(LanternState.Duration(Coal) * 20f);
        Assert.Equal(before, state.Remaining);
        Assert.Equal(1, state.Storage.CountOf(Coal));
        Assert.Equal(1, state.Storage.CountOf(Everfuel));
        Assert.True(state.Endless);
        Assert.Equal(1f, state.Fraction);
        var slot = Enumerable.Range(0, state.Storage.SlotCount).Single(i => state.Storage[i].Id == Everfuel);
        state.Move(Inventory, ChestMove.Withdraw(slot, 0));
        state.Advance(.5f);
        Assert.Equal(before - .5f, state.Remaining);
    }

    [Fact]
    public void OnlySelectedAndPlacedLanternsBurnAndCopiesDoNotAlias()
    {
        var a = AcquireLantern();
        var fuelA = Fuel(a, Coal, 3);
        var b = AcquireLantern();
        var fuelB = Fuel(b, Slime, 2);
        World.Lanterns.UpdateHeld(.5f, World, serverMode: true);
        Assert.Equal(0f, fuelA.Remaining);
        Assert.True(fuelB.Remaining > 0f);
        Inventory.ClearSlot(Inventory.SelectedIndex);
        var paused = fuelB.Remaining;
        World.Lanterns.UpdateHeld(3f, World, serverMode: true);
        Assert.Equal(paused, fuelB.Remaining);
        var chest = new ChestState(2);
        Assert.True(ChestTransfer.DepositInstance(Inventory, chest, b.InstanceId));
        World.Lanterns.UpdateHeld(3f, World, serverMode: true);
        Assert.Equal(paused, LanternState.Of(chest[0].Instance)!.Remaining);
        Assert.Equal(1, ChestTransfer.Withdraw(Inventory, chest, 0, 1));
        var returned = Inventory.Tools.Last();
        Assert.Equal(paused, LanternState.Of(returned)!.Remaining);
        LanternState.Of(returned)!.Advance(.25f);
        Assert.Equal(paused, fuelB.Remaining); // A transfer copied mutable state, it did not alias the source.
    }

    [Fact]
    public void RealClickPrecedenceOpensInAirSneaksPastDoorsAndPicksUpPlacedBeforeHeld()
    {
        var stack = AcquireLantern();
        var fuel = Fuel(stack, Coal, 2);
        _test.Player.Aim(_test.Player.Position, Vector3.UnitY);
        Assert.True(Click(out var air)); Assert.Equal(RightClickOutcome.Lantern, air);
        var door = BlockType.Types.Values.First(t => t.UpperHalfId != 0 && t.Openable);
        _test.Set(4, _test.Top + 1, 4, door.Id);
        AimAt(new Vector3(4, _test.Top + 1, 4));
        Assert.True(Click(out var normal)); Assert.Equal(RightClickOutcome.Station, normal);
        _test.Player.Context.RemoteSneaking = true;
        Assert.True(Click(out var sneak)); Assert.Equal(RightClickOutcome.Lantern, sneak);
        _test.Set(4, _test.Top + 1, 4, 0);
        _test.Player.Context.RemoteSneaking = false;
        Assert.True(World.TryPlaceSelectedBlockAt(_test.Player.Context, new Vector3(4, _test.Top, 4), Vector3.UnitY));
        Assert.Null(Inventory.GetToolStack(stack.InstanceId));
        var cell = (4, _test.Top + 1, 4);
        var placed = LanternState.Of(World.Lanterns.Peek(cell))!;
        Assert.Equal(2, placed.Storage.CountOf(Coal));
        World.Lanterns.Update(.5f, _test.Manager, new DropManager());
        var burned = placed.Remaining;
        Assert.True(burned > 0f);
        AcquireLantern();
        AimAt(new Vector3(cell.Item1, cell.Item2, cell.Item3));
        Assert.True(Click(out var open)); Assert.Equal(RightClickOutcome.Lantern, open);
        Assert.Equal(0, World.RightClick.Lantern.Instance);
        _test.Player.Context.RemoteSneaking = true;
        Assert.True(Click(out var pickup)); Assert.Equal(RightClickOutcome.LanternPickup, pickup);
        Assert.Equal(BlockType.Air, _test.IdAt(cell.Item1, cell.Item2, cell.Item3));
        Assert.Null(World.Lanterns.Peek(cell));
        var picked = LanternState.Of(Inventory.Tools.Last())!;
        Assert.Equal(burned, picked.Remaining);
        Assert.Equal(1, picked.Storage.CountOf(Coal));
        Assert.Empty(World.Drops);
        Assert.Equal(2, fuel.Storage.CountOf(Coal));
    }

    [Fact]
    public void ABlastDropsOneCompleteLanternAndTheStaleSweepDoesNotDuplicateIt()
    {
        var state = Fuel(AcquireLantern(), Coal, 3);
        state.Advance(.5f);
        Assert.True(World.TryPlaceSelectedBlockAt(_test.Player.Context, new Vector3(4, _test.Top, 4), Vector3.UnitY));
        var drops = new DropManager();
        drops.InstanceLoot = (id, cell) => World.Lanterns.DropRemoved(id, cell, drops);
        var tnt = new TntManager();
        tnt.Spawn(new Vector3(4, _test.Top + 1, 4), ContentRegistry.RequireBlock("tnt").Id, 0f);
        tnt.Update(.0001f, _test.Manager, new LodManager(null!), drops, new FluidManager(), new FallingBlockManager());
        World.Lanterns.Update(.1f, _test.Manager, drops);
        var drop = Assert.Single(drops.Drops, d => d.BlockId == Lantern);
        Assert.Equal(state.Remaining, LanternState.Of(drop.Instance)!.Remaining);
        Assert.Equal(2, LanternState.Of(drop.Instance)!.Storage.CountOf(Coal));
        Assert.Null(World.Lanterns.Peek((4, _test.Top + 1, 4)));
        var saved = EntitySaveCodec.TryParse(EntitySaveCodec.ToBytes(new EntitySaveData
        {
            Drops = drops.Drops.Select(d => new EntitySaveData.DropData
            {
                Item = SaveNames.NameOf(d.BlockId),
                Instance = d.Instance == null ? null : InventorySaveCodec.ExportInstance(d.Instance, []),
            }).ToList(),
        }))!;
        var persisted = Assert.Single(saved.Drops!, d => d.Item == SaveNames.NameOf(Lantern));
        var restored = InventorySaveCodec.ImportInstance(0, persisted.Instance!, [])!;
        Assert.Equal(state.Remaining, LanternState.Of(restored)!.Remaining);
        Assert.Equal(2, LanternState.Of(restored)!.Storage.CountOf(Coal));
    }

    [Fact]
    public void FullInventoryPickupKeepsThePlacedFuelUntouched()
    {
        var state = Fuel(AcquireLantern(), Coal, 2);
        var cell = new Vector3(4, _test.Top, 4);
        Assert.True(World.TryPlaceSelectedBlockAt(_test.Player.Context, cell, Vector3.UnitY));
        Inventory.Add(Lantern, Inventory.ToolCapacity);
        var before = InventoryNetCodec.Encode(Inventory.Export());
        AimAt(cell + Vector3.UnitY);
        _test.Player.Context.RemoteSneaking = true;
        Assert.True(Click(out var result)); Assert.Equal(RightClickOutcome.LanternPickup, result);
        Assert.Equal(Lantern, _test.IdAt(4, _test.Top + 1, 4));
        Assert.Equal(before, InventoryNetCodec.Encode(Inventory.Export()));
        Assert.Equal(2, LanternState.Of(World.Lanterns.Peek((4, _test.Top + 1, 4)))!.Storage.CountOf(Coal));
        Assert.Equal(2, state.Storage.CountOf(Coal));
    }

    [Fact]
    public void DropPlaceSaveAndWireRoundTripsRetainAnIndividualFuelPayload()
    {
        var stack = AcquireLantern();
        var fuel = Fuel(stack, Coal, 2); fuel.Advance(.75f);
        var remaining = fuel.Remaining;
        var inv = Inventory.Empty(); inv.Import(InventoryNetCodec.Decode(InventoryNetCodec.Encode(Inventory.Export())));
        var clone = inv.GetToolStack(stack.InstanceId)!;
        Assert.Equal(remaining, LanternState.Of(clone)!.Remaining);
        Assert.True(World.ThrowSelected(false, _test.Player.Position, Vector3.UnitX, Inventory));
        Assert.Null(Inventory.GetToolStack(stack.InstanceId));
        var drop = Assert.Single(World.Drops);
        using var writer = new PacketWriter(NetMessage.EntityList);
        NetEntityCodec.Write(writer, new NetEntity(NetEntityKind.Drop, 1, drop.Position, 0, 0, 0, drop.BlockId,
            drop.Velocity, Instance: InventorySaveCodec.ExportInstance(drop.Instance!, new List<string>())));
        using var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.EntityList, reader.Message);
        var wire = NetEntityCodec.Read(reader);
        var restoredDrop = InventorySaveCodec.ImportInstance(0, wire.Instance!, new List<string>())!;
        Assert.Equal(remaining, LanternState.Of(restoredDrop)!.Remaining);
        Assert.True(DropPickup.Credit(Inventory, wire.ItemId, restoredDrop));
        var instance = Inventory.Tools.Last();
        Assert.Equal(instance.InstanceId, Inventory.SelectedToolInstance);
        Assert.True(World.TryPlaceSelectedBlockAt(_test.Player.Context, new Vector3(4, _test.Top, 4), Vector3.UnitY));
        var saved = StationSaveCodec.TryParse(StationSaveCodec.ToBytes(new StationSaveData { Lanterns = World.Lanterns.Capture() }))!;
        var restored = new LanternManager(); restored.Restore(saved.Lanterns);
        Assert.Equal(remaining, LanternState.Of(restored.Peek((4, _test.Top + 1, 4)))!.Remaining);
        Assert.Equal(1, LanternState.Of(restored.Peek((4, _test.Top + 1, 4)))!.Storage.CountOf(Coal));
    }

    [Fact]
    public void LegacyCountsAndHotbarMigrateWithoutToolCapLossAndSurviveAnotherSave()
    {
        var count = Inventory.ToolCapacity + 3;
        var legacy = new PlayerSaveData.InventoryData
        {
            Blocks = [new() { Name = "block/lantern", Count = count }],
            Hotbar = [new() { IsTool = false, Name = "block/lantern" }],
        };
        var inventory = Inventory.Empty(); inventory.Import(legacy);
        Assert.Equal(count, inventory.CountFor(Lantern));
        Assert.True(inventory.SelectedToolInstance > 0);
        Assert.Equal(Lantern, inventory.SelectedBlockId);
        var reloaded = Inventory.Empty(); reloaded.Import(inventory.Export());
        Assert.Equal(count, reloaded.CountFor(Lantern));
        Assert.Equal(count, reloaded.ToolEntries().Select(t => t.InstanceId).Distinct().Count());
    }

    [Fact]
    public void UnsupportedMetadataFailsExplicitlyAndUnloadedPlacedLanternsPause()
    {
        Assert.Throws<System.IO.InvalidDataException>(() => LanternCodec.Import(new LanternData { Version = 99 }));
        Inventory.Add(Coal, 2);
        Assert.Throws<System.IO.InvalidDataException>(() => Inventory.Import(new PlayerSaveData.InventoryData
        {
            Tools = [new() { Name = "block/lantern", InstanceId = 1, Lantern = new LanternData { Version = 99 } }],
        }));
        foreach (var name in new[] { "item/missing-lantern", "item/coal" })
            Assert.Throws<System.IO.InvalidDataException>(() => Inventory.Import(new PlayerSaveData.InventoryData
            {
                Tools = [new() { Name = name, InstanceId = 1, Lantern = new LanternData() }],
            }));
        Assert.Equal(2, Inventory.CountFor(Coal));
        var fuel = Fuel(AcquireLantern(), Coal, 2);
        var data = LanternCodec.Export(fuel); data.Cell = [100000, 100000, 100000]; data.Block = "block/lantern";
        var manager = new LanternManager(); manager.Restore([data]);
        manager.Update(100f, _test.Manager, new DropManager());
        var paused = LanternState.Of(manager.Peek((100000, 100000, 100000)))!;
        Assert.Equal(2, paused.Storage.CountOf(Coal));
        Assert.Equal(0f, paused.Remaining);
    }
}
