using System;
using System.Numerics;
using VoxelEngine.Rendering.ScreenSpace;

namespace VoxelEngine.Effects.Post;

/// <summary>
/// GL-free twin of the lens flare: the row of tinted circles ("ghosts", warm gold and orange with a
/// few greenish ones) a lens throws across the picture with the sun in view, strung along the line
/// from the sun through the screen center. Drawn by the sun dazzle's pass
/// (<see cref="SunDazzleRenderer"/>, dazzle-fragment.glsl) and hidden with the sun's disc like it.
/// The circles are <see cref="PostProcessConfig.LensFlareGhosts"/>; how strong they are this frame
/// is <see cref="Intensity"/>: nothing looking straight at the sun, where they would all sit on it
/// (that is the dazzle's), and nothing once the sun is well off to the side.
/// </summary>
public static class LensFlare
{
    /// <summary>The most circles the shader draws: twin of <c>MaxGhosts</c> in dazzle-fragment.glsl.</summary>
    public const int MaxGhosts = 8;

    /// <summary>
    /// The flare's strength, 0..1, for a camera whose
    /// forward has dot <paramref name="alignment"/> with the sun direction, a sun disc of visibility
    /// <paramref name="sunVisibility"/> and an eye <paramref name="wet"/> (0 dry .. 1 fully under
    /// water): zero within <see cref="PostProcessConfig.LensFlareStartDegrees"/> of the sun, full
    /// from <see cref="PostProcessConfig.LensFlareFullDegrees"/> to
    /// <see cref="PostProcessConfig.LensFlareFadeDegrees"/>, zero again
    /// <see cref="PostProcessConfig.LensFlareGoneDegrees"/> or more from it; zero without a sun disc
    /// and zero under water.
    /// </summary>
    public static float Intensity(float alignment, float sunVisibility, float wet)
    {
        var offTheSun = 1f - SunDazzle.Facing(alignment, PostProcessConfig.LensFlareStartDegrees, PostProcessConfig.LensFlareFullDegrees);
        var inView = SunDazzle.Facing(alignment, PostProcessConfig.LensFlareFadeDegrees, PostProcessConfig.LensFlareGoneDegrees);
        return offTheSun * inView * MathUtils.Clamp01(sunVisibility) * (1f - MathUtils.Clamp01(wet));
    }

    /// <summary>
    /// How much of the picture behind it a circle covers per unit of its brightness, for a flare of
    /// strength <paramref name="intensity"/> under a sun of linear color <paramref name="sunColor"/>
    /// (<see cref="PostProcessConfig.LensFlareCover"/>): the circles are faint films of their tint,
    /// so their colors read over a bright sky. A dim sun covers less in step with the light its
    /// circles add, so they never turn into dark smudges at dusk.
    /// </summary>
    public static float Cover(float intensity, Vector3 sunColor)
    {
        var sun = MathUtils.Clamp01(MathF.Max(sunColor.X, MathF.Max(sunColor.Y, sunColor.Z)));
        return PostProcessConfig.LensFlareCover * MathUtils.Clamp01(intensity) * sun;
    }

    /// <summary>
    /// Where a circle at <paramref name="offset"/> along the flare's line lies, for a sun at
    /// <paramref name="sun"/>; both measured from the screen center in screen heights.
    /// </summary>
    public static Vector2 Center(Vector2 sun, float offset) => sun * offset;

    /// <summary>
    /// How bright a circle is at <paramref name="distance"/> from its center (in its own radii),
    /// 0..1: nothing at and beyond its edge, which fades over the outer <paramref name="softness"/>
    /// of the radius; inside, evenly lit for <paramref name="hollow"/> 0 and darkening toward the
    /// middle as it rises, to a ring at 1. Twin of <c>GhostShape</c> in dazzle-fragment.glsl.
    /// </summary>
    public static float Shape(float distance, float hollow, float softness)
    {
        var inside = 1f - MathUtils.SmoothStep(1f - softness, 1f, distance);
        var d2 = distance * distance;
        return inside * MathUtils.Lerp(1f, d2 * d2, hollow);
    }

    /// <summary>Each circle's offset, radius, hollow and softness, as the shader's <c>uGhostShape</c> takes them.</summary>
    public static Vector4[] PackShapes()
    {
        var ghosts = PostProcessConfig.LensFlareGhosts;
        var shapes = new Vector4[ghosts.Length];
        for (var i = 0; i < ghosts.Length; i++)
            shapes[i] = new Vector4(ghosts[i].Offset, ghosts[i].Radius, ghosts[i].Hollow, ghosts[i].Softness);
        return shapes;
    }

    /// <summary>Each circle's linear tint times its brightness, and that brightness (what its cover
    /// scales with), as the shader's <c>uGhostColor</c> takes them.</summary>
    public static Vector4[] PackColors()
    {
        var ghosts = PostProcessConfig.LensFlareGhosts;
        var colors = new Vector4[ghosts.Length];
        for (var i = 0; i < ghosts.Length; i++)
            colors[i] = new Vector4(ColorSpace.SrgbToLinear(ghosts[i].Tint) * ghosts[i].Brightness, ghosts[i].Brightness);
        return colors;
    }
}
