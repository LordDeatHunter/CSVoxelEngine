using System;

namespace silkgltriangle;

/// <summary>
/// GL-free twin of the surface fresnel terms in test-fragment.glsl: the shared <c>Schlick</c>
/// helper the ore path and the wet waterline both read, and the wet weight that decides how
/// much a face's albedo darkens (SH-22). Keep the two in step.
/// </summary>
public static class SurfaceFresnel
{
    /// <summary>Normal-incidence reflectance of water and wet surfaces.</summary>
    public const float F0 = 0.06f;

    /// <summary>Schlick's fresnel approximation with <see cref="F0"/>: F0 head-on, 1 at a graze.</summary>
    public static float Schlick(float cosTheta)
    {
        var m = 1f - MathUtils.Clamp01(cosTheta);
        return F0 + (1f - F0) * m * m * m * m * m;
    }

    /// <summary>
    /// How wet a face is, 0..1: a submerged face is fully wet, a wet-band face (a dry face the
    /// mesher flagged as just above a fluid surface) fades from wet at the surface to dry
    /// <paramref name="band"/> blocks above it, and every other face is dry.
    /// </summary>
    public static float WetWeight(bool submerged, bool wetBand, float heightAboveSurface, float band)
    {
        if (submerged) return 1f;
        if (!wetBand) return 0f;
        return 1f - MathUtils.SmoothStep(0f, band, heightAboveSurface);
    }
}
