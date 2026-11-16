using System;

namespace VoxelEngine.Entities.TowerMobs;

/// <summary>
/// How much more a battle tower's bone piles raise on a higher floor: the floor's tier (the floor index,
/// 0..<see cref="MobConfig.TowerMaxTier"/>) sets how many piles each pile spot lays out and how many living skeletons
/// may stand near a pile. The skeletons themselves are the same on every floor. GL-free.
/// </summary>
public static class TowerDifficulty
{
    /// <summary><paramref name="tier"/> held to the floors that exist, 0..<see cref="MobConfig.TowerMaxTier"/>.</summary>
    public static int Clamp(int tier) => Math.Clamp(tier, 0, MobConfig.TowerMaxTier);

    /// <summary>The pack size of <paramref name="tier"/>: one, plus one more every
    /// <see cref="MobConfig.TowerPackTiersPerExtra"/> tiers.</summary>
    public static int PackSize(int tier) => 1 + Clamp(tier) / MobConfig.TowerPackTiersPerExtra;

    /// <summary>How many bone piles one pile spot of a <paramref name="tier"/> floor lays out, each raising a single
    /// skeleton: <see cref="PackSize"/> times <see cref="MobConfig.TowerPilesPerPackSlot"/>.</summary>
    public static int PilesPerSpot(int tier) => PackSize(tier) * MobConfig.TowerPilesPerPackSlot;

    /// <summary>How many living skeletons may stand near a pile on <paramref name="tier"/> before it raises another:
    /// <see cref="MobConfig.SkeletonPileNearLimitBase"/> on the ground floor, <see cref="MobConfig.SkeletonPileNearLimitPerTier"/>
    /// more on every floor above.</summary>
    public static int PileNearLimit(int tier) =>
        MobConfig.SkeletonPileNearLimitBase + Clamp(tier) * MobConfig.SkeletonPileNearLimitPerTier;
}
