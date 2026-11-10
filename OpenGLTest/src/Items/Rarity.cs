using System;
using VoxelEngine.Data;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Items;

/// <summary>Item rarity, common to legendary: tooltip color and loot weight come from it, and it sets the chrome
/// its list rows and tooltip wear.</summary>
public enum Rarity : byte
{
    Common,
    Uncommon,
    Rare,
    Epic,
    Legendary,
}

/// <summary>Parsing, display and loot weight of <see cref="Rarity"/>.</summary>
public static class Rarities
{
    /// <summary>The rarity's display name.</summary>
    public static string Label(Rarity rarity) => rarity.ToString();

    /// <summary>The rarity an item shows: a stack's own (its roll, else its item's, see
    /// <see cref="GearStatsCalculator.RarityOf"/>), else with no stack the item's base rarity; Common for a block or
    /// an unknown id.</summary>
    public static Rarity Of(byte id, ItemStack? stack) =>
        stack != null ? GearStatsCalculator.RarityOf(stack) : ItemRegistry.TryGet(id)?.Rarity ?? Rarity.Common;

    /// <summary>The color a rarity's name and slot border are drawn in.</summary>
    public static Rgba Color(Rarity rarity) => rarity switch
    {
        Rarity.Uncommon => new Rgba(126, 217, 87),
        Rarity.Rare => new Rgba(96, 160, 255),
        Rarity.Epic => new Rgba(190, 110, 240),
        Rarity.Legendary => new Rgba(255, 170, 40),
        _ => Rgba.White,
    };

    /// <summary>How likely a rarity is in loot, relative to the others.</summary>
    public static int LootWeight(Rarity rarity) => GearConfig.LootWeightPerRarity[(int)rarity];

    /// <summary>Picks a rarity by loot weight from <paramref name="unit"/>, a roll in 0..1.</summary>
    public static Rarity PickByWeight(float unit)
    {
        var total = 0;
        foreach (var weight in GearConfig.LootWeightPerRarity) total += weight;
        var target = Math.Clamp(unit, 0f, 0.999999f) * total;
        var sum = 0;
        for (var i = 0; i < GearConfig.LootWeightPerRarity.Length; i++)
        {
            sum += GearConfig.LootWeightPerRarity[i];
            if (target < sum) return (Rarity)i;
        }
        return Rarity.Common;
    }

    /// <summary>Parses a JSON rarity name (case-insensitive); false when unknown.</summary>
    public static bool TryParse(string? text, out Rarity rarity) =>
        Enum.TryParse(text?.Trim(), ignoreCase: true, out rarity) && Enum.IsDefined(rarity);
}
