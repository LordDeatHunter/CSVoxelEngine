using System.Linq;
using VoxelEngine.Content.Spells;
using VoxelEngine.Items;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.UI.InventoryPanel;

/// <summary>Crafting with a staff as an ingredient spends the least-bound instance, so bound spells are kept.</summary>
public sealed class StaffCraftingTests
{
    public StaffCraftingTests() => TestContent.EnsureLoaded();

    [Fact]
    public void CraftingConsumesAnUnboundStaffBeforeABoundOne()
    {
        var recipe = ContentRegistry.CraftingRecipes.First(r =>
            r.Ingredients.Any(i => !i.IsTag && ItemRegistry.TryGet(i.Id) is StaffItem));
        var staffIngredient = recipe.Ingredients.First(i => !i.IsTag && ItemRegistry.TryGet(i.Id) is StaffItem);
        var staff = (StaffItem)ItemRegistry.TryGet(staffIngredient.Id)!;

        var inventory = Inventory.Empty();
        Assert.True(inventory.Add(staff.Id, staffIngredient.Count + 1));
        var instances = inventory.ToolEntries().Where(t => t.ItemId == staff.Id).Select(t => t.InstanceId).ToArray();
        var boundInstance = instances[0];
        SpellPart[] spellRecipe =
        [
            SpellPartRegistry.All.First(p => p.Kind == SpellPartKind.Element && p.IsAllowedBy(staff.AllowedTags)),
            SpellPartRegistry.All.First(p => p.Kind == SpellPartKind.Shape && p.IsAllowedBy(staff.AllowedTags)),
        ];
        Assert.True(StaffBinding.TryBind(staff, default, 0, spellRecipe, out var spells, out _));
        inventory.GetToolStack(boundInstance)!.Set(spells);
        foreach (var ingredient in recipe.Ingredients.Where(i => i.Id != staff.Id || i.IsTag))
            Assert.True(inventory.Add(ingredient.AcceptedIds[0], ingredient.Count));

        Assert.True(inventory.TryCraft(recipe));

        Assert.Equal(spells, inventory.GetToolStack(boundInstance)?.Get<StaffComponent>());
        foreach (var unbound in instances.Skip(1))
            Assert.Null(inventory.GetToolStack(unbound));
    }
}
