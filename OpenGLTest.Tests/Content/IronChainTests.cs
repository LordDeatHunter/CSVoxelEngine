using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Items;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>
/// The iron chain as relations, so retuning a recipe can never strand the player: ore is smelted by the bloomery into
/// what the anvil takes, the anvil makes the iron its forge recipes take (and no crafting recipe does), and the anvil
/// and its hammer can be had without any iron (the bootstrap), so every piece of iron gear is reachable from the raw
/// world. The same anvil works vulcanite: its ore drops shards the anvil refines before it forges anything of them.
/// </summary>
public sealed class IronChainTests
{
    public IronChainTests() => TestContent.EnsureLoaded();

    private static BlockType Bloomery => ContentRegistry.RequireBlock("bloomery");

    private static BlockType AnvilBlock =>
        BlockType.Types.Values.First(b => b.Anvil != null);

    [Fact]
    public void WhatTheBloomeryMakesIsWhatTheAnvilTakesAndTheOreComesFromTheQuern()
    {
        var bloomery = Bloomery.Bloomery!;
        Assert.Equal(bloomery.BloomId, AnvilBlock.Anvil!.InputId);
        Assert.NotEqual(bloomery.BloomId, bloomery.OreId);
        Assert.True(Enumerable.Range(1, BlockType.ById.Length - 1).Any(i => ContentRegistry.CrushRecipeForInput((ushort)i)?.ResultId == bloomery.OreId),
            "the ore the bloomery smelts has to be something the quern makes");
    }

    [Fact]
    public void TheBloomeryNeverMeltsIronSoNoCrucibleMetalExists()
    {
        Assert.Null(ContentRegistry.MeltRecipeForInput(Bloomery.Bloomery!.OreId));
        Assert.Null(ContentRegistry.MeltRecipeForInput(AnvilBlock.Anvil!.ResultId));
    }

    [Fact]
    public void NoCraftingRecipeTakesWroughtIronItIsForgedOnTheAnvil()
    {
        var wrought = AnvilBlock.Anvil!.ResultId;
        Assert.True(ContentRegistry.IsForgeMetal(wrought));
        Assert.DoesNotContain(ContentRegistry.CraftingRecipes, r => r.Ingredients.Any(i => i.Accepts(wrought)));
        // No other forge metal is crafted with either: a solid metal is worked only on the anvil
        Assert.DoesNotContain(ContentRegistry.CraftingRecipes,
            r => r.Ingredients.Any(i => i.AcceptedIds.Any(ContentRegistry.IsForgeMetal)));
    }

    [Fact]
    public void EveryForgeRecipeIsReachableFromTheRawWorld()
    {
        var anvil = AnvilBlock;
        var wrought = anvil.Anvil!.ResultId;
        var stationMade = new HashSet<ushort> { wrought, anvil.Anvil.InputId, anvil.Anvil.SlagId, Bloomery.Bloomery!.SlagId };

        // Raw: every item with no crafting or forge recipe, except what the iron works themselves make
        var obtainable = new HashSet<ushort>(ItemRegistry.All
            .Select(i => i.Id)
            .Where(id => ContentRegistry.RecipeForResult(id) == null && ContentRegistry.ForgeRecipeForResult(id) == null
                         && !stationMade.Contains(id)));

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

            // The anvil and the hammer forge a stack of the metal into its recipes
            foreach (var recipe in ContentRegistry.ForgeRecipes)
                if (obtainable.Contains(recipe.MetalId) && obtainable.Contains(anvil.Id))
                    grew |= obtainable.Add(recipe.ResultId);
        }

        Assert.Contains(wrought, obtainable);
        Assert.Contains(ContentRegistry.ForgeRecipes, r => r.MetalId == wrought);
        // Every metal's recipes, wrought iron's and the vulcanite chain's alike
        var forged = ContentRegistry.ForgeRecipes;
        Assert.All(forged, r => Assert.True(obtainable.Contains(r.ResultId),
            $"{r.Name} is a forge recipe but cannot be reached"));
        // ...and so is everything crafted from what the anvil forges (the hafted tools)
        Assert.All(ContentRegistry.CraftingRecipes.Where(r => r.Ingredients.Any(i => forged.Any(f => i.Accepts(f.ResultId)))),
            r => Assert.True(obtainable.Contains(r.ResultId), $"{r.Name} needs a forged part but cannot be reached"));
    }

    [Fact]
    public void VulcaniteOreDropsShardsTheAnvilRefinesBeforeItForgesAnythingOfThem()
    {
        var ore = ContentRegistry.RequireBlock("vulcanite_ore");
        var shards = ore.LootTable.AllEntries()
            .Where(e => e.Nested == null && !e.IsEmpty && ContentRegistry.IsForgeMetal(e.Item))
            .Select(e => e.Item).Distinct().ToList();
        Assert.NotEmpty(shards);

        foreach (var shard in shards)
        {
            // Only mined: no recipe makes a shard
            Assert.Null(ContentRegistry.RecipeForResult(shard));
            Assert.Null(ContentRegistry.ForgeRecipeForResult(shard));

            // The shard forges nothing but a refined metal, and the refined metal forges the parts
            var refining = ContentRegistry.ForgeRecipes.Where(r => r.MetalId == shard).ToList();
            Assert.NotEmpty(refining);
            Assert.All(refining, r => Assert.True(ContentRegistry.IsForgeMetal(r.ResultId),
                $"{r.Name} forges shards straight into a part"));
            var parts = ContentRegistry.ForgeRecipes.Where(r => refining.Any(f => f.ResultId == r.MetalId)).ToList();
            Assert.NotEmpty(parts);

            // Every part is hafted into a tool, and no crafting recipe takes the shard or the refined metal
            Assert.All(parts, p => Assert.Contains(ContentRegistry.CraftingRecipes,
                c => c.Ingredients.Any(i => i.Accepts(p.ResultId))));
            Assert.DoesNotContain(ContentRegistry.CraftingRecipes, c => c.Ingredients.Any(
                i => i.Accepts(shard) || refining.Any(f => i.Accepts(f.ResultId))));
        }
    }

    [Fact]
    public void ThePieceOfIronGearTheGameShipsAreAllForgedFromWroughtIron()
    {
        // Every gear piece of the iron set is forged on the anvil from the iron the anvil's own work makes
        var iron = ItemRegistry.All.OfType<GearItem>().Where(i => i.Info.SetName == "iron").ToList();
        Assert.NotEmpty(iron);
        var wrought = AnvilBlock.Anvil!.ResultId;
        foreach (var piece in iron)
        {
            var recipe = ContentRegistry.ForgeRecipeForResult(piece.Id);
            Assert.NotNull(recipe);
            Assert.Equal(wrought, recipe!.MetalId);
        }
    }
}
