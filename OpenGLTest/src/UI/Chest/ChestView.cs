using System;
using System.Collections.Generic;
using VoxelEngine.Simulation.Chests;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.UI.Chest;

/// <summary>
/// The state of the chest panel, GL-free: whether a chest is open, which one, what it holds (the live state in
/// singleplayer, the server's last snapshot of it on a client; null while a client waits for the first answer) and
/// the panel's own search field and scroll. The panel is drawn beside the [E] inventory list
/// (<c>ChestCardRenderer</c>), and what a click does is <c>ChestInput</c>. Owned by <see cref="World"/>.
/// </summary>
public sealed class ChestView
{
    private readonly List<int> _rows = [];
    private int _rowsVersion = -1;
    private string _rowsQuery = "";
    private ChestState? _rowsFor;

    /// <summary>Whether a chest panel is up (it owns part of the mouse while it is).</summary>
    public bool IsOpen { get; private set; }

    /// <summary>The cell of the open chest.</summary>
    public (int X, int Y, int Z) Cell { get; private set; }

    /// <summary>The panel title (the chest block's name).</summary>
    public string Title { get; private set; } = "";

    /// <summary>What the chest holds, or null while the server has not answered.</summary>
    public ChestState? Contents { get; private set; }

    /// <summary>The chest side's search field: it filters the chest's rows by name.</summary>
    public SearchFieldState Search { get; } = new();

    /// <summary>Canvas pixels the chest's rows are scrolled by; the draw and the hit-test clamp on read.</summary>
    public float ScrollOffset { get; set; }

    /// <summary>The panel's open and close easing, stepped by the UI pass.</summary>
    public PanelTransition Transition { get; } = new();

    /// <summary>Opens the panel on the chest at a cell with what it holds (null to wait for the server).</summary>
    public void Show((int X, int Y, int Z) cell, string title, ChestState? contents)
    {
        IsOpen = true;
        Cell = cell;
        Title = title;
        Contents = contents;
        Search.Clear();
        ScrollOffset = 0f;
    }

    /// <summary>Takes the server's newest snapshot of the open chest.</summary>
    public void SetContents(ChestState? contents) => Contents = contents;

    /// <summary>Closes the panel and forgets the contents.</summary>
    public void Close()
    {
        IsOpen = false;
        Contents = null;
        Search.Clear();
        ScrollOffset = 0f;
    }

    /// <summary>Back to a fresh state when a world is opened or closed.</summary>
    public void ResetForNewWorld()
    {
        Close();
        Transition.Reset();
    }

    /// <summary>
    /// The slot indices the list shows: the non-empty slots whose name contains the search query (case-insensitive), in
    /// slot order. Cached until the contents or the query change.
    /// </summary>
    public IReadOnlyList<int> Rows()
    {
        var chest = Contents;
        if (chest == null)
        {
            _rows.Clear();
            _rowsFor = null;
            return _rows;
        }
        var query = Search.IsEmpty ? "" : Search.Query.Trim();
        if (ReferenceEquals(_rowsFor, chest) && _rowsVersion == chest.Version && _rowsQuery == query)
            return _rows;

        _rows.Clear();
        for (var i = 0; i < chest.SlotCount; i++)
        {
            var slot = chest[i];
            if (slot.IsEmpty) continue;
            if (query.Length > 0 && NameOf(slot).IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
            _rows.Add(i);
        }
        _rowsFor = chest;
        _rowsVersion = chest.Version;
        _rowsQuery = query;
        return _rows;
    }

    /// <summary>The display name of a slot's content (a rolled piece of gear by the name it earned).</summary>
    public static string NameOf(ChestSlot slot) =>
        slot.Instance?.ResolvedName ?? Inventory.DisplayName(slot.Id);
}
