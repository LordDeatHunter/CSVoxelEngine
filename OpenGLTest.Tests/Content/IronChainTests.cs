using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Items;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>
/// The iron chain as relations, so retuning a recipe can never strand the player: ore is smelted by the bloomery into
/// what the anvil takes, the anvil makes the iron every iron recipe consumes, and the anvil and its hammer can be had
/// without any iron (the bootstrap), so every piece of iron gear is reachable from the raw world.
/// </summary>
public sealed class IronChainTests
{
    public IronChainTests() => TestContent.EnsureLoaded();

    private static BlockType Bloomery => ContentRegistry.RequireBlock("bloomery");

    private static BlockType AnvilBlock =>
        Enumerable.Range(1, 255).Select(i => BlockType.ById[i]).First(b => b?.Anvil != null)!;

    [Fact]
    public void WhatTheBloomeryMakesIsWhatTheAnvilTakesAndTheOreComesFromTheQuern()
    {
        var bloomery = Bloomery.Bloomery!;
        Assert.Equal(bloomery.BloomId, AnvilBlock.Anvil!.InputId);
        Assert.NotEqual(bloomery.BloomId, bloomery.OreId);
        Assert.True(Enumerable.Range(1, 255).Any(i => ContentRegistry.CrushRecipeForInput((byte)i)?.ResultId == bloomery.OreId),
            "the ore the bloomery smelts has to be something the quern makes");
    }

    [Fact]
    public void TheBloomeryNeverMeltsIronSoNoCrucibleMetalExists()
    {
        Assert.Null(ContentRegistry.MeltRecipeForInput(Bloomery.Bloomery!.OreId));
        Assert.Null(ContentRegistry.MeltRecipeForInput(AnvilBlock.Anvil!.ResultId));
    }

    [Fact]
    public void EveryRecipeThatConsumesWroughtIronIsReachableFromTheRawWorld()
    {
        var anvil = AnvilBlock;
        var wrought = anvil.Anvil!.ResultId;
        var stationMade = new HashSet<byte> { wrought, anvil.Anvil.InputId, anvil.Anvil.SlagId, Bloomery.Bloomery!.SlagId };

        // Raw: every item with no crafting recipe, except what the iron works themselves make
        var obtainable = new HashSet<byte>(ItemRegistry.All
            .Select(i => i.Id)
            .Where(id => ContentRegistry.RecipeForResult(id) == null && !stationMade.Contains(id)));

        bool Have(CraftingRecipe.Ingredient ingredient) => ingredient.AcceptedIds.Any(obtainable.Contains);

        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var recipe in ContentRegistry.CraftingRecipes)
                if (!obtainable.Contains(recipe.ResultId) && recipe.Ingredients.All(Have))
                    grew |= obtainable.Add(recipe.ResultId);

            // The bloomery, built from its source block and fed ore and fuel, makes the bloom; the anvil, with a
            // hammer in hand, turns the bloom into iron
            var bloom = Bloomery.Bloomery.BloomId;
            if (obtainable.Contains(Bloomery.Bloomery.OreId) && obtainable.Contains(Bloomery.MultiblockSourceId)
                && ItemRegistry.All.Any(i => i.FuelSeconds > 0f && obtainable.Contains(i.Id)))
                grew |= obtainable.Add(bloom);
            if (obtainable.Contains(bloom) && obtainable.Contains(anvil.Id)
                && ItemRegistry.All.Any(i => obtainable.Contains(i.Id) && ContentRegistry.IsInTag("hammer", i.Id)))
            {
                grew |= obtainable.Add(wrought);
                grew |= obtainable.Add(anvil.Anvil.SlagId);
            }
        }

        Assert.Contains(wrought, obtainable);
        var consumers = ContentRegistry.CraftingRecipes
            .Where(r => r.Ingredients.Any(i => i.AcceptedIds.Contains(wrought)))
            .ToList();
        Assert.NotEmpty(consumers);
        Assert.All(consumers, r => Assert.True(obtainable.Contains(r.ResultId),
            $"{r.Name} consumes wrought iron but cannot be reached"));
    }

    [Fact]
    public void ThePieceOfIronGearTheGameShipsAreAllMadeOfWroughtIron()
    {
        // Every gear piece of the iron set has a recipe, and no recipe for it asks for anything but iron and ordinary goods
        var iron = ItemRegistry.All.OfType<GearItem>().Where(i => i.Info.SetName == "iron").ToList();
        Assert.NotEmpty(iron);
        var wrought = AnvilBlock.Anvil!.ResultId;
        foreach (var piece in iron)
        {
            var recipe = ContentRegistry.RecipeForResult(piece.Id);
            Assert.NotNull(recipe);
            Assert.Contains(recipe!.Ingredients, i => i.AcceptedIds.Contains(wrought));
        }
    }
}
