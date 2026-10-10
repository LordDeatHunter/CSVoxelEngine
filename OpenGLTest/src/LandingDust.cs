using System;

namespace silkgltriangle;

/// <summary>
/// The landing-dust rule, GL-free: how many chips a landing kicks up and how fast they fly, as a
/// function of the fall that ended. Nothing below <see cref="WorldGenerationConfig.LandingDustMinFallBlocks"/>,
/// then both grow with the drop along <see cref="MathUtils.ImpactStrength"/> and stop at their caps
/// at <see cref="WorldGenerationConfig.LandingDustFullFallBlocks"/>. <see cref="GroundDust"/> spawns
/// what this decides.
/// </summary>
public static class LandingDust
{
    /// <summary>Chips a landing after a fall of <paramref name="fallBlocks"/> kicks up; 0 below the minimum fall.</summary>
    public static int Count(float fallBlocks)
    {
        if (!(fallBlocks >= WorldGenerationConfig.LandingDustMinFallBlocks)) return 0;
        return (int)MathF.Round(MathUtils.Lerp(WorldGenerationConfig.LandingDustMinCount,
            WorldGenerationConfig.LandingDustMaxCount, Strength(fallBlocks)));
    }

    /// <summary>Launch speed in blocks per second of those chips; 0 below the minimum fall.</summary>
    public static float Speed(float fallBlocks)
    {
        if (!(fallBlocks >= WorldGenerationConfig.LandingDustMinFallBlocks)) return 0f;
        return MathUtils.Lerp(WorldGenerationConfig.LandingDustMinSpeed,
            WorldGenerationConfig.LandingDustMaxSpeed, Strength(fallBlocks));
    }

    /// <summary>The height a body under <paramref name="gravity"/> fell from rest to reach
    /// <paramref name="impactSpeed"/> (v² / 2g), which is how a mob's landing speed becomes a fall.</summary>
    public static float FallFromImpactSpeed(float impactSpeed, float gravity) =>
        impactSpeed <= 0f || gravity <= 0f ? 0f : impactSpeed * impactSpeed / (2f * gravity);

    private static float Strength(float fallBlocks) =>
        MathUtils.ImpactStrength(fallBlocks, WorldGenerationConfig.LandingDustMinFallBlocks,
            WorldGenerationConfig.LandingDustFullFallBlocks);
}
