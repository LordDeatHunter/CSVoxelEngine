using System;

namespace silkgltriangle;

/// <summary>
/// GL-free twin of the surface fresnel terms in test-fragment.glsl: the shared <c>Schlick</c>
/// helper the ore path reads, and the wet weight that decides whether a face's albedo darkens
/// (SH-22). Keep the two in step.
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
    /// How wet a fragment is, 0 or 1: a fragment of a submerged face is wet at or below that
    /// face's own baked water surface (<paramref name="heightAboveSurface"/> at most 0) and dry
    /// above it, so the top of a side face poking out of a thin flow keeps its colour; a dry face
    /// is never wet. The twin of the shader's <c>step(fWorldPos.y, fWaterSurfaceY)</c>.
    /// </summary>
    public static float WetWeight(bool submerged, float heightAboveSurface) =>
        submerged && heightAboveSurface <= 0f ? 1f : 0f;
}
