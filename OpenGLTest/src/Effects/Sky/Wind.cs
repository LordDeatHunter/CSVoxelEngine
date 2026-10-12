using System;
using System.Numerics;

namespace VoxelEngine.Effects.Sky;

/// <summary>
/// The one wind of the world: a horizontal direction that turns slowly and steadily with the
/// lighting clock (window time plus <c>VOXEL_TIME</c>), so every consumer agrees on it without
/// sharing state. Leaf sway leans along it and advects its gust pattern by <see cref="Offset"/>
/// (<c>uWind</c> / <c>uWindOffset</c> in test-vertex.glsl and shadow-vertex.glsl), fire smoke
/// drifts along it, and the clouds are carried by it; rain or snow should read it too. A pure
/// function of time, computed in double so a large <c>VOXEL_TIME</c> keeps its precision. GL-free.
/// </summary>
public static class Wind
{
    private const double DegToRad = Math.PI / 180.0;

    /// <summary>Wind heading in radians at lighting time <paramref name="t"/>, measured from +X toward +Z.</summary>
    public static double AngleRad(double t) =>
        WorldGenerationConfig.WindStartDegrees * DegToRad
        + WorldGenerationConfig.WindTurnDegreesPerSecond * DegToRad * t;

    /// <summary>Unit downwind direction at time <paramref name="t"/>, as (x, z).</summary>
    public static Vector2 Direction(double t)
    {
        var a = AngleRad(t);
        return new Vector2((float)Math.Cos(a), (float)Math.Sin(a));
    }

    /// <summary>
    /// How far something carried at <paramref name="speed"/> blocks per second has traveled
    /// between time 0 and <paramref name="t"/>, as (x, z): the closed-form integral of
    /// <c>speed * Direction</c>. While the wind turns it traces a circle, so it stays bounded
    /// however long the clock runs.
    /// </summary>
    public static Vector2 Offset(double t, float speed)
    {
        var omega = WorldGenerationConfig.WindTurnDegreesPerSecond * DegToRad;
        var a0 = WorldGenerationConfig.WindStartDegrees * DegToRad;
        if (Math.Abs(omega) < 1e-9)
            return new Vector2((float)(speed * t * Math.Cos(a0)), (float)(speed * t * Math.Sin(a0)));
        var a = a0 + omega * t;
        var r = speed / omega;
        return new Vector2((float)(r * (Math.Sin(a) - Math.Sin(a0))), (float)(-r * (Math.Cos(a) - Math.Cos(a0))));
    }

    /// <summary>
    /// The C# twin of the leaf-sway gust phase in the vertex shaders: the fixed-axis gust wave
    /// sampled at the anchor minus the gust offset, so the pattern rolls downwind while its phase
    /// rate stays the same everywhere in the (unbounded) world.
    /// </summary>
    public static float GustPhase(Vector2 anchorXZ, double t) =>
        GustPhase(anchorXZ, t, Offset(t, WorldGenerationConfig.WindGustSpeed));

    /// <summary><see cref="GustPhase(Vector2, double)"/> with the gust offset
    /// (<c>Offset(t, WindGustSpeed)</c>) already in hand, so a caller sampling many anchors in one
    /// frame computes it once.</summary>
    public static float GustPhase(Vector2 anchorXZ, double t, Vector2 gustOffset) =>
        (float)(t * 0.5) + (anchorXZ.X - gustOffset.X) * 0.08f + (anchorXZ.Y - gustOffset.Y) * 0.06f;

    /// <summary>
    /// How hard the wind gusts at <paramref name="anchorXZ"/>, 0.2..1: the C# twin of the leaf-sway
    /// shaders' <c>gust = 0.6 + 0.4 * sin(phase)</c>, so the falling leaves are pushed by the very
    /// gusts the canopy sways in. <paramref name="gustOffset"/> is <c>Offset(t, WindGustSpeed)</c>.
    /// </summary>
    public static float Gust(Vector2 anchorXZ, double t, Vector2 gustOffset) =>
        0.6f + 0.4f * MathF.Sin(GustPhase(anchorXZ, t, gustOffset));
}
