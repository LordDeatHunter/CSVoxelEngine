using System;
using System.Numerics;

namespace VoxelEngine.Effects.Surfaces;

/// <summary>
/// GL-free twin of the lava glow pulse in test-fragment.glsl: the lava surface breathes in
/// brightness on a slow noise field. <see cref="Field"/> is the shader's <c>LavaPulseField</c> (two
/// octaves of the shader's <c>ValueNoise</c> drifting slowly against each other) and
/// <see cref="Brightness"/> the factor the shader multiplies the lava's glow by. Float math, so it
/// follows the shader closely but not to the last bit.
/// </summary>
public static class LavaPulse
{
    /// <summary>The slow noise field at world position <paramref name="xz"/> and time <paramref name="time"/>, 0..1.</summary>
    public static float Field(Vector2 xz, float time) =>
        0.6f * ValueNoise(xz * 0.22f + new Vector2(time * 0.11f, -time * 0.07f))
        + 0.4f * ValueNoise(xz * 0.47f + new Vector2(-time * 0.09f, time * 0.13f));

    /// <summary>
    /// The factor the lava's glow is multiplied by: <c>1 + strength * (2 * Field - 1)</c>, so it
    /// breathes between <c>1 - strength</c> and <c>1 + strength</c> around the unchanged glow; a
    /// strength of 0 is exactly 1.
    /// </summary>
    public static float Brightness(Vector2 xz, float time, float strength) =>
        1f + strength * (2f * Field(xz, time) - 1f);

    /// <summary>The shader's smooth value noise on its <c>Hash12</c> lattice, 0..1.</summary>
    public static float ValueNoise(Vector2 p)
    {
        var i = new Vector2(MathF.Floor(p.X), MathF.Floor(p.Y));
        var f = p - i;
        f = f * f * (new Vector2(3f) - 2f * f);
        var a = Hash12(i);
        var b = Hash12(i + new Vector2(1f, 0f));
        var c = Hash12(i + new Vector2(0f, 1f));
        var d = Hash12(i + new Vector2(1f, 1f));
        return Lerp(Lerp(a, b, f.X), Lerp(c, d, f.X), f.Y);
    }

    /// <summary>The shader's <c>Hash12</c>: a 0..1 hash of a 2D point.</summary>
    public static float Hash12(Vector2 p)
    {
        var q = new Vector3(Fract(p.X * 0.1031f), Fract(p.Y * 0.1031f), Fract(p.X * 0.1031f));
        var d = Vector3.Dot(q, new Vector3(q.Y, q.Z, q.X) + new Vector3(33.33f));
        q += new Vector3(d);
        return Fract((q.X + q.Y) * q.Z);
    }

    private static float Fract(float x) => x - MathF.Floor(x);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
