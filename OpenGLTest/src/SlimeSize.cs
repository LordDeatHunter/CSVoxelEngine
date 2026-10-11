using System;

namespace silkgltriangle;

/// <summary>
/// GL-free per-size slime rules. A slime's size is an integer from <see cref="Min"/> to
/// <see cref="Max"/>, and every stat it scales is the size-1 value times
/// <c>1 + (size - 1) * step</c> with one step per stat in <see cref="WorldGenerationConfig"/>
/// (<c>SlimeSize*Step</c>), so no stat branches on a particular size.
/// </summary>
public static class SlimeSize
{
    /// <summary>The plain, smallest slime.</summary>
    public const int Min = 1;

    /// <summary>The largest size a slime can have.</summary>
    public const int Max = WorldGenerationConfig.SlimeMaxSize;

    /// <summary><paramref name="size"/> clamped into <see cref="Min"/>..<see cref="Max"/>, so a
    /// stale save or an old server's zero still names a real size.</summary>
    public static int Clamp(int size) => Math.Clamp(size, Min, Max);

    /// <summary>The multiplier <c>1 + (size - 1) * step</c> of a stat that grows by
    /// <paramref name="step"/> per size.</summary>
    public static float Multiplier(int size, float step) => 1f + (Clamp(size) - Min) * step;

    /// <summary>The body scale: the drawn model and the collision box are the size-1 ones times this.</summary>
    public static float Scale(int size) => Multiplier(size, WorldGenerationConfig.SlimeSizeScaleStep);

    /// <summary>
    /// The horizontal speed a shove of <paramref name="speed"/> (a punch, a slice, a grounded player
    /// walking into it) leaves a slime of <paramref name="size"/> with: the shove divided by the body
    /// <see cref="Scale"/>, so a larger, heavier slime is pushed less and the smallest takes the shove
    /// as given.
    /// </summary>
    public static float ShoveSpeed(float speed, int size) => speed / Scale(size);

    /// <summary>How much faster than a size-1 slime's a hop of <paramref name="size"/> leaves the ground
    /// (the vertical launch speed): the body <see cref="Scale"/> to
    /// <see cref="WorldGenerationConfig.SlimeHopLiftExponent"/>.</summary>
    public static float HopLift(int size) => MathF.Pow(Scale(size), WorldGenerationConfig.SlimeHopLiftExponent);

    /// <summary>How much faster than a size-1 slime's a hop of <paramref name="size"/> travels sideways:
    /// the body <see cref="Scale"/> to <see cref="WorldGenerationConfig.SlimeHopReachExponent"/>.</summary>
    public static float HopReach(int size) => MathF.Pow(Scale(size), WorldGenerationConfig.SlimeHopReachExponent);

    /// <summary>How much longer than a size-1 slime's the crouch before a hop of <paramref name="size"/>
    /// lasts: the body <see cref="Scale"/> to <see cref="WorldGenerationConfig.SlimeHopWindupExponent"/>.</summary>
    public static float WindupScale(int size) => MathF.Pow(Scale(size), WorldGenerationConfig.SlimeHopWindupExponent);

    /// <summary>The health pool of a slime whose kind's JSON pool is <paramref name="baseHealth"/>.</summary>
    public static int Health(int baseHealth, int size) =>
        Math.Max(1, (int)MathF.Round(baseHealth * Multiplier(size, WorldGenerationConfig.SlimeSizeHealthStep)));

    /// <summary>The damage one airborne touch deals.</summary>
    public static int ContactDamage(int size) =>
        (int)MathF.Round(WorldGenerationConfig.SlimeContactDamage * Multiplier(size, WorldGenerationConfig.SlimeSizeDamageStep));

    /// <summary>How many times the slime's loot table is rolled on its death.</summary>
    public static int LootRolls(int size) =>
        Math.Max(1, (int)MathF.Round(Multiplier(size, WorldGenerationConfig.SlimeSizeLootStep)));

    /// <summary>
    /// The chance a ring spawn grows past each size: the day chance in full day, the night chance in
    /// full night (<paramref name="night"/> is <see cref="DayCycle.NightFactor"/>, 0..1), and the
    /// night chance whenever the spawn is <paramref name="underground"/>.
    /// </summary>
    public static float LargeChance(float night, bool underground) =>
        MathUtils.Lerp(WorldGenerationConfig.SlimeLargeChanceDay, WorldGenerationConfig.SlimeLargeChanceNight,
            underground ? 1f : MathUtils.Clamp01(night));

    /// <summary>
    /// The size a ring spawn gets from one uniform <paramref name="roll"/> in [0, 1): it grows one
    /// size past <see cref="Min"/> while the roll stays under <c>chance^k</c> (k = 1, 2, ...), so
    /// size <c>s + 1</c> or larger has probability <c>chance^s</c>, mostly small by day and larger
    /// more often at night or underground. The last step, into <see cref="Max"/> (the giant), takes
    /// <see cref="WorldGenerationConfig.SlimeMaxSizeChanceFactor"/> of that threshold on top, so the
    /// giant is rarer still. A pure function: for one roll, a higher chance never gives a smaller slime.
    /// </summary>
    public static int Roll(float night, bool underground, float roll)
    {
        var chance = LargeChance(night, underground);
        var size = Min;
        var threshold = chance;
        while (size < Max)
        {
            var step = size + 1 == Max ? threshold * WorldGenerationConfig.SlimeMaxSizeChanceFactor : threshold;
            if (roll >= step) break;
            size++;
            threshold *= chance;
        }
        return size;
    }

    /// <summary>
    /// Whether a hit splits a slime of <paramref name="size"/>: the rule is on the transition, not on
    /// being low. The health before the hit was above half of <paramref name="maxHealth"/>, the health
    /// after it is at or below half and above zero (the slime survives), and the slime is larger than
    /// <see cref="Min"/>. So a killing blow never splits, a slime already at or below half never splits
    /// again, and the smallest slime never splits at all.
    /// </summary>
    public static bool SplitsOnHit(int healthBefore, int healthAfter, int maxHealth, int size) =>
        size > Min && healthAfter > 0 && healthBefore * 2 > maxHealth && healthAfter * 2 <= maxHealth;

    /// <summary>The size of the children a slime of <paramref name="size"/> splits into.</summary>
    public static int ChildSize(int size) => Clamp(size - 1);

    /// <summary>Whether a spawn standing on <paramref name="groundY"/> is below ground: at least
    /// <see cref="WorldGenerationConfig.SlimeUndergroundDepthBlocks"/> under its column's terrain
    /// height <paramref name="surfaceHeight"/> (a cave floor, not the open surface).</summary>
    public static bool IsUnderground(int groundY, int surfaceHeight) =>
        surfaceHeight - groundY >= WorldGenerationConfig.SlimeUndergroundDepthBlocks;
}
