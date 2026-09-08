namespace silkgltriangle;

/// <summary>
/// Selection state of the [E] inventory panel: which tab is open and which list row is
/// picked for preview (a stackable id, or a tool instance). Owned by <see cref="Program"/>;
/// the panel in <see cref="UIRenderer"/> only reads it.
/// </summary>
public sealed class InventoryView
{
    public bool IsOpen { get; set; }
    public InventoryCategory Tab { get; set; } = InventoryCategory.Blocks;

    /// <summary>Picked stackable id for the blocks/misc tabs, or 0 for none.</summary>
    public byte SelectedId { get; set; }

    /// <summary>Picked tool instance for the tools tab, or 0 for none.</summary>
    public int SelectedToolInstance { get; set; }

    public void ClearSelection()
    {
        SelectedId = 0;
        SelectedToolInstance = 0;
    }
}
