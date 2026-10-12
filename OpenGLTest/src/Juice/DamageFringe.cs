using System;
using VoxelEngine.Player;

namespace VoxelEngine.Juice;

/// <summary>
/// GL-free twin of the damage chromatic fringe: on a hit the screen edges split red and blue for a
/// moment (post-fragment.glsl's <c>uDamageFringe</c>, display space). It is driven by the same damage
/// flash as the vignette (<see cref="PlayerHealth.DamageFlash"/>, 1 on a hit decaying to 0 over
/// <see cref="PlayerHealth.DamageFlashSeconds"/>), but only its first
/// <see cref="WorldGenerationConfig.DamageFringeSeconds"/>; the shader shifts red outward and blue
/// inward by that strength, nothing at the centre of the screen and the most at the corners
/// (<see cref="EdgeWeight"/>).
/// </summary>
public static class DamageFringe
{
    /// <summary>
    /// The fringe's strength, 0..1, for damage flash <paramref name="flash"/>: 1 at the hit, easing to
    /// 0 once the flash has decayed for <see cref="WorldGenerationConfig.DamageFringeSeconds"/>.
    /// </summary>
    public static float Strength(float flash)
    {
        var start = 1f - WorldGenerationConfig.DamageFringeSeconds / PlayerHealth.DamageFlashSeconds;
        return MathUtils.SmoothStep(start, 1f, flash);
    }

    /// <summary>
    /// How much of the shift a pixel gets at <paramref name="distanceFromCentre"/> (its offset from the
    /// screen centre in uv, 0 at the centre to about 0.707 at a corner): nothing in the middle third,
    /// rising to 1 toward the corners. Twin of the edge weight in post-fragment.glsl.
    /// </summary>
    public static float EdgeWeight(float distanceFromCentre) =>
        MathUtils.SmoothStep(0.2f, 0.7f, distanceFromCentre);
}
