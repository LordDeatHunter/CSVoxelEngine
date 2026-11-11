using System;
using System.Collections.Generic;
using VoxelEngine.Config;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Items;

/// <summary>The outcome of one roll: the component to put on the stack and the name it earned.</summary>
public readonly record struct RolledGear(ModifiersComponent Modifiers, string Name);

/// <summary>
/// Rolls the modifiers of loot gear, a pure function of the piece, its rarity and a seed (GL-free), so a ruin
/// chest that rolls from the hash of its cell gives the same piece on every machine. A common roll adds nothing; each
/// tier above adds one more modifier (<see cref="GearConfig.ModifiersPerRarity"/>), picked among the stats that suit
/// the slot, with a value scaled by the rarity. The piece is named after its strongest roll with a prefix and, from
/// rare up, a suffix for its second.
/// </summary>
public static class GearRoller
{
    /// <summary>Stats any piece may roll, by slot; accessories roll the rest of the vocabulary.</summary>
    private static readonly GearStat[] ArmorPool =
        [GearStat.Armor, GearStat.MaxHealth, GearStat.MoveSpeed, GearStat.MiningSpeed, GearStat.AttackDamage, GearStat.MaxMana];

    private static readonly GearStat[] AccessoryPool =
        [GearStat.MaxHealth, GearStat.MaxMana, GearStat.ManaRegen, GearStat.MoveSpeed, GearStat.MiningSpeed,
            GearStat.AttackSpeed, GearStat.AttackDamage];

    /// <summary>The largest value a modifier of this stat can have, a roll's top range at the top rarity; what a save
    /// restore clamps to.</summary>
    public static float MaxModifierValue(GearStat stat) => RoundValue(stat, Range(stat).High * (0.6f + 0.2f * 5));

    // The one rounding a rolled value gets: whole points for the flat stats, hundredths for the fractions
    private static float RoundValue(GearStat stat, float value) =>
        GearStats.IsFlat(stat) ? MathF.Max(1f, MathF.Round(value)) : MathF.Round(value * 100f) / 100f;

    /// <summary>Rolls <paramref name="gear"/> at <paramref name="rarity"/> from <paramref name="seed"/>.</summary>
    public static RolledGear Roll(GearItem gear, Rarity rarity, ulong seed)
    {
        var count = GearConfig.ModifiersPerRarity[(int)rarity];
        var rng = new SplitMix64(seed ^ ((ulong)gear.Id * 0x9E3779B97F4A7C15UL));
        var pool = new List<GearStat>(gear.Info.Slot == GearSlot.Accessory ? AccessoryPool : ArmorPool);
        var rolled = new List<GearModifier>(count);
        for (var i = 0; i < count && pool.Count > 0; i++)
        {
            var stat = pool[rng.NextInt(pool.Count)];
            pool.Remove(stat);
            rolled.Add(new GearModifier(stat, ValueFor(stat, rarity, rng)));
        }
        return new RolledGear(new ModifiersComponent(rarity, rolled.ToArray()), NameFor(gear, rolled, rarity));
    }

    // A value from the stat's range, scaled up with rarity
    private static float ValueFor(GearStat stat, Rarity rarity, SplitMix64 rng)
    {
        var (low, high) = Range(stat);
        var tier = (int)rarity + 1;
        var value = low + (high - low) * rng.NextFloat();
        value *= 0.6f + 0.2f * tier;
        return RoundValue(stat, value);
    }

    private static (float Low, float High) Range(GearStat stat) => stat switch
    {
        GearStat.Armor => (1f, 3f),
        GearStat.MaxHealth => (5f, 15f),
        GearStat.MaxMana => (10f, 30f),
        GearStat.ManaRegen => (0.05f, 0.15f),
        GearStat.MoveSpeed => (0.02f, 0.06f),
        GearStat.MiningSpeed => (0.05f, 0.15f),
        GearStat.AttackSpeed => (0.03f, 0.1f),
        GearStat.UndeadDamage => (0.05f, 0.15f),
        _ => (0.03f, 0.1f),
    };

    private static string NameFor(GearItem gear, List<GearModifier> rolled, Rarity rarity)
    {
        if (rolled.Count == 0) return gear.Name;
        var name = $"{Prefix(rolled[0].Stat)} {gear.Name}";
        return rolled.Count >= 2 && rarity >= Rarity.Rare ? $"{name} {Suffix(rolled[1].Stat)}" : name;
    }

    private static string Prefix(GearStat stat) => stat switch
    {
        GearStat.Armor => "Sturdy",
        GearStat.MaxHealth => "Hardy",
        GearStat.MaxMana => "Arcane",
        GearStat.ManaRegen => "Flowing",
        GearStat.MoveSpeed => "Swift",
        GearStat.MiningSpeed => "Delving",
        GearStat.AttackSpeed => "Quick",
        GearStat.UndeadDamage => "Hallowed",
        _ => "Fierce",
    };

    private static string Suffix(GearStat stat) => stat switch
    {
        GearStat.Armor => "of the Bulwark",
        GearStat.MaxHealth => "of the Bear",
        GearStat.MaxMana => "of the Magus",
        GearStat.ManaRegen => "of the Spring",
        GearStat.MoveSpeed => "of the Hare",
        GearStat.MiningSpeed => "of the Miner",
        GearStat.AttackSpeed => "of the Viper",
        GearStat.UndeadDamage => "of the Dawn",
        _ => "of the Wolf",
    };
}
