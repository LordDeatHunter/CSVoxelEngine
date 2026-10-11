using System;

namespace silkgltriangle;

/// <summary>
/// The goblin torch rule, GL-free: how many torches a spawn carries (0, 1 or 2) from one uniform
/// roll and the light, so the same roll always gives the same count for the same night and depth,
/// and whether a carried torch is out (dark: night past
/// <see cref="WorldGenerationConfig.GoblinTorchNightFactor"/>, or underground). Mirrors
/// <see cref="SlimeSize"/>'s size roll.
/// </summary>
public static class GoblinTorches
{
    /// <summary>
    /// The chance a spawn carries a torch: the day chance in full day, the night chance in full
    /// night (<paramref name="night"/> is <see cref="DayCycle.NightFactor"/>, 0..1), and at least
    /// the underground chance whenever the spawn is <paramref name="underground"/>.
    /// </summary>
    public static float Chance(float night, bool underground)
    {
        var chance = MathUtils.Lerp(WorldGenerationConfig.GoblinTorchChanceDay,
            WorldGenerationConfig.GoblinTorchChanceNight, MathUtils.Clamp01(night));
        return underground ? MathF.Max(chance, WorldGenerationConfig.GoblinTorchChanceUnderground) : chance;
    }

    /// <summary>
    /// The torches a spawn carries from one uniform <paramref name="roll"/> in [0, 1): one while
    /// the roll stays under <c>chance</c>, a second while it also stays under <c>chance²</c>, so
    /// mostly none by day and one or two often at night or underground. A pure function: for one
    /// roll, a higher chance never gives fewer torches.
    /// </summary>
    public static int Roll(float night, bool underground, float roll)
    {
        var chance = Chance(night, underground);
        var count = 0;
        var threshold = chance;
        while (count < WorldGenerationConfig.GoblinTorchMaxCount)
        {
            if (roll >= threshold) break;
            count++;
            threshold *= chance;
        }
        return count;
    }

    /// <summary>Whether a goblin carrying <paramref name="torches"/> has one out right now: it is
    /// dark, i.e. underground or at least <see cref="WorldGenerationConfig.GoblinTorchNightFactor"/>
    /// into the night.</summary>
    public static bool Lit(int torches, float night, bool underground) =>
        torches > 0 && (underground || night >= WorldGenerationConfig.GoblinTorchNightFactor);
}
