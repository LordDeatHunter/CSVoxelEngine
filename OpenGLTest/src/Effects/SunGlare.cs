using System;

namespace silkgltriangle;

/// <summary>
/// GL-free twin of the sun's lens glare: a soft, wide glow around the sun added by the sky dome
/// (<c>uGlareColor</c> in sky-fragment.glsl) on top of the sun disc's own small halo. How strong it is
/// this frame (<see cref="Intensity"/>) is worked out once on the CPU from how directly the camera
/// faces the sun, how high the sun stands and how much cloud covers it; the shader only spreads that
/// over the sky with the falloff <see cref="Radial"/>. It is a camera effect, so only the main view
/// draws it: never the water mirror (which would reflect a lens flare) nor any shadow or LOD pass.
/// </summary>
public static class SunGlare
{
    /// <summary>
    /// The glare's strength, 0..<see cref="WorldGenerationConfig.SunGlareStrength"/>, for a camera
    /// whose forward has dot <paramref name="alignment"/> with the sun direction, a sun at
    /// sine-elevation <paramref name="sunElevation"/> and <paramref name="cloudCover"/> (0..1) of
    /// cloud over the sun's disc. Zero once the view turns
    /// <see cref="WorldGenerationConfig.SunGlareFacingDegrees"/> away from the sun, zero with the
    /// sun at or below the horizon, zero behind a full cloud; full only looking straight at a high,
    /// clear sun. Non-decreasing in the alignment.
    /// </summary>
    public static float Intensity(float alignment, float sunElevation, float cloudCover)
    {
        var facing = MathUtils.SmoothStep(MathUtils.CosDeg(WorldGenerationConfig.SunGlareFacingDegrees), 1f, alignment);
        var height = MathUtils.SmoothStep(0f, WorldGenerationConfig.SunGlareFullElevation, sunElevation);
        return WorldGenerationConfig.SunGlareStrength * facing * height * (1f - MathUtils.Clamp01(cloudCover));
    }

    /// <summary>
    /// How much of the glare reaches a sky direction whose cosine to the sun is
    /// <paramref name="cosToSun"/>: 1 at the sun, falling off smoothly to 0 a quarter turn away.
    /// Twin of the falloff in sky-fragment.glsl.
    /// </summary>
    public static float Radial(float cosToSun) => MathF.Pow(MathF.Max(cosToSun, 0f), WorldGenerationConfig.SunGlareExponent);
}
