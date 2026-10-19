using System;
using System.Numerics;
using VoxelEngine.Content.Models;
using VoxelEngine.Entities.Slimes;

namespace VoxelEngine.Particles;

/// <summary>
/// The landing-dust rule, GL-free: how many chips a landing kicks up and how fast they fly, as a
/// function of the fall that ended. Nothing below <see cref="JuiceConfig.LandingDustMinFallBlocks"/>,
/// then both grow with the drop along <see cref="MathUtils.ImpactStrength"/> and stop at their caps
/// at <see cref="JuiceConfig.LandingDustFullFallBlocks"/>. A slime splats its own goo
/// instead (<see cref="SplatCount"/>, <see cref="SplatSpeed"/>, <see cref="SkinColor"/>).
/// <see cref="GroundDust"/> spawns what this decides.
/// </summary>
public static class LandingDust
{
    /// <summary>Chips a landing after a fall of <paramref name="fallBlocks"/> kicks up; 0 below the minimum fall.</summary>
    public static int Count(float fallBlocks)
    {
        if (!(fallBlocks >= JuiceConfig.LandingDustMinFallBlocks)) return 0;
        return (int)MathF.Round(MathUtils.Lerp(JuiceConfig.LandingDustMinCount,
            JuiceConfig.LandingDustMaxCount, Strength(fallBlocks)));
    }

    /// <summary>Launch speed in blocks per second of those chips; 0 below the minimum fall.</summary>
    public static float Speed(float fallBlocks)
    {
        if (!(fallBlocks >= JuiceConfig.LandingDustMinFallBlocks)) return 0f;
        return MathUtils.Lerp(JuiceConfig.LandingDustMinSpeed,
            JuiceConfig.LandingDustMaxSpeed, Strength(fallBlocks));
    }

    /// <summary>
    /// Chips a falling block (sand, gravel) throws when it settles after a fall of
    /// <paramref name="fallBlocks"/>: none under <see cref="JuiceConfig.FallingBlockDustMinFallBlocks"/>
    /// (a cube that only slid off a ledge's lip), then growing with the drop along
    /// <see cref="MathUtils.ImpactStrength"/> to the cap at <see cref="JuiceConfig.FallingBlockDustFullFallBlocks"/>.
    /// A block is far heavier than a body, so a one-block drop already puffs.
    /// </summary>
    public static int FallingBlockCount(float fallBlocks)
    {
        if (!(fallBlocks >= JuiceConfig.FallingBlockDustMinFallBlocks)) return 0;
        return (int)MathF.Round(MathUtils.Lerp(JuiceConfig.FallingBlockDustMinCount,
            JuiceConfig.FallingBlockDustMaxCount, FallingBlockStrength(fallBlocks)));
    }

    /// <summary>Launch speed in blocks per second of those chips; 0 under the minimum fall.</summary>
    public static float FallingBlockSpeed(float fallBlocks)
    {
        if (!(fallBlocks >= JuiceConfig.FallingBlockDustMinFallBlocks)) return 0f;
        return MathUtils.Lerp(JuiceConfig.FallingBlockDustMinSpeed,
            JuiceConfig.FallingBlockDustMaxSpeed, FallingBlockStrength(fallBlocks));
    }

    /// <summary>The height a body under <paramref name="gravity"/> fell from rest to reach
    /// <paramref name="impactSpeed"/> (v² / 2g), which is how a mob's landing speed becomes a fall.</summary>
    public static float FallFromImpactSpeed(float impactSpeed, float gravity) =>
        impactSpeed <= 0f || gravity <= 0f ? 0f : impactSpeed * impactSpeed / (2f * gravity);

    /// <summary>
    /// Goo chips a slime's landing at <paramref name="impactSpeed"/> blocks per second throws: none
    /// under <see cref="JuiceConfig.SlimeSplatMinImpact"/>, then growing linearly with the
    /// impact, as its squash kick does, to the cap at <see cref="JuiceConfig.SlimeSplatFullImpact"/>.
    /// <paramref name="scale"/> is the slime's body scale (<see cref="SlimeSize.Scale"/>, 1 for the
    /// smallest), and a bigger slime throws proportionally more.
    /// </summary>
    public static int SplatCount(float impactSpeed, float scale = 1f)
    {
        if (!(impactSpeed >= JuiceConfig.SlimeSplatMinImpact) || !(scale > 0f)) return 0;
        return (int)MathF.Round(MathUtils.Lerp(JuiceConfig.SlimeSplatMinCount,
            JuiceConfig.SlimeSplatMaxCount, SplatStrength(impactSpeed)) * scale);
    }

    /// <summary>Launch speed in blocks per second of those chips, 0 under the minimum impact; a bigger
    /// slime (<paramref name="scale"/>) flings them further, by the square root of its size.</summary>
    public static float SplatSpeed(float impactSpeed, float scale = 1f)
    {
        if (!(impactSpeed >= JuiceConfig.SlimeSplatMinImpact) || !(scale > 0f)) return 0f;
        return MathUtils.Lerp(JuiceConfig.SlimeSplatMinLaunch, JuiceConfig.SlimeSplatMaxLaunch,
            SplatStrength(impactSpeed)) * MathF.Sqrt(scale);
    }

    /// <summary>
    /// The sRGB color (0..1) of an entity skin: the mean of its opaque texels, so a slime splats its
    /// own green. <paramref name="fallback"/> when the model has no skin or not one opaque texel.
    /// </summary>
    public static Vector3 SkinColor(BbmodelData model, Vector3 fallback) =>
        MeanColor(model.TexturePixels, fallback);

    /// <summary>
    /// The mean sRGB color (0..1) of an RGBA pixel array's opaque texels (an entity skin, an item
    /// sprite, so a breaking tool shatters in its own colors). <paramref name="fallback"/> when
    /// there is no array or not one opaque texel.
    /// </summary>
    public static Vector3 MeanColor(byte[]? pixels, Vector3 fallback)
    {
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
        MathUtils.ImpactStrength(fallBlocks, JuiceConfig.LandingDustMinFallBlocks,
            JuiceConfig.LandingDustFullFallBlocks);

    private static float FallingBlockStrength(float fallBlocks) =>
        MathUtils.ImpactStrength(fallBlocks, JuiceConfig.FallingBlockDustMinFallBlocks,
            JuiceConfig.FallingBlockDustFullFallBlocks);

    private static float SplatStrength(float impactSpeed) =>
        MathUtils.InverseLerp(JuiceConfig.SlimeSplatMinImpact, JuiceConfig.SlimeSplatFullImpact,
            impactSpeed);
}
