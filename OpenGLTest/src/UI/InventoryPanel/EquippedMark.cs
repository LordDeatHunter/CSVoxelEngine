using VoxelEngine.Data;

namespace VoxelEngine.UI.InventoryPanel;

/// <summary>
/// How a listed row of the [E] panel shows that its item is equipped, the same in every list (a tab's own rows, the
/// search results and the creative catalog): on the hotbar it wears the slot's key on a chip over its icon, worn it
/// reads "worn" in place of its count, and either way its accent bar is green. GL-free; built per drawn row from
/// <see cref="RowEquip.HotbarSlotOf"/> and <see cref="RowEquip.IsWorn"/>.
/// </summary>
public readonly record struct EquippedMark(int HotbarSlot, bool Worn)
{
    /// <summary>The mark of the row naming item <paramref name="id"/> and, for a tool or gear row, its instance (else 0).</summary>
    public static EquippedMark Of(Inventory inventory, byte id, int toolInstance) =>
        new(RowEquip.HotbarSlotOf(inventory, id, toolInstance), RowEquip.IsWorn(inventory, id, toolInstance));

    /// <summary>Whether the row's item is on the hotbar or worn.</summary>
    public bool IsEquipped => HotbarSlot >= 0 || Worn;

    /// <summary>The row's accent bar: green while equipped, else null for the row's own default.</summary>
    public Rgba? Accent => IsEquipped ? UiTheme.ReadyGreen : null;

    /// <summary>The row's count label: "worn" for a worn piece, else <paramref name="label"/>.</summary>
    public string CountLabel(string label) => Worn ? "worn" : label;

    /// <summary>The number key that selects hotbar slot <paramref name="slot"/>: "1".."9", then "0" for the tenth.</summary>
    public static string HotbarKey(int slot) => slot == Inventory.SlotCount - 1 ? "0" : (slot + 1).ToString();
}
