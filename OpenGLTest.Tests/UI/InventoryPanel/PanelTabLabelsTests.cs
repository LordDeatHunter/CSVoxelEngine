using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.UI.InventoryPanel;

/// <summary>The Craft tab's count: survival counts only the recipes it lists and never names the total; creative,
/// which lists every recipe, does.</summary>
public sealed class PanelTabLabelsTests
{
    public PanelTabLabelsTests() => TestContent.EnsureLoaded();

    [Fact]
    public void ASurvivalCraftTabCountsTheRecipesItListsAndNamesNoTotal()
    {
        foreach (var inventory in new[] { Inventory.Empty(), TestInventory.Stocked() })
        {
            var subtitle = PanelTabLabels.Subtitle(inventory, InventoryCategory.Crafting, creative: false);
            Assert.Equal(inventory.ListedRecipes(creative: false).Count.ToString(), subtitle);
            Assert.DoesNotContain("/", subtitle);
        }
    }

    [Fact]
    public void ACreativeCraftTabCountsAgainstEveryRecipe()
    {
        var subtitle = PanelTabLabels.Subtitle(TestInventory.Stocked(), InventoryCategory.Crafting, creative: true);
        Assert.EndsWith($"/{ContentRegistry.CraftingRecipes.Count}", subtitle);
    }
}
