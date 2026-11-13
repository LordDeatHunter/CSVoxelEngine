using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.UI.InventoryPanel;

public sealed class InventorySearchTests
{
    public InventorySearchTests() => TestContent.EnsureLoaded();

    [Fact]
    public void EmptyQueryMatchesNothing()
    {
        var inventory = TestInventory.Stocked();

        Assert.Empty(InventorySearch.Search(null, inventory));
        Assert.Empty(InventorySearch.Search("", inventory));
        Assert.Empty(InventorySearch.Search("   ", inventory));
    }

    [Fact]
    public void FindsAnOwnedBlockByItsName()
    {
        var inventory = TestInventory.Stocked();
        var stone = ContentRegistry.RequireBlock("stone");

        var hits = InventorySearch.Search(stone.DisplayName, inventory);

        var hit = Assert.Single(hits, h => h.Kind == SearchHitKind.Inventory && h.Id == stone.Id);
        Assert.Equal(InventoryCategory.Blocks, hit.Category);
        Assert.True(hit.Count > 0);
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        var inventory = TestInventory.Stocked();
        var stone = ContentRegistry.RequireBlock("stone");

        var hits = InventorySearch.Search(stone.DisplayName.ToUpperInvariant(), inventory);

        Assert.Contains(hits, h => h.Kind == SearchHitKind.Inventory && h.Id == stone.Id);
    }

    [Fact]
    public void FindsARecipeByItsResultName()
    {
        var inventory = TestInventory.Stocked();
        var pickaxe = ContentRegistry.RequireItem("primitive_pickaxe");

        var hits = InventorySearch.Search(Inventory.DisplayName(pickaxe.Id), inventory, creative: true);

        Assert.Contains(hits, h => h.IsRecipe && h.Id == pickaxe.Id && h.Recipe != null);
    }

    [Fact]
    public void FindsARecipeByAnIngredientName()
    {
        var inventory = TestInventory.Stocked();
        var flint = ContentRegistry.RequireItem("flint");

        var hits = InventorySearch.Search(Inventory.DisplayName(flint.Id), inventory, creative: true);

        Assert.Contains(hits, h => h.IsRecipe
            && h.Recipe!.Ingredients.Any(i => i.Accepts(flint.Id)));
    }

    [Fact]
    public void ASurvivalSearchOnlyFindsTheRecipesTheCraftTabLists()
    {
        var inventory = TestInventory.Stocked();
        var listed = inventory.ListedRecipes(creative: false);

        foreach (var letter in "aeiou")
        {
            var recipeHits = InventorySearch.Search(letter.ToString(), inventory).Where(h => h.IsRecipe);
            Assert.All(recipeHits, h => Assert.Contains(h.Recipe!, listed));
        }
    }

    [Fact]
    public void ARecipeJoinsTheSurvivalSearchOnlyOnceItCanBeMade()
    {
        var inventory = Inventory.Empty();
        // A recipe of stackables only, so stocking its ingredients is a plain Add
        var recipe = ContentRegistry.CraftingRecipes.First(r => r.Ingredients.All(i =>
            Inventory.CategoryOf(i.AcceptedIds[0]) is InventoryCategory.Blocks or InventoryCategory.Misc));
        var query = Inventory.DisplayName(recipe.ResultId);

        Assert.DoesNotContain(InventorySearch.Search(query, inventory), h => h.Recipe == recipe);
        Assert.Contains(InventorySearch.Search(query, inventory, creative: true), h => h.Recipe == recipe);

        foreach (var ingredient in recipe.Ingredients)
            Assert.True(inventory.Add(ingredient.AcceptedIds[0], ingredient.Count));
        Assert.True(inventory.CanCraft(recipe));
        Assert.Contains(InventorySearch.Search(query, inventory), h => h.Recipe == recipe);
    }

    [Fact]
    public void TheScopeKeepsOnlyTheItemsOrOnlyTheRecipes()
    {
        var inventory = TestInventory.Stocked();
        var pickaxe = ContentRegistry.RequireItem("primitive_pickaxe");
        var query = Inventory.DisplayName(pickaxe.Id);

        var all = InventorySearch.Search(query, inventory, creative: true);
        Assert.Contains(all, h => h.IsRecipe);
        Assert.Contains(all, h => !h.IsRecipe);

        var items = InventorySearch.Search(query, inventory, creative: true, SearchScope.Items);
        Assert.All(items, h => Assert.False(h.IsRecipe));
        Assert.Equal(all.Count(h => !h.IsRecipe), items.Count);

        var recipes = InventorySearch.Search(query, inventory, creative: true, SearchScope.Recipes);
        Assert.All(recipes, h => Assert.True(h.IsRecipe));
        Assert.Equal(all.Count(h => h.IsRecipe), recipes.Count);
    }

    [Fact]
    public void TheScopeButtonCyclesThroughEveryScopeBackToTheDefault()
    {
        var view = new InventoryView();
        Assert.Equal(SearchScope.All, view.SearchScope);

        var seen = new HashSet<SearchScope>();
        foreach (var _ in Enum.GetValues<SearchScope>())
        {
            seen.Add(view.SearchScope);
            view.CycleSearchScope();
        }
        Assert.Equal(Enum.GetValues<SearchScope>().Length, seen.Count);
        Assert.Equal(SearchScope.All, view.SearchScope);

        view.CycleSearchScope();
        view.ResetForNewWorld();
        Assert.Equal(SearchScope.All, view.SearchScope);
    }

    [Fact]
    public void FindsAnOwnedToolWithALiveInstance()
    {
        var inventory = TestInventory.Stocked();
        var bucket = ContentRegistry.RequireItem("bucket");

        var hits = InventorySearch.Search(Inventory.DisplayName(bucket.Id), inventory);

        var hit = Assert.Single(hits, h => h.Kind == SearchHitKind.Inventory && h.Id == bucket.Id);
        Assert.Equal(InventoryCategory.Tools, hit.Category);
        Assert.True(hit.ToolInstance > 0);
    }

    [Fact]
    public void ListsEveryInstanceOfANonStackableTool()
    {
        var inventory = TestInventory.Stocked();
        var bucket = ContentRegistry.RequireItem("bucket");
        inventory.Add(bucket.Id, 2, autoEquip: false);
        var owned = inventory.ToolEntries().Count(t => t.ItemId == bucket.Id);

        var hits = InventorySearch.Search(Inventory.DisplayName(bucket.Id), inventory)
            .Where(h => h.Kind == SearchHitKind.Inventory && h.Id == bucket.Id).ToList();

        Assert.True(owned > 1);
        Assert.Equal(owned, hits.Count);
        Assert.Equal(owned, hits.Select(h => h.ToolInstance).Distinct().Count());
    }

    [Fact]
    public void PrefixMatchesRankAheadOfSubstrings()
    {
        var inventory = TestInventory.Stocked();
        var stone = ContentRegistry.RequireBlock("stone");

        var hits = InventorySearch.Search(stone.DisplayName, inventory);

        Assert.NotEmpty(hits);
        Assert.StartsWith(stone.DisplayName, hits[0].Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnAllSpaceQueryReadsAsEmpty()
    {
        var field = new SearchFieldState();
        Assert.True(field.IsEmpty);
        field.Text.TryAppend(' ');
        field.Text.TryAppend(' ');
        Assert.True(field.IsEmpty);
        field.Text.TryAppend('a');
        Assert.False(field.IsEmpty);
    }
}
