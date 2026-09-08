using Xunit;

namespace silkgltriangle.Tests;

public sealed class CraftingTests
{
    public CraftingTests() => TestContent.EnsureLoaded();

    [Fact]
    public void PickaxeRecipeLoadsWithExpectedIngredients()
    {
        var pickaxe = ContentRegistry.RequireItem("primitive_pickaxe").Id;
        var recipe = ContentRegistry.RecipeForResult(pickaxe);
        Assert.NotNull(recipe);
        Assert.Equal(1, recipe.ResultCount);
        Assert.Equal(
            new[]
            {
                ContentRegistry.RequireItem("flint").Id,
                ContentRegistry.RequireItem("branch").Id,
                ContentRegistry.RequireItem("vine").Id,
            },
            new[] { recipe.Ingredients[0].Id, recipe.Ingredients[1].Id, recipe.Ingredients[2].Id });
        Assert.Equal(new[] { 1, 1, 2 },
            new[] { recipe.Ingredients[0].Count, recipe.Ingredients[1].Count, recipe.Ingredients[2].Count });
    }

    [Fact]
    public void TryCraftConsumesIngredientsAndAddsPickaxe()
    {
        var inventory = new Inventory();
        var flint = ContentRegistry.RequireItem("flint").Id;
        var branch = ContentRegistry.RequireItem("branch").Id;
        var vine = ContentRegistry.RequireItem("vine").Id;
        var pickaxe = ContentRegistry.RequireItem("primitive_pickaxe").Id;
        var recipe = ContentRegistry.RecipeForResult(pickaxe)!;

        Assert.Empty(inventory.Craftable());
        Assert.False(inventory.TryCraft(recipe));

        Assert.True(inventory.Add(flint));
        Assert.True(inventory.Add(branch));
        Assert.True(inventory.Add(vine, 2));
        Assert.Equal(new[] { recipe }, inventory.Craftable());

        Assert.True(inventory.TryCraft(recipe));
        Assert.Equal(0, inventory.CountFor(flint));
        Assert.Equal(0, inventory.CountFor(branch));
        Assert.Equal(0, inventory.CountFor(vine));
        Assert.Equal(1, inventory.CountFor(pickaxe));
        Assert.Empty(inventory.Craftable());
    }

    [Fact]
    public void TryCraftStillFitsWhenThePoolIsFull()
    {
        var inventory = new Inventory();
        var flint = ContentRegistry.RequireItem("flint").Id;
        var branch = ContentRegistry.RequireItem("branch").Id;
        var vine = ContentRegistry.RequireItem("vine").Id;
        var pickaxe = ContentRegistry.RequireItem("primitive_pickaxe").Id;
        var recipe = ContentRegistry.RecipeForResult(pickaxe)!;

        Assert.True(inventory.Add(flint, Inventory.MiscCapacity - 3));
        Assert.True(inventory.Add(branch));
        Assert.True(inventory.Add(vine, 2));
        Assert.Equal(Inventory.MiscCapacity, inventory.UsedOf(InventoryCategory.Misc));

        Assert.True(inventory.CanCraft(recipe));
        Assert.True(inventory.TryCraft(recipe));
        Assert.Equal(1, inventory.CountFor(pickaxe));
    }
}
