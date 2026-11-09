using System.Numerics;
using VoxelEngine.Networking;
using VoxelEngine.Networking.Client;
using VoxelEngine.Rendering;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.App;

/// <summary>
/// Dragging a listed row of the [E] panel onto a slot: a left press on an item row starts a <see cref="RowDrag"/>, the
/// frame tracks it, and a release after it lifted drops the row into the hotbar slot, or the equipment card's slot,
/// under the pointer (<see cref="RowEquip.DropOnHotbar"/>, <see cref="RowEquip.DropOnEquipment"/>); a creative player
/// owning none of it is granted one first. An unlifted release is left to the click. Applied here at once and, while
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
        var creative = world.Camera.IsCreative;
        if (panel.EntryAt(window, world.Inventory, creative) is not { Recipe: null } entry) return;
        if (creative && panel.IsOnCatalogButton(window, world.Inventory)) return;
        inventoryView.Drag = new RowDrag(entry.Id, entry.ToolInstance, renderManager.Ui.ToCanvas(window));
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

        var inventory = world.Inventory;
        if (renderManager.Ui.Hud.HotbarSlotAt(window) is { } slot and >= 0)
        {
            EnsureOwned(world, drag);
            Send(RowEquip.DropOnHotbar(inventory, drag.Id, drag.ToolInstance, slot));
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
