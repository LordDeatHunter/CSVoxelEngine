using System;

namespace VoxelEngine.Particles;

/// <summary>
/// The GL-free twin of <c>slice-trail-fragment.glsl</c>'s glow profile across and along the slice-trail
/// ribbon. <c>v</c> runs 0 at the ribbon's inner edge to 1 at the tip edge, <c>along</c> is the stable
/// arc length in blocks the ribbon carries in its UV (<see cref="SliceTrailRibbon"/>). The profile is a
/// soft colored body brightening toward the tip edge (<see cref="ParticleConfig.SliceTrailGlowPower"/>),
/// a white core band near that edge (<see cref="Core"/>), and hashed streak lanes that switch on over
/// the first <see cref="ParticleConfig.SliceTrailStreakSpanBlocks"/> of arc length and only ever dim the
/// body (<see cref="Streak"/>), which reads as wispy cuts inside the arc.
/// <para>
/// It reads the ParticleConfig glow knobs, which <c>SliceTrailRenderer</c> uploads to the shader as the
/// same-named uniforms. It twins the formula, not the GPU's bits: GPU <c>sin</c> is not bit-exact with
/// <see cref="MathF.Sin(float)"/>, so a lane's streak start can differ slightly, which is why the tests
/// assert relations only. Change the shader and this class together (shader-sync skill).
/// </para>
/// </summary>
public static class SliceTrailGlow
{
    /// <summary>The classic sine hash: a pseudo-random fraction in [0, 1) of <paramref name="x"/>.</summary>
    public static float Hash(float x) => Fract(MathF.Sin(x * 12.9898f) * 43758.5453f);

    /// <summary>The white core band: a gaussian of the cross coordinate around the core center, 1 at its peak.</summary>
    public static float Core(float v) =>
        MathF.Exp(-Square((v - ParticleConfig.SliceTrailCoreCenter) / ParticleConfig.SliceTrailCoreWidth));

    /// <summary>
    /// The streak of the lane <paramref name="v"/> falls in: 0 before the lane's hashed start, easing to 1
    /// across a fifth of the span. Every lane is fully on from <see cref="ParticleConfig.SliceTrailStreakSpanBlocks"/>
    /// of arc length on, since the hash is below 1.
    /// </summary>
    public static float Streak(float along, float v)
    {
        var lane = MathF.Floor(v * ParticleConfig.SliceTrailStreakLanes);
        var h = Hash(lane + 1f);
        const float span = ParticleConfig.SliceTrailStreakSpanBlocks;
        return MathUtils.SmoothStep(h * 0.8f * span, (h * 0.8f + 0.2f) * span, along);
    }

    /// <summary>
    /// The colored glow: the body <c>v^GlowPower</c>, dimmed by an unlit streak by up to
    /// <see cref="ParticleConfig.SliceTrailStreakDepth"/>, never inside the core band.
    /// </summary>
    public static float Glow(float along, float v)
    {
        var body = MathF.Pow(MathUtils.Clamp01(v), ParticleConfig.SliceTrailGlowPower);
        return body * MathUtils.Lerp(1f, Streak(along, v), ParticleConfig.SliceTrailStreakDepth * (1f - Core(v)));
    }

    /// <summary>The fragment's alpha before the distance fade: the vertex alpha times the brighter of glow and core.</summary>
    public static float Alpha(float along, float v, float vertexAlpha) =>
        vertexAlpha * MathF.Max(Glow(along, v), Core(v));

    private static float Fract(float x) => x - MathF.Floor(x);

    private static float Square(float x) => x * x;
}
