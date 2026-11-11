using System;
using VoxelEngine.Entities.HellMobs;

namespace VoxelEngine.Entities.TowerMobs;

/// <summary>
/// How much harder a battle tower mob is on a higher floor: the floor's tier (the floor index,
/// 0..<see cref="MobConfig.TowerMaxTier"/>) gives one multiplier, <c>1 + tier * </c><see cref="MobConfig.TowerDifficultyPerTier"/>,
/// capped at <see cref="MobConfig.TowerDifficultyMaxMultiplier"/>, that scales a mob's health and damage, plus the
/// pack a pile wave raises there. Unlike the hell and sky realms the tier cannot be read back from a position
/// (one tower floor looks like any other height), so a mob's tier is set when it spawns, fixed for life, and saved
/// and sent with it explicitly. GL-free.
/// </summary>
public static class TowerDifficulty
{
    /// <summary><paramref name="tier"/> held to the floors that exist, 0..<see cref="MobConfig.TowerMaxTier"/>.</summary>
    public static int Clamp(int tier) => Math.Clamp(tier, 0, MobConfig.TowerMaxTier);

    /// <summary>The difficulty multiplier of <paramref name="tier"/>: 1 on the ground floor, never falling with
    /// height, never past <see cref="MobConfig.TowerDifficultyMaxMultiplier"/>.</summary>
    public static float Multiplier(int tier) =>
        MathF.Min(1f + Clamp(tier) * MobConfig.TowerDifficultyPerTier, MobConfig.TowerDifficultyMaxMultiplier);

    /// <summary><paramref name="baseAmount"/> (a health pool or a hit) scaled to <paramref name="tier"/>, never
    /// below the base.</summary>
    public static int Scale(int baseAmount, int tier) => LayerDifficulty.ScaleBy(baseAmount, Multiplier(tier));

    /// <summary>How many mobs one pile wave on <paramref name="tier"/> raises at most: one, plus one more every
    /// <see cref="MobConfig.TowerPackTiersPerExtra"/> tiers, up to <see cref="MobConfig.TowerPackMaxSize"/>. The
    /// bone pile's wave hook (BT-4).</summary>
    public static int PackSize(int tier) =>
        Math.Min(MobConfig.TowerPackMaxSize, 1 + Clamp(tier) / MobConfig.TowerPackTiersPerExtra);

    /// <summary>How many living skeletons may stand near a pile on <paramref name="tier"/> before it raises another:
    /// <see cref="MobConfig.SkeletonPileNearLimitBase"/> on the ground floor, <see cref="MobConfig.SkeletonPileNearLimitPerTier"/>
    /// more on every floor above.</summary>
    public static int PileNearLimit(int tier) =>
        MobConfig.SkeletonPileNearLimitBase + Clamp(tier) * MobConfig.SkeletonPileNearLimitPerTier;
}
