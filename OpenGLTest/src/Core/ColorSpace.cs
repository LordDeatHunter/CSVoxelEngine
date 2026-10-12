using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Exact sRGB transfer functions. CPU colors (sun, ambient, sky palette, fog, tints) are
/// authored in sRGB (display) space and must pass through <see cref="SrgbToLinear(float)"/>
/// before they reach a shader that runs ahead of the tonemap, since the world is lit in
/// linear light. <c>post-fragment.glsl</c> and <c>ui-fragment.glsl</c> carry GLSL twins of
/// <see cref="LinearToSrgb(float)"/>.
/// </summary>
public static class ColorSpace
{
    /// <summary>
    /// sRGB-encoded value to linear light, the exact piecewise decode. Values above 1 take the
    /// power branch, so HDR multipliers (a tint of 1.2) stay monotonic rather than clamping.
    /// </summary>
    public static float SrgbToLinear(float c) =>
        c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    /// <summary>Linear light to sRGB encoding, the exact inverse of <see cref="SrgbToLinear(float)"/>; negatives clamp to 0.</summary>
    public static float LinearToSrgb(float c)
    {
        if (c <= 0f) return 0f;
        return c <= 0.0031308f ? 12.92f * c : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
    }

    /// <summary>Per-component <see cref="SrgbToLinear(float)"/>.</summary>
    public static Vector3 SrgbToLinear(Vector3 c) =>
        new(SrgbToLinear(c.X), SrgbToLinear(c.Y), SrgbToLinear(c.Z));

    /// <summary>Per-component <see cref="LinearToSrgb(float)"/>.</summary>
    public static Vector3 LinearToSrgb(Vector3 c) =>
        new(LinearToSrgb(c.X), LinearToSrgb(c.Y), LinearToSrgb(c.Z));
}
