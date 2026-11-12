using System;

namespace VoxelEngine.Entities.TowerMobs;

/// <summary>
/// How much more a battle tower's bone piles raise on a higher floor: the floor's tier (the floor index,
/// 0..<see cref="MobConfig.TowerMaxTier"/>) sets the size of a pile's wave and how many living skeletons may stand
/// near a pile. The skeletons themselves are the same on every floor. GL-free.
/// </summary>
public static class TowerDifficulty
{
    /// <summary><paramref name="tier"/> held to the floors that exist, 0..<see cref="MobConfig.TowerMaxTier"/>.</summary>
    public static int Clamp(int tier) => Math.Clamp(tier, 0, MobConfig.TowerMaxTier);

    /// <summary>How many mobs one pile wave on <paramref name="tier"/> raises at most: one, plus one more every
    /// <see cref="MobConfig.TowerPackTiersPerExtra"/> tiers. The bone pile's wave hook (BT-4).</summary>
    public static int PackSize(int tier) => 1 + Clamp(tier) / MobConfig.TowerPackTiersPerExtra;

    /// <summary>How many living skeletons may stand near a pile on <paramref name="tier"/> before it raises another:
    /// <see cref="MobConfig.SkeletonPileNearLimitBase"/> on the ground floor, <see cref="MobConfig.SkeletonPileNearLimitPerTier"/>
    /// more on every floor above.</summary>
    public static int PileNearLimit(int tier) =>
        MobConfig.SkeletonPileNearLimitBase + Clamp(tier) * MobConfig.SkeletonPileNearLimitPerTier;
}
