using System;
using System.Collections.Generic;
using VoxelEngine.Config;
using VoxelEngine.Items;

namespace VoxelEngine.UI.InventoryPanel;

/// <summary>
/// What a player wears: one gear instance (or none) per equipment slot, armor slots first, then accessories.
/// A worn piece stays in the Gear pool, so the slots only name instance ids, the way a hotbar slot names a tool.
/// GL-free. <see cref="Version"/> moves on every real change, and <see cref="Stats"/> and <see cref="Abilities"/> are rebuilt only then.
/// </summary>
public sealed class Equipment
{
    private readonly Inventory _inventory;
    private readonly int[] _slots = new int[GearConfig.EquipmentSlotCount];
    private int _statsVersion = -1;
    private GearStats _stats;
    private GearAbility _abilities;

    internal Equipment(Inventory inventory) => _inventory = inventory;

    /// <summary>Moves on every change to what is worn, so a reader can rebuild only then.</summary>
    public int Version { get; private set; }

    /// <summary>The instance id worn in a slot, or 0 when it is empty or the index is not a slot.</summary>
    public int InstanceAt(int slot) => GearSlots.IsValid(slot) ? _slots[slot] : 0;

    /// <summary>The live stack worn in a slot, or null.</summary>
    public ItemStack? StackAt(int slot) =>
        InstanceAt(slot) is var id and not 0 && _inventory.TryGetToolStack(id, out var stack) ? stack : null;

    /// <summary>The slot a gear instance is worn in, or -1.</summary>
    public int SlotOf(int instanceId)
    {
        if (instanceId == 0) return -1;
        for (var i = 0; i < _slots.Length; i++)
            if (_slots[i] == instanceId)
                return i;
        return -1;
    }

    /// <summary>Whether the instance is worn.</summary>
    public bool IsEquipped(int instanceId) => SlotOf(instanceId) >= 0;

    /// <summary>
    /// Wears an owned gear instance in a slot that fits its kind, replacing what was there (which goes back to
    /// the pool, still owned). A piece already worn elsewhere moves. False, changing nothing, for an unknown
    /// slot, a missing instance, a non-gear item or a slot of the wrong kind.
    /// </summary>
    public bool TryEquip(int slot, int instanceId)
    {
        if (!GearSlots.IsValid(slot) || !_inventory.TryGetToolStack(instanceId, out var stack)) return false;
        if (stack.Item is not GearItem gear || !GearSlots.Fits(gear.Info.Slot, slot)) return false;
        if (_slots[slot] == instanceId) return true;

        var from = SlotOf(instanceId);
        if (from >= 0) _slots[from] = 0;
        _slots[slot] = instanceId;
        Version++;
        return true;
    }

    /// <summary>Wears an owned gear instance in the first fitting slot: an empty one if there is any, else the
    /// first fitting one. False when it fits nowhere.</summary>
    public bool TryEquipAuto(int instanceId)
    {
        if (!_inventory.TryGetToolStack(instanceId, out var stack) || stack.Item is not GearItem gear) return false;
        var first = -1;
        for (var i = 0; i < _slots.Length; i++)
        {
            if (!GearSlots.Fits(gear.Info.Slot, i)) continue;
            if (_slots[i] == instanceId) return true;
            if (_slots[i] == 0) return TryEquip(i, instanceId);
            if (first < 0) first = i;
        }
        return first >= 0 && TryEquip(first, instanceId);
    }

    /// <summary>Takes off whatever a slot holds; false when it was empty or not a slot.</summary>
    public bool Unequip(int slot)
    {
        if (!GearSlots.IsValid(slot) || _slots[slot] == 0) return false;
        _slots[slot] = 0;
        Version++;
        return true;
    }

    /// <summary>The worn stacks, in slot order, skipping empty slots.</summary>
    public IEnumerable<ItemStack> Worn()
    {
        for (var i = 0; i < _slots.Length; i++)
            if (StackAt(i) is { } stack)
                yield return stack;
    }

    /// <summary>The summed, clamped stats of everything worn.</summary>
    public GearStats Stats
    {
        get
        {
            Refresh();
            return _stats;
        }
    }

    /// <summary>The abilities everything worn grants, rebuilt only when what is worn changes.</summary>
    public GearAbility Abilities
    {
        get
        {
            Refresh();
            return _abilities;
        }
    }

    /// <summary>Whether what is worn grants <paramref name="ability"/>.</summary>
    public bool Has(GearAbility ability) => ability != GearAbility.None && (Abilities & ability) == ability;

    private void Refresh()
    {
        if (_statsVersion == Version) return;
        _stats = GearStatsCalculator.Compute(Worn());
        _abilities = GearStatsCalculator.Abilities(Worn());
        _statsVersion = Version;
    }

    /// <summary>Whether any worn piece makes its wearer immune to the named status effect.</summary>
    public bool IsImmuneTo(string effectName)
    {
        foreach (var stack in Worn())
            if (stack.Item is GearItem gear && gear.Info.Immune.Contains(effectName))
                return true;
        return false;
    }

    /// <summary>Forgets an instance that left the pool (called by the inventory), unequipping it.</summary>
    internal void Forget(int instanceId)
    {
        var slot = SlotOf(instanceId);
        if (slot >= 0) Unequip(slot);
    }

    /// <summary>The raw slot ids, for the save codec and the tests.</summary>
    internal int[] Slots => _slots;

    /// <summary>Empties every slot (a save import).</summary>
    internal void Clear()
    {
        Array.Clear(_slots);
        Version++;
    }

    /// <summary>Replaces the slots with saved ones that still resolve to gear that fits: anything else is skipped.</summary>
    internal void Restore(IReadOnlyList<int>? saved)
    {
        Clear();
        if (saved == null) return;
        for (var i = 0; i < Math.Min(saved.Count, _slots.Length); i++)
            if (saved[i] != 0)
                TryEquip(i, saved[i]);
    }
}
