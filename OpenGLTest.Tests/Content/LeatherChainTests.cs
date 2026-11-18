using System.Linq;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Content.Stations;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Wildlife;
using VoxelEngine.Items;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>
/// The leather chain as relations, so retuning it can never strand the player: a hunted animal's hide is scraped on a
/// fleshing beam and taken off with salt that rock salt drops, the salted hide is what the tanning vat tans with bark
/// that splitting a log drops, the vat's leather is what the leather table cuts every piece of the leather set from,
/// and the knife both stations need is crafted without iron. No crafting recipe skips a step.
/// </summary>
public sealed class LeatherChainTests
{
    public LeatherChainTests() => TestContent.EnsureLoaded();

    private static TanningProfile Vat => BlockType.Types.Values.Select(b => b.Tanning).OfType<TanningProfile>().Single();

    // The recipe that takes a raw hide: the scrape that keeps its result waiting for salt
    private static WorkRecipe Scrape => ContentRegistry.WorkRecipes.Single(r => r.CollectWithId != 0);

    [Fact]
    public void TheBeamScrapesWhatHuntingDropsAndSaltFromRockSaltTakesItOff()
    {
        var hunted = WildlifeSpecies.Names.SelectMany(n => EntityRegistry.Require(n).LootTable!.AllEntries())
            .Select(e => e.Item);
        Assert.Contains(Scrape.InputId, hunted);
        var salted = BlockType.Types.Values.Any(b => b.LootTable != null
            && b.LootTable.AllEntries().Any(e => e.Item == Scrape.CollectWithId));
        Assert.True(salted, "no block drops what takes a scraped hide off the beam");
    }

    [Fact]
    public void TheVatTansWhatTheBeamMakesWithWhatSplittingALogDrops()
    {
        Assert.Equal(Scrape.ResultId, Vat.InputId);
        var split = BlockType.Types.Values.Where(b => b.SplitLoot != null)
            .SelectMany(b => b.SplitLoot!.AllEntries()).Select(e => e.Item);
        Assert.Contains(Vat.TanninId, split);
    }

    [Fact]
    public void EveryPieceOfTheLeatherSetIsCutFromTheVatsLeatherOnAStation()
    {
        var pieces = ItemRegistry.All.OfType<GearItem>().Where(i => i.Info.SetName == "leather").ToList();
        Assert.NotEmpty(pieces);
        foreach (var piece in pieces)
        {
            Assert.Null(ContentRegistry.RecipeForResult(piece.Id));
            var cut = ContentRegistry.WorkRecipeForResult(piece.Id);
            Assert.NotNull(cut);
            Assert.Equal(Vat.ResultId, cut!.InputId);
        }
    }

    [Fact]
    public void NoCraftingRecipeTakesAHideAtAnyStage()
    {
        Assert.DoesNotContain(ContentRegistry.CraftingRecipes, r => r.Ingredients.Any(
            i => i.Accepts(Scrape.InputId) || i.Accepts(Scrape.ResultId)));
    }

    [Fact]
    public void BothLeatherStationsTakeAKnifeThatIsCraftedWithoutIron()
    {
        var stations = BlockType.Types.Values.Where(b => b.WorkStation is { } p && p.Recipes != "forge").ToList();
        Assert.NotEmpty(stations);
        foreach (var station in stations)
        {
            var tag = station.WorkStation!.ToolTag;
            // Some member of the tool tag is a crafting recipe whose ingredients no station makes
            Assert.Contains(ItemRegistry.All, i => ContentRegistry.IsInTag(tag, i.Id)
                && ContentRegistry.RecipeForResult(i.Id) is { } recipe
                && recipe.Ingredients.All(g => g.AcceptedIds.All(id => ContentRegistry.WorkRecipeForResult(id) == null)));
        }
    }
}
