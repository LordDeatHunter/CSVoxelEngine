using System;
using VoxelEngine.Effects.Sky;
using VoxelEngine.Rendering.ScreenSpace;

namespace VoxelEngine.Effects.Post;

/// <summary>
/// GL-free twin of the sun dazzle: the blinding bloom of looking straight at the sun
/// (<see cref="SunDazzleRenderer"/>, dazzle-fragment.glsl). Where <see cref="SunGlare"/> is a faint
/// glow in the sky dome that the terrain hides, the dazzle is light in the eye: a bright core, a
/// fan of rays and a wide veil added over the whole frame, the foreground included. How strong it
/// is this frame (<see cref="Intensity"/>) is decided on the CPU from how directly the camera faces
/// the sun; how much of the sun's disc is in open sky is measured on the GPU from the scene depth
/// and eased there (<see cref="EaseShare"/>); the shader spreads the result with <see cref="Glow"/>.
/// </summary>
public static class SunDazzle
{
    // The two waves whose product is the fan of rays (Rays): whole numbers of rays per turn, so the
    // fan closes on itself, and phases that keep the brightest rays off the screen's axes
    private const float RayWavesA = 6f;
    private const float RayWavesB = 10f;
    private const float RayPhaseA = 0.4f;
    private const float RayPhaseB = 2.3f;

    /// <summary>
    /// The dazzle's strength, 0..<see cref="PostProcessConfig.SunDazzleStrength"/>, for a camera
    /// whose forward has dot <paramref name="alignment"/> with the sun direction, a sun disc of
    /// visibility <paramref name="sunVisibility"/> (0 below the horizon, in hell and in lava) and an
    /// eye <paramref name="wet"/> (0 dry .. 1 fully under water): full within
    /// <see cref="PostProcessConfig.SunDazzleFullDegrees"/> of the sun, zero turned
    /// <see cref="PostProcessConfig.SunDazzleFacingDegrees"/> or more from it, zero without a sun
    /// disc and zero under water.
    /// </summary>
    public static float Intensity(float alignment, float sunVisibility, float wet)
    {
        var degrees = MathUtils.ToDegrees(MathF.Acos(MathUtils.Clamp(alignment, -1f, 1f)));
        var facing = 1f - MathUtils.SmoothStep(PostProcessConfig.SunDazzleFullDegrees,
            PostProcessConfig.SunDazzleFacingDegrees, degrees);
        return PostProcessConfig.SunDazzleStrength * facing * MathUtils.Clamp01(sunVisibility)
               * (1f - MathUtils.Clamp01(wet));
    }

    /// <summary>
    /// The radius of a sun disc of angular radius <paramref name="sunRadiusDegrees"/> on screen, as a
    /// share of the screen height, under a vertical field of view of <paramref name="fovDegrees"/>
    /// (exact at the screen center, where the dazzle is strongest).
    /// </summary>
    public static float DiscRadius(float sunRadiusDegrees, float fovDegrees) =>
        MathF.Tan(MathUtils.ToRadians(sunRadiusDegrees)) / (2f * MathF.Tan(MathUtils.ToRadians(fovDegrees) * 0.5f));

    /// <summary>
    /// How far the eased share of the disc in open sky moves toward this frame's measurement after
    /// <paramref name="seconds"/> since the last one, 0..1
    /// (<see cref="PostProcessConfig.SunDazzleEaseRate"/>): frame-rate independent, and a whole step
    /// after a pause in the pass, so a stale measurement never shows.
    /// </summary>
    public static float EaseShare(float seconds) =>
        1f - MathF.Exp(-PostProcessConfig.SunDazzleEaseRate * MathF.Max(seconds, 0f));

    /// <summary>
    /// The core and the veil at <paramref name="distance"/> from the sun on screen (a share of the
    /// screen height), 1 at the sun and falling steadily away from it: a Gaussian core of
    /// <see cref="PostProcessConfig.SunDazzleCoreRadius"/> mixed with a veil that reaches across the
    /// frame (<see cref="PostProcessConfig.SunDazzleVeilShare"/>,
    /// <see cref="PostProcessConfig.SunDazzleVeilReach"/>).
    /// </summary>
    public static float Falloff(float distance) =>
        MathUtils.Lerp(Core(distance), MathF.Exp(-distance / PostProcessConfig.SunDazzleVeilReach),
            PostProcessConfig.SunDazzleVeilShare);

    /// <summary>
    /// The fan of rays at <paramref name="angle"/> (radians) round the sun, 0..1: two waves of whole
    /// rays per turn multiplied, so the rays differ in brightness and the fan closes on itself,
    /// then squared, which thins them.
    /// </summary>
    public static float Rays(float angle)
    {
        var waves = (0.5f + 0.5f * MathF.Sin(RayWavesA * angle + RayPhaseA)) * (0.5f + 0.5f * MathF.Sin(RayWavesB * angle + RayPhaseB));
        return waves * waves;
    }

    /// <summary>
    /// What the dazzle adds at <paramref name="distance"/> and <paramref name="angle"/> from the sun
    /// on screen, as a share of <see cref="Intensity"/> times the visible disc: <see cref="Falloff"/>
    /// plus the <see cref="Rays"/>, which start outside the core
    /// (<see cref="PostProcessConfig.SunDazzleRayShare"/>) and fade over
    /// <see cref="PostProcessConfig.SunDazzleRayReach"/>. Twin of dazzle-fragment.glsl's main.
    /// </summary>
    public static float Glow(float distance, float angle) =>
        Falloff(distance) + PostProcessConfig.SunDazzleRayShare * Rays(angle)
        * MathF.Exp(-distance / PostProcessConfig.SunDazzleRayReach) * (1f - Core(distance));

    private static float Core(float distance)
    {
        var x = distance / PostProcessConfig.SunDazzleCoreRadius;
        return MathF.Exp(-x * x);
    }
}
