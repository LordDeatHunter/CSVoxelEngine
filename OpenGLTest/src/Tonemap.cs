using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The C# twin of <c>post-fragment.glsl</c>'s tonemap curve, used only by tests: a
/// hue-preserving Reinhard shoulder on the max channel. It is the identity below the knee,
/// so mid-tones and darks pass through unchanged, and rolls off toward 1 above it with a
/// continuous value and slope at the knee.
/// </summary>
public static class Tonemap
{
    /// <summary>
    /// <paramref name="m"/> for <c>m &lt;= knee</c>, otherwise
    /// <c>knee + (1 - knee) * x / (1 + x)</c> with <c>x = (m - knee) / (1 - knee)</c>.
    /// </summary>
    public static float Shoulder(float m, float knee)
    {
        if (m <= knee) return m;
        float x = (m - knee) / (1f - knee);
        return knee + (1f - knee) * x / (1f + x);
    }

    /// <summary>
    /// Clamp to non-negative, scale by <paramref name="exposure"/>, then scale the colour by
    /// <c>Shoulder(m) / m</c> where m is its max channel, so channel ratios (hue) survive.
    /// </summary>
    public static Vector3 Apply(Vector3 hdr, float exposure, float knee)
    {
        Vector3 c = Vector3.Max(hdr, Vector3.Zero) * exposure;
        float m = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        if (m <= 1e-6f) return c;
        return c * (Shoulder(m, knee) / m);
    }
}
