using System;
using System.Numerics;
using VoxelEngine.Effects.Sky;

namespace VoxelEngine.Effects.Surfaces;

/// <summary>
/// GL-free twin of the grass wind shimmer in test-fragment.glsl: soft bright bands that lie across
/// the wind on the grass tops and roll downwind with the leaf sway's gusts. The pattern is read at
/// the position carried back by the gusts' traveled offset (<see cref="Wind.Offset"/> at
/// <see cref="WorldGenerationConfig.WindGustSpeed"/>, the shader's <c>uWindOffset</c>), the way the
/// sway samples its gust wave, and weighted by that same gust (<see cref="Wind.Gust"/>), so the
/// bands travel and swell with the canopy. Only biome-tinted up faces shimmer (<see cref="Applies"/>).
/// </summary>
public static class GrassShimmer
{
    /// <summary>
    /// Whether a face shimmers: a biome-tinted (<c>Vertex.Normal</c> bit 4) face looking up, never a
    /// swaying one (canopy leaves and plants, bit 5) nor the far LOD's opaque crowns.
    /// </summary>
    public static bool Applies(bool biomeTinted, bool upFace, bool swaying, bool lodFoliage) =>
        biomeTinted && upFace && !swaying && !lodFoliage;

    /// <summary>
    /// The band strength at <paramref name="worldXZ"/>, 0..1: the crest pattern along the wind
    /// (<paramref name="windDirection"/>, any length, normalized here as the shader does) at the
    /// position less <paramref name="gustOffset"/>, times the gust there at <paramref name="time"/>.
    /// </summary>
    public static float Band(Vector2 worldXZ, Vector2 windDirection, Vector2 gustOffset, double time)
    {
        var wd = windDirection / MathF.Max(windDirection.Length(), 1e-6f);
        var g = worldXZ - gustOffset;
        var along = Vector2.Dot(g, wd);
        var across = Vector2.Dot(g, new Vector2(-wd.Y, wd.X));
        var k = MathF.Tau / WorldGenerationConfig.GrassShimmerWavelengthBlocks;
        var wave = MathF.Sin(along * k + 1.5f * MathF.Sin(across * k * 0.31f + along * k * 0.17f));
        return MathUtils.SmoothStep(0.35f, 1f, wave) * Wind.Gust(worldXZ, time, gustOffset);
    }

    /// <summary>1 near the eye, easing to 0 between the shimmer's fade distances.</summary>
    public static float Fade(float distance) =>
        1f - MathUtils.SmoothStep(WorldGenerationConfig.GrassShimmerFadeNearBlocks,
            WorldGenerationConfig.GrassShimmerFadeFarBlocks, distance);

    /// <summary>
    /// The share of its own light a grass top gains: <see cref="WorldGenerationConfig.GrassShimmerStrength"/>
    /// times the fade at <paramref name="distance"/> and the <see cref="Band"/>.
    /// </summary>
    public static float Brightening(Vector2 worldXZ, Vector2 windDirection, Vector2 gustOffset, double time, float distance) =>
        WorldGenerationConfig.GrassShimmerStrength * Fade(distance) * Band(worldXZ, windDirection, gustOffset, time);
}
