using System;
using VoxelEngine.Config;

namespace VoxelEngine.Player;

/// <summary>
/// How worn armor changes a blow (GL-free): <c>damage = max(min(hit, minDamage(source)), hit - armor)</c>, so armor
/// takes points off every blow but a blow never drops below its source's minimum (nor below itself: a 1-point hit
/// stays 1). Drowning and magic damage over time bypass armor, and a blow with no named source is raw, which is what
/// the save restore and the client's health mirror use.
/// </summary>
public static class ArmorRules
{
    /// <summary>Whether armor never applies to blows from <paramref name="source"/>.</summary>
    public static bool Bypasses(DamageSource source) =>
        source is DamageSource.None or DamageSource.Drowning or DamageSource.Magic;

    /// <summary>The least a blow from <paramref name="source"/> keeps through armor.</summary>
    public static int MinDamage(DamageSource source) => source switch
    {
        DamageSource.Mob => PlayerConfig.ArmorMinDamageMob,
        DamageSource.Fall => PlayerConfig.ArmorMinDamageFall,
        DamageSource.Lava => PlayerConfig.ArmorMinDamageLava,
        DamageSource.Cactus => PlayerConfig.ArmorMinDamageCactus,
        _ => GearConfig.UniversalMinDamage,
    };

    /// <summary>The damage a blow of <paramref name="hit"/> deals through <paramref name="armor"/> points.</summary>
    public static int Reduce(int hit, int armor, DamageSource source)
    {
        if (hit <= 0 || armor <= 0 || Bypasses(source)) return Math.Max(0, hit);
        return Math.Max(Math.Min(hit, MinDamage(source)), hit - armor);
    }
}
