using System;
using System.Numerics;

namespace silkgltriangle;

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
    /// How far something carried at <paramref name="speed"/> blocks per second has travelled
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
    public static float GustPhase(Vector2 anchorXZ, double t)
    {
        var off = Offset(t, WorldGenerationConfig.WindGustSpeed);
        return (float)(t * 0.5) + (anchorXZ.X - off.X) * 0.08f + (anchorXZ.Y - off.Y) * 0.06f;
    }
}
