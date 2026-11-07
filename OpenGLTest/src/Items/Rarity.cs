using System;

namespace VoxelEngine.Items;

/// <summary>Item rarity, common to legendary: tooltip color and loot weight come from it.</summary>
public enum Rarity : byte
{
    Common,
    Uncommon,
    Rare,
    Epic,
    Legendary,
}

/// <summary>Parsing and display of <see cref="Rarity"/>.</summary>
public static class Rarities
{
    /// <summary>Parses a JSON rarity name (case-insensitive); false when unknown.</summary>
    public static bool TryParse(string? text, out Rarity rarity) =>
        Enum.TryParse(text?.Trim(), ignoreCase: true, out rarity) && Enum.IsDefined(rarity);
}
