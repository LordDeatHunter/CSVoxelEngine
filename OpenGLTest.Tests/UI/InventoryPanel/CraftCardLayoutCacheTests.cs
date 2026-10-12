using System;
using System.Numerics;
using VoxelEngine.Content.Recipes;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.UI.InventoryPanel;

public sealed class CraftCardLayoutCacheTests
{
    private static readonly Vector2 Screen = new(1920f, 1080f);

    public CraftCardLayoutCacheTests() => TestContent.EnsureLoaded();

    private sealed class Counter
    {
        public int Calls;
        public float PerChar = 10f;
        public float Measure(ReadOnlySpan<char> s) { Calls++; return s.Length * PerChar; }
    }

    private static (CraftingRecipe A, CraftingRecipe B) TwoRecipes()
    {
        var recipes = ContentRegistry.CraftingRecipes;
        Assert.True(recipes.Count >= 2);
        return (recipes[0], recipes[1]);
    }

    [Fact]
    public void UnchangedInputsReturnTheSameInstanceWithoutMeasuring()
    {
        var head = new Counter();
        var body = new Counter();
        var cache = new CraftCardLayoutCache(head.Measure, body.Measure, 44f);
        var (recipe, _) = TwoRecipes();

        var first = cache.Get(Screen, recipe, "Ready to craft");
        Assert.True(head.Calls + body.Calls > 0);
        head.Calls = body.Calls = 0;

        var second = cache.Get(Screen, recipe, "Ready to craft");
        Assert.Same(first, second);
        Assert.Equal(0, head.Calls);
        Assert.Equal(0, body.Calls);
        Assert.Same(second, cache.Current);
    }

    [Fact]
    public void AnyKeyChangeRebuilds()
    {
        var m = new Counter();
        var cache = new CraftCardLayoutCache(m.Measure, m.Measure, 44f);
        var (a, b) = TwoRecipes();
        const string status = "Need 2 more flint";

        var baseLayout = cache.Get(Screen, a, status);
        Assert.Same(baseLayout, cache.Get(Screen, a, new string(status.ToCharArray())));

        var otherStatus = cache.Get(Screen, a, status + "!");
        Assert.NotSame(baseLayout, otherStatus);
        Assert.Same(a, otherStatus.Recipe);

        var wide = new Vector2(2560f, 1080f);
        var otherScreen = cache.Get(wide, a, status);
        Assert.NotSame(otherStatus, otherScreen);
        Assert.Equal(wide, otherScreen.Screen);

        var otherRecipe = cache.Get(wide, b, status);
        Assert.NotSame(otherScreen, otherRecipe);
        Assert.Same(b, otherRecipe.Recipe);
        Assert.Equal(wide, otherRecipe.Screen);

        cache.Clear();
        Assert.Null(cache.Current);
        var rebuilt = cache.Get(wide, b, status);
        Assert.NotSame(otherRecipe, rebuilt);
        Assert.Same(rebuilt, cache.Current);
    }

    [Fact]
    public void ContentStringsFollowTheRecipe()
    {
        var m = new Counter { PerChar = 0f };
        var cache = new CraftCardLayoutCache(m.Measure, m.Measure, 44f);
        foreach (var recipe in ContentRegistry.CraftingRecipes)
        {
            var layout = cache.Get(Screen, recipe, "status");
            Assert.Equal(Inventory.DisplayName(recipe.ResultId), string.Join(" ", layout.TitleLines));
            Assert.Equal(recipe.Ingredients.Length, layout.Ingredients.Length);
            for (var i = 0; i < recipe.Ingredients.Length; i++)
            {
                var ing = recipe.Ingredients[i];
                var joined = string.Join(" ", layout.IngredientTitleLines[i]);
                var name = ing.IsTag ? $"Any {ing.Tag}" : Inventory.DisplayName(ing.Id);
                Assert.Contains(name, joined);
                Assert.Contains($"x{ing.Count}/craft", joined);
            }
        }
    }
}
