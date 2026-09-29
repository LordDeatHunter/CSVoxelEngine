using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The sky dome's colours for one moment of the day: a zenith, a horizon and an additive dusk
/// glow toward the sun. Computed once a frame on the CPU from the sun's elevation terms and
/// uploaded as uniforms; <see cref="Sample"/> is the C# reference of the GLSL <c>SkySample</c>
/// helper shared by the sky dome, the world fog and the cloud fog, so a fully fogged fragment
/// comes out exactly the colour of the dome behind it. The palette is currently the flat sky
/// colour of 28198cc (the look before the SH-11 dome): zenith and horizon are the same colour
/// and the glow is zero, so every direction samples that one colour and its dusk/night mix.
/// </summary>
public readonly record struct SkyPalette(Vector3 Zenith, Vector3 Horizon, Vector3 Glow)
{
    // sRGB, exactly the old RenderManager.SetLighting sky colours; RenderManager decodes the
    // computed palette to linear light where it uploads it
    private static readonly Vector3 NightSky = new(0.012f, 0.018f, 0.045f);
    private static readonly Vector3 DaySky = new(0.3f, 0.6f, 0.9f);
    private static readonly Vector3 DuskSky = new(0.98f, 0.45f, 0.2f);

    /// <summary>
    /// Builds the palette from <paramref name="dayness"/> (0 night, 1 full day) and
    /// <paramref name="duskAmount"/> (1 with the sun on the horizon, 0 well away from it):
    /// night mixed toward day by dayness, then toward the dusk orange by
    /// <c>duskAmount * SkyDuskMix</c>, used for both zenith and horizon with no glow.
    /// </summary>
    public static SkyPalette Compute(float dayness, float duskAmount)
    {
        var flat = Vector3.Lerp(NightSky, DaySky, dayness);
        flat = Vector3.Lerp(flat, DuskSky, duskAmount * WorldGenerationConfig.SkyDuskMix);
        return new SkyPalette(flat, flat, Vector3.Zero);
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
}
