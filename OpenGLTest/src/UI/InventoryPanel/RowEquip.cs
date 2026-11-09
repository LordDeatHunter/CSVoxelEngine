using VoxelEngine.Items;
using VoxelEngine.Networking;

namespace VoxelEngine.UI.InventoryPanel;

/// <summary>
/// The [E] panel's right-click on a listed row: equipping what the row names in one click, where a left click only
/// picks it. A piece of gear is worn in its first fitting slot, or taken off when it is already on; a tool or an
/// owned stackable goes into the hotbar's selected slot, or leaves it when the slot already shows it (the same toggle
/// a click on the slot is). A row that names an item kind rather than an instance (a creative catalog row, a catalog
/// search hit) equips the first owned instance of it. GL-free: the change is applied here and the intent that mirrors
/// it to a server is returned, the same intents the panel's slot clicks send.
/// </summary>
public static class RowEquip
{
    /// <summary>
    /// Equips the row naming item <paramref name="id"/> (and, for a tool or gear row, its
    /// <paramref name="toolInstance"/>, else 0). Returns the intent to mirror, or null when nothing changed: an id
    /// the player does not own, or gear that fits no slot.
    /// </summary>
    public static InventoryIntent? Apply(Inventory inventory, byte id, int toolInstance)
    {
        var instance = toolInstance != 0 ? toolInstance : FirstInstanceOf(inventory, id);
        if (instance != 0)
        {
            if (inventory.GetToolStack(instance)?.Item is GearItem) return ToggleWorn(inventory, instance);
            var toolSlot = inventory.SelectedIndex;
            return inventory.EquipTool(toolSlot, instance) ? InventoryIntent.EquipTool(toolSlot, instance) : null;
        }

        // Only a stackable the pools actually hold goes on the hotbar
        if (Inventory.CategoryOf(id) is not (InventoryCategory.Blocks or InventoryCategory.Misc)
            || inventory.CountFor(id) <= 0)
            return null;
        var slot = inventory.SelectedIndex;
        inventory.EquipStackable(slot, id);
        return InventoryIntent.EquipStackable(slot, id);
    }

    // The first live instance of a tool or gear kind, or 0 for a stackable id or a kind the player does not own
    private static int FirstInstanceOf(Inventory inventory, byte id)
    {
        var category = Inventory.CategoryOf(id);
        if (category is not (InventoryCategory.Tools or InventoryCategory.Gear)) return 0;
        var entries = category == InventoryCategory.Gear ? inventory.GearEntries() : inventory.ToolEntries();
        foreach (var entry in entries)
            if (entry.ItemId == id)
                return entry.InstanceId;
        return 0;
    }

    // Wears an owned gear instance in its first fitting slot, or takes it off when it is worn
    private static InventoryIntent? ToggleWorn(Inventory inventory, int instance)
    {
        var worn = inventory.Equipment.SlotOf(instance);
        if (worn >= 0)
            return inventory.Equipment.Unequip(worn) ? InventoryIntent.UnequipGear(worn) : null;
        return inventory.Equipment.TryEquipAuto(instance)
            ? InventoryIntent.EquipGear(inventory.Equipment.SlotOf(instance), instance)
            : null;
    }
}
