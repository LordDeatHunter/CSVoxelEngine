using System;
using VoxelEngine.Entities.HellMobs;

namespace VoxelEngine.Entities.CaveMobs;

/// <summary>
/// How much harder a cave mob is deeper down (plans/CAVE_BIOMES.md CB-4): its tier is 0 at or above
/// <see cref="WorldGenerationConfig.CaveEchoBottomY"/> and rises one every
/// <see cref="MobConfig.CaveDifficultyBandBlocks"/> below it, up to <see cref="MobConfig.CaveMaxTier"/>, and gives one
/// multiplier, <c>1 + tier * </c><see cref="MobConfig.CaveDifficultyPerTier"/> capped at
/// <see cref="MobConfig.CaveDifficultyMaxMultiplier"/>, that scales a mob's health and damage. The tier is read once,
/// where the mob spawned, and then kept with it (saved and sent like a skeleton's), so a mirror or a restore that sees
/// the mob at another height never gets another pool. GL-free.
/// </summary>
public static class CaveDifficulty
{
    /// <summary>The tier of a mob spawning with its body at height <paramref name="y"/>: 0 at or above the echo
    /// bottom, never falling with depth, held to 0..<see cref="MobConfig.CaveMaxTier"/>.</summary>
    public static int TierAt(float y)
    {
        var below = WorldGenerationConfig.CaveEchoBottomY - y;
        if (below <= 0f) return 0;
        return Clamp((int)MathF.Ceiling(below / MobConfig.CaveDifficultyBandBlocks));
    }

    /// <summary><paramref name="tier"/> held to 0..<see cref="MobConfig.CaveMaxTier"/>.</summary>
    public static int Clamp(int tier) => Math.Clamp(tier, 0, MobConfig.CaveMaxTier);

    /// <summary>The difficulty multiplier of <paramref name="tier"/>: 1 at tier 0, never falling with the tier,
    /// never past <see cref="MobConfig.CaveDifficultyMaxMultiplier"/>.</summary>
    public static float Multiplier(int tier) =>
        MathF.Min(1f + Clamp(tier) * MobConfig.CaveDifficultyPerTier, MobConfig.CaveDifficultyMaxMultiplier);

    /// <summary><paramref name="baseAmount"/> (a health pool or a hit) scaled to <paramref name="tier"/>, never below
    /// the base.</summary>
    public static int Scale(int baseAmount, int tier) => LayerDifficulty.ScaleBy(baseAmount, Multiplier(tier));
}
