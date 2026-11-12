using System;
using VoxelEngine.Config;
using VoxelEngine.Content.Spells;

namespace VoxelEngine.Magic;

/// <summary>
/// The pure risk rules of a cast, GL-free: whether it fizzles and what the backfire costs the caster.
/// A fizzle wastes the mana already billed and fires no effect. <c>resistance</c> (0..1) is the hook
/// gear will use to cut both (MG-14); nothing sets it yet.
/// </summary>
internal static class CastRisk
{
    /// <summary>Whether a roll in 0..1 fizzles a recipe of <paramref name="stats"/>, the chance cut by <paramref name="resistance"/>.</summary>
    public static bool Fizzles(SpellStats stats, float roll, float resistance = 0f) =>
        roll < stats.FizzleChance * (1f - Math.Clamp(resistance, 0f, 1f));

    /// <summary>
    /// The health a fizzled cast costs its caster: <see cref="SpellConfig.BackfireShare"/> of
    /// <paramref name="maxHealth"/> scaled by how risky the recipe was, at least 1, cut by
    /// <paramref name="resistance"/>; 0 when <paramref name="immune"/> (creative).
    /// </summary>
    public static int BackfireDamage(SpellStats stats, int maxHealth, bool immune, float resistance = 0f)
    {
        if (immune) return 0;
        var scaled = maxHealth * SpellConfig.BackfireShare * Math.Max(stats.FizzleChance, 0.2f)
            * (1f - Math.Clamp(resistance, 0f, 1f));
        return scaled <= 0f ? 0 : Math.Max(1, (int)MathF.Round(scaled));
    }
}
