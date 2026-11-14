using System.Numerics;
using VoxelEngine.Networking;
using VoxelEngine.Networking.Client;
using VoxelEngine.Rendering;
using VoxelEngine.Simulation.Chests;
using VoxelEngine.UI.Chest;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.App;

/// <summary>
/// Dragging an item in the [E] panel: a left press on an item row, on a hotbar slot showing something, or on a row of the open
/// chest's card starts a <see cref="RowDrag"/>, the frame tracks it, and a release after it lifted drops the row into the
/// hotbar slot, or the equipment card's slot, under the pointer (<see cref="RowEquip.DropOnHotbar"/>,
/// <see cref="RowEquip.DropOnEquipment"/>); a creative player owning none of it is granted one first. A listed row
/// released on the open chest's card deposits it. A hotbar slot's drag moves it to another slot, deposits it in an open chest,
/// or empties its slot anywhere else on the panel; a chest row's drag takes its whole slot into the inventory when released
/// on the panel or the hotbar. A release elsewhere changes nothing. An unlifted release is left
/// to the click. Applied here at once and, while
/// connected, sent as the intent the server replays. Owned by <see cref="InventoryPanelInput"/>; main thread only.
/// </summary>
internal sealed class RowDragInput(
    RenderManager renderManager, InventoryView inventoryView, ClientSession session, InventoryPanelInput panel)
{
    /// <summary>A left press in the open panel: an item row (not a recipe, nor a creative row's grant button) starts a
    /// drag that is a click until it lifts.</summary>
    internal void Press(Vector2 window)
    {
        inventoryView.Drag = null;
        if (inventoryView.ThumbDragGrab.HasValue || Program.ActiveWorld is not { } world) return;
        var canvas = renderManager.Ui.ToCanvas(window);
        if (renderManager.Ui.Hud.HotbarSlotAt(window) is { } slot and >= 0)
        {
            var inventory = world.Inventory;
            if (inventory.IdAt(slot) != 0)
                inventoryView.Drag = new RowDrag(inventory.IdAt(slot), inventory.ToolInstanceAt(slot), canvas, slot);
            return;
        }
        if (ChestRowAt(world, window) is { } chestRow)
        {
            var (chestSlot, item) = chestRow;
            inventoryView.Drag = new RowDrag(item.Id, item.Instance?.InstanceId ?? 0, canvas, fromChestSlot: chestSlot);
            return;
        }
        var creative = world.Camera.IsCreative;
        if (panel.EntryAt(window, world.Inventory, creative) is not { Recipe: null } entry) return;
        if (creative && panel.IsOnCatalogButton(window, world.Inventory)) return;
        inventoryView.Drag = new RowDrag(entry.Id, entry.ToolInstance, canvas);
    }

    /// <summary>Once a frame: a pressed row lifts once the pointer leaves the press.</summary>
    internal void Track()
    {
        if (inventoryView.Drag is { } drag && panel.PointerCanvas() is { } pointer)
            drag.Track(pointer);
    }

    /// <summary>
    /// A left release: a lifted drag drops into the slot under the pointer (anywhere else it falls back into the list)
    /// and spends the release; false when there was no drag or it never lifted, so the release is a click.
    /// </summary>
    internal bool TryDrop(Vector2 window)
    {
        if (inventoryView.Drag is not { } drag) return false;
        inventoryView.Drag = null;
        if (!drag.Lifted || Program.ActiveWorld is not { } world) return false;
        if (drag.FromSlot >= 0)
        {
            DropHotbarSlot(world, drag.FromSlot, window);
            return true;
        }
        if (drag.FromChestSlot >= 0)
        {
            DropChestSlot(world, drag.FromChestSlot, window);
            return true;
        }

        var inventory = world.Inventory;
        if (renderManager.Ui.Hud.HotbarSlotAt(window) is { } slot and >= 0)
        {
            EnsureOwned(world, drag);
            Send(RowEquip.DropOnHotbar(inventory, drag.Id, drag.ToolInstance, slot));
        }
        else if (panel.Chest.IsOpen && ChestCardLayout.Contains(renderManager.Ui.ToCanvas(window), renderManager.Ui.ScreenSize))
        {
            DepositListedRow(world, drag);
        }
        // The equipment card shows on the Gear tab only, and an open chest's card covers it
        else if (inventoryView.Tab == InventoryCategory.Gear && !panel.Chest.IsOpen
                 && EquipmentCardLayout.SlotAt(renderManager.Ui.ToCanvas(window), renderManager.Ui.ScreenSize) is { } gearSlot)
        {
            EnsureOwned(world, drag);
            Send(RowEquip.DropOnEquipment(inventory, drag.Id, drag.ToolInstance, gearSlot));
        }
        return true;
    }

    // A hotbar slot dragged off the slot it shows: onto another hotbar slot it moves there, onto the open chest's card it
    // deposits (a creative player's deposit is refused, as a row click's is), anywhere else on the [E] panel it empties
    // the slot (the item stays in its pool), and outside all of them nothing changes.
    private void DropHotbarSlot(World world, int from, Vector2 window)
    {
        var inventory = world.Inventory;
        if (renderManager.Ui.Hud.HotbarSlotAt(window) is { } target and >= 0)
        {
            if (target == from || !inventory.MoveSlot(from, target)) return;
            Send(InventoryIntent.MoveSlot(from, target));
            return;
        }

        var canvas = renderManager.Ui.ToCanvas(window);
        var screen = renderManager.Ui.ScreenSize;
        if (panel.Chest.IsOpen && ChestCardLayout.Contains(canvas, screen))
        {
            if (world.Camera.IsCreative) return;
            var instance = inventory.ToolInstanceAt(from);
            if (instance != 0) panel.Chest.DepositInstance(instance);
            else panel.Chest.DepositStack(inventory.IdAt(from), wholeStack: true);
            return;
        }

        if (!InventoryPanelLayout.IsOverInventory(canvas, screen)) return;
        inventory.ClearSlot(from);
        Send(InventoryIntent.ClearSlot(from));
    }

    // A chest row dropped anywhere on the inventory panel or the hotbar takes its whole slot into the inventory; onto the
    // chest card itself (or outside all of them) nothing changes
    private void DropChestSlot(World world, int slot, Vector2 window)
    {
        var canvas = renderManager.Ui.ToCanvas(window);
        var screen = renderManager.Ui.ScreenSize;
        if (panel.Chest.IsOpen && ChestCardLayout.Contains(canvas, screen)) return;
        if (renderManager.Ui.Hud.HotbarSlotAt(window) is { } and >= 0 || InventoryPanelLayout.IsOverInventory(canvas, screen))
            panel.Chest.Withdraw(world, slot, all: true);
    }

    // A listed row dropped on the open chest's card deposits what it names, as a click with shift would; a creative
    // player's deposit is refused, as a row click's is, and a stackable the player does not hold moves nothing
    private void DepositListedRow(World world, RowDrag drag)
    {
        if (world.Camera.IsCreative) return;
        if (drag.ToolInstance != 0) panel.Chest.DepositInstance(drag.ToolInstance);
        else if (world.Inventory.CountFor(drag.Id) > 0 && !ChestTransfer.IsInstance(drag.Id))
            panel.Chest.DepositStack(drag.Id, wholeStack: true);
    }

    // The open chest's slot under the pointer and its contents, or null when no chest row is there
    private (int Slot, ChestSlot Item)? ChestRowAt(World world, Vector2 window)
    {
        var view = world.ChestPanel;
        if (!view.IsOpen || view.Contents is not { } chest) return null;
        var screen = renderManager.Ui.ScreenSize;
        var point = renderManager.Ui.ToCanvas(window);
        if (!ChestCardLayout.Contains(point, screen)) return null;
        var rows = view.Rows();
        var offset = ChestCardLayout.ClampedOffset(view.ScrollOffset, rows.Count, screen);
        if (ChestCardLayout.RowAt(point, screen, offset, rows.Count) is not { } row) return null;
        var slot = rows[row];
        var item = chest[slot];
        return item.IsEmpty ? null : (slot, item);
    }

    // A creative catalog row names a kind the player may own none of: the drop grants one, as a click on the row would
    private void EnsureOwned(World world, RowDrag drag)
    {
        if (world.Camera.IsCreative && drag.ToolInstance == 0 && world.Inventory.CountFor(drag.Id) <= 0)
            panel.GrantFromCreative(drag.Id, 1);
    }

    private void Send(InventoryIntent? intent)
    {
        if (intent is { } sent)
            session.SendInventoryIntent(sent);
    }
}
