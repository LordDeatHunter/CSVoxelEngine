using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Items;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>
/// The iron chain as relations, so retuning a recipe can never strand the player: ore from the quern is smelted by the
/// bloomery into the bloom, the bloom is the metal the anvil's forge recipes take (and no crafting recipe does), and the
/// anvil, its hammer and the bellows can be had without any iron (the bootstrap), so every piece of iron gear is
/// reachable from the raw world. The same anvil works vulcanite: its ore drops shards the anvil refines before it forges
/// anything of them.
/// </summary>
public sealed class IronChainTests
{
    public IronChainTests() => TestContent.EnsureLoaded();

    private static BlockType Bloomery => ContentRegistry.RequireBlock("bloomery");

    private static BlockType BellowsBlock => BlockType.Types.Values.First(b => b.IsBellows);

    private static ushort Bloom => Bloomery.Bloomery!.BloomId;

    // The anvil's recipe folder: what "a forge recipe" and "a forge metal" mean here
    private const string Forge = "forge";

    private static IEnumerable<WorkRecipe> ForgeRecipes => ContentRegistry.WorkRecipes.Where(r => r.Station == Forge);

    private static bool IsForgeMetal(ushort id) => ContentRegistry.IsWorkInput(Forge, id);

    [Fact]
    public void WhatTheBloomeryMakesIsWhatTheAnvilForgesAndTheOreComesFromTheQuern()
    {
        var bloomery = Bloomery.Bloomery!;
        Assert.True(IsForgeMetal(bloomery.BloomId));
        Assert.NotEqual(bloomery.BloomId, bloomery.OreId);
        Assert.True(Enumerable.Range(1, BlockType.ById.Length - 1).Any(i => ContentRegistry.CrushRecipeForInput((ushort)i)?.ResultId == bloomery.OreId),
            "the ore the bloomery smelts has to be something the quern makes");
    }

    [Fact]
    public void TheBloomeryNeverMeltsIronSoNoCrucibleMetalExists()
    {
        Assert.Null(ContentRegistry.MeltRecipeForInput(Bloomery.Bloomery!.OreId));
        Assert.Null(ContentRegistry.MeltRecipeForInput(Bloom));
    }

    /// <summary>The crafting recipes minus the cinder sword's, the one recipe that takes a refined metal bar directly
    /// (24 ember shards, 30 basalt and 1 refined vulcanite).</summary>
    private static IEnumerable<CraftingRecipe> ExceptCinderSword =>
        ContentRegistry.CraftingRecipes.Where(r => r.ResultId != ContentRegistry.ItemIds["cinder_sword"]);

    [Fact]
    public void NoCraftingRecipeTakesABloomItIsForgedOnTheAnvil()
    {
        Assert.DoesNotContain(ExceptCinderSword, r => r.Ingredients.Any(i => i.Accepts(Bloom)));
        // No other forge metal is crafted with either: a solid metal is worked only on the anvil (bar the cinder sword)
        Assert.DoesNotContain(ExceptCinderSword,
            r => r.Ingredients.Any(i => i.AcceptedIds.Any(IsForgeMetal)));
    }

    [Fact]
    public void EveryWorkRecipeIsReachableFromTheRawWorld()
    {
        // What the works themselves make: the bloom, and every work recipe's byproduct
        var stationMade = new HashSet<ushort>(ContentRegistry.WorkRecipes.Where(r => r.ByproductId != 0)
            .Select(r => r.ByproductId)) { Bloom };

        // Raw: every item with no crafting or work recipe, except what the works themselves make
        var obtainable = new HashSet<ushort>(ItemRegistry.All
            .Select(i => i.Id)
            .Where(id => ContentRegistry.RecipeForResult(id) == null && ContentRegistry.WorkRecipeForResult(id) == null
                         && !stationMade.Contains(id)));

        bool Have(CraftingRecipe.Ingredient ingredient) => ingredient.AcceptedIds.Any(obtainable.Contains);

        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var recipe in ContentRegistry.CraftingRecipes)
                if (!obtainable.Contains(recipe.ResultId) && recipe.Ingredients.All(Have))
                    grew |= obtainable.Add(recipe.ResultId);

            // The bloomery, built from its source block, fed ore and fuel and heated by its bellows, makes the bloom
            if (obtainable.Contains(Bloomery.Bloomery!.OreId) && obtainable.Contains(Bloomery.MultiblockSourceId)
                && obtainable.Contains(BellowsBlock.Id)
                && ItemRegistry.All.Any(i => i.FuelSeconds > 0f && obtainable.Contains(i.Id)))
                grew |= obtainable.Add(Bloom);

            // A work station and its tool work a stack of the input into its recipes and leave their byproducts; a
            // result that waits on the station also needs the item that takes it off
            foreach (var recipe in ContentRegistry.WorkRecipes)
                if (obtainable.Contains(recipe.InputId) && HasStation(recipe.Station)
                    && (recipe.CollectWithId == 0 || obtainable.Contains(recipe.CollectWithId)))
                {
                    grew |= obtainable.Add(recipe.ResultId);
                    if (recipe.ByproductId != 0)
                        grew |= obtainable.Add(recipe.ByproductId);
                }
        }

        Assert.Contains(Bloom, obtainable);
        Assert.Contains(ForgeRecipes, r => r.InputId == Bloom);
        // Every station's recipes, the bloom's, the vulcanite chain's and the leather's alike
        var forged = ContentRegistry.WorkRecipes;
        Assert.All(forged, r => Assert.True(obtainable.Contains(r.ResultId),
            $"{r.Name} is a work recipe but cannot be reached"));
        // ...and so is everything crafted from what the stations make (the hafted tools)
        Assert.All(ContentRegistry.CraftingRecipes.Where(r => r.Ingredients.Any(i => forged.Any(f => i.Accepts(f.ResultId)))),
            r => Assert.True(obtainable.Contains(r.ResultId), $"{r.Name} needs a forged part but cannot be reached"));

        // A station of that recipe folder whose block and tool are both obtainable
        bool HasStation(string station) => BlockType.Types.Values.Any(b => b.WorkStation is { } profile
            && profile.Recipes == station && obtainable.Contains(b.Id)
            && ItemRegistry.All.Any(i => obtainable.Contains(i.Id) && ContentRegistry.IsInTag(profile.ToolTag, i.Id)));
    }

    [Fact]
    public void VulcaniteOreDropsShardsTheAnvilRefinesBeforeItForgesAnythingOfThem()
    {
        var ore = ContentRegistry.RequireBlock("vulcanite_ore");
        var shards = ore.LootTable.AllEntries()
            .Where(e => e.Nested == null && !e.IsEmpty && IsForgeMetal(e.Item))
            .Select(e => e.Item).Distinct().ToList();
        Assert.NotEmpty(shards);

        foreach (var shard in shards)
        {
            // Only mined: no recipe makes a shard
            Assert.Null(ContentRegistry.RecipeForResult(shard));
            Assert.Null(ContentRegistry.WorkRecipeForResult(shard));

            // The shard forges nothing but a refined metal, and the refined metal forges the parts
            var refining = ForgeRecipes.Where(r => r.InputId == shard).ToList();
            Assert.NotEmpty(refining);
            Assert.All(refining, r => Assert.True(IsForgeMetal(r.ResultId),
                $"{r.Name} forges shards straight into a part"));
            var parts = ForgeRecipes.Where(r => refining.Any(f => f.ResultId == r.InputId)).ToList();
            Assert.NotEmpty(parts);

            // Every part is hafted into a tool, and no crafting recipe takes the shard or the refined metal
            Assert.All(parts, p => Assert.Contains(ContentRegistry.CraftingRecipes,
                c => c.Ingredients.Any(i => i.Accepts(p.ResultId))));
            Assert.DoesNotContain(ExceptCinderSword, c => c.Ingredients.Any(
                i => i.Accepts(shard) || refining.Any(f => i.Accepts(f.ResultId))));
        }
    }

    [Fact]
    public void ThePiecesOfIronGearTheGameShipsAreAllForgedFromBlooms()
    {
        // Every gear piece of the iron set is forged on the anvil straight from the bloomery's blooms
        var iron = ItemRegistry.All.OfType<GearItem>().Where(i => i.Info.SetName == "iron").ToList();
        Assert.NotEmpty(iron);
        foreach (var piece in iron)
        {
            var recipe = ContentRegistry.WorkRecipeForResult(piece.Id);
            Assert.NotNull(recipe);
            Assert.Equal(Bloom, recipe!.InputId);
        }
    }
}
