using System;
using System.Collections.Generic;
using VoxelEngine.Rendering.Overlays;

namespace VoxelEngine.UI.InventoryPanel;

/// <summary>Which inventory text field has keyboard focus, if any.</summary>
public enum InventoryFocus
{
    None,
    Search,

    /// <summary>The chest panel's search field.</summary>
    ChestSearch,
}

/// <summary>
/// Selection state of the [E] inventory panel: which tab is open and which list row is
/// picked for preview (a stackable id, or a tool instance). Owned by <see cref="Program"/>;
/// the panel in <see cref="InventoryPanelRenderer"/> only reads it.
/// </summary>
public sealed class InventoryView
{
    public bool IsOpen { get; set; }

    /// <summary>The panel's open and close easing, stepped by the UI pass.</summary>
    public PanelTransition Transition { get; } = new();
    public InventoryCategory Tab { get; set; } = InventoryCategory.Blocks;

    /// <summary>The panel's dropdown search field; an empty query shows the tab's own list.</summary>
    public SearchFieldState Search { get; } = new();

    /// <summary>What the search lists: items and recipes (the default), or only one of them. Kept while the panel
    /// closes; a new world starts back at <c>All</c>.</summary>
    public SearchScope SearchScope { get; private set; }

    /// <summary>Steps <see cref="SearchScope"/> to the next scope, wrapping back to the first (the cycle button).
    /// The results list starts again at its top.</summary>
    public void CycleSearchScope()
    {
        SearchScope = (SearchScope)(((int)SearchScope + 1) % Enum.GetValues<SearchScope>().Length);
        Search.Highlight = -1;
        Search.ScrollOffset = 0f;
    }

    /// <summary>This frame's search results for the field's query in the current <see cref="SearchScope"/>; the
    /// draw, the clicks, the keys and the wheel all read the list through here, so they always agree.</summary>
    public IReadOnlyList<SearchHit> SearchHits(Inventory inventory, bool creative) =>
        InventorySearch.Search(Search.Query, inventory, creative, SearchScope);

    /// <summary>Which text field keys go to; clicking the box focuses it, clicking elsewhere clears it.</summary>
    public InventoryFocus Focus { get; set; }

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

    // One scroll offset per tab, indexed by (int)InventoryCategory, which is contiguous from 0
    // (InventoryCategory.cs), so it is sized from the enum.
    private readonly float[] _scroll = new float[Enum.GetValues<InventoryCategory>().Length];

    /// <summary>
    /// Canvas pixels the active tab's list is scrolled by. The input handlers clamp on write,
    /// and both the draw and the hit-test clamp on read through
    /// <see cref="InventoryPanelLayout.ClampedOffset"/> with that frame's row count, so the two
    /// always agree. A list that shrinks without input keeps the stored number and resumes it
    /// if it grows back. <see cref="InventoryPanelRenderer"/> never writes it.
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
    /// 0 when another recipe is opened; <see cref="CraftCardRenderer"/> never writes it.
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

    /// <summary>A left press on a listed row that may lift into a drag onto a slot; null while none.</summary>
    public RowDrag? Drag { get; set; }

    /// <summary>The pointer in canvas units, set once a frame before the panel draws; null when there is none.
    /// Read only for hover looks, never for hit-testing a click.</summary>
    public System.Numerics.Vector2? HoverCanvas { get; set; }

    /// <summary>Eased tab position (fractional index of <see cref="InventoryPanelLayout.Tabs"/>) the active-tab
    /// underline sits at.</summary>
    public float TabIndicator { get; private set; }

    private float _motionTime = float.NaN;

    /// <summary>Steps the panel's eased looks toward the current tab by the time since the last call.</summary>
    public void StepMotion(float timeSeconds)
    {
        var first = float.IsNaN(_motionTime);
        var dt = first ? 0f : Math.Clamp(timeSeconds - _motionTime, 0f, 0.1f);
        _motionTime = timeSeconds;
        var target = Array.IndexOf(InventoryPanelLayout.Tabs, Tab);
        TabIndicator = first ? target : MathUtils.Damp(TabIndicator, target, TabIndicatorRate, dt);
    }

    /// <summary>How fast the tab underline catches up with the picked tab (per second, see <see cref="MathUtils.Damp"/>).</summary>
    public const float TabIndicatorRate = 18f;

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

    /// <summary>Empties the search field and drops keyboard focus (panel close).</summary>
    public void ClearInput()
    {
        Search.Clear();
        Focus = InventoryFocus.None;
    }

    /// <summary>
    /// Returns the panel to its fresh state when a world is opened or closed: the crafting session
    /// must not keep pointing at a dead world's <see cref="Inventory"/>, and no tab may carry the
    /// old world's scroll.
    /// </summary>
    public void ResetForNewWorld()
    {
        IsOpen = false;
        Transition.Reset();
        HoverCanvas = null;
        _motionTime = float.NaN;
        ClearSelection();
        ClearInput();
        SearchScope = SearchScope.All;
        Craft.Clear();
        EndThumbDrag();
        ThumbDragRowCount = 0;
        Drag = null;
        CraftScrollOffset = 0f;
        for (var i = 0; i < _scroll.Length; i++)
            _scroll[i] = 0f;
        Tab = InventoryCategory.Blocks;
    }
}
