namespace VoxelEngine.UI.InventoryPanel;

/// <summary>
/// What the [E] panel's tab strip reads: each tab's title and the count under it. A pool tab reads used/capacity; the
/// Craft tab counts the recipes it lists (<see cref="Inventory.ListedRecipes"/>), so a survival player sees how many
/// it can make right now and never a total that would give the locked recipes away, while creative, which lists them
/// all, reads craftable/total. GL-free; drawn by <c>InventoryPanelRenderer</c>.
/// </summary>
public static class PanelTabLabels
{
    public static string Title(InventoryCategory tab) => tab switch
    {
        InventoryCategory.Blocks => "Blocks",
        InventoryCategory.Tools => "Tools",
        InventoryCategory.Gear => "Gear",
        InventoryCategory.Crafting => "Craft",
        _ => "Misc",
    };

    public static string Subtitle(Inventory inventory, InventoryCategory tab, bool creative)
    {
        if (tab != InventoryCategory.Crafting) return $"{inventory.UsedOf(tab)}/{Inventory.CapacityOf(tab)}";
        var listed = inventory.ListedRecipes(creative).Count;
        return creative ? $"{inventory.Craftable().Count}/{listed}" : listed.ToString();
    }
}
