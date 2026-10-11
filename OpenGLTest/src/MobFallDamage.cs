using System;

namespace silkgltriangle;

/// <summary>
/// The mobs' fall-damage rule, GL-free: the mob counterpart of <see cref="PlayerHealth.FallDamage"/>,
/// scaled to the mob's own pool. A landing from at most <c>safeBlocks</c> is free; past it every whole
/// block costs <see cref="WorldGenerationConfig.MobFallDamagePerBlockShare"/> of the mob's full health
/// (at least 1), so a fall hurts a small and a large pool alike, and the damage never falls as the
/// drop grows. <see cref="Mob"/> bills it on every landing its physics reports, in the authoritative
/// simulation only.
/// </summary>
public static class MobFallDamage
{
    /// <summary>
    /// Health a landing after a drop of <paramref name="dropBlocks"/> takes off a mob whose full health
    /// is <paramref name="maxHealth"/> and which falls <paramref name="safeBlocks"/> unhurt: 0 up to one
    /// whole block past the safe height, then one share of the pool per whole block, clamped to the pool.
    /// </summary>
    public static int Damage(float dropBlocks, float safeBlocks, int maxHealth)
    {
        if (!(dropBlocks > safeBlocks) || maxHealth <= 0) return 0;
        var blocks = (int)MathF.Floor(dropBlocks - safeBlocks + 1e-3f);
        if (blocks <= 0) return 0;
        var perBlock = Math.Max(1, (int)MathF.Ceiling(maxHealth * WorldGenerationConfig.MobFallDamagePerBlockShare));
        return Math.Min(maxHealth, blocks * perBlock);
    }
}
