using System.Collections.Generic;
using System.Globalization;
using VoxelEngine.Items;

namespace VoxelEngine.UI.Hud;

/// <summary>The words of gear: one stat change as a line, and a stat sum as the lines of the equipment card (GL-free).</summary>
public static class GearText
{
    /// <summary>A stat's display name.</summary>
    public static string Label(GearStat stat) => stat switch
    {
        GearStat.Armor => "Armor",
        GearStat.MaxHealth => "Max health",
        GearStat.MaxMana => "Max mana",
        GearStat.ManaRegen => "Mana regen",
        GearStat.MoveSpeed => "Move speed",
        GearStat.MiningSpeed => "Mining speed",
        GearStat.AttackSpeed => "Attack speed",
        GearStat.UndeadDamage => "Damage vs undead",
        _ => "Damage",
    };

    /// <summary>One stat change as a tooltip line: "+30 Max health", "+10% Move speed", "Armor 4".</summary>
    public static string Line(GearModifier modifier) => Line(modifier.Stat, modifier.Value);

    /// <summary>One stat and its amount as a line; flat stats are whole points, the rest percentages.</summary>
    public static string Line(GearStat stat, float value)
    {
        if (stat == GearStat.Armor)
            return $"Armor {value.ToString("0", CultureInfo.InvariantCulture)}";
        var sign = value >= 0f ? "+" : "-";
        var amount = GearStats.IsFlat(stat)
            ? System.Math.Abs(value).ToString("0", CultureInfo.InvariantCulture)
            : $"{System.Math.Abs(value * 100f).ToString("0", CultureInfo.InvariantCulture)}%";
        return $"{sign}{amount} {Label(stat)}";
    }

    /// <summary>A passive as a tooltip line: "On hit: burning (50%)".</summary>
    public static string Passive(GearPassive passive)
    {
        var trigger = passive.Trigger switch
        {
            PassiveTrigger.OnHit => "On hit",
            PassiveTrigger.OnDamaged => "When hurt",
            _ => $"Under {passive.Below * 100f:0}% health",
        };
        var chance = passive.Chance < 1f ? $" ({passive.Chance * 100f:0}%)" : "";
        return $"{trigger}: {passive.Effect}{chance}";
    }

    /// <summary>The non-zero stats of a sum as lines, armor first.</summary>
    public static List<string> Lines(in GearStats stats)
    {
        var lines = new List<string>();
        if (stats.Armor != 0) lines.Add(Line(GearStat.Armor, stats.Armor));
        if (stats.MaxHealth != 0) lines.Add(Line(GearStat.MaxHealth, stats.MaxHealth));
        if (stats.MaxMana != 0) lines.Add(Line(GearStat.MaxMana, stats.MaxMana));
        if (stats.ManaRegen != 0f) lines.Add(Line(GearStat.ManaRegen, stats.ManaRegen));
        if (stats.MoveSpeed != 0f) lines.Add(Line(GearStat.MoveSpeed, stats.MoveSpeed));
        if (stats.MiningSpeed != 0f) lines.Add(Line(GearStat.MiningSpeed, stats.MiningSpeed));
        if (stats.AttackSpeed != 0f) lines.Add(Line(GearStat.AttackSpeed, stats.AttackSpeed));
        if (stats.AttackDamage != 0f) lines.Add(Line(GearStat.AttackDamage, stats.AttackDamage));
        if (stats.UndeadDamage != 0f) lines.Add(Line(GearStat.UndeadDamage, stats.UndeadDamage));
        return lines;
    }
}
