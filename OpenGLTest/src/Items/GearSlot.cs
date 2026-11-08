using System;
using VoxelEngine.Config;

namespace VoxelEngine.Items;

/// <summary>Which kind of equipment slot a piece of gear fits.</summary>
public enum GearSlot : byte
{
    Head,
    Chest,
    Legs,
    Feet,
    Gloves,
    Accessory,
}

/// <summary>Maps gear slot kinds to the player's equipment slot indices (armor slots first, then accessories).</summary>
public static class GearSlots
{
    /// <summary>Whether equipment slot <paramref name="index"/> takes a piece of the given kind.</summary>
    public static bool Fits(GearSlot slot, int index) => slot == GearSlot.Accessory
        ? index >= GearConfig.ArmorSlotCount && index < GearConfig.EquipmentSlotCount
        : index == (int)slot;

    /// <summary>Whether a piece in this slot counts toward a set's bonuses: head, chest and legs only. Boots and gloves are free-standing armor.</summary>
    public static bool CountsForSets(GearSlot slot) => slot is GearSlot.Head or GearSlot.Chest or GearSlot.Legs;

    /// <summary>The slot kind an equipment index holds.</summary>
    public static GearSlot KindOf(int index) => index < GearConfig.ArmorSlotCount ? (GearSlot)index : GearSlot.Accessory;

    /// <summary>Whether <paramref name="index"/> names an equipment slot.</summary>
    public static bool IsValid(int index) => index >= 0 && index < GearConfig.EquipmentSlotCount;

    /// <summary>Display name of a slot kind.</summary>
    public static string Label(GearSlot slot) => slot switch
    {
        GearSlot.Head => "Head",
        GearSlot.Chest => "Chest",
        GearSlot.Legs => "Legs",
        GearSlot.Feet => "Feet",
        GearSlot.Gloves => "Hands",
        _ => "Trinket",
    };

    /// <summary>Parses a JSON slot name (case-insensitive); false when unknown.</summary>
    public static bool TryParse(string? text, out GearSlot slot) =>
        Enum.TryParse(text?.Trim(), ignoreCase: true, out slot) && Enum.IsDefined(slot);
}
