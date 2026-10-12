using System;
using VoxelEngine.Effects.Water;

namespace VoxelEngine.Effects.Surfaces;

/// <summary>
/// GL-free twin of the ice term in test-fragment.glsl, a separate tile term on the ice block's
/// tile (like the snow sparkle, not the ore material path): a fresnel sheen of the sky the face
/// mirrors, mixed over the lit ice by <see cref="SheenWeight"/>, and a Blinn sun highlight added
/// in linear light, <see cref="Highlight"/> times the direct sun.
/// </summary>
public static class IceGlint
{
    /// <summary>
    /// How much of the mirrored sky an ice face shows when the eye sees it at
    /// <paramref name="cosView"/> (the cosine between the view and the face normal):
    /// <see cref="WorldGenerationConfig.IceSheenStrength"/> times <see cref="SurfaceFresnel.Schlick"/>,
    /// so a little head-on and the most at a graze.
    /// </summary>
    public static float SheenWeight(float cosView) =>
        WorldGenerationConfig.IceSheenStrength * SurfaceFresnel.Schlick(cosView);

    /// <summary>
    /// The sun highlight as a multiple of the direct sun, 0..<see cref="WorldGenerationConfig.IceGlintStrength"/>:
    /// a Blinn lobe of <paramref name="nDotH"/> (the face normal against the eye/sun half vector),
    /// gated by the sun on the face's own side (<paramref name="nDotL"/>), the sun's gain
    /// (<paramref name="sunIntensity"/>) and how much of the sun reaches the face
    /// (<paramref name="sunReach"/>: skylight times the shadow and cloud shadow, 0..1). Zero in shadow,
    /// zero with the sun behind the face, never decreasing as the half vector lines up with the normal.
    /// </summary>
    public static float Highlight(float nDotH, float nDotL, float sunIntensity, float sunReach) =>
        WorldGenerationConfig.IceGlintStrength
        * MathF.Pow(MathF.Max(nDotH, 0f), WorldGenerationConfig.IceGlintShine)
        * MathUtils.Clamp01(sunReach)
        * MathUtils.SmoothStep(0f, 0.15f, nDotL)
        * MathUtils.SmoothStep(0f, 0.2f, sunIntensity);
}
