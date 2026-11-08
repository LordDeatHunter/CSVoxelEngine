using System;
using System.Numerics;
using Silk.NET.Input;
using VoxelEngine.Networking;
using VoxelEngine.Networking.Client;
using VoxelEngine.Platform;
using VoxelEngine.Rendering;
using VoxelEngine.Simulation.Waystones;
using VoxelEngine.UI.Waystone;

namespace VoxelEngine.App;

/// <summary>
/// The waystone screen's input: a right-click on a waystone discovers it (<see cref="World.DiscoverWaystoneFor"/>) and opens
/// the discovery popup when it is new to the player (CONFIRM, and RENAME for its first discoverer, which opens the rename
/// dialog) or else the teleport list; a click on a list row travels there (<see cref="World.TeleportViaWaystone"/>). The
/// cursor is free and the look frozen while it is up; it closes on E or Escape (Escape steps back out of the rename
/// dialog), the pause menu, the death screen, leaving the waystone's reach or the waystone breaking. Connected to a
/// server, all of that is the server's: the right-click's Use intent discovers the waystone there and the screen opens on
/// the server's answer (<see cref="WaystoneMirror"/>), the list reads the rows the server sent, a rename is a
/// <see cref="NetMessage.WaystoneRename"/> and a trip a <see cref="NetIntentKind.Waystone"/> intent
/// whose landing the server's correction brings. Owned by Program; main thread only.
/// </summary>
internal sealed class WaystoneInput
{
    private readonly InputManager inputManager;
    private readonly RenderManager renderManager;
    private readonly WaystoneView view;
    private readonly ClientSession session;
    private readonly TextFieldKeys textKeys;
    private readonly Func<int, int, int, WaystoneRecord?> recordAt;
    private readonly Func<int, int, int, WaystoneRealm> realmAt;

    // The world the cached lookups read, set by Update before the rows are built
    private World? _world;

    internal WaystoneInput(InputManager inputManager, RenderManager renderManager, WaystoneView view, ClientSession session)
    {
        this.inputManager = inputManager;
        this.renderManager = renderManager;
        this.view = view;
        this.session = session;
        textKeys = new TextFieldKeys(inputManager);
        recordAt = RecordAt;
        realmAt = RealmAt;
    }

    /// <summary>Whether a page of the screen is up (it owns the mouse while it is).</summary>
    internal bool IsOpen => view.IsOpen;

    /// <summary>Whether one of its text fields owns the keyboard (the rename dialog, or a focused search field).</summary>
    internal bool HasFocusedField => view.HasFocusedField;

    // Connected to a server: the waystone rows, the activation answers and the trip are the server's
    private bool Connected => session.Client != null;

    /// <summary>
    /// A waystone claimed the right-click: the local player discovers it, then the discovery popup opens for a waystone
    /// new to it and the teleport list for one it knew. Connected to a server the click's Use intent already went out:
    /// the server discovers it and the screen opens on its answer (<see cref="Update"/>).
    /// </summary>
    internal void OpenFromStation(World w, Vector3 cell)
    {
        if (!w.IsPlaying || Connected) return;
        if (w.DiscoverWaystoneFor(w.LocalPlayer.LocalContext(), cell) is not { } found) return;
        Show(w, ((int)cell.X, (int)cell.Y, (int)cell.Z), found.Record.Name, found.FirstDiscoverer, found.NewToPlayer);
    }

    // Opens the discovery popup for a waystone new to the player, the teleport list for one it knew
    private void Show(World w, (int X, int Y, int Z) cell, string name, bool canRename, bool newToPlayer)
    {
        if (newToPlayer)
            view.ShowDiscovery(cell, name, canRename);
        else
            view.ShowList(cell, name, canRename);
        Capture(w, true);
        Update(w);
    }

    /// <summary>Closes the screen and gives the mouse back to the look.</summary>
    internal void Close()
    {
        if (!view.IsOpen) return;
        view.Close();
        if (Program.ActiveWorld is { } w) Capture(w, false);
    }

    /// <summary>Escape: steps back out of the rename dialog or the search field, else closes.</summary>
    internal void Back()
    {
        view.Back();
        if (!view.IsOpen && Program.ActiveWorld is { } w) Capture(w, false);
    }

    /// <summary>
    /// Every playing frame: closes the screen once the player left the waystone's reach or its block is gone, else
    /// refreshes the list (a no-op unless something it shows changed), the mana footer and the hover.
    /// </summary>
    internal void Update(World w)
    {
        // The server answered a right-click on a waystone: the screen opens on what it says
        if (Connected && session.Waystones.TryTakeOpened(out var opened) && w.IsPlaying)
            Show(w, opened.Cell, opened.Name, opened.MayRename, opened.NewToPlayer);
        if (!view.IsOpen) return;
        if (!w.IsPlaying || !WaystoneTeleport.IsAtWaystone(w.GetChunkManager(), w.Camera.Position, view.Cell, World.InteractionReach))
        {
            Close();
            return;
        }
        _world = w;
        view.Mana = (WaystoneTeleport.IsFree(w.Camera.IsCreative, w.Camera.IsFlying), w.Mana.MoreThanHalf);
        view.Rows(w.Inventory.Waystones, Connected ? session.Waystones.Version : w.Waystones.Version, recordAt, realmAt);
        view.Hover = inputManager.Mouse is { } mouse ? renderManager.Ui.ToCanvas(mouse.Position) : null;
    }

    /// <summary>Routes one left-click on the screen: the popup's and the dialog's buttons, the search field, a row.</summary>
    internal void HandleClick(Vector2 windowPos)
    {
        if (Program.ActiveWorld is not { IsPlaying: true } w || !view.IsOpen) return;
        var hit = WaystoneLayout.HitAt(view.Page, renderManager.Ui.ToCanvas(windowPos), renderManager.Ui.ScreenSize,
            view.CanRename, view.LastRows.Count, view.ScrollOffset);
        switch (hit.Kind)
        {
            case WaystoneHitKind.Confirm:
                view.Confirm();
                Update(w);
                break;
            case WaystoneHitKind.Rename:
                view.BeginRename();
                break;
            case WaystoneHitKind.RenameOk:
                SubmitRename(w);
                break;
            case WaystoneHitKind.RenameCancel:
                Back();
                break;
            case WaystoneHitKind.Search:
                view.SearchFocused = true;
                break;
            case WaystoneHitKind.Row:
                TeleportTo(w, hit.Row);
                break;
            case WaystoneHitKind.Panel or WaystoneHitKind.None when view.Page == WaystonePage.List:
                view.SearchFocused = false;
                break;
        }
    }

    /// <summary>The wheel while the screen is up: scrolls the list over its panel. True when the screen took it (always
    /// while open, so nothing behind it scrolls).</summary>
    internal bool HandleScroll(Vector2 windowPos, float wheelY)
    {
        if (!view.IsOpen) return false;
        var canvas = renderManager.Ui.ScreenSize;
        if (view.Page == WaystonePage.List && WaystoneLayout.Panel(canvas).Contains(renderManager.Ui.ToCanvas(windowPos)))
            view.ScrollOffset = WaystoneLayout.ClampedOffset(
                view.ScrollOffset - wheelY * WaystoneLayout.RowHeight * WaystoneLayout.WheelStepRows, view.LastRows.Count,
                canvas);
        return true;
    }

    /// <summary>A typed character: into the rename field, or the list's search field while it is focused.</summary>
    internal void Type(char c)
    {
        if (view.Page == WaystonePage.Rename)
        {
            view.RenameField.TryAppend(c);
            return;
        }
        if (view.Page != WaystonePage.List || !view.SearchFocused || !view.Search.Text.TryAppend(c)) return;
        view.ScrollOffset = 0f;
    }

    /// <summary>A pressed key: Enter confirms the popup or saves the rename; the editing keys edit the focused field.</summary>
    internal void HandleKey(Key key)
    {
        if (Program.ActiveWorld is not { IsPlaying: true } w) return;
        if (key is Key.Enter or Key.KeypadEnter)
        {
            if (view.Page == WaystonePage.Discovered)
            {
                view.Confirm();
                Update(w);
            }
            else if (view.Page == WaystonePage.Rename)
                SubmitRename(w);
            return;
        }
        if (view.Page == WaystonePage.Rename)
            textKeys.TryHandle(view.RenameField, key, upDownJump: false, out _);
        else if (view.Page == WaystonePage.List && view.SearchFocused
                 && textKeys.TryHandle(view.Search.Text, key, upDownJump: false, out var changed) && changed)
            view.ScrollOffset = 0f;
    }

    // ---- Scenario seams: the same steps a player's clicks take ----

    /// <summary>CONFIRM on the popup.</summary>
    internal void Confirm()
    {
        if (Program.ActiveWorld is not { } w) return;
        view.Confirm();
        Update(w);
    }

    /// <summary>RENAME on the popup; false when this player may not rename.</summary>
    internal bool BeginRename() => view.BeginRename();

    /// <summary>Types a whole string into the focused field (the rename field, or the list's search field, focused first).</summary>
    internal void TypeText(string text)
    {
        if (view.Page == WaystonePage.List) view.SearchFocused = true;
        foreach (var c in text) Type(c);
    }

    /// <summary>Clears the rename field (a scenario retypes the name from scratch).</summary>
    internal void ClearRenameField() => view.RenameField.Clear();

    /// <summary>RENAME in the dialog.</summary>
    internal void SubmitRename()
    {
        if (Program.ActiveWorld is { } w) SubmitRename(w);
    }

    /// <summary>A click on list row <paramref name="row"/> (0-based); false when the row is not listed.</summary>
    internal bool TeleportToRow(int row)
    {
        if (Program.ActiveWorld is not { } w || row < 0 || row >= view.LastRows.Count) return false;
        TeleportTo(w, row);
        return true;
    }

    private void SubmitRename(World w)
    {
        if (view.Page != WaystonePage.Rename || !WaystoneNames.TrySanitize(view.RenameField.Text, out var clean)) return;
        if (Connected)
        {
            // The server judges it and its rows bring the name back (the old one again, should it refuse)
            if (session.SendWaystoneRename(view.Cell, clean))
                view.FinishRename(clean);
            return;
        }
        var cell = new Vector3(view.Cell.X, view.Cell.Y, view.Cell.Z);
        if (!w.RenameWaystoneFor(w.LocalPlayer.LocalContext(), cell, clean)) return;
        view.FinishRename(w.Waystones.Peek(view.Cell.X, view.Cell.Y, view.Cell.Z)?.Name ?? clean);
    }

    private void TeleportTo(World w, int row)
    {
        var rows = view.LastRows;
        if (row < 0 || row >= rows.Count || !rows[row].Selectable) return;
        var target = rows[row];
        if (Connected)
        {
            RequestTrip(w, (target.X, target.Y, target.Z));
            return;
        }
        var verdict = w.TeleportViaWaystone(new Vector3(view.Cell.X, view.Cell.Y, view.Cell.Z),
            new Vector3(target.X, target.Y, target.Z));
        if (verdict == WaystoneTeleportVerdict.Teleport)
            Close();
        else
            w.Toast.Show(WaystoneText.Refusal(verdict));
    }

    // A connected trip: judged here first on the mirrored rows and pool, so a refusal gets its reason at once, then sent
    // as an intent; the screen closes and the server's correction moves the player (and its status the drained pool)
    private void RequestTrip(World w, (int X, int Y, int Z) to)
    {
        var verdict = WaystoneTeleport.Judge(w.Inventory.Waystones, session.Waystones.Peek(to.X, to.Y, to.Z), view.Cell,
            to, WaystoneTeleport.IsFree(w.Camera.IsCreative, w.Camera.IsFlying), w.Mana);
        if (verdict != WaystoneTeleportVerdict.Teleport)
        {
            w.Toast.Show(WaystoneText.Refusal(verdict));
            return;
        }
        w.RaiseIntent(WorldIntent.Waystone(w.Camera.Position, w.Camera.LastAimDirection, to));
        Close();
    }

    private void Capture(World w, bool open)
    {
        w.Camera.SuppressLook = open;
        inputManager.Mouse?.Cursor.CursorMode = open ? CursorMode.Normal : CursorMode.Disabled;
    }

    private WaystoneRecord? RecordAt(int x, int y, int z) =>
        Connected ? session.Waystones.Peek(x, y, z) : _world?.Waystones.Peek(x, y, z);

    // The realm by the cell's height and its column's surface, when the column source has it
    private WaystoneRealm RealmAt(int x, int y, int z)
    {
        int? surface = null;
        if (_world?.GetChunkManager().ColumnSource is { } columns
            && columns.TryGet(VoxelCoords.WorldToChunk(x), VoxelCoords.WorldToChunk(z), out var terrain) && !terrain.IsEmpty)
            surface = terrain.Heights[VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(x), VoxelCoords.WorldToLocal(z))];
        return WaystoneRealms.Classify(y, surface);
    }
}
