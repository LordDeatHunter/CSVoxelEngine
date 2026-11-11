using System.Numerics;
using Silk.NET.Input;
using VoxelEngine.Magic;
using VoxelEngine.Networking;
using VoxelEngine.Networking.Client;
using VoxelEngine.Platform;
using VoxelEngine.Rendering;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.UI.SpellBuilder;

namespace VoxelEngine.App;

/// <summary>
/// The spell builder's input: opening it from a spell table click (only with a staff in hand, else a
/// hint toast), closing it (E, Escape, the pause menu, the death screen, or the held staff going away),
/// the cursor and look capture while it is open, its clicks, and the bind and clear requests, which are
/// predicted locally and sent as inventory intents. Owned by Program; main thread only.
/// </summary>
internal sealed class SpellBuilderInput
{
    private readonly InputManager inputManager;
    private readonly RenderManager renderManager;
    private readonly SpellBuilderView view;
    private readonly ClientSession session;

    // The staff the builder was opened for: a different tool in hand (dropped, swapped, or replaced by
    // a server snapshot) closes it, so a bind never lands on a staff the panel was not showing.
    private int _openedToolInstance;

    internal SpellBuilderInput(InputManager inputManager, RenderManager renderManager, SpellBuilderView view, ClientSession session)
    {
        this.inputManager = inputManager;
        this.renderManager = renderManager;
        this.view = view;
        this.session = session;
    }

    /// <summary>Whether the builder is up (it owns the mouse while it is).</summary>
    internal bool IsOpen => view.IsOpen;

    /// <summary>
    /// Opens or closes the builder: the mouse is freed for clicking (and the look frozen) while it is
    /// open, then captured again. Opening rebuilds the known-part columns and shows the held staff's
    /// selected slot; closing drops the selection.
    /// </summary>
    internal void SetOpen(bool open)
    {
        // Only a playing world has a builder; while loading, SuppressLook belongs to the World
        if (Program.ActiveWorld is not { IsPlaying: true } w) return;
        view.IsOpen = open;
        if (open)
        {
            view.Invalidate();
            view.Refresh(w.Inventory, w.Camera.IsCreative);
            if (HeldStaff.TryGet(w.Inventory, out var staff, out var stack))
            {
                var component = stack.Get<StaffComponent>();
                view.SelectSlot(component.Selected, staff, component);
                _openedToolInstance = w.Inventory.SelectedToolInstance;
            }
        }
        else
        {
            view.ClearSelection();
        }
        w.Camera.SuppressLook = open;
        inputManager.Mouse?.Cursor.CursorMode = open ? CursorMode.Normal : CursorMode.Disabled;
    }

    /// <summary>A spell table claimed the right-click: opens the builder with a staff in hand, else shows the hint.</summary>
    internal void OpenFromStation(World w)
    {
        if (HeldStaff.TryGet(w.Inventory, out _, out _))
            SetOpen(true);
        else
            w.Toast.Show(SpellBuilderText.HoldStaffHint);
    }

    /// <summary>
    /// Every playing frame: closes an open builder whose staff left the hand, otherwise refreshes its
    /// columns (a no-op unless creative or the known parts changed).
    /// </summary>
    internal void Update(World w)
    {
        if (!view.IsOpen) return;
        if (!HeldStaff.TryGet(w.Inventory, out _, out _) || w.Inventory.SelectedToolInstance != _openedToolInstance)
        {
            SetOpen(false);
            return;
        }
        view.Refresh(w.Inventory, w.Camera.IsCreative);
    }

    /// <summary>Routes one left-click inside the open builder: a part toggles, a slot selects, BIND and CLEAR request their change.</summary>
    internal void HandleClick(Vector2 windowPos)
    {
        if (Program.ActiveWorld is not { IsPlaying: true } w) return;
        if (!HeldStaff.TryGet(w.Inventory, out var staff, out var stack)) return;
        var canvas = renderManager.Ui.ScreenSize;
        var p = renderManager.Ui.ToCanvas(windowPos);
        var hit = SpellBuilderLayout.HitAt(p, canvas,
            col => view.Column(SpellBuilderCatalog.Columns[col]).Count, staff.Slots);
        switch (hit.Kind)
        {
            case SpellBuilderHitKind.Part:
                view.Toggle(view.Column(SpellBuilderCatalog.Columns[hit.Column])[hit.Index]);
                break;
            case SpellBuilderHitKind.Slot:
                view.SelectSlot(hit.Index, staff, stack.Get<StaffComponent>());
                break;
            case SpellBuilderHitKind.Bind:
                RequestBind(w);
                break;
            case SpellBuilderHitKind.Clear:
                RequestClear(w);
                break;
        }
    }

    // Predicted locally; while connected the intent goes out and the server's InventoryState keeps or
    // reverts it. In singleplayer the send returns false and the local change stands (the
    // GameplayInput.RequestCycleStaff pattern).
    private void RequestBind(World w)
    {
        var keys = view.PartKeys();
        if (HeldStaff.TryBind(w.Inventory, view.Slot, keys, w.Camera.IsCreative))
            session.SendInventoryIntent(InventoryIntent.BindStaffSlot(view.Slot, keys));
    }

    private void RequestClear(World w)
    {
        if (HeldStaff.TryClear(w.Inventory, view.Slot))
            session.SendInventoryIntent(InventoryIntent.ClearStaffSlot(view.Slot));
    }
}
