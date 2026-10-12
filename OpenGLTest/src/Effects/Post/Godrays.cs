using System;
using VoxelEngine.Effects.Sky;
using VoxelEngine.Rendering;

namespace VoxelEngine.Effects.Post;

/// <summary>
/// GL-free twin of the godrays' strength: visible light shafts through trees, clouds and gaps when the
/// sun is in view (<see cref="GodraysRenderer"/>, godrays-fragment.glsl). The shader marches the air
/// in front of the eye through the near shadow cascade and the cloud shadows; how strongly the
/// result shows this frame is decided here, <see cref="SunGlare"/>-style, from how directly the camera
/// faces the sun, how high it stands (strongest low, a fraction at noon, nothing at and below the
/// horizon), how much cloud covers it, and whether the eye is under water (where the underwater
/// shafts take over). The shader's own forward-scatter falloff is <see cref="Phase"/>.
/// </summary>
public static class Godrays
{
    /// <summary>
    /// The godrays' strength, 0..<see cref="WorldGenerationConfig.GodraysStrength"/>, for a camera whose
    /// forward has dot <paramref name="alignment"/> with the sun, a sun at sine-elevation
    /// <paramref name="sunElevation"/>, <paramref name="cloudCover"/> (0..1) of cloud over the sun's
    /// disc and an eye <paramref name="wet"/> (0 dry .. 1 fully under water): zero turned
    /// <see cref="WorldGenerationConfig.GodraysFacingDegrees"/> or more from the sun, zero with the sun
    /// at or below the horizon, zero under water; peaking for a sun near
    /// <see cref="WorldGenerationConfig.GodraysPeakElevation"/> and falling to
    /// <see cref="WorldGenerationConfig.GodraysNoonShare"/> of that overhead.
    /// </summary>
    public static float Intensity(float alignment, float sunElevation, float cloudCover, float wet)
    {
        var facing = MathUtils.SmoothStep(MathUtils.CosDeg(WorldGenerationConfig.GodraysFacingDegrees), 1f, alignment);
        return WorldGenerationConfig.GodraysStrength * facing * Height(sunElevation)
               * (1f - WorldGenerationConfig.GodraysCloudDim * MathUtils.Clamp01(cloudCover))
               * (1f - MathUtils.Clamp01(wet));
    }

    /// <summary>
    /// How the sun's height weights the shafts, 0..1: rising from 0 at the horizon to 1 at
    /// <see cref="WorldGenerationConfig.GodraysPeakElevation"/>, then easing down to
    /// <see cref="WorldGenerationConfig.GodraysNoonShare"/> with the sun straight overhead.
    /// </summary>
    public static float Height(float sunElevation)
    {
        var peak = WorldGenerationConfig.GodraysPeakElevation;
        var rise = MathUtils.SmoothStep(0f, peak, sunElevation);
        var fall = MathUtils.Lerp(1f, WorldGenerationConfig.GodraysNoonShare, MathUtils.SmoothStep(peak, 1f, sunElevation));
        return rise * fall;
    }

    /// <summary>
    /// The share of the light scattered toward the eye over <paramref name="litBlocks"/> blocks of
    /// sunlit air along a view ray, 0..1: 1 - exp(-litBlocks * <see cref="WorldGenerationConfig.GodraysDensityPerBlock"/>).
    /// Linear for short sunlit stretches (the shafts between shadows) and saturating for a long open
    /// ray toward the sun, so the sky round it is never washed out. Twin of the scatter in
    /// godrays-fragment.glsl, which multiplies it by <see cref="WorldGenerationConfig.GodraysMaxAdd"/>.
    /// </summary>
    public static float Scatter(float litBlocks) =>
        1f - MathF.Exp(-MathF.Max(litBlocks, 0f) * WorldGenerationConfig.GodraysDensityPerBlock);

    /// <summary>
    /// How much of the light scattered along a view ray reaches the eye for a ray whose cosine to the
    /// sun is <paramref name="cosToSun"/>: a floor of <see cref="WorldGenerationConfig.GodraysPhaseFloor"/>
    /// looking away, rising to 1 straight at the sun. Twin of the phase in godrays-fragment.glsl.
    /// </summary>
    public static float Phase(float cosToSun) =>
        MathUtils.Lerp(WorldGenerationConfig.GodraysPhaseFloor, 1f,
            MathF.Pow(MathF.Max(cosToSun, 0f), WorldGenerationConfig.GodraysPhaseExponent));
}
