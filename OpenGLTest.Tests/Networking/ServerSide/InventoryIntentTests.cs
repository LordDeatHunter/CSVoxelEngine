using System.IO;
using System.Linq;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Magic;
using VoxelEngine.Networking;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Saves;
using VoxelEngine.Tests.Magic;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Networking.ServerSide;

/// <summary>
/// The inventory intents a client sends the server: every action reads back exactly what was
/// written, a structurally impossible frame is malformed rather than applied, and the rules the
/// server replays them with never let a refused intent change anything.
/// </summary>
public sealed class InventoryIntentTests
{
    public InventoryIntentTests() => TestContent.EnsureLoaded();

    private static InventoryIntent RoundTrip(InventoryIntent intent)
    {
        using var writer = new PacketWriter(NetMessage.InventoryRequest);
        InventoryIntentCodec.Write(writer, intent);
        using var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.InventoryRequest, reader.Message);
        return InventoryIntentCodec.Read(reader);
    }

    [Fact]
    public void EveryActionRoundTripsTheFieldsItUses()
    {
        Assert.Equal(InventoryIntent.Select(7), RoundTrip(InventoryIntent.Select(7)));
        Assert.Equal(InventoryIntent.Drop(false), RoundTrip(InventoryIntent.Drop(false)));
        Assert.Equal(InventoryIntent.Drop(true), RoundTrip(InventoryIntent.Drop(true)));
        Assert.Equal(InventoryIntent.EquipStackable(4, 200), RoundTrip(InventoryIntent.EquipStackable(4, 200)));
        Assert.Equal(InventoryIntent.EquipTool(9, 31), RoundTrip(InventoryIntent.EquipTool(9, 31)));
        Assert.Equal(InventoryIntent.CreativeGrant(17, 100), RoundTrip(InventoryIntent.CreativeGrant(17, 100)));
        Assert.Equal(InventoryIntent.CreativeClear(17), RoundTrip(InventoryIntent.CreativeClear(17)));
        Assert.Equal(InventoryIntent.PickBlock(42), RoundTrip(InventoryIntent.PickBlock(42)));
        Assert.Equal(InventoryIntent.SelectStaffSlot(3), RoundTrip(InventoryIntent.SelectStaffSlot(3)));

        // Records compare arrays by reference, so the craft is checked field by field
        var craft = RoundTrip(InventoryIntent.Craft("some_recipe", 3, [5, 6, 7]));
        Assert.Equal(NetInventoryAction.Craft, craft.Action);
        Assert.Equal("some_recipe", craft.Recipe);
        Assert.Equal(3, craft.Count);
        Assert.Equal(new ushort[] { 5, 6, 7 }, craft.Members);

        var bind = RoundTrip(InventoryIntent.BindStaffSlot(2, ["element/fire", "shape/self"]));
        Assert.Equal(NetInventoryAction.BindStaffSlot, bind.Action);
        Assert.Equal(2, bind.Slot);
        Assert.Equal(new[] { "element/fire", "shape/self" }, bind.Parts);

        var clear = RoundTrip(InventoryIntent.ClearStaffSlot(1));
        Assert.Equal(NetInventoryAction.ClearStaffSlot, clear.Action);
        Assert.Equal(1, clear.Slot);

        // A bind with no list goes out empty
        var empty = RoundTrip(new InventoryIntent(NetInventoryAction.BindStaffSlot, Slot: 0));
        Assert.Equal(NetInventoryAction.BindStaffSlot, empty.Action);
        Assert.Empty(empty.Parts!);
    }

    [Fact]
    public void TheHighestIdRoundTripsInEveryFieldThatCarriesAnId()
    {
        const ushort top = ushort.MaxValue;
        Assert.Equal(InventoryIntent.CreativeGrant(top, 9), RoundTrip(InventoryIntent.CreativeGrant(top, 9)));
        Assert.Equal(InventoryIntent.CreativeClear(top), RoundTrip(InventoryIntent.CreativeClear(top)));
        Assert.Equal(InventoryIntent.PickBlock(top), RoundTrip(InventoryIntent.PickBlock(top)));
        Assert.Equal(InventoryIntent.EquipStackable(1, top), RoundTrip(InventoryIntent.EquipStackable(1, top)));

        var craft = RoundTrip(InventoryIntent.Craft("some_recipe", 2, [top, BlockType.Air, top]));
        Assert.Equal(2, craft.Count);
        Assert.Equal(new[] { top, BlockType.Air, top }, craft.Members);
    }

    [Fact]
    public void ABindWithTooManyPartsOrAnOverlongKeyIsMalformed()
    {
        // More parts than a bind may carry
        using (var writer = new PacketWriter(NetMessage.InventoryRequest))
        {
            writer.Write((byte)NetInventoryAction.BindStaffSlot);
            writer.Write(0);
            writer.Write((byte)(InventoryIntentCodec.MaxSpellParts + 1));
            using var reader = new PacketReader(writer.ToArray());
            _ = reader.Message;
            Assert.Throws<InvalidDataException>(() => InventoryIntentCodec.Read(reader));
        }

        // A part key past the length cap
        using (var writer = new PacketWriter(NetMessage.InventoryRequest))
        {
            writer.Write((byte)NetInventoryAction.BindStaffSlot);
            writer.Write(0);
            writer.Write((byte)1);
            writer.Write(new string('k', InventoryIntentCodec.MaxPartKeyLength + 1));
            using var reader = new PacketReader(writer.ToArray());
            _ = reader.Message;
            Assert.Throws<InvalidDataException>(() => InventoryIntentCodec.Read(reader));
        }

        // The writer refuses to send one
        using (var writer = new PacketWriter(NetMessage.InventoryRequest))
        {
            var parts = Enumerable.Repeat("element/fire", InventoryIntentCodec.MaxSpellParts + 1).ToArray();
            Assert.Throws<System.ArgumentException>(() => InventoryIntentCodec.Write(writer, InventoryIntent.BindStaffSlot(0, parts)));
        }
    }

    [Fact]
    public void AStaffBindNeedsAStaffInHandAndKnownParts()
    {
        var keys = StaffFixture.ValidRecipe(StaffFixture.Staff("wooden_staff")).Select(p => p.Key).ToArray();

        // No staff in hand
        Assert.False(InventoryIntentRules.Apply(Inventory.Empty(), InventoryIntent.BindStaffSlot(0, keys), creative: true));

        var inventory = StaffFixture.Holding("wooden_staff", out _, out var stack);
        var before = stack.Get<StaffComponent>();

        // No list at all
        Assert.False(InventoryIntentRules.Apply(inventory, new InventoryIntent(NetInventoryAction.BindStaffSlot), creative: true));

        // A part the player does not know
        inventory.ReplaceKnownParts(VoxelEngine.Content.Spells.SpellPartRegistry.All.Select(p => p.Key).Where(k => k != keys[1]));
        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.BindStaffSlot(0, keys), creative: false));
        Assert.Equal(before, stack.Get<StaffComponent>());
    }

    [Fact]
    public void AValidKnownBindIsAcceptedAndAClearUnbindsIt()
    {
        var inventory = StaffFixture.Holding("wooden_staff", out var staff, out var stack);
        StaffFixture.KnowAll(inventory);
        var keys = StaffFixture.ValidRecipe(staff).Select(p => p.Key).ToArray();

        Assert.True(InventoryIntentRules.Apply(inventory, InventoryIntent.BindStaffSlot(0, keys), creative: false));
        Assert.Equal(keys, stack.Get<StaffComponent>().PartsAt(0));

        Assert.True(InventoryIntentRules.Apply(inventory, InventoryIntent.ClearStaffSlot(0), creative: false));
        Assert.False(stack.Get<StaffComponent>().IsBound(0));
        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.ClearStaffSlot(0), creative: false));
    }

    [Fact]
    public void AStructurallyImpossibleIntentIsMalformed()
    {
        // An action byte no build sends
        using (var writer = new PacketWriter(NetMessage.InventoryRequest))
        {
            writer.Write((byte)250);
            using var reader = new PacketReader(writer.ToArray());
            _ = reader.Message;
            Assert.Throws<InvalidDataException>(() => InventoryIntentCodec.Read(reader));
        }

        // More fed members than any recipe can have
        using (var writer = new PacketWriter(NetMessage.InventoryRequest))
        {
            writer.Write((byte)NetInventoryAction.Craft);
            writer.Write("recipe");
            writer.Write(1);
            writer.Write((byte)(InventoryIntentCodec.MaxCraftMembers + 1));
            using var reader = new PacketReader(writer.ToArray());
            _ = reader.Message;
            Assert.Throws<InvalidDataException>(() => InventoryIntentCodec.Read(reader));
        }

        // A frame cut short
        using (var writer = new PacketWriter(NetMessage.InventoryRequest))
        {
            writer.Write((byte)NetInventoryAction.Equip);
            writer.Write(2);
            using var reader = new PacketReader(writer.ToArray());
            _ = reader.Message;
            Assert.Throws<EndOfStreamException>(() => InventoryIntentCodec.Read(reader));
        }
    }

    [Fact]
    public void ARefusedIntentChangesNothing()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var inventory = Inventory.Empty();
        inventory.Add(stone, 3);
        var before = PlayerSaveCodec.ToBytes(new PlayerSaveData { Inventory = inventory.Export() });

        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.Select(Inventory.SlotCount), creative: false));
        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.Select(-1), creative: false));
        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.EquipStackable(Inventory.SlotCount, stone), creative: false));
        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.Craft("no_such_recipe", 1, []), creative: false));
        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.CreativeGrant(stone, 1), creative: false));
        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.CreativeGrant(stone, 0), creative: true));
        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.CreativeClear(stone), creative: false));
        // The world applies the throws; the inventory rules never do
        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.Drop(true), creative: true));

        var after = PlayerSaveCodec.ToBytes(new PlayerSaveData { Inventory = inventory.Export() });
        Assert.Equal(before, after);
    }

    [Fact]
    public void AnIdPastTheLoadedContentIsRefusedWithoutThrowing()
    {
        // The first id this build's content does not assign: a client with other content could name it
        var outside = BlockTables.IdCount;
        var inventory = Inventory.Empty();
        var before = PlayerSaveCodec.ToBytes(new PlayerSaveData { Inventory = inventory.Export() });

        Assert.False(InventoryIntentRules.Apply(inventory,
            new InventoryIntent(NetInventoryAction.CreativeGrant, Id: outside, Count: 1), creative: true));
        Assert.False(InventoryIntentRules.Apply(inventory,
            new InventoryIntent(NetInventoryAction.CreativeClear, Id: outside), creative: true));
        Assert.False(InventoryIntentRules.Apply(inventory,
            new InventoryIntent(NetInventoryAction.PickBlock, Id: outside), creative: true));
        Assert.False(InventoryIntentRules.Apply(inventory,
            new InventoryIntent(NetInventoryAction.Equip, Slot: 0, Id: outside), creative: true));

        var after = PlayerSaveCodec.ToBytes(new PlayerSaveData { Inventory = inventory.Export() });
        Assert.Equal(before, after);
    }

    [Fact]
    public void AStaffSlotSelectionNeedsAStaffInHand()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var inventory = Inventory.Empty();
        inventory.Add(stone, 3);
        inventory.EquipStackable(0, stone);
        inventory.Select(0);
        var before = PlayerSaveCodec.ToBytes(new PlayerSaveData { Inventory = inventory.Export() });

        // Bare hand or a block in hand: there is no spell slot to select
        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.SelectStaffSlot(0), creative: true));
        Assert.Equal(before, PlayerSaveCodec.ToBytes(new PlayerSaveData { Inventory = inventory.Export() }));
    }

    [Fact]
    public void AStaffSlotSelectionChangesOnlyTheHeldStaffsSelectedSlot()
    {
        var inventory = StaffFixture.Holding("crystal_staff", out var staff, out var stack);
        Assert.True(staff.Slots >= 2, "the selection needs a staff with a second slot");
        StaffFixture.Bind(staff, stack, 0, out _);
        var hotbar = inventory.SelectedIndex;
        var instance = inventory.SelectedToolInstance;
        var bindings = Enumerable.Range(0, staff.Slots).Select(i => stack.Get<StaffComponent>().PartsAt(i).ToArray()).ToArray();
        var start = stack.Get<StaffComponent>().Selected;
        var target = (start + 1) % staff.Slots;

        // A slot the staff lacks is refused and keeps the selection
        foreach (var slot in new[] { -1, staff.Slots, SpellConfig.StaffMaxSlots })
        {
            Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.SelectStaffSlot(slot), creative: false));
            Assert.Equal(start, stack.Get<StaffComponent>().Selected);
        }

        Assert.True(InventoryIntentRules.Apply(inventory, InventoryIntent.SelectStaffSlot(target), creative: false));
        Assert.Equal(target, stack.Get<StaffComponent>().Selected);
        Assert.Equal(target, HeldStaff.SelectedSlot(inventory));

        // Nothing else moved: the hotbar, the held instance and every binding stay as they were
        Assert.Equal(hotbar, inventory.SelectedIndex);
        Assert.Equal(instance, inventory.SelectedToolInstance);
        for (var i = 0; i < staff.Slots; i++)
            Assert.Equal(bindings[i], stack.Get<StaffComponent>().PartsAt(i).ToArray());
    }

    [Fact]
    public void ACraftMustFeedEveryIngredientWithAMemberItAccepts()
    {
        // The first recipe an inventory holding exactly its first-member ingredients can craft
        CraftingRecipe? recipe = null;
        Inventory? inventory = null;
        ushort[] members = [];
        foreach (var candidate in ContentRegistry.CraftingRecipes)
        {
            var stocked = Inventory.Empty();
            var fed = new ushort[candidate.Ingredients.Length];
            for (var i = 0; i < fed.Length; i++)
            {
                fed[i] = candidate.Ingredients[i].AcceptedIds[0];
                stocked.Add(fed[i], candidate.Ingredients[i].Count);
            }
            if (fed.Length == 0 || !stocked.CanCraftBatch(candidate, 1, (index, _) => fed[index]))
                continue;
            (recipe, inventory, members) = (candidate, stocked, fed);
            break;
        }
        Assert.NotNull(recipe);
        Assert.NotNull(inventory);
        var before = PlayerSaveCodec.ToBytes(new PlayerSaveData { Inventory = inventory!.Export() });

        // One member short, or a batch outside 1..MaxBatch: refused before anything is spent
        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.Craft(recipe.Name, 1, members[..^1]), creative: false));
        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.Craft(recipe.Name, 0, members), creative: false));
        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.Craft(recipe.Name, CraftingSession.MaxBatch + 1, members), creative: false));
        Assert.Equal(before, PlayerSaveCodec.ToBytes(new PlayerSaveData { Inventory = inventory.Export() }));

        // Fed exactly, it crafts
        Assert.True(InventoryIntentRules.Apply(inventory, InventoryIntent.Craft(recipe.Name, 1, members), creative: false));
        Assert.True(inventory.CountFor(recipe.ResultId) > 0);
    }

    [Fact]
    public void EquipNeedsOwnershipAndAnEquippedSlotTogglesOff()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var inventory = Inventory.Empty();

        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.EquipStackable(1, stone), creative: false));
        Assert.Equal(0, inventory.IdAt(1));

        inventory.Add(stone, 1);
        Assert.True(InventoryIntentRules.Apply(inventory, InventoryIntent.EquipStackable(1, stone), creative: false));
        Assert.Equal(stone, inventory.IdAt(1));

        // Equipping what the slot already shows clears it, the panel's toggle
        Assert.True(InventoryIntentRules.Apply(inventory, InventoryIntent.EquipStackable(1, stone), creative: false));
        Assert.Equal(0, inventory.IdAt(1));
    }
}
