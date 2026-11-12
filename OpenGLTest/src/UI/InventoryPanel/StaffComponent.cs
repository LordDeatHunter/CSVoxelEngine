using System;
using System.Collections.Generic;
using System.Linq;

namespace VoxelEngine.UI.InventoryPanel;

/// <summary>
/// Staff component: the spells bound to one staff instance, one recipe per slot as spell part keys
/// (<c>&lt;kind&gt;/&lt;name&gt;</c>, never ids), plus the selected slot. The default is nothing bound with
/// slot 0 selected. Immutable: every change builds a new component (<see cref="Items.StaffBinding"/>),
/// so a copied struct never sees a later edit through the arrays it shares.
/// </summary>
public readonly struct StaffComponent : IEquatable<StaffComponent>
{
    // Index = slot, an empty array = unbound; trailing unbound slots are trimmed and an all-unbound
    // staff stores null, so equal contents always have the same shape.
    private readonly string[][]? _slots;

    /// <summary>The bound recipes by slot (null or empty = unbound) and the selected slot.</summary>
    public StaffComponent(IReadOnlyList<IReadOnlyList<string>?>? slots, int selected)
    {
        Selected = selected;
        if (slots == null) return;

        var last = -1;
        for (var i = 0; i < slots.Count; i++)
            if (slots[i] is { Count: > 0 }) last = i;
        if (last < 0) return;

        var copy = new string[last + 1][];
        for (var i = 0; i <= last; i++)
            copy[i] = slots[i] is { Count: > 0 } keys ? keys.ToArray() : Array.Empty<string>();
        _slots = copy;
    }

    /// <summary>The selected slot, the one a cast uses.</summary>
    public int Selected { get; }

    /// <summary>The part keys bound in that slot, in recipe order; empty when unbound or out of range.</summary>
    public IReadOnlyList<string> PartsAt(int slot) =>
        _slots != null && slot >= 0 && slot < _slots.Length ? _slots[slot] : Array.Empty<string>();

    /// <summary>Whether that slot holds a recipe.</summary>
    public bool IsBound(int slot) => PartsAt(slot).Count > 0;

    /// <summary>How many slots hold a recipe.</summary>
    public int BoundCount
    {
        get
        {
            if (_slots == null) return 0;
            var count = 0;
            foreach (var slot in _slots)
                if (slot.Length > 0) count++;
            return count;
        }
    }

    /// <inheritdoc/>
    public bool Equals(StaffComponent other)
    {
        if (Selected != other.Selected) return false;
        var length = Math.Max(_slots?.Length ?? 0, other._slots?.Length ?? 0);
        for (var i = 0; i < length; i++)
            if (!PartsAt(i).SequenceEqual(other.PartsAt(i), StringComparer.Ordinal)) return false;
        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is StaffComponent other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Selected);
        if (_slots == null) return hash.ToHashCode();
        for (var i = 0; i < _slots.Length; i++)
        {
            if (_slots[i].Length == 0) continue;
            hash.Add(i);
            foreach (var key in _slots[i]) hash.Add(key, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }

    public static bool operator ==(StaffComponent left, StaffComponent right) => left.Equals(right);

    public static bool operator !=(StaffComponent left, StaffComponent right) => !left.Equals(right);
}
