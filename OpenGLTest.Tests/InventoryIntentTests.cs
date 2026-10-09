using System;
using System.IO;
using Xunit;

namespace silkgltriangle.Tests;

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

        // Records compare arrays by reference, so the craft is checked field by field
        var craft = RoundTrip(InventoryIntent.Craft("some_recipe", 3, [5, 6, 7]));
        Assert.Equal(NetInventoryAction.Craft, craft.Action);
        Assert.Equal("some_recipe", craft.Recipe);
        Assert.Equal(3, craft.Count);
        Assert.Equal(new byte[] { 5, 6, 7 }, craft.Members);
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
    public void ACraftMustFeedEveryIngredientWithAMemberItAccepts()
    {
        // The first recipe an inventory holding exactly its first-member ingredients can craft
        CraftingRecipe? recipe = null;
        Inventory? inventory = null;
        byte[] members = [];
        foreach (var candidate in ContentRegistry.CraftingRecipes)
        {
            var stocked = Inventory.Empty();
            var fed = new byte[candidate.Ingredients.Length];
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
