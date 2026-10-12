using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// GL-free biome color grade: a faint warm tint over the desert and a cool one over the snow,
/// multiplied over the finished image in the post pass (post-fragment.glsl's <c>uGrade</c>, display
/// space). It is keyed to the climate at the eye (<see cref="EyeClimate"/>, the column source's stored
/// corner climate, so a client reads what its server streamed) and eased, so it drifts in and out as
/// the player crosses a biome border. The temperate lands, and any unknown column, are left exactly
/// neutral; no channel ever moves more than <see cref="WorldGenerationConfig.BiomeGradeStrength"/>.
/// </summary>
public static class BiomeGrade
{
    // Hue directions, each channel within -1..1: warm lifts red and pulls blue, cool the reverse
    private static readonly Vector3 WarmDirection = new(1f, 0.2f, -1f);
    private static readonly Vector3 CoolDirection = new(-0.8f, -0.1f, 1f);

    /// <summary>
    /// The grade for a climate (<paramref name="warmth"/>, <paramref name="wetness"/>, both 0..1 as
    /// <see cref="TerrainGenerator.Climate"/> gives them): neutral (1, 1, 1) through the temperate band,
    /// warm where it is both warm and dry (the desert's own weighting, the smaller of
    /// <see cref="EyeClimate.Warmness"/> and the dryness), cool where it is cold enough for snow
    /// (<see cref="EyeClimate.Coldness"/> past <see cref="WorldGenerationConfig.BiomeGradeColdStart"/>).
    /// </summary>
    public static Vector3 Tint(float warmth, float wetness)
    {
        var desert = MathF.Min(EyeClimate.Warmness(warmth), MathUtils.Clamp01(1f - wetness));
        var cold = MathUtils.SmoothStep(WorldGenerationConfig.BiomeGradeColdStart, WorldGenerationConfig.BiomeGradeColdFull,
            EyeClimate.Coldness(warmth));
        return Vector3.One + (WarmDirection * desert + CoolDirection * cold) * WorldGenerationConfig.BiomeGradeStrength;
    }

    /// <summary>
    /// The grade the eased value heads for: <see cref="Tint"/> of the eye's climate when its column is
    /// known (<paramref name="hasClimate"/>), else neutral, so an unknown column eases the grade out.
    /// </summary>
    public static Vector3 Target(bool hasClimate, float warmth, float wetness) =>
        hasClimate ? Tint(warmth, wetness) : Vector3.One;

    /// <summary>Eases <paramref name="current"/> toward <paramref name="target"/> channel by channel at
    /// <see cref="WorldGenerationConfig.BiomeGradeEaseRate"/> over <paramref name="deltaTime"/> seconds.</summary>
    public static Vector3 Ease(Vector3 current, Vector3 target, float deltaTime)
    {
        var rate = WorldGenerationConfig.BiomeGradeEaseRate;
        return new Vector3(MathUtils.Damp(current.X, target.X, rate, deltaTime),
            MathUtils.Damp(current.Y, target.Y, rate, deltaTime),
            MathUtils.Damp(current.Z, target.Z, rate, deltaTime));
    }
}
