using System;
using silkgltriangle.Content;

namespace silkgltriangle;

/// <summary>
/// What a server accepts of a client's <see cref="InventoryIntent"/>, replayed against that
/// player's own <see cref="Inventory"/> with the same inventory calls the client ran
/// optimistically. GL-free; the throws (<see cref="NetInventoryAction.DropSelected"/>,
/// <see cref="NetInventoryAction.DropAllSelected"/>) need the world and are applied by the server
/// itself, so they are refused here.
/// <list type="bullet">
/// <item>A slot must lie on the hotbar.</item>
/// <item>Equip only points a slot at what the player owns: a stackable with a pool count, or a live
/// tool instance. Equipping what the slot already shows clears it (the panel's toggle), which needs
/// no ownership.</item>
/// <item>Craft runs <see cref="Inventory.TryCraft(CraftingRecipe, int, Func{int, CraftingRecipe.Ingredient, byte})"/>
/// with exactly the fed members, so it consumes from this inventory only and nothing when it fails.</item>
/// <item>Creative grants and clears need the player's server-owned creative flag.</item>
/// <item>Pick-block only selects or equips what the player owns (<see cref="Inventory.PickBlock"/>).</item>
/// </list>
/// </summary>
public static class InventoryIntentRules
{
    /// <summary>
    /// Applies <paramref name="intent"/> to <paramref name="inventory"/>; <paramref name="creative"/>
    /// is the player's server-owned creative flag. Returns whether the intent was accepted (a
    /// refusal changes nothing).
    /// </summary>
    public static bool Apply(Inventory inventory, in InventoryIntent intent, bool creative)
    {
        switch (intent.Action)
        {
            case NetInventoryAction.SelectSlot:
                if (!IsSlot(intent.Slot)) return false;
                inventory.Select(intent.Slot);
                return true;

            case NetInventoryAction.Equip:
                return Equip(inventory, intent);

            case NetInventoryAction.Craft:
                return Craft(inventory, intent);

            case NetInventoryAction.CreativeGrant:
                return creative && IsId(intent.Id) && intent.Count > 0
                    && inventory.Add((byte)intent.Id, intent.Count);

            case NetInventoryAction.CreativeClear:
                return creative && IsId(intent.Id) && inventory.RemoveAll((byte)intent.Id);

            case NetInventoryAction.PickBlock:
                return IsId(intent.Id) && inventory.PickBlock((byte)intent.Id);

            default:
                return false;
        }
    }

    /// <summary>The crafting recipe with this file name, or null.</summary>
    public static CraftingRecipe? RecipeNamed(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var recipes = ContentRegistry.CraftingRecipes;
        for (var i = 0; i < recipes.Count; i++)
            if (string.Equals(recipes[i].Name, name, StringComparison.Ordinal))
                return recipes[i];
        return null;
    }

    private static bool Equip(Inventory inventory, in InventoryIntent intent)
    {
        if (!IsSlot(intent.Slot)) return false;
        var slot = inventory.SlotAt(intent.Slot);

        if (intent.IsTool)
        {
            // Clicking the instance the slot already holds clears it; otherwise it must be alive
            var showing = slot.IsTool && slot.ToolInstance == intent.Id;
            if (!showing && (intent.Id <= 0 || inventory.GetToolStack(intent.Id) == null)) return false;
            inventory.EquipTool(intent.Slot, intent.Id);
            return true;
        }

        if (!IsId(intent.Id)) return false;
        var id = (byte)intent.Id;
        var shown = !slot.IsTool && slot.Id == id;
        if (!shown)
        {
            // A stackable pool entry the player holds; a tool-pool id is equipped by its instance
            if (Inventory.CategoryOf(id) is not (InventoryCategory.Blocks or InventoryCategory.Misc)) return false;
            if (inventory.CountFor(id) <= 0) return false;
        }
        inventory.EquipStackable(intent.Slot, id);
        return true;
    }

    private static bool Craft(Inventory inventory, in InventoryIntent intent)
    {
        var recipe = RecipeNamed(intent.Recipe);
        var members = intent.Members;
        if (recipe == null || members == null) return false;
        if (intent.Count < 1 || intent.Count > CraftingSession.MaxBatch) return false;
        if (members.Length != recipe.Ingredients.Length) return false;
        for (var i = 0; i < members.Length; i++)
            if (!recipe.Ingredients[i].Accepts(members[i]))
                return false;
        return inventory.TryCraft(recipe, intent.Count, (index, _) => members[index]);
    }

    private static bool IsSlot(int slot) => slot >= 0 && slot < Inventory.SlotCount;

    private static bool IsId(int id) => id is > 0 and <= byte.MaxValue;
}
