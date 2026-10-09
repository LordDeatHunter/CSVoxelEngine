using System;
using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class InventorySearchTests
{
    public InventorySearchTests() => TestContent.EnsureLoaded();

    [Fact]
    public void EmptyQueryMatchesNothing()
    {
        var inventory = new Inventory();

        Assert.Empty(InventorySearch.Search(null, inventory));
        Assert.Empty(InventorySearch.Search("", inventory));
        Assert.Empty(InventorySearch.Search("   ", inventory));
    }

    [Fact]
    public void FindsAnOwnedBlockByItsName()
    {
        var inventory = new Inventory();
        var stone = ContentRegistry.RequireBlock("stone");

        var hits = InventorySearch.Search(stone.DisplayName, inventory);

        var hit = Assert.Single(hits, h => h.Kind == SearchHitKind.Inventory && h.Id == stone.Id);
        Assert.Equal(InventoryCategory.Blocks, hit.Category);
        Assert.True(hit.Count > 0);
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        var inventory = new Inventory();
        var stone = ContentRegistry.RequireBlock("stone");

        var hits = InventorySearch.Search(stone.DisplayName.ToUpperInvariant(), inventory);

        Assert.Contains(hits, h => h.Kind == SearchHitKind.Inventory && h.Id == stone.Id);
    }

    [Fact]
    public void FindsARecipeByItsResultName()
    {
        var inventory = new Inventory();
        var pickaxe = ContentRegistry.RequireItem("primitive_pickaxe");

        var hits = InventorySearch.Search(Inventory.DisplayName(pickaxe.Id), inventory);

        Assert.Contains(hits, h => h.IsRecipe && h.Id == pickaxe.Id && h.Recipe != null);
    }

    [Fact]
    public void FindsARecipeByAnIngredientName()
    {
        var inventory = new Inventory();
        var flint = ContentRegistry.RequireItem("flint");

        var hits = InventorySearch.Search(Inventory.DisplayName(flint.Id), inventory);

        Assert.Contains(hits, h => h.IsRecipe
            && h.Recipe!.Ingredients.Any(i => i.Accepts(flint.Id)));
    }

    [Fact]
    public void FindsAnOwnedToolWithALiveInstance()
    {
        var inventory = new Inventory();
        var bucket = ContentRegistry.RequireItem("bucket");

        var hits = InventorySearch.Search(Inventory.DisplayName(bucket.Id), inventory);

        var hit = Assert.Single(hits, h => h.Kind == SearchHitKind.Inventory && h.Id == bucket.Id);
        Assert.Equal(InventoryCategory.Tools, hit.Category);
        Assert.True(hit.ToolInstance > 0);
    }

    [Fact]
    public void PrefixMatchesRankAheadOfSubstrings()
    {
        var inventory = new Inventory();
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
