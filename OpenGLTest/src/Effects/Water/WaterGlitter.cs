using System;

namespace VoxelEngine.Effects.Water;

/// <summary>
/// GL-free twin of the sun glitter on water in test-fragment.glsl: the water plane is cut into
/// <see cref="WorldGenerationConfig.WaterGlitterCellsPerBlock"/> cells per block, a share of them
/// holds a tiny facet tilted off the wave normal, and a facet mirrors the sun (<see cref="Sparkle"/>)
/// while the eye/sun half vector lines up with it, so the sparkles gather along the sun's reflection
/// path. Each cell re-rolls its facet a few times a second and fades in and out within each roll
/// (<see cref="Life"/>). <see cref="Gate"/> is what the shader multiplies in, and the result is added
/// as <see cref="WorldGenerationConfig.WaterGlitterStrength"/> times the direct sun.
/// </summary>
public static class WaterGlitter
{
    /// <summary>
    /// How brightly a facet mirrors the sun, 0..1, from <paramref name="facetDotHalf"/> (its normal
    /// against the eye/sun half vector): a narrow mirror lobe like the snow glint's, full within about
    /// a degree and a half and gone past about three and a half.
    /// </summary>
    public static float Sparkle(float facetDotHalf) => MathUtils.SmoothStep(0.9980f, 0.9996f, facetDotHalf);

    /// <summary>
    /// How far through its life a sparkle is at <paramref name="rollPhase"/> (0..1 through one roll of
    /// its cell): <c>sin(pi * phase)</c>, nothing at either end of a roll and full in its middle, so a
    /// cell re-rolling its facet never pops.
    /// </summary>
    public static float Life(float rollPhase) => MathF.Sin(MathF.PI * (rollPhase - MathF.Floor(rollPhase)));

    /// <summary>
    /// How much of a sparkle lights a point <paramref name="distanceInCells"/> (in cell widths) from its
    /// hashed spot: full near the spot, nothing past 0.28 of a cell (the spot sits 0.3 in from every
    /// edge), so a sparkle is a small round point of sun rather than its whole cell.
    /// </summary>
    public static float Spot(float distanceInCells) => 1f - MathUtils.SmoothStep(0.12f, 0.28f, distanceInCells);

    /// <summary>
    /// How much glitter a water fragment may show, 0..1: never on a face seen from below (the sun is seen
    /// refracted through the window there) nor with the eye <paramref name="underwater"/> (0..1), and only
    /// as far as the sun reaches it (<paramref name="shadow"/> times <paramref name="skyLight"/>), with
    /// the sun risen (<paramref name="sunElevation"/>, <paramref name="sunIntensity"/>) and before the
    /// distance fade ends.
    /// </summary>
    public static float Gate(bool fromBelow, float underwater, float shadow, float skyLight, float sunElevation,
        float sunIntensity, float distance) =>
        fromBelow
            ? 0f
            : MathUtils.Clamp01(shadow) * MathUtils.Clamp01(skyLight)
              * MathUtils.SmoothStep(0f, 0.05f, sunElevation)
              * MathUtils.SmoothStep(0f, 0.2f, sunIntensity)
              * (1f - MathUtils.Clamp01(underwater))
              * (1f - MathUtils.SmoothStep(WorldGenerationConfig.WaterGlitterFadeNearBlocks,
                  WorldGenerationConfig.WaterGlitterFadeFarBlocks, distance));
}
