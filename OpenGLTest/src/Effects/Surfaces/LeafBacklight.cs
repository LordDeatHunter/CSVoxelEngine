using System;

namespace VoxelEngine.Effects.Surfaces;

/// <summary>
/// GL-free twin of the leaf backlight in test-fragment.glsl: a leaf-sway face (canopy leaves and
/// rooted plants, <c>Vertex.Normal</c> bit 5) standing between the eye and the sun glows warm, as
/// if lit through. The shader multiplies <see cref="Weight"/> into the direct sun on the face's
/// tinted albedo; whether sunlight reaches the leaf from behind is the shadow map read
/// <see cref="WorldGenerationConfig.LeafBacklightReachBlocks"/> toward the sun from it, so a
/// crown's thin, sunlit rim lets light through while leaves in its own shadow stay dark.
/// </summary>
public static class LeafBacklight
{
    /// <summary>
    /// How strongly a leaf is lit through, 0..<see cref="WorldGenerationConfig.LeafBacklightStrength"/>:
    /// <paramref name="forward"/> is the dot of the unit view ray (eye to leaf) with the sun
    /// direction, <paramref name="sunElevation"/> the sun direction's Y, <paramref name="sunIntensity"/>
    /// the direct sun's gain and <paramref name="lightThrough"/> (0..1) how much sun the shadow map
    /// lets reach the leaf from behind. Zero looking away from the sun or square to it, zero with the
    /// sun at or below the horizon, zero for a leaf the sun cannot reach; never decreasing as the
    /// view turns toward the sun.
    /// </summary>
    public static float Weight(float forward, float sunElevation, float sunIntensity, float lightThrough) =>
        WorldGenerationConfig.LeafBacklightStrength
        * MathF.Pow(MathF.Max(forward, 0f), WorldGenerationConfig.LeafBacklightPower)
        * MathUtils.SmoothStep(0f, 0.1f, sunElevation)
        * MathUtils.SmoothStep(0f, 0.2f, sunIntensity)
        * MathUtils.Clamp01(lightThrough);
}
