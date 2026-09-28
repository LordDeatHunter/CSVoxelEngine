using System;

namespace silkgltriangle;

/// <summary>
/// GL-free twin of the surface fresnel terms in test-fragment.glsl: the shared <c>Schlick</c>
/// helper the ore path and the wet waterline both read, and the wet weight that decides how
/// much a face's albedo darkens (SH-22), plus the universal grazing rim on non-ore tiles (SH-23).
/// Keep the two in step.
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
    /// The universal grazing rim weight, 0..1: <see cref="Schlick"/> renormalised so a head-on
    /// view adds nothing and a full graze gives the whole rim. A back face (cosine at or below 0,
    /// a cutout leaf seen from behind) gets none rather than the clamped full graze. The shader
    /// further gates it by the reflected ray heading up and by the sun shadow.
    /// </summary>
    public static float Rim(float cosTheta) => cosTheta > 0f ? (Schlick(cosTheta) - F0) / (1f - F0) : 0f;

    /// <summary>
    /// How much of the rim survives at <paramref name="dist"/> blocks from the camera: all of it
    /// up to <paramref name="near"/>, none past <paramref name="far"/>, smooth in between.
    /// </summary>
    public static float RimFade(float dist, float near, float far) => 1f - MathUtils.SmoothStep(near, far, dist);

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
