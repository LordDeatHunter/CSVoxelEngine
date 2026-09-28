using System;

namespace silkgltriangle;

/// <summary>
/// GL-free twin of the world fog in test-fragment.glsl (SH-24). Keep the two in step.
/// <para>The distance fog is GL_EXP2-shaped but normalised over [near, far]:
/// <c>(1 - exp(-(k t)^2)) / (1 - exp(-k^2))</c> with <c>t</c> the linear ramp and
/// <c>k = density * (far - near)</c>, so it is exactly 0 at near and exactly 1 at far (the LOD
/// edge melts into the sky) and convex like the old <c>t^2</c> close to the camera. Underwater and
/// in lava (<c>wet = 1</c>) it is exactly the old <c>t^2</c>.</para>
/// <para>The height fog integrates an exponential density falling off above a base height along
/// the stretch of the view ray past near only, so the first <c>near</c> blocks stay clear, and it
/// is capped below 1. The two combine as independent transmittances, which keeps full distance
/// fog exactly the sky colour whatever the height term says.</para>
/// </summary>
public static class FogModel
{
    /// <summary>Distance fog amount 0..1 at <paramref name="dist"/> blocks from the eye.</summary>
    public static float Distance(float dist, float near, float far, float density, float wet)
    {
        var t = MathUtils.Clamp01((dist - near) / (far - near));
        var t2 = t * t;
        var k = density * (far - near);
        var dry = k < 1e-3f ? t2 : (1f - MathF.Exp(-(k * t) * (k * t))) / (1f - MathF.Exp(-k * k));
        return dry + (t2 - dry) * MathUtils.Clamp01(wet);
    }

    /// <summary>Height fog amount 0..<paramref name="max"/>: the optical depth of an exponential
    /// layer (<paramref name="density"/> at <paramref name="baseY"/>, 1/e per
    /// <paramref name="falloff"/> blocks up, constant below the base) over the ray's length past
    /// <paramref name="near"/>, averaged analytically between the eye and fragment heights.</summary>
    public static float Height(float dist, float near, float camY, float fragY, float baseY,
        float falloff, float density, float max)
    {
        var s = MathF.Max(dist - near, 0f);
        var y0 = MathF.Max(camY - baseY, 0f);
        var y1 = MathF.Max(fragY - baseY, 0f);
        var avg = MathF.Abs(y1 - y0) < 1e-3f
            ? MathF.Exp(-y0 / falloff)
            : falloff * (MathF.Exp(-y0 / falloff) - MathF.Exp(-y1 / falloff)) / (y1 - y0);
        return MathF.Min(1f - MathF.Exp(-density * s * avg), max);
    }

    /// <summary>Combines distance fog <paramref name="fd"/> and height fog <paramref name="fh"/> as
    /// independent transmittances; wet (underwater, lava) switches the height term off.</summary>
    public static float Combine(float fd, float fh, float wet) =>
        1f - (1f - fd) * (1f - fh * (1f - MathUtils.Clamp01(wet)));
}
