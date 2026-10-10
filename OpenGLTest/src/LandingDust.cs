using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The landing-dust rule, GL-free: how many chips a landing kicks up and how fast they fly, as a
/// function of the fall that ended. Nothing below <see cref="WorldGenerationConfig.LandingDustMinFallBlocks"/>,
/// then both grow with the drop along <see cref="MathUtils.ImpactStrength"/> and stop at their caps
/// at <see cref="WorldGenerationConfig.LandingDustFullFallBlocks"/>. A slime splats its own goo
/// instead (<see cref="SplatCount"/>, <see cref="SplatSpeed"/>, <see cref="SkinColour"/>).
/// <see cref="GroundDust"/> spawns what this decides.
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

    /// <summary>
    /// Goo chips a slime's landing at <paramref name="impactSpeed"/> blocks per second throws: none
    /// under <see cref="WorldGenerationConfig.SlimeSplatMinImpact"/>, then growing linearly with the
    /// impact, as its squash kick does, to the cap at <see cref="WorldGenerationConfig.SlimeSplatFullImpact"/>.
    /// <paramref name="scale"/> is the slime's body scale (<see cref="SlimeSize.Scale"/>, 1 for the
    /// smallest), and a bigger slime throws proportionally more.
    /// </summary>
    public static int SplatCount(float impactSpeed, float scale = 1f)
    {
        if (!(impactSpeed >= WorldGenerationConfig.SlimeSplatMinImpact) || !(scale > 0f)) return 0;
        return (int)MathF.Round(MathUtils.Lerp(WorldGenerationConfig.SlimeSplatMinCount,
            WorldGenerationConfig.SlimeSplatMaxCount, SplatStrength(impactSpeed)) * scale);
    }

    /// <summary>Launch speed in blocks per second of those chips, 0 under the minimum impact; a bigger
    /// slime (<paramref name="scale"/>) flings them further, by the square root of its size.</summary>
    public static float SplatSpeed(float impactSpeed, float scale = 1f)
    {
        if (!(impactSpeed >= WorldGenerationConfig.SlimeSplatMinImpact) || !(scale > 0f)) return 0f;
        return MathUtils.Lerp(WorldGenerationConfig.SlimeSplatMinLaunch, WorldGenerationConfig.SlimeSplatMaxLaunch,
            SplatStrength(impactSpeed)) * MathF.Sqrt(scale);
    }

    /// <summary>
    /// The sRGB colour (0..1) of an entity skin: the mean of its opaque texels, so a slime splats its
    /// own green. <paramref name="fallback"/> when the model has no skin or not one opaque texel.
    /// </summary>
    public static Vector3 SkinColour(BbmodelData model, Vector3 fallback)
    {
        var pixels = model.TexturePixels;
        if (pixels == null) return fallback;
        long r = 0, g = 0, b = 0, n = 0;
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0) continue;
            r += pixels[i];
            g += pixels[i + 1];
            b += pixels[i + 2];
            n++;
        }

        return n == 0 ? fallback : new Vector3(r, g, b) / (255f * n);
    }

    private static float Strength(float fallBlocks) =>
        MathUtils.ImpactStrength(fallBlocks, WorldGenerationConfig.LandingDustMinFallBlocks,
            WorldGenerationConfig.LandingDustFullFallBlocks);

    private static float SplatStrength(float impactSpeed) =>
        MathUtils.InverseLerp(WorldGenerationConfig.SlimeSplatMinImpact, WorldGenerationConfig.SlimeSplatFullImpact,
            impactSpeed);
}
