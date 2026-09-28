using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>The contact AO pass's tuning, uploaded as uniforms by <see cref="ContactAoRenderer"/>.
/// Distances are in blocks except <see cref="MaxPx"/>.</summary>
public readonly record struct ContactAoSettings(
    float Strength, float MinBlocks, float PeakBlocks, float RangeBlocks,
    float FadeNear, float FadeFar, float RadiusBlocks, float MaxPx)
{
    /// <summary>The shipped values from <see cref="WorldGenerationConfig"/>.</summary>
    public static ContactAoSettings Default => new(
        WorldGenerationConfig.ContactAoStrength, WorldGenerationConfig.ContactAoMinBlocks,
        WorldGenerationConfig.ContactAoPeakBlocks, WorldGenerationConfig.ContactAoRangeBlocks,
        WorldGenerationConfig.ContactAoFadeNear, WorldGenerationConfig.ContactAoFadeFar,
        WorldGenerationConfig.ContactAoRadiusBlocks, WorldGenerationConfig.ContactAoMaxPx);
}

/// <summary>
/// GL-free twin of contact-ao-fragment.glsl (SH-28): a cheap screen-space contact AO read from the
/// HDR scene depth, four taps per pixel. Keep the two in step.
///
/// The measure is a crease: for each opposite tap pair (left/right, down/up) the midpoint of their
/// raw window depths is reconstructed on the centre pixel's own ray and compared with the centre
/// depth. Raw depth is affine in screen space over any plane (it is a + b/w, and 1/w is affine
/// over a plane), so on a flat surface the midpoint is exactly the centre depth and a plane never
/// darkens, whatever the viewing angle. In a concave corner the centre sits farther than the
/// midpoint of its two neighbours, giving a positive crease in blocks; a convex edge or a
/// silhouette gives a negative one, or a jump far past <see cref="ContactAoSettings.RangeBlocks"/>,
/// and both are ignored.
/// </summary>
public static class ContactAo
{
    /// <summary>World position of the pixel at <paramref name="uv"/> (0..1) whose raw window depth
    /// is <paramref name="depth"/>: the water pass's reconstruction, verbatim (the inverse of the
    /// exact matrix that wrote the depth, so no clip-depth convention is assumed).</summary>
    public static Vector3 Reconstruct(Matrix4x4 invViewProj, Vector2 uv, float depth)
    {
        var p = Vector4.Transform(new Vector4(uv.X * 2f - 1f, uv.Y * 2f - 1f, depth * 2f - 1f, 1f), invViewProj);
        return new Vector3(p.X, p.Y, p.Z) / p.W;
    }

    /// <summary>How much farther, in blocks, the centre surface lies than the midpoint of taps
    /// <paramref name="dA"/> and <paramref name="dB"/>, both measured along the centre ray. 0 on
    /// any plane, positive in a concave corner, and 0 when either tap sees the sky.</summary>
    public static float Crease(Matrix4x4 invViewProj, Vector3 eye, Vector2 uv, float dC, float dA, float dB)
    {
        if (dA >= 1f || dB >= 1f) return 0f;
        var centre = Vector3.Distance(Reconstruct(invViewProj, uv, dC), eye);
        var mid = Vector3.Distance(Reconstruct(invViewProj, uv, 0.5f * (dA + dB)), eye);
        return centre - mid;
    }

    /// <summary>Darkening, 0..<see cref="ContactAoSettings.Strength"/>, for the centre pixel with
    /// depth <paramref name="dC"/> and its four taps; the shader multiplies the scene by
    /// <c>1 - occlusion</c>. The sky (depth 1) is never occluded.</summary>
    public static float Occlusion(Matrix4x4 invViewProj, Vector3 eye, Vector2 uv,
        float dC, float dL, float dR, float dD, float dU, in ContactAoSettings s)
    {
        if (dC >= 1f) return 0f;
        var dist = Vector3.Distance(Reconstruct(invViewProj, uv, dC), eye);
        var fade = 1f - MathUtils.SmoothStep(s.FadeNear, s.FadeFar, dist);
        if (fade <= 0f) return 0f;
        var wH = Weight(Crease(invViewProj, eye, uv, dC, dL, dR), s);
        var wV = Weight(Crease(invViewProj, eye, uv, dC, dD, dU), s);
        return s.Strength * MathF.Max(wH, wV) * fade;
    }

    /// <summary>A crease's AO weight: rises from <see cref="ContactAoSettings.MinBlocks"/> to
    /// 1 at <see cref="ContactAoSettings.PeakBlocks"/> and falls back to 0 by
    /// <see cref="ContactAoSettings.RangeBlocks"/>.</summary>
    private static float Weight(float c, in ContactAoSettings s) =>
        MathUtils.SmoothStep(s.MinBlocks, s.PeakBlocks, c) * (1f - MathUtils.SmoothStep(s.PeakBlocks, s.RangeBlocks, c));

    /// <summary>Tap radius in pixels for a surface <paramref name="dist"/> blocks away:
    /// <paramref name="radiusBlocks"/> projected with <paramref name="pxPerBlockAtUnitDist"/>
    /// (viewport height / (2 tan(fovY / 2))), clamped to 1..<paramref name="maxPx"/>.</summary>
    public static float PixelRadius(float dist, float radiusBlocks, float pxPerBlockAtUnitDist, float maxPx) =>
        Math.Clamp(radiusBlocks * pxPerBlockAtUnitDist / MathF.Max(dist, 1e-4f), 1f, maxPx);

    /// <summary>The <c>pxPerBlockAtUnitDist</c> <see cref="PixelRadius"/> takes, for a viewport
    /// <paramref name="viewportHeight"/> pixels tall and a vertical field of view in radians.</summary>
    public static float PxPerBlockAtUnitDist(float viewportHeight, float fovYRadians) =>
        viewportHeight / (2f * MathF.Tan(fovYRadians * 0.5f));
}
