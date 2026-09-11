using Xunit;

namespace silkgltriangle.Tests;

public sealed class CraftingTests
{
    public CraftingTests() => TestContent.EnsureLoaded();

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
        var firestarter = ContentRegistry.RecipeForResult(ContentRegistry.RequireItem("firestarter").Id)!;
        var quern = ContentRegistry.RecipeByName("quern")!;
        Assert.Equal([firestarter, recipe, quern], inventory.Craftable());

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

        // Fill the pool to exactly full whatever the starting stock holds, so the
        // craft below only fits because its ingredients free the room first.
        Assert.True(inventory.Add(flint, Inventory.MiscCapacity - inventory.UsedOf(InventoryCategory.Misc) - 3));
        Assert.True(inventory.Add(branch));
        Assert.True(inventory.Add(vine, 2));
        Assert.Equal(Inventory.MiscCapacity, inventory.UsedOf(InventoryCategory.Misc));

        Assert.True(inventory.CanCraft(recipe));
        Assert.True(inventory.TryCraft(recipe));
        Assert.Equal(1, inventory.CountFor(pickaxe));
    }

    [Fact]
    public void TagIngredientPoolsEveryMemberStock()
    {
        var inventory = new Inventory();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var cobble = ContentRegistry.RequireBlock("cobblestone").Id;
        var dirt = ContentRegistry.RequireBlock("dirt").Id;
        var recipe = new CraftingRecipe("test_rock",
            dirt, 1, [new CraftingRecipe.Ingredient(0, 2, "test_rock", [stone, cobble])]);

        Assert.True(recipe.Ingredients[0].IsTag);
        Assert.True(recipe.Ingredients[0].Accepts(stone));
        Assert.True(recipe.Ingredients[0].Accepts(cobble));
        Assert.False(recipe.Ingredients[0].Accepts(dirt));

        // The greedy spend takes stone first: 25 crafts drain the 50 stone, and the 26th
        // spills over into cobblestone, proving the pool spans both members.
        for (var i = 0; i < 25; i++)
            Assert.True(inventory.TryCraft(recipe));
        Assert.Equal(0, inventory.CountFor(stone));
        Assert.Equal(Inventory.StartingStock, inventory.CountFor(cobble));
        Assert.True(inventory.TryCraft(recipe));
        Assert.Equal(Inventory.StartingStock - 2, inventory.CountFor(cobble));
    }

    [Fact]
    public void TryCraftBatchConsumesExplicitMembers()
    {
        var inventory = new Inventory();
        var flint = ContentRegistry.RequireItem("flint").Id;
        var branch = ContentRegistry.RequireItem("branch").Id;
        var vine = ContentRegistry.RequireItem("vine").Id;
        var pickaxe = ContentRegistry.RequireItem("primitive_pickaxe").Id;
        var recipe = ContentRegistry.RecipeForResult(pickaxe)!;

        Assert.True(inventory.Add(flint, 2));
        Assert.True(inventory.Add(branch, 2));
        Assert.True(inventory.Add(vine, 4));

        Assert.True(inventory.TryCraft(recipe, 2, (index, ingredient) => ingredient.Id));
        Assert.Equal(0, inventory.CountFor(flint));
        Assert.Equal(0, inventory.CountFor(branch));
        Assert.Equal(0, inventory.CountFor(vine));
        Assert.Equal(2, inventory.CountFor(pickaxe));
    }

    [Fact]
    public void BatchCraftRefusesAPickWithoutStock()
    {
        var inventory = new Inventory();
        var flint = ContentRegistry.RequireItem("flint").Id;
        var branch = ContentRegistry.RequireItem("branch").Id;
        var vine = ContentRegistry.RequireItem("vine").Id;
        var pickaxe = ContentRegistry.RequireItem("primitive_pickaxe").Id;
        var recipe = ContentRegistry.RecipeForResult(pickaxe)!;

        Assert.True(inventory.Add(flint));
        Assert.True(inventory.Add(branch));
        Assert.True(inventory.Add(vine, 2));

        // Only one set on hand: a batch of two fails and consumes nothing.
        Assert.False(inventory.TryCraft(recipe, 2, (index, ingredient) => ingredient.Id));
        Assert.Equal(1, inventory.CountFor(flint));
        Assert.Equal(3, inventory.Craftable().Count);
        Assert.Equal(0, inventory.CountFor(pickaxe));
    }

    [Fact]
    public void SessionFeedsThenCraftsWithoutTakingUntilCraft()
    {
        var inventory = new Inventory();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var dirt = ContentRegistry.RequireBlock("dirt").Id;
        var recipe = new CraftingRecipe("test_feed",
            dirt, 1, [new CraftingRecipe.Ingredient(stone, 2)]);
        var session = new CraftingSession();

        var stoneBefore = inventory.CountFor(stone);
        session.Select(recipe);
        Assert.False(session.IsReady(inventory));

        session.FeedMore(0, inventory);
        Assert.False(session.IsReady(inventory));
        Assert.Equal(stoneBefore, inventory.CountFor(stone));

        session.FeedMore(0, inventory);
        Assert.True(session.IsReady(inventory));
        Assert.Equal("Ready — craft 1 Dirt", session.StatusLine(inventory));

        Assert.True(session.Craft(inventory));
        Assert.Equal(stoneBefore - 2, inventory.CountFor(stone));
        Assert.False(session.IsReady(inventory));
    }

    [Fact]
    public void SessionBatchScalesNeedsAndResults()
    {
        var inventory = new Inventory();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var dirt = ContentRegistry.RequireBlock("dirt").Id;
        var recipe = new CraftingRecipe("test_batch",
            dirt, 1, [new CraftingRecipe.Ingredient(stone, 2)]);
        var session = new CraftingSession();
        session.Select(recipe);
        session.SetBatch(3);

        Assert.Equal(6, session.NeedAt(0));
        Assert.Equal(inventory.CountFor(stone) / 2, session.MaxAffordable(inventory));
        for (var i = 0; i < 6; i++)
            session.FeedMore(0, inventory);
        Assert.True(session.IsReady(inventory));
        var dirtBefore = inventory.CountFor(dirt);
        Assert.True(session.Craft(inventory));
        Assert.Equal(dirtBefore + 3, inventory.CountFor(dirt));
    }

    [Fact]
    public void SessionClearsFedWhenSwitchingRecipes()
    {
        var inventory = new Inventory();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var dirt = ContentRegistry.RequireBlock("dirt").Id;
        var first = new CraftingRecipe("test_first",
            dirt, 1, [new CraftingRecipe.Ingredient(stone, 1)]);
        var second = new CraftingRecipe("test_second",
            dirt, 1, [new CraftingRecipe.Ingredient(stone, 1)]);
        var session = new CraftingSession();

        session.Select(first);
        session.FeedMore(0, inventory);
        Assert.Equal(1, session.FedAt(0).Count);

        session.Select(second);
        Assert.Equal(0, session.FedAt(0).Count);
        Assert.Equal(1, session.Batch);
        Assert.Equal(Inventory.StartingStock, inventory.CountFor(stone));
    }

    [Fact]
    public void SessionFeedsTaggedMembersByChoice()
    {
        var inventory = new Inventory();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var cobble = ContentRegistry.RequireBlock("cobblestone").Id;
        var dirt = ContentRegistry.RequireBlock("dirt").Id;
        var recipe = new CraftingRecipe("test_choice",
            dirt, 1, [new CraftingRecipe.Ingredient(0, 1, "test_rock", [stone, cobble])]);
        var session = new CraftingSession();
        session.Select(recipe);

        session.SetMember(0, cobble, inventory);
        session.FeedMore(0, inventory);
        Assert.Equal(cobble, session.FedAt(0).MemberId);
        Assert.True(session.IsReady(inventory));

        var cobbleBefore = inventory.CountFor(cobble);
        Assert.True(session.Craft(inventory));
        Assert.Equal(cobbleBefore - 1, inventory.CountFor(cobble));
        Assert.Equal(Inventory.StartingStock, inventory.CountFor(stone));
    }

    [Fact]
    public void SessionFeedFromInventoryTargetsNeedySlots()
    {
        var inventory = new Inventory();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var dirt = ContentRegistry.RequireBlock("dirt").Id;
        var recipe = new CraftingRecipe("test_slots", dirt, 1,
        [
            new CraftingRecipe.Ingredient(stone, 1),
            new CraftingRecipe.Ingredient(dirt, 1),
        ]);
        var session = new CraftingSession();
        session.Select(recipe);

        Assert.True(session.FeedFromInventory(dirt, inventory));
        Assert.Equal(dirt, session.FedAt(1).MemberId);
        Assert.Equal(0, session.FedAt(0).Count);
        Assert.False(session.FeedFromInventory(dirt, inventory));
    }
}
