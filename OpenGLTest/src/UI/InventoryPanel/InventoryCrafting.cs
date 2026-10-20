using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content;
using VoxelEngine.Content.Recipes;

namespace VoxelEngine.UI.InventoryPanel;

/// <summary>
/// The crafting rules of an <see cref="Inventory"/>: whether a recipe or a batch of it is
/// affordable with room for the result, and spending the ingredients for it. Static over the
/// inventory it is handed, whose pools it reads and changes in place; <see cref="Inventory"/>
/// forwards its public crafting methods here.
/// </summary>
internal static class InventoryCrafting
{
    /// <inheritdoc cref="Inventory.Craftable"/>
    internal static IReadOnlyList<CraftingRecipe> Craftable(Inventory inventory) =>
        ContentRegistry.CraftingRecipes.Where(inventory.CanCraft).ToArray();

    /// <inheritdoc cref="Inventory.CanCraftBatch"/>
    internal static bool CanCraftBatch(Inventory inventory, CraftingRecipe recipe, int batch,
        Func<int, CraftingRecipe.Ingredient, byte>? pickMember)
    {
        if (batch < 1) return false;
        for (var i = 0; i < recipe.Ingredients.Length; i++)
        {
            var ingredient = recipe.Ingredients[i];
            var need = ingredient.Count * batch;
            if (pickMember != null)
            {
                var picked = pickMember(i, ingredient);
                if (!ingredient.Accepts(picked) || inventory.CountFor(picked) < need) return false;
            }
            else if (AvailableFor(inventory, ingredient) < need) return false;
        }

        var category = Inventory.CategoryOf(recipe.ResultId);
        if (!category.HasValue) return false;
        return inventory.UsedOf(category.Value) - FreedBy(recipe, category.Value) * batch
            + recipe.ResultCount * batch <= Inventory.CapacityOf(category.Value);
    }

    /// <inheritdoc cref="Inventory.TryCraft(CraftingRecipe, int, Func{int, CraftingRecipe.Ingredient, byte})"/>
    internal static bool TryCraft(Inventory inventory, CraftingRecipe recipe, int batch,
        Func<int, CraftingRecipe.Ingredient, byte>? pickMember)
    {
        if (!CanCraftBatch(inventory, recipe, batch, pickMember)) return false;
        for (var i = 0; i < recipe.Ingredients.Length; i++)
        {
            var ingredient = recipe.Ingredients[i];
            var need = ingredient.Count * batch;
            if (pickMember != null)
                RemoveUnits(inventory, pickMember(i, ingredient), need);
            else if (ingredient.IsTag)
            {
                foreach (var member in ingredient.AcceptedIds)
                {
                    if (need <= 0) break;
                    var take = Math.Min(need, inventory.CountFor(member));
                    if (take > 0)
                    {
                        RemoveUnits(inventory, member, take);
                        need -= take;
                    }
                }
            }
            else RemoveUnits(inventory, ingredient.Id, need);
        }
        return inventory.Add(recipe.ResultId, recipe.ResultCount * batch);
    }

    private static int AvailableFor(Inventory inventory, CraftingRecipe.Ingredient ingredient)
    {
        var total = 0;
        foreach (var id in ingredient.AcceptedIds)
            total += inventory.CountFor(id);
        return total;
    }

    private static int FreedBy(CraftingRecipe recipe, InventoryCategory category)
    {
        var freed = 0;
        foreach (var ingredient in recipe.Ingredients)
        {
            if (ingredient.IsTag)
            {
                if (ingredient.AcceptedIds.All(id => Inventory.CategoryOf(id) == category))
                    freed += ingredient.Count;
            }
            else if (Inventory.CategoryOf(ingredient.Id) == category) freed += ingredient.Count;
        }
        return freed;
    }

    private static void RemoveUnits(Inventory inventory, byte id, int count)
    {
        if (Inventory.CategoryOf(id) == InventoryCategory.Tools)
        {
            foreach (var instance in inventory.Tools.Where(t => t.DefinitionId == id).Take(count).ToArray())
                inventory.RemoveTool(instance.InstanceId);
            return;
        }
        var store = inventory.StoreOf(id);
        if (store == null) return;
        var left = store.GetValueOrDefault(id) - count;
        if (left <= 0)
        {
            store.Remove(id);
            inventory.ClearSlotsShowing(id);
        }
        else store[id] = left;
    }
}
