using System;
using VoxelEngine.Config;

namespace VoxelEngine.Items;

/// <summary>The summed stats of everything a player wears (GL-free). Flat values are whole points; the
/// factors are the bonus fractions, so the multiplier is <c>1 + factor</c>.</summary>
public readonly record struct GearStats(
    int Armor = 0,
    int MaxHealth = 0,
    int MaxMana = 0,
    float ManaRegen = 0f,
    float MoveSpeed = 0f,
    float MiningSpeed = 0f,
    float AttackSpeed = 0f,
    float AttackDamage = 0f,
    float UndeadDamage = 0f)
{
    /// <summary>No gear worn.</summary>
    public static readonly GearStats None = default;

    /// <summary>This plus <paramref name="value"/> of one stat (flat stats truncate toward zero).</summary>
    public GearStats With(GearStat stat, float value) => stat switch
    {
        GearStat.Armor => this with { Armor = Armor + (int)value },
        GearStat.MaxHealth => this with { MaxHealth = MaxHealth + (int)value },
        GearStat.MaxMana => this with { MaxMana = MaxMana + (int)value },
        GearStat.ManaRegen => this with { ManaRegen = ManaRegen + value },
        GearStat.MoveSpeed => this with { MoveSpeed = MoveSpeed + value },
        GearStat.MiningSpeed => this with { MiningSpeed = MiningSpeed + value },
        GearStat.AttackSpeed => this with { AttackSpeed = AttackSpeed + value },
        GearStat.AttackDamage => this with { AttackDamage = AttackDamage + value },
        GearStat.UndeadDamage => this with { UndeadDamage = UndeadDamage + value },
        _ => this,
    };

    /// <summary>The sums clamped to what the game allows, so a stack of rolled gear can never break the rules.</summary>
    public GearStats Clamped() => new(
        Math.Clamp(Armor, 0, GearConfig.ArmorCap),
        Math.Clamp(MaxHealth, 0, GearConfig.MaxHealthBonusCap),
        Math.Clamp(MaxMana, 0, GearConfig.MaxManaBonusCap),
        Math.Clamp(ManaRegen, -0.9f, GearConfig.FactorBonusCap),
        Math.Clamp(MoveSpeed, -0.5f, GearConfig.FactorBonusCap),
        Math.Clamp(MiningSpeed, -0.5f, GearConfig.FactorBonusCap),
        Math.Clamp(AttackSpeed, -0.5f, GearConfig.FactorBonusCap),
        Math.Clamp(AttackDamage, -0.9f, GearConfig.FactorBonusCap),
        Math.Clamp(UndeadDamage, 0f, GearConfig.FactorBonusCap));

    /// <summary>Multiplier of the move speed.</summary>
    public float MoveFactor => 1f + MoveSpeed;

    /// <summary>Multiplier of how fast mining hits land.</summary>
    public float MiningFactor => 1f + MiningSpeed;

    /// <summary>Multiplier of how fast swings land.</summary>
    public float AttackSpeedFactor => 1f + AttackSpeed;

    /// <summary>Multiplier of the damage a blow deals.</summary>
    public float DamageFactor => 1f + AttackDamage;

    /// <summary>Multiplier of the mana regeneration rate.</summary>
    public float ManaRegenFactor => 1f + ManaRegen;

    /// <summary>Parses a JSON stat name (case-insensitive); false when unknown.</summary>
    public static bool TryParse(string? text, out GearStat stat) =>
        Enum.TryParse(text?.Trim(), ignoreCase: true, out stat) && Enum.IsDefined(stat);

    /// <summary>Whether a stat is a flat whole-number one.</summary>
    public static bool IsFlat(GearStat stat) => stat is GearStat.Armor or GearStat.MaxHealth or GearStat.MaxMana;
}
