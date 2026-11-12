using System.Linq;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Wildlife;
using VoxelEngine.Items;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>
/// What hunting the wild animals pays: every species drops something, and only items; meat is food only
/// once a campfire has cooked it; a hide makes the same gear as cotton with fewer units; and a feather
/// fletches more arrows than a bone.
/// </summary>
public sealed class WildlifeDropTests
{
    public WildlifeDropTests() => TestContent.EnsureLoaded();

    [Fact]
    public void EveryWildAnimalDropsItemsOfItsOwn()
    {
        foreach (var name in WildlifeSpecies.Names)
        {
            var table = EntityRegistry.Require(name).LootTable;
            Assert.True(table != null, $"{name} has no loot table");
            var drops = table!.AllEntries().Where(e => !e.IsEmpty).ToArray();
            Assert.NotEmpty(drops);
            Assert.All(drops, entry => Assert.True(ItemRegistry.IsItem(entry.Item), $"{name} drops a block (id {entry.Item})"));
        }
    }

    [Fact]
    public void MeatIsFoodOnlyOnceCooked()
    {
        var cooked = 0;
        foreach (var name in WildlifeSpecies.Names)
        foreach (var entry in EntityRegistry.Require(name).LootTable!.AllEntries())
        {
            if (ContentRegistry.CookRecipeForInput(entry.Item) is not { } recipe) continue;
            cooked++;
            var raw = ItemRegistry.TryGet(entry.Item)!;
            Assert.Equal(ItemUseAction.None, raw.UseAction);
            var food = Assert.IsType<HealItem>(ItemRegistry.TryGet(recipe.ResultId));
            Assert.True(food.Heals > 0);
            // Nothing cooks the cooked meat again
            Assert.Null(ContentRegistry.CookRecipeForInput(recipe.ResultId));
        }
        Assert.True(cooked > 0, "no animal drops anything a campfire cooks");
    }

    [Fact]
    public void AHideMakesTheSameGearAsCottonWithFewerUnits()
    {
        var hide = ContentRegistry.RequireItem("hide").Id;
        var fromHide = ContentRegistry.CraftingRecipes
            .Where(r => r.Ingredients.Length == 1 && !r.Ingredients[0].IsTag && r.Ingredients[0].Id == hide).ToArray();
        Assert.NotEmpty(fromHide);

        foreach (var recipe in fromHide)
        {
            var plain = ContentRegistry.RecipeForResult(recipe.ResultId)!;
            Assert.NotSame(recipe, plain);
            Assert.Equal(plain.ResultCount, recipe.ResultCount);
            Assert.True(recipe.Ingredients[0].Count < plain.Ingredients.Sum(i => i.Count),
                $"{recipe.Name} should take fewer hides than {plain.Name} takes units");
        }
    }

    [Fact]
    public void AFeatherFletchesMoreArrowsThanABone()
    {
        var bone = ContentRegistry.RecipeByName("flint_arrow")!;
        var feather = ContentRegistry.RecipeByName("flint_arrow_from_crow_feather")!;

        Assert.Equal(bone.ResultId, feather.ResultId);
        Assert.Equal(bone.Ingredients.Sum(i => i.Count), feather.Ingredients.Sum(i => i.Count));
        Assert.True(feather.ResultCount > bone.ResultCount);
    }
}
