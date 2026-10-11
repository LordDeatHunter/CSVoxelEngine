using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// GL-free twin of the glass sheen in test-fragment.glsl: glass blocks and panes (which share the
/// glass tile and draw blended in the translucent pass) mirror the sky at a graze. Glass is mostly
/// see-through, so the sky is folded into the blend: the alpha rises to <see cref="Alpha"/> and the
/// colour becomes <see cref="Colour"/>, which composited with straight alpha over any background is
/// exactly the plain glass composite mixed toward the sky by <see cref="Weight"/>.
/// </summary>
public static class GlassSheen
{
    /// <summary>
    /// How far toward the mirrored sky a glass face goes when the eye sees it at
    /// <paramref name="cosView"/> (the cosine between the view and the face normal, either side):
    /// <see cref="WorldGenerationConfig.GlassSheenStrength"/> times <see cref="SurfaceFresnel.Schlick"/>.
    /// </summary>
    public static float Weight(float cosView) =>
        WorldGenerationConfig.GlassSheenStrength * SurfaceFresnel.Schlick(MathF.Abs(cosView));

    /// <summary>The blended alpha of a glass texel of alpha <paramref name="texAlpha"/> taking a
    /// sky share of <paramref name="weight"/>: <c>a + F - aF</c>, never below the texel's own.</summary>
    public static float Alpha(float texAlpha, float weight) => texAlpha + weight - texAlpha * weight;

    /// <summary>The colour the shader writes for a lit glass texel <paramref name="glass"/> of alpha
    /// <paramref name="texAlpha"/> mirroring <paramref name="sky"/> by <paramref name="weight"/>.</summary>
    public static Vector3 Colour(Vector3 glass, float texAlpha, Vector3 sky, float weight) =>
        (glass * (texAlpha * (1f - weight)) + sky * weight) / MathF.Max(Alpha(texAlpha, weight), 1e-4f);

    /// <summary>Straight-alpha blending (SrcAlpha, OneMinusSrcAlpha), what the translucent pass does.</summary>
    public static Vector3 Over(Vector3 source, float alpha, Vector3 destination) =>
        source * alpha + destination * (1f - alpha);
}
