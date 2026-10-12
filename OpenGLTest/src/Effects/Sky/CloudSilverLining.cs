using System;
using System.Numerics;
using VoxelEngine.Rendering;

namespace VoxelEngine.Effects.Sky;

/// <summary>
/// GL-free twin of the cloud layer's silver lining (cloud-fragment.glsl): the thin edges of a cloud
/// near the sun light up, as sunlight scatters forward through them toward the eye. Per fragment it
/// is the forward-scatter falloff <see cref="Weight"/> of the view ray's angle to the sun, times how
/// thin the cloud is along that ray behind the lit surface (<see cref="Thinness"/>, read cell by cell
/// from the same baked coverage grid the stars' cloud cover and the cloud shadows read); per frame it
/// is scaled by <see cref="Strength"/>, which is zero with the sun at or below the horizon.
/// </summary>
public static class CloudSilverLining
{
    /// <summary>How many points along the view ray the thinness test reads behind a cloud surface.</summary>
    public const int Samples = 6;

    /// <summary>
    /// The frame's silver-lining strength, 0..<see cref="WorldGenerationConfig.CloudSilverLiningStrength"/>,
    /// for a sun at sine-elevation <paramref name="sunElevation"/>: zero at and below the horizon,
    /// rising to full by <see cref="WorldGenerationConfig.CloudSilverLiningFullElevation"/>.
    /// </summary>
    public static float Strength(float sunElevation) =>
        WorldGenerationConfig.CloudSilverLiningStrength
        * MathUtils.SmoothStep(0f, WorldGenerationConfig.CloudSilverLiningFullElevation, sunElevation);

    /// <summary>
    /// The per-fragment lining, 0..1 before <see cref="Strength"/>: the forward-scatter lobe
    /// cos(angle to the sun)^<see cref="WorldGenerationConfig.CloudSilverLiningExponent"/> (nothing
    /// a quarter turn or more from the sun) times <paramref name="thinness"/> (0..1). Twin of the
    /// term in cloud-fragment.glsl.
    /// </summary>
    public static float Weight(float cosToSun, float thinness) =>
        MathF.Pow(MathF.Max(cosToSun, 0f), WorldGenerationConfig.CloudSilverLiningExponent) * MathUtils.Clamp01(thinness);

    /// <summary>
    /// How thin the cloud is behind <paramref name="surface"/> (a point on the drawn cloud) along the
    /// unit view direction <paramref name="view"/>, 0..1: the share of <see cref="Samples"/> points
    /// one <see cref="WorldGenerationConfig.CloudSilverLiningStepBlocks"/> apart along the ray that are
    /// no longer inside the cloud volume (above the base and below the top of the stack the cell under
    /// them holds, <see cref="CloudCover.LevelAt"/>). An edge the ray leaves at once is 1, the heart
    /// of a thick cloud 0. Twin of <c>CloudThinness</c> in cloud-fragment.glsl.
    /// </summary>
    public static float Thinness(ReadOnlySpan<byte> coverage, int cells, Vector3 surface, Vector3 view, Vector2 drift)
    {
        var inside = 0;
        for (var i = 1; i <= Samples; i++)
        {
            var q = surface + view * (i * WorldGenerationConfig.CloudSilverLiningStepBlocks);
            var top = WorldGenerationConfig.CloudHeight
                      + CloudCover.LevelAt(coverage, cells, q.X, q.Z, drift) * WorldGenerationConfig.CloudLayerThickness;
            if (q.Y > WorldGenerationConfig.CloudHeight && q.Y < top) inside++;
        }

        return 1f - inside / (float)Samples;
    }
}
