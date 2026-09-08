using System;
using System.Linq;

namespace silkgltriangle;

/// <summary>
/// The player's hotbar: a fixed row of slots, one of which is selected. Slots are seeded from
/// the registered full-cube block types (taper variants are wood states, not items, so they
/// are skipped) followed by the pure item types (see <see cref="ItemType"/>), so a newly added
/// type shows up without touching this class; leftover slots stay empty (id 0).
///
/// Each slot tracks how many blocks or items it holds. Counts start at zero and are filled by
/// mining, so a slot renders its icon whether or not it holds any — only placing checks the
/// count. Items can be collected but never placed. The player spawns with a starter stock
/// of TNT.
/// </summary>
public class Inventory
{
    // Room to spare: every block with an item form plus every pure item needs a slot, and a
    // truncated list would make the last types unreachable (BuildSlots warns if it happens).
    public const int SlotCount = 16;

    /// <summary>How much TNT the player starts with.</summary>
    public const int StartingTnt = 50;

    /// <summary>How many lamps the player starts with (lamps are not generated in terrain).</summary>
    public const int StartingLamps = 64;

    /// <summary>How many (empty) buckets the player starts with; water is only reachable with one.</summary>
    public const int StartingBuckets = 1;

    // A pickup makes the slot's icon jump and settle back, echoing the count change
    // drawn in the slot's corner
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

    /// <summary>
    /// Whether the selected slot holds a block that can actually be placed. Pure items
    /// (see <see cref="ItemType"/>) are collected but never placed.
    /// </summary>
    public bool CanPlaceSelected => SelectedBlockId != 0 && SelectedCount > 0 && !ItemType.IsItem(SelectedBlockId);

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
    /// Credits picked-up blocks or items to the slot holding that type and pops its icon.
    /// Returns false for an id with no slot, whose drop the caller should discard.
    /// </summary>
    public bool Add(byte blockId, int amount = 1)
    {
        var index = IndexOf(blockId);
        if (index < 0) return false;

        _counts[index] += amount;
        _popElapsed[index] = 0f;
        return true;
    }

    /// <summary>
    /// Spends one unit of <paramref name="fromId"/> and credits one of <paramref name="toId"/>,
    /// then selects the slot that received it — so using a bucket leaves the water bucket in
    /// hand and scooping and pouring alternate on the same button. Returns false when the source
    /// slot is empty or either id has no slot.
    /// </summary>
    public bool Exchange(byte fromId, byte toId)
    {
        var from = IndexOf(fromId);
        var to = IndexOf(toId);
        if (from < 0 || to < 0 || _counts[from] <= 0) return false;

        _counts[from]--;
        Add(toId);
        SelectedIndex = to;
        return true;
    }

    /// <summary>Spends one unit from the selected slot. Returns false when it is empty.</summary>
    public bool ConsumeSelected()
    {
        if (SelectedBlockId == 0 || SelectedCount <= 0) return false;

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

    /// <summary>
    /// Ids the hotbar shows, in order: every block that declares <c>hasItem</c>, then every pure
    /// item. Blocks without an item form (the taper variants, which are visual states of wood,
    /// and pebbles, which drop flint) never enter the inventory, so they get no slot.
    /// </summary>
    private static byte[] HotbarIds() =>
        BlockType.Types.Values
            .Where(type => type.HasItem)
            .Select(type => type.Id)
            .OrderBy(id => id)
            .Concat(ItemType.Types.Keys.OrderBy(id => id))
            .ToArray();

    private static byte[] BuildSlots()
    {
        var ids = HotbarIds();

        // The hotbar is the only way to reach a type, so overflowing it is a content bug the
        // author has to see rather than a silently dropped tail
        if (ids.Length > SlotCount)
            Console.WriteLine(
                $"Hotbar has {SlotCount} slots but {ids.Length} block and item types want one: " +
                $"the last {ids.Length - SlotCount} are unreachable. Raise Inventory.SlotCount.");

        var slots = new byte[SlotCount];
        ids.AsSpan(0, Math.Min(ids.Length, SlotCount)).CopyTo(slots);
        return slots;
    }

    // The player spawns holding TNT, lamps and a bucket; everything else is earned by mining
    private static int[] BuildCounts()
    {
        var counts = new int[SlotCount];
        var ids = HotbarIds();

        // Resolved once per inventory rather than per slot; this runs when the UI is built
        var tnt = ContentRegistry.RequireBlock("tnt").Id;
        var lamp = ContentRegistry.RequireBlock("lamp").Id;
        var glass = ContentRegistry.RequireBlock("glass").Id;
        var sand = ContentRegistry.RequireBlock("sand").Id;
        var gravel = ContentRegistry.RequireBlock("gravel").Id;
        var bucket = ContentRegistry.RequireItem("bucket").Id;

        for (var i = 0; i < ids.Length && i < SlotCount; i++)
            if (ids[i] == tnt)
                counts[i] = StartingTnt;
            else if (ids[i] == lamp)
                counts[i] = StartingLamps;
            else if (ids[i] == bucket)
                counts[i] = StartingBuckets;
            else if (ids[i] == glass || ids[i] == sand || ids[i] == gravel)
                counts[i] = 50;
        return counts;
    }

    private static float[] FinishedPops()
    {
        var pops = new float[SlotCount];
        Array.Fill(pops, PopSeconds);
        return pops;
    }
}
