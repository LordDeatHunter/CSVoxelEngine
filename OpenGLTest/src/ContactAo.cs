using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>The contact AO pass's tuning, uploaded as uniforms by <see cref="ContactAoRenderer"/>.
/// Distances are in blocks except <see cref="MaxPx"/> and the two reach bounds, which are in tap
/// radii.</summary>
public readonly record struct ContactAoSettings(
    float Strength, float MinBlocks, float PeakBlocks, float RangeBlocks,
    float FadeNear, float FadeFar, float RadiusBlocks, float MaxPx,
    float ReachNear, float ReachFar)
{
    /// <summary>The shipped values from <see cref="WorldGenerationConfig"/>.</summary>
    public static ContactAoSettings Default => new(
        WorldGenerationConfig.ContactAoStrength, WorldGenerationConfig.ContactAoMinBlocks,
        WorldGenerationConfig.ContactAoPeakBlocks, WorldGenerationConfig.ContactAoRangeBlocks,
        WorldGenerationConfig.ContactAoFadeNear, WorldGenerationConfig.ContactAoFadeFar,
        WorldGenerationConfig.ContactAoRadiusBlocks, WorldGenerationConfig.ContactAoMaxPx,
        WorldGenerationConfig.ContactAoReachNear, WorldGenerationConfig.ContactAoReachFar);
}

/// <summary>
/// GL-free twin of contact-ao-fragment.glsl (SH-28): a cheap screen-space contact AO read from the
/// HDR scene depth, four taps and four 1-pixel neighbours per pixel. Keep the two in step.
///
/// The measure is a crease: for each opposite tap pair (left/right, down/up) the midpoint of their
/// raw window depths is reconstructed on the centre pixel's own ray and compared with the centre
/// depth. Raw depth is affine in screen space over any plane (it is a + b/w, and 1/w is affine
/// over a plane), so on a flat surface the midpoint is exactly the centre depth and a plane never
/// darkens, whatever the viewing angle. In a concave corner the centre sits farther than the
/// midpoint of its two neighbours, giving a positive crease in blocks; a convex edge or a
/// silhouette gives a negative one, or a jump far past <see cref="ContactAoSettings.RangeBlocks"/>,
/// and both are ignored.
///
/// A thin object standing in front of the ground (a torch, a drop, a mob) puts one tap of a ground
/// pixel a tap radius beside it on the object, which creases exactly like a corner and would draw
/// a displaced copy of its silhouette. So the crease is split between the two taps along the
/// centre surface's own slope, read from its 1-pixel neighbours (<see cref="Held"/>): a tap in
/// front of that surface is an occluder, and it only counts while its surface lies within a few tap
/// radii of the centre point (<see cref="Reach"/>). A wall or pebble rising beside the centre is
/// that close; the part of a post well above the ground behind it is not. With every occluder in
/// reach the crease is exactly the plain midpoint crease.
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

    /// <summary>How fully an occluding tap counts, 0..1: 1 while the surface it sees lies within
    /// <see cref="ContactAoSettings.ReachNear"/> tap radii (<paramref name="tapBlocks"/>) of the
    /// centre point, falling to 0 by <see cref="ContactAoSettings.ReachFar"/>.</summary>
    public static float Reach(Matrix4x4 invViewProj, Vector2 tapUv, float dTap, Vector3 centre,
        float tapBlocks, in ContactAoSettings s)
    {
        var d = Vector3.Distance(Reconstruct(invViewProj, tapUv, dTap), centre);
        return 1f - MathUtils.SmoothStep(s.ReachNear * tapBlocks, s.ReachFar * tapBlocks, d);
    }

    /// <summary>The raw-depth crease <c>2 * dC - dA - dB</c> with each occluding tap's share scaled
    /// by its reach (<paramref name="reachA"/>, <paramref name="reachB"/>), the shares split along
    /// <paramref name="slope"/>, the centre surface's raw-depth change over one tap offset: a tap
    /// that lies in front of that surface is an occluder, one behind it is not. With both reaches 1
    /// it is the plain crease, whatever the slope.</summary>
    public static float Held(float dC, float dA, float dB, float slope, float reachA, float reachB)
    {
        var devA = dC - slope - dA;
        var devB = dC + slope - dB;
        return (devA > 0f ? devA * reachA : devA) + (devB > 0f ? devB * reachB : devB);
    }

    /// <summary>How much farther, in blocks, the centre surface lies than the midpoint of taps
    /// <paramref name="dA"/> (at <c>uv - tapUv</c>) and <paramref name="dB"/> (at
    /// <c>uv + tapUv</c>), both measured along the centre ray, with each occluding tap's share
    /// scaled by its <see cref="Reach"/>. <paramref name="dMinus1"/> and <paramref name="dPlus1"/>
    /// are the centre's 1-pixel neighbours on the same axis, and <paramref name="tapPx"/> the tap
    /// offset in pixels. 0 on any plane, positive in a concave corner, and 0 when either tap sees
    /// the sky or the taps sit on the centre (at the screen edge).</summary>
    public static float Crease(Matrix4x4 invViewProj, Vector3 eye, Vector2 uv, float dC,
        Vector3 centre, float centreDist, Vector2 tapUv, float dA, float dB, float dMinus1,
        float dPlus1, float tapPx, float tapBlocks, in ContactAoSettings s)
    {
        if (dA >= 1f || dB >= 1f || tapPx <= 0f) return 0f;
        var reachA = Reach(invViewProj, uv - tapUv, dA, centre, tapBlocks, s);
        var reachB = Reach(invViewProj, uv + tapUv, dB, centre, tapBlocks, s);
        // The centre surface's slope, read from one neighbour or the other. A slope read across a
        // silhouette right beside the centre misses its own side's tap by the whole jump, so the
        // one that carries the surface out to its tap wins; at the very pixel of a crease both do
        // (each neighbour on its own face), and the stronger crease is kept.
        var minusSlope = (dC - dMinus1) * tapPx;
        var plusSlope = (dPlus1 - dC) * tapPx;
        var minusMiss = MathF.Abs(dC - minusSlope - dA);
        var plusMiss = MathF.Abs(dC + plusSlope - dB);
        var tolerance = MathF.Min(minusMiss, plusMiss) + 0.25f * MathF.Abs(2f * dC - dA - dB);
        var held = float.MinValue;
        if (minusMiss <= tolerance) held = Held(dC, dA, dB, minusSlope, reachA, reachB);
        if (plusMiss <= tolerance) held = MathF.Max(held, Held(dC, dA, dB, plusSlope, reachA, reachB));
        return centreDist - Vector3.Distance(Reconstruct(invViewProj, uv, dC - 0.5f * held), eye);
    }

    /// <summary>Darkening, 0..<see cref="ContactAoSettings.Strength"/>, for the centre pixel at
    /// <paramref name="uv"/> with depths <paramref name="t"/>, its taps
    /// <paramref name="tapPx"/> pixels away on each axis in a <paramref name="resolution"/> viewport
    /// whose <see cref="PxPerBlockAtUnitDist"/> is <paramref name="pxPerBlock"/>; the shader
    /// multiplies the scene by <c>1 - occlusion</c>. The sky (depth 1) is never occluded.</summary>
    public static float Occlusion(Matrix4x4 invViewProj, Vector3 eye, Vector2 uv, in ContactAoTaps t,
        Vector2 tapPx, Vector2 resolution, float pxPerBlock, in ContactAoSettings s)
    {
        if (t.Centre >= 1f) return 0f;
        var centre = Reconstruct(invViewProj, uv, t.Centre);
        var dist = Vector3.Distance(centre, eye);
        var fade = 1f - MathUtils.SmoothStep(s.FadeNear, s.FadeFar, dist);
        if (fade <= 0f) return 0f;
        var tapUv = tapPx / resolution;
        var tapBlocks = tapPx * dist / pxPerBlock;
        var wH = Weight(Crease(invViewProj, eye, uv, t.Centre, centre, dist, new Vector2(tapUv.X, 0f),
            t.Left, t.Right, t.Left1, t.Right1, tapPx.X, tapBlocks.X, s), s);
        var wV = Weight(Crease(invViewProj, eye, uv, t.Centre, centre, dist, new Vector2(0f, tapUv.Y),
            t.Down, t.Up, t.Down1, t.Up1, tapPx.Y, tapBlocks.Y, s), s);
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
