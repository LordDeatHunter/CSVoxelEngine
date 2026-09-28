using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The sky dome's colours for one moment of the day: a zenith, a horizon and an additive dusk
/// glow toward the sun. Computed once a frame on the CPU from the sun's elevation terms and
/// uploaded as uniforms; <see cref="Sample"/> is the C# reference of the GLSL <c>SkySample</c>
/// helper shared by the sky dome, the world fog and the cloud fog, so a fully fogged fragment
/// comes out exactly the colour of the dome behind it.
/// </summary>
public readonly record struct SkyPalette(Vector3 Zenith, Vector3 Horizon, Vector3 Glow)
{
    private static readonly Vector3 NightZenith = new(0.004f, 0.008f, 0.025f);
    private static readonly Vector3 DayZenith = new(0.12f, 0.3f, 0.7f);
    private static readonly Vector3 NightHorizon = new(0.015f, 0.022f, 0.055f);
    // Chosen so the hazed day horizon, Horizon * (1 + SkyHazeStrength), lands on the old flat
    // sky colour (0.3, 0.6, 0.9) and the dry look does not jump.
    private static readonly Vector3 DayHorizon = new(0.25f, 0.5f, 0.75f);
    private static readonly Vector3 DuskHorizon = new(0.82f, 0.38f, 0.17f);
    private static readonly Vector3 DuskZenith = new(0.2f, 0.2f, 0.38f);
    private static readonly Vector3 DuskGlow = new(1f, 0.45f, 0.15f);

    /// <summary>
    /// Builds the palette from <paramref name="dayness"/> (0 night, 1 full day) and
    /// <paramref name="duskAmount"/> (1 with the sun on the horizon, 0 well away from it).
    /// The glow is exactly zero when <paramref name="duskAmount"/> is 0.
    /// </summary>
    public static SkyPalette Compute(float dayness, float duskAmount)
    {
        var zenith = Vector3.Lerp(NightZenith, DayZenith, dayness);
        var horizon = Vector3.Lerp(NightHorizon, DayHorizon, dayness);
        horizon = Vector3.Lerp(horizon, DuskHorizon, duskAmount * WorldGenerationConfig.SkyDuskHorizonMix);
        zenith = Vector3.Lerp(zenith, DuskZenith, duskAmount * WorldGenerationConfig.SkyDuskZenithMix);
        var glow = DuskGlow * (duskAmount * WorldGenerationConfig.SkyGlowStrength);
        return new SkyPalette(zenith, horizon, glow);
    }

    /// <summary>
    /// The dome's shape constants as the shaders' <c>uSkyShape</c>: (horizon band, haze width,
    /// haze strength, glow exponent).
    /// </summary>
    public static Vector4 Shape => new(
        WorldGenerationConfig.SkyHorizonBand,
        WorldGenerationConfig.SkyHazeWidth,
        WorldGenerationConfig.SkyHazeStrength,
        WorldGenerationConfig.SkyGlowExponent);

    /// <summary>
    /// The horizon sample with no glow (facing away from the sun): the flat fog colour the
    /// consumers that cannot sample the dome per direction use.
    /// </summary>
    public Vector3 HazedHorizon => Horizon * (1f + WorldGenerationConfig.SkyHazeStrength);

    /// <summary>
    /// The dome colour in direction <paramref name="dir"/> (need not be normalized in XZ, but
    /// its Y must be the sine of the elevation). Every downward direction clamps to the
    /// horizon at the same azimuth, which is what keeps fogged terrain seamless against the sky.
    /// Twin copies: sky-fragment.glsl, test-fragment.glsl, cloud-fragment.glsl (SkySample) -
    /// change all four together.
    /// </summary>
    public Vector3 Sample(Vector3 dir, Vector3 sunDir)
    {
        var shape = Shape;
        var e = MathF.Max(dir.Y, 0f);
        var az = (dir.X * sunDir.X + dir.Z * sunDir.Z)
                 / MathF.Sqrt(MathF.Max((dir.X * dir.X + dir.Z * dir.Z) * (sunDir.X * sunDir.X + sunDir.Z * sunDir.Z), 1e-6f));
        // MathUtils.SmoothStep clamps through InverseLerp, matching GLSL smoothstep
        var band = MathUtils.SmoothStep(0f, shape.X, e);
        var lobe = MathF.Pow(MathF.Max(az, 0f), shape.W) * (1f - band);
        var haze = shape.Z * (1f - MathUtils.SmoothStep(0f, shape.Y, e));
        return Vector3.Lerp(Horizon * (1f + haze), Zenith, band) + Glow * lobe;
    }

    /// <summary>
    /// The cloud shader's light weights (ambient weight, sun base, sun N·L) for a dusk amount:
    /// as the sun lowers, weight moves from the ambient term to the dusk-orange sun term. The
    /// dusk-0 endpoint is cloud-fragment.glsl's pre-SH-21 expression
    /// (<c>uAmbient * 1.2 + uSunColor * (uSunIntensity * (0.26 + 0.04 * ndl))</c>).
    /// </summary>
    public static Vector3 CloudLightWeights(float duskAmount) =>
        Vector3.Lerp(
            new Vector3(1.2f, 0.26f, 0.04f),
            new Vector3(WorldGenerationConfig.CloudDuskAmbientWeight,
                WorldGenerationConfig.CloudDuskSunBase,
                WorldGenerationConfig.CloudDuskSunNdl),
            MathUtils.Clamp01(duskAmount));
}
