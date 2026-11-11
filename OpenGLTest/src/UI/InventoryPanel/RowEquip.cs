using VoxelEngine.Items;
using VoxelEngine.Networking;

namespace VoxelEngine.UI.InventoryPanel;

/// <summary>
/// Equipping a listed row of the [E] panel, and reading whether it is equipped. The right-click (<see cref="Apply"/>)
/// equips what the row names in one click, where a left click only picks it: a piece of gear is worn in its first
/// fitting slot, or taken off when it is already on; a tool or an owned stackable goes into the hotbar's selected
/// slot, or leaves it when the slot already shows it (the same toggle a click on the slot is). A row dragged onto a
/// hotbar or equipment slot goes into that slot (<see cref="DropOnHotbar"/>, <see cref="DropOnEquipment"/>). A row
/// that names an item kind rather than an instance (a creative catalog row, a catalog search hit) equips the first
/// owned instance of it. GL-free: the change is applied here and the intent that mirrors it to a server is returned,
/// the same intents the panel's slot clicks send.
/// </summary>
public static class RowEquip
{
    /// <summary>
    /// Equips the row naming item <paramref name="id"/> (and, for a tool or gear row, its
    /// <paramref name="toolInstance"/>, else 0). Returns the intent to mirror, or null when nothing changed: an id
    /// the player does not own, or gear that fits no slot.
    /// </summary>
    public static InventoryIntent? Apply(Inventory inventory, ushort id, int toolInstance)
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

    /// <summary>
    /// Puts the row naming item <paramref name="id"/> into hotbar slot <paramref name="slot"/> (a drop): a tool or an
    /// owned stackable now shows there. Never a toggle, so a drop onto the slot already showing it changes nothing, and
    /// gear, which is worn rather than held, never goes on the hotbar. Returns the intent to mirror, or null.
    /// </summary>
    public static InventoryIntent? DropOnHotbar(Inventory inventory, ushort id, int toolInstance, int slot)
    {
        var held = inventory.SlotAt(slot);
        var instance = toolInstance != 0 ? toolInstance : FirstInstanceOf(inventory, id);
        if (instance != 0)
        {
            if (inventory.GetToolStack(instance)?.Item is null or GearItem) return null;
            if (held.IsTool && held.ToolInstance == instance) return null;
            return inventory.EquipTool(slot, instance) ? InventoryIntent.EquipTool(slot, instance) : null;
        }

        if (Inventory.CategoryOf(id) is not (InventoryCategory.Blocks or InventoryCategory.Misc)
            || inventory.CountFor(id) <= 0
            || !held.IsTool && held.Id == id)
            return null;
        inventory.EquipStackable(slot, id);
        return InventoryIntent.EquipStackable(slot, id);
    }

    /// <summary>
    /// Wears the row's piece of gear in equipment slot <paramref name="slot"/> (a drop), when it fits there and is not
    /// already on in it. Returns the intent to mirror, or null.
    /// </summary>
    public static InventoryIntent? DropOnEquipment(Inventory inventory, ushort id, int toolInstance, int slot)
    {
        var instance = toolInstance != 0 ? toolInstance : FirstInstanceOf(inventory, id);
        if (instance == 0 || inventory.Equipment.InstanceAt(slot) == instance) return null;
        return inventory.Equipment.TryEquip(slot, instance) ? InventoryIntent.EquipGear(slot, instance) : null;
    }

    /// <summary>
    /// The hotbar slot showing the row's item, or -1: its own instance's slot for a tool row, else the first slot
    /// showing the id (a stackable, or any instance of a tool kind).
    /// </summary>
    public static int HotbarSlotOf(Inventory inventory, ushort id, int toolInstance)
    {
        if (toolInstance != 0) return inventory.SlotOfTool(toolInstance);
        for (var i = 0; i < Inventory.SlotCount; i++)
            if (inventory.IdAt(i) == id)
                return i;
        return -1;
    }

    /// <summary>Whether the row's piece is worn: its own instance, or for a row naming only a kind, any worn piece of it.</summary>
    public static bool IsWorn(Inventory inventory, ushort id, int toolInstance)
    {
        if (toolInstance != 0) return inventory.Equipment.IsEquipped(toolInstance);
        for (var slot = 0; slot < GearConfig.EquipmentSlotCount; slot++)
            if (inventory.Equipment.StackAt(slot)?.DefinitionId == id)
                return true;
        return false;
    }

    /// <summary>The first live instance of a tool or gear kind, or 0 for a stackable id or a kind the player does not own.</summary>
    internal static int FirstInstanceOf(Inventory inventory, ushort id)
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
