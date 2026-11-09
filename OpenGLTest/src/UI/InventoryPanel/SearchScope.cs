namespace VoxelEngine.UI.InventoryPanel;

/// <summary>
/// What the [E] panel's search lists (<see cref="InventorySearch.Search"/>): owned items and recipes, the default,
/// or only one of the two. The cycle button beside the search field steps through them in declaration order
/// (<see cref="InventoryView.CycleSearchScope"/>).
/// </summary>
public enum SearchScope
{
    /// <summary>The owned items and the recipes.</summary>
    All,

    /// <summary>Only the owned items (in creative, every item that exists).</summary>
    Items,

    /// <summary>Only the recipes.</summary>
    Recipes,
}
