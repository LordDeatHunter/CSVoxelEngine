using System;
using System.Numerics;

namespace VoxelEngine.Effects.Sky;

/// <summary>
/// GL-free moon: which phase it shows on a given lighting time, and how that phase lights its disc.
/// The moon stands opposite the sun (<see cref="DayCycle.SunDirection"/> negated) and is drawn from
/// one full-disc sprite; moon-fragment.glsl darkens the unlit part from <see cref="LightDirection"/>,
/// so one image covers every phase. The phase steps once a day, at noon, while the moon is below the
/// horizon, so it never changes in view: day <see cref="DayIndex"/> 0 (the first night of a new world)
/// is full, and the moon wanes, goes new and waxes back to full over
/// <see cref="WorldGenerationConfig.MoonCycleDays"/> days.
/// </summary>
public static class MoonPhase
{
    /// <summary>The phase fraction of day 0: 0.5, a full moon on the first night.</summary>
    public const float FirstNightPhase = 0.5f;

    /// <summary>Which day the lighting <paramref name="time"/> belongs to, counted in whole days from
    /// the first noon (the day changes at noon, so a night is one day from dusk to dawn).</summary>
    public static long DayIndex(double time) =>
        (long)Math.Floor((time - DayCycle.NoonTime) / WorldGenerationConfig.DayLengthSeconds);

    /// <summary>
    /// The phase on lighting <paramref name="time"/>, 0..1: 0 new, 0.25 first quarter (waxing, the
    /// right half lit), 0.5 full, 0.75 last quarter. Advances by 1 / <see cref="WorldGenerationConfig.MoonCycleDays"/>
    /// each day and repeats every cycle.
    /// </summary>
    public static float Fraction(double time)
    {
        var cycle = WorldGenerationConfig.MoonCycleDays;
        var day = DayIndex(time) % cycle;
        if (day < 0) day += cycle;
        var f = FirstNightPhase + day / (float)cycle;
        return f - MathF.Floor(f);
    }

    /// <summary>
    /// Where the sunlight comes from in the moon disc's own frame for phase <paramref name="fraction"/>:
    /// (x, z) with x across the disc to the right and z toward the viewer, so a disc point with sphere
    /// normal n is lit where dot(n, (x, 0, z)) &gt; 0. New moon (0) is lit from behind, full (0.5) from
    /// the front, and the quarters from the side. Twin of uPhaseLight in moon-fragment.glsl.
    /// </summary>
    public static Vector2 LightDirection(float fraction)
    {
        var phi = fraction * MathF.Tau;
        return new Vector2(MathF.Sin(phi), -MathF.Cos(phi));
    }

    /// <summary>
    /// How much of the disc is lit at phase <paramref name="fraction"/>, 0 (new) .. 1 (full): the
    /// area of the visible hemisphere the light direction reaches, (1 - cos phi) / 2.
    /// </summary>
    public static float IlluminatedFraction(float fraction) => (1f - MathF.Cos(fraction * MathF.Tau)) * 0.5f;

    /// <summary>
    /// Whether the disc point with sphere normal <paramref name="normal"/> (z toward the viewer) is
    /// lit at phase <paramref name="fraction"/>, softened over
    /// <see cref="WorldGenerationConfig.MoonTerminatorSoftness"/> across the terminator: 0 dark .. 1 lit.
    /// Twin of the terminator in moon-fragment.glsl.
    /// </summary>
    public static float Lit(Vector3 normal, float fraction)
    {
        var l = LightDirection(fraction);
        var soft = WorldGenerationConfig.MoonTerminatorSoftness;
        return MathUtils.SmoothStep(-soft, soft, normal.X * l.X + normal.Z * l.Y);
    }

    /// <summary>How visible the moon is for a sun at sine-elevation <paramref name="sunElevation"/>:
    /// it fades in and out with the stars (<see cref="StarField.Visibility"/>), so it is gone by day
    /// and stands well above the horizon whenever it shows.</summary>
    public static float Visibility(float sunElevation) => StarField.Visibility(sunElevation);
}
