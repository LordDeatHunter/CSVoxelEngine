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

    /// <summary>
    /// The crafting tab's working state: open recipe, batch size and fed reservations.
    /// Survives tab switches (so counts can be checked mid-craft) but is cleared when the
    /// panel closes or another recipe is opened.
    /// </summary>
    public CraftingSession Craft { get; } = new();

    // One scroll offset per tab, indexed by (int)InventoryCategory, which is 0..3 contiguous
    // (Inventory.cs). If a tab is added, size this from Enum.GetValues instead.
    private readonly float[] _scroll = new float[4];

    /// <summary>
    /// Canvas pixels the active tab's list is scrolled by. The input handlers clamp on write,
    /// and both the draw and the hit-test clamp on read through
    /// <see cref="InventoryPanelLayout.ClampedOffset"/> with that frame's row count, so the two
    /// always agree. A list that shrinks without input keeps the stored number and resumes it
    /// if it grows back. <see cref="UIRenderer"/> never writes it.
    /// </summary>
    public float ScrollOffset
    {
        get => _scroll[(int)Tab];
        set => _scroll[(int)Tab] = value;
    }

    /// <summary>
    /// Canvas pixels the crafting card's content is scrolled by. Both the draw and the hit-test
    /// clamp on read through <see cref="CraftCardLayout.ClampScroll"/> with that frame's layout, so
    /// a card that shrinks below its viewport simply reads as 0. <see cref="Program"/> resets it to
    /// 0 when another recipe is opened; <see cref="UIRenderer"/> never writes it.
    /// </summary>
    public float CraftScrollOffset { get; set; }

    /// <summary>
    /// Canvas-Y distance from the scrollbar thumb's top edge to the cursor when the thumb was
    /// grabbed; null while no thumb drag is live.
    /// </summary>
    public float? ThumbDragGrab { get; set; }

    /// <summary>
    /// Row count latched when the thumb was grabbed, so the per-frame drag never re-runs
    /// <see cref="Inventory.StackEntries"/>.
    /// </summary>
    public int ThumbDragRowCount { get; set; }

    /// <summary>Drops the selection picks; the per-tab scroll offsets are kept.</summary>
    public void ClearSelection()
    {
        SelectedId = 0;
        SelectedToolInstance = 0;
    }

    public void EndThumbDrag()
    {
        ThumbDragGrab = null;
    }
}
