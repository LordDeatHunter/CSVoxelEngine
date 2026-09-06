using System;
using System.Linq;

namespace silkgltriangle;

/// <summary>
/// The player's hotbar: a fixed row of slots, one of which is selected. Slots are seeded from
/// the registered full-cube block types (taper variants are wood states, not items, so they
/// are skipped) so a newly added type shows up without touching this class; leftover slots
/// stay empty (block id 0).
///
/// Each slot tracks how many blocks it holds. Counts start at zero and are filled by mining, so
/// a slot renders its block whether or not it holds any — only placing checks the count. The
/// player spawns with a starter stock of TNT.
/// </summary>
public class Inventory
{
    public const int SlotCount = 8;

    /// <summary>How much TNT the player starts with.</summary>
    public const int StartingTnt = 50;

    /// <summary>How many lamps the player starts with (lamps are not generated in terrain).</summary>
    public const int StartingLamps = 64;

    // A pickup makes the slot's icon jump and settle back, so a count change is visible even
    // though the count itself isn't drawn yet
    public const float PopSeconds = 0.25f;
    public const float PopScale = 1.3f;

    private readonly byte[] _slots = BuildSlots();
    private readonly int[] _counts = BuildCounts();

    // Seconds since each slot's last pickup, parked at PopSeconds once the pop has finished
    private readonly float[] _popElapsed = FinishedPops();

    /// <summary>Which slot the player has selected; always a valid index.</summary>
    public int SelectedIndex { get; private set; }

    /// <summary>Block id in the selected slot, or 0 when that slot is empty.</summary>
    public byte SelectedBlockId => _slots[SelectedIndex];

    public int SelectedCount => _counts[SelectedIndex];

    public ReadOnlySpan<byte> Slots => _slots;

    /// <summary>Whether the selected slot holds a block that can actually be placed.</summary>
    public bool CanPlaceSelected => SelectedBlockId != 0 && SelectedCount > 0;

    public int CountAt(int index) => _counts[index];

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

    /// <summary>Advances the slot pop animations.</summary>
    public void Update(float deltaTime)
    {
        for (var i = 0; i < _popElapsed.Length; i++)
            _popElapsed[i] = MathF.Min(PopSeconds, _popElapsed[i] + deltaTime);
    }

    /// <summary>
    /// Credits picked-up blocks to the slot holding that type and pops its icon. Returns false
    /// for a block with no slot, whose drop the caller should discard.
    /// </summary>
    public bool Add(byte blockId, int amount = 1)
    {
        var index = IndexOf(blockId);
        if (index < 0) return false;

        _counts[index] += amount;
        _popElapsed[index] = 0f;
        return true;
    }

    /// <summary>Spends one block from the selected slot. Returns false when it is empty.</summary>
    public bool ConsumeSelected()
    {
        if (!CanPlaceSelected) return false;

        _counts[SelectedIndex]--;
        return true;
    }

    /// <summary>Icon scale for a slot: 1 at rest, briefly larger just after a pickup.</summary>
    public float PopScaleAt(int index) =>
        MathUtils.Lerp(1f, PopScale, MathUtils.Pulse(_popElapsed[index] / PopSeconds));

    private int IndexOf(byte blockId)
    {
        if (blockId == 0) return -1;

        for (var i = 0; i < _slots.Length; i++)
            if (_slots[i] == blockId)
                return i;

        return -1;
    }

    private static int Wrap(int index) => ((index % SlotCount) + SlotCount) % SlotCount;

    private static byte[] BuildSlots()
    {
        var slots = new byte[SlotCount];
        // Taper variants (branch/limb/sprig/...) are visual states of wood, not items: mining
        // any of them drops the default log, so only the log itself takes a slot
        var ids = BlockType.Types.Keys.Where(id => !BlockType.IsPartialCube(id)).OrderBy(id => id).Take(SlotCount).ToArray();
        ids.CopyTo(slots, 0);
        return slots;
    }

    // The player spawns holding TNT and lamps; everything else is earned by mining
    private static int[] BuildCounts()
    {
        var counts = new int[SlotCount];
        var ids = BlockType.Types.Keys.Where(id => !BlockType.IsPartialCube(id)).OrderBy(id => id).Take(SlotCount).ToArray();
        for (var i = 0; i < ids.Length; i++)
            if (ids[i] == BlockType.Tnt)
                counts[i] = StartingTnt;
            else if (ids[i] == BlockType.Lamp)
                counts[i] = StartingLamps;
        return counts;
    }

    private static float[] FinishedPops()
    {
        var pops = new float[SlotCount];
        Array.Fill(pops, PopSeconds);
        return pops;
    }
}
