using System;

namespace silkgltriangle;

/// <summary>
/// GL-free twin of the ore twinkle in test-fragment.glsl: on the reflective tiles (a non-zero
/// <c>material.metallic</c>) a <see cref="WorldGenerationConfig.OreTwinkleDensity"/> share of the
/// texels, hashed from their place on the face like the snow glint's, flash now and then, each on its
/// own rate and phase. A twinkling texel lights once per cycle (<see cref="Cycle"/>, <see cref="Flash"/>);
/// <see cref="Gate"/> is the sun and distance gate the shader multiplies in, and the result is added as
/// that many times the direct sun.
/// </summary>
public static class OreTwinkle
{
    /// <summary>
    /// Where in its cycle a texel twinkling at the hashed <paramref name="rateHash"/> and
    /// <paramref name="phaseHash"/> (both 0..1) is at <paramref name="time"/>, 0..1: the rate runs
    /// from <see cref="WorldGenerationConfig.OreTwinkleRateMin"/> to <see cref="WorldGenerationConfig.OreTwinkleRateMax"/>
    /// flashes per second.
    /// </summary>
    public static float Cycle(float time, float rateHash, float phaseHash)
    {
        var rate = WorldGenerationConfig.OreTwinkleRateMin
                   + (WorldGenerationConfig.OreTwinkleRateMax - WorldGenerationConfig.OreTwinkleRateMin) * rateHash;
        var c = time * rate + phaseHash;
        return c - MathF.Floor(c);
    }

    /// <summary>
    /// The flash at a point <paramref name="cycle"/> (0..1) through a texel's cycle, 0..1: a squared tent
    /// of half-width <see cref="WorldGenerationConfig.OreTwinkleWidth"/> around mid-cycle, 1 at its peak
    /// and 0 for the rest of the cycle.
    /// </summary>
    public static float Flash(float cycle)
    {
        var k = MathF.Max(1f - MathF.Abs(cycle - 0.5f) / WorldGenerationConfig.OreTwinkleWidth, 0f);
        return k * k;
    }

    /// <summary>
    /// How much of a flash shows, 0..1: the direct sun the face really gets (<paramref name="skyLight"/>
    /// times a thresholded <paramref name="shadow"/>, which already carries the cloud shadow), the sun on
    /// the face's side (<paramref name="nDotL"/>), the sun's gain and a fade with
    /// <paramref name="distance"/> between the twinkle's fade distances.
    /// </summary>
    public static float Gate(float skyLight, float shadow, float nDotL, float sunIntensity, float distance) =>
        MathUtils.Clamp01(skyLight)
        * MathUtils.SmoothStep(0.6f, 0.95f, shadow)
        * MathUtils.SmoothStep(0f, 0.15f, nDotL)
        * MathUtils.SmoothStep(0f, 0.2f, sunIntensity)
        * (1f - MathUtils.SmoothStep(WorldGenerationConfig.OreTwinkleFadeNearBlocks,
            WorldGenerationConfig.OreTwinkleFadeFarBlocks, distance));
}
