using System;
using System.Collections.Generic;
using VoxelEngine.Simulation.Waystones;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.UI.Menus;

namespace VoxelEngine.UI.Waystone;

/// <summary>
/// The state of the waystone screen, GL-free: which page is up (<see cref="WaystonePage"/>), the waystone it was opened
/// at, the name the discovery popup shows and whether this player may rename it, the rename dialog's field, and the
/// teleport list's search field, scroll and rows. The page rules live here (<see cref="Confirm"/>,
/// <see cref="BeginRename"/>, <see cref="FinishRename"/>, <see cref="Back"/>); what a click does to the world is
/// <c>WaystoneInput</c>, what is drawn <c>WaystoneRenderer</c>. Owned by Program; main thread only.
/// </summary>
public sealed class WaystoneView
{
    private readonly List<WaystoneRow> _rows = [];
    private int _rowsDiscovered = -1;
    private int _rowsRegistry = -1;
    private string _rowsQuery = "";
    private (int X, int Y, int Z) _rowsCell;
    private DiscoveredWaystones? _rowsFor;

    /// <summary>Which page is up.</summary>
    public WaystonePage Page { get; private set; }

    /// <summary>Whether any page is up (the screen owns the mouse while it is).</summary>
    public bool IsOpen => Page != WaystonePage.Closed;

    /// <summary>The cell of the waystone the screen was opened at (the one the player stands at).</summary>
    public (int X, int Y, int Z) Cell { get; private set; }

    /// <summary>The name of that waystone, as the discovery popup and the list title show it.</summary>
    public string Name { get; private set; } = "";

    /// <summary>Whether this player discovered it first, the only one who may rename it.</summary>
    public bool CanRename { get; private set; }

    /// <summary>The rename dialog's text field, capped at <see cref="WaystoneNames.MaxLength"/>.</summary>
    public TextFieldState RenameField { get; } = new(WaystoneNames.MaxLength);

    /// <summary>The teleport list's search field: it filters the rows by name.</summary>
    public SearchFieldState Search { get; } = new();

    /// <summary>Whether the search field has the keyboard (a click on it gives it, Escape takes it back).</summary>
    public bool SearchFocused { get; set; }

    /// <summary>Canvas pixels the list is scrolled by; the draw and the hit-test clamp on read.</summary>
    public float ScrollOffset { get; set; }

    /// <summary>The screen's open and close easing, stepped by the UI pass.</summary>
    public PanelTransition Transition { get; } = new();

    /// <summary>The rows <see cref="Rows"/> last built, which the draw shows.</summary>
    public IReadOnlyList<WaystoneRow> LastRows => _rows;

    /// <summary>Whether the trip is free (creative or flying) and whether the mana is over half, for the footer.</summary>
    public (bool Free, bool MoreThanHalf) Mana { get; set; }

    /// <summary>The pointer in canvas units for the row hover look, or null.</summary>
    public System.Numerics.Vector2? Hover { get; set; }

    /// <summary>Whether a text field owns the keyboard: the rename dialog always, the list while its search is focused.</summary>
    public bool HasFocusedField => Page == WaystonePage.Rename || (Page == WaystonePage.List && SearchFocused);

    /// <summary>Whether the rename field holds a name a waystone would keep (<see cref="WaystoneNames.TrySanitize"/>).</summary>
    public bool CanSubmitRename => WaystoneNames.TrySanitize(RenameField.Text, out _);

    /// <summary>Opens the discovery popup for a waystone the player just found.</summary>
    public void ShowDiscovery((int X, int Y, int Z) cell, string name, bool canRename)
    {
        Open(cell, name, canRename);
        Page = WaystonePage.Discovered;
    }

    /// <summary>Opens the teleport list at a waystone the player had found before.</summary>
    public void ShowList((int X, int Y, int Z) cell, string name, bool canRename)
    {
        Open(cell, name, canRename);
        Page = WaystonePage.List;
    }

    /// <summary>CONFIRM on the discovery popup: on to the teleport list.</summary>
    public void Confirm()
    {
        if (Page == WaystonePage.Discovered)
            Page = WaystonePage.List;
    }

    /// <summary>RENAME on the discovery popup: the dialog opens holding the current name, all of it selected. Refused
    /// (false) unless the popup is up and this player may rename.</summary>
    public bool BeginRename()
    {
        if (Page != WaystonePage.Discovered || !CanRename) return false;
        RenameField.Set(Name);
        RenameField.SelectAll();
        Page = WaystonePage.Rename;
        return true;
    }

    /// <summary>The rename went through as <paramref name="name"/>: back to the popup, which now shows it.</summary>
    public void FinishRename(string name)
    {
        if (Page != WaystonePage.Rename) return;
        Name = name;
        Page = WaystonePage.Discovered;
    }

    /// <summary>
    /// Escape: the rename dialog steps back to the popup (the name unchanged), a focused search field lets the
    /// keyboard go, and anything else closes the screen.
    /// </summary>
    public void Back()
    {
        switch (Page)
        {
            case WaystonePage.Rename:
                Page = WaystonePage.Discovered;
                break;
            case WaystonePage.List when SearchFocused:
                SearchFocused = false;
                break;
            default:
                Close();
                break;
        }
    }

    /// <summary>Closes the screen and forgets its fields.</summary>
    public void Close()
    {
        Page = WaystonePage.Closed;
        RenameField.Clear();
        Search.Clear();
        SearchFocused = false;
        ScrollOffset = 0f;
        _rowsFor = null;
    }

    /// <summary>Back to a fresh state when a world is opened or closed.</summary>
    public void ResetForNewWorld()
    {
        Close();
        Transition.Reset();
    }

    /// <summary>
    /// The list's rows (<see cref="WaystoneRows.Build"/>) for <paramref name="discovered"/>, cached until the discovered
    /// list, the registry (<paramref name="registryVersion"/>), the query or the current waystone changes.
    /// </summary>
    public IReadOnlyList<WaystoneRow> Rows(DiscoveredWaystones discovered, int registryVersion,
        Func<int, int, int, WaystoneRecord?> record, Func<int, int, int, WaystoneRealm> realm)
    {
        var query = Search.Query;
        if (ReferenceEquals(_rowsFor, discovered) && _rowsDiscovered == discovered.Version
            && _rowsRegistry == registryVersion && _rowsQuery == query && _rowsCell == Cell)
            return _rows;
        WaystoneRows.Build(_rows, discovered.Entries, record, realm, Cell, query);
        _rowsFor = discovered;
        _rowsDiscovered = discovered.Version;
        _rowsRegistry = registryVersion;
        _rowsQuery = query;
        _rowsCell = Cell;
        return _rows;
    }

    private void Open((int X, int Y, int Z) cell, string name, bool canRename)
    {
        Close();
        Cell = cell;
        Name = name;
        CanRename = canRename;
    }
}
