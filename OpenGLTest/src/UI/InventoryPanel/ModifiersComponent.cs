using System;
using System.Collections.Generic;
using VoxelEngine.Items;

namespace VoxelEngine.UI.InventoryPanel;

/// <summary>
/// The rolled part of one gear instance: its rarity and the stat modifiers it rolled on top of the item's own
/// fixed ones. Minted server-side when loot gear is made (<see cref="GearRoller"/>), immutable, and saved with the
/// instance (<c>ToolData.Rolled</c>). Crafted gear carries none and is fixed by its material.
/// </summary>
public readonly struct ModifiersComponent(Rarity rarity, GearModifier[]? modifiers) : IEquatable<ModifiersComponent>
{
    private readonly GearModifier[]? _modifiers = modifiers is { Length: > 0 } ? (GearModifier[])modifiers.Clone() : null;

    /// <summary>The instance's rarity (its item's base rarity is only the fallback).</summary>
    public Rarity Rarity { get; } = rarity;

    /// <summary>The rolled modifiers, possibly none.</summary>
    public IReadOnlyList<GearModifier> Modifiers => _modifiers ?? [];

    public bool Equals(ModifiersComponent other)
    {
        if (Rarity != other.Rarity) return false;
        var a = Modifiers;
        var b = other.Modifiers;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is ModifiersComponent other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Rarity, Modifiers.Count);
}
