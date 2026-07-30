using System;
using System.Linq;

namespace silkgltriangle;

/// <summary>
/// The player's hotbar: a fixed row of slots, one of which is selected. Slots are seeded from
/// the registered block types so a newly added type shows up without touching this class;
/// leftover slots stay empty (block id 0).
/// </summary>
public class Inventory
{
    public const int SlotCount = 4;

    private readonly byte[] _slots = BuildSlots();

    /// <summary>Which slot the player has selected; always a valid index.</summary>
    public int SelectedIndex { get; private set; }

    /// <summary>Block id in the selected slot, or 0 when that slot is empty.</summary>
    public byte SelectedBlockId => _slots[SelectedIndex];

    public ReadOnlySpan<byte> Slots => _slots;

    public void Select(int index) => SelectedIndex = Wrap(index);

    /// <summary>
    /// Moves the selection by a scroll wheel delta. Scrolling up (positive) walks left along
    /// the hotbar, matching the usual "previous item" feel, and the selection wraps.
    /// </summary>
    public void Scroll(float delta)
    {
        if (delta == 0f) return;
        Select(SelectedIndex - Math.Sign(delta));
    }

    private static int Wrap(int index) => ((index % SlotCount) + SlotCount) % SlotCount;

    private static byte[] BuildSlots()
    {
        var slots = new byte[SlotCount];
        var ids = BlockType.Types.Keys.OrderBy(id => id).Take(SlotCount).ToArray();
        ids.CopyTo(slots, 0);
        return slots;
    }
}
