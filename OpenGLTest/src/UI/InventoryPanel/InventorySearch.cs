using System;
using System.Collections.Generic;
using VoxelEngine.Content;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Items;

namespace VoxelEngine.UI.InventoryPanel;

/// <summary>What one search hit refers to: an item the player owns, or a crafting recipe.</summary>
public enum SearchHitKind
{
    /// <summary>An id the player's pools or hotbar hold, equippable from the panel.</summary>
    Inventory,

    /// <summary>A crafting recipe, opened on the Craft tab.</summary>
    Recipe,
}

/// <summary>
/// One row the inventory's search field offers. Inventory hits carry the id's pool category,
/// its stock count and, for a tool, the instance to equip; recipe hits carry the recipe the
/// Craft tab should open. <see cref="Id"/> is the item/block id either way.
/// </summary>
public readonly record struct SearchHit(
    SearchHitKind Kind,
    byte Id,
    string Name,
    InventoryCategory Category,
    int ToolInstance,
    int Count,
    CraftingRecipe? Recipe)
{
    public bool IsRecipe => Kind == SearchHitKind.Recipe;
}

/// <summary>
/// GL-free matching behind the inventory panel's search field. A query narrows the panel to
/// whatever it can act on right now: the items the player actually owns (blocks, misc and one
/// row per tool kind) and the crafting recipes that make or consume a matching thing. The
/// ranking puts a name that starts with the query ahead of a mere substring, and an owned item
/// ahead of a recipe that produces it, so typing a few letters surfaces what is in hand first.
/// </summary>
public static class InventorySearch
{
    /// <summary>Rows for <paramref name="query"/> over <paramref name="inventory"/>'s stock and
    /// every compiled recipe, best match first; an empty query matches nothing. In creative mode
    /// <paramref name="includeCatalog"/> widens the inventory side to every item that exists, so
    /// a query reaches something the player has not collected yet.</summary>
    public static IReadOnlyList<SearchHit> Search(string? query, Inventory inventory, bool includeCatalog = false)
    {
        var q = query?.Trim() ?? "";
        if (q.Length == 0) return [];

        var hits = new List<SearchHit>();
        var owned = new HashSet<byte>();
        AddInventoryHits(q, inventory, hits, owned);
        if (includeCatalog)
            AddCatalogHits(q, inventory, hits, owned);
        AddRecipeHits(q, hits);

        hits.Sort((a, b) =>
        {
            var rank = Rank(a.Name, q).CompareTo(Rank(b.Name, q));
            if (rank != 0) return rank;
            var kind = a.Kind.CompareTo(b.Kind);
            if (kind != 0) return kind;
            var name = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            if (name != 0) return name;
            return a.Id.CompareTo(b.Id);
        });
        return hits;
    }

    private static void AddInventoryHits(string query, Inventory inventory, List<SearchHit> hits,
        HashSet<byte> owned)
    {
        AddStacks(query, inventory, InventoryCategory.Blocks, hits, owned);
        AddStacks(query, inventory, InventoryCategory.Misc, hits, owned);

        // One row per tool kind, pointing at its first live instance: tools are instances, but
        // a search should offer "Bucket", not one row per duplicate.
        var seen = new HashSet<byte>();
        foreach (var tool in inventory.ToolEntries())
        {
            if (!seen.Add(tool.ItemId)) continue;
            owned.Add(tool.ItemId);
            if (!Matches(tool.Name, query)) continue;
            hits.Add(new SearchHit(SearchHitKind.Inventory, tool.ItemId, tool.Name,
                InventoryCategory.Tools, tool.InstanceId, inventory.CountFor(tool.ItemId), null));
        }
    }

    private static void AddStacks(string query, Inventory inventory, InventoryCategory category,
        List<SearchHit> hits, HashSet<byte> owned)
    {
        foreach (var row in inventory.StackEntries(category))
        {
            owned.Add(row.Id);
            if (!Matches(row.Name, query)) continue;
            hits.Add(new SearchHit(SearchHitKind.Inventory, row.Id, row.Name, category, 0, row.Count, null));
        }
    }

    private static void AddCatalogHits(string query, Inventory inventory, List<SearchHit> hits,
        HashSet<byte> owned)
    {
        foreach (var item in ItemRegistry.All)
        {
            if (Inventory.CategoryOf(item.Id) is not { } category) continue;
            if (!owned.Add(item.Id)) continue;
            if (!Matches(item.Name, query)) continue;
            hits.Add(new SearchHit(SearchHitKind.Inventory, item.Id, item.Name, category, 0,
                inventory.CountFor(item.Id), null));
        }
    }

    private static void AddRecipeHits(string query, List<SearchHit> hits)
    {
        foreach (var recipe in ContentRegistry.CraftingRecipes)
        {
            var name = Inventory.DisplayName(recipe.ResultId);
            if (!Matches(name, query) && !IngredientMatches(recipe, query)) continue;
            hits.Add(new SearchHit(SearchHitKind.Recipe, recipe.ResultId, name,
                InventoryCategory.Crafting, 0, recipe.ResultCount, recipe));
        }
    }

    private static bool IngredientMatches(CraftingRecipe recipe, string query)
    {
        foreach (var ingredient in recipe.Ingredients)
        {
            if (!ingredient.IsTag)
            {
                if (Matches(Inventory.DisplayName(ingredient.Id), query)) return true;
                continue;
            }
            foreach (var member in ingredient.AcceptedIds)
                if (Matches(Inventory.DisplayName(member), query))
                    return true;
        }
        return false;
    }

    /// <summary>Case-insensitive substring match (the font covers ASCII, so ordinal compare is exact).</summary>
    private static bool Matches(string name, string query) =>
        name.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>0 when the name starts with the query, else 1; the sort's primary key.</summary>
    private static int Rank(string name, string query) =>
        name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
}
