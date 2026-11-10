using System;

namespace VoxelEngine.Simulation.Chests;

/// <summary>
/// What one chest block holds: a fixed number of <see cref="ChestSlot"/>s. <see cref="Version"/> moves on every change,
/// so the server knows which viewers to send a fresh snapshot to. GL-free; the transfer rules are
/// <see cref="ChestTransfer"/>.
/// </summary>
public sealed class ChestState
{
    private readonly ChestSlot[] _slots;

    public ChestState(int slotCount) => _slots = new ChestSlot[slotCount];

    /// <summary>How many slots the chest has.</summary>
    public int SlotCount => _slots.Length;

    /// <summary>Moves on every change to the contents.</summary>
    public int Version { get; private set; }

    /// <summary>The slot at an index (empty for an index outside the chest).</summary>
    public ChestSlot this[int index] => (uint)index < (uint)_slots.Length ? _slots[index] : ChestSlot.Empty;

    /// <summary>Replaces one slot; ignored outside the chest.</summary>
    public void Set(int index, ChestSlot slot)
    {
        if ((uint)index >= (uint)_slots.Length) return;
        _slots[index] = slot.IsEmpty ? ChestSlot.Empty : slot;
        Version++;
    }

    /// <summary>Whether every slot is empty.</summary>
    public bool IsEmpty
    {
        get
        {
            foreach (var slot in _slots)
                if (!slot.IsEmpty)
                    return false;
            return true;
        }
    }

    /// <summary>The first empty slot, or -1 when the chest is full.</summary>
    public int FirstEmpty()
    {
        for (var i = 0; i < _slots.Length; i++)
            if (_slots[i].IsEmpty)
                return i;
        return -1;
    }

    /// <summary>Total units of a stackable id held across the chest.</summary>
    public int CountOf(ushort id)
    {
        var total = 0;
        foreach (var slot in _slots)
            if (slot.Id == id && slot.Instance == null)
                total += slot.Count;
        return total;
    }
}
