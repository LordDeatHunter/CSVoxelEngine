using System;
using System.Numerics;
using VoxelEngine.Rendering;

namespace VoxelEngine.Effects.Sky;

/// <summary>
/// GL-free day/night clock: where the sun is at a given lighting time (<see cref="World.LightingTime"/>)
/// and how much daylight that gives. <see cref="FrameLighting.SetLighting"/> lights the world from
/// it and gameplay that depends on the hour (the slime size roll) reads the same numbers, so the
/// two can never disagree about whether it is night.
/// </summary>
public static class DayCycle
{
    /// <summary>The sun's orbit angle at lighting time 0, in radians: mid-morning.</summary>
    public const float StartAngle = 0.8f;

    /// <summary>The first lighting time at which the sun stands highest (its orbit angle a quarter
    /// turn), about 147 s; noon recurs every <see cref="SkyConfig.DayLengthSeconds"/>.</summary>
    public static double NoonTime => (MathF.PI / 2f - StartAngle) / MathF.Tau * SkyConfig.DayLengthSeconds;

    /// <summary>The sun's orbit angle at <paramref name="time"/>, in radians: one turn per
    /// <see cref="SkyConfig.DayLengthSeconds"/> about the +Z axis the whole sky turns around, wrapped into
    /// one turn (in double, before the float cast) so the angle keeps its precision however long the clock runs.</summary>
    public static float OrbitAngle(double time)
    {
        var turns = time / SkyConfig.DayLengthSeconds;
        return (float)(turns - Math.Floor(turns)) * MathF.Tau + StartAngle;
    }

    /// <summary>The unit direction toward the sun: one orbit per
    /// <see cref="SkyConfig.DayLengthSeconds"/>, phased to start mid-morning.</summary>
    public static Vector3 SunDirection(double time)
    {
        var angle = OrbitAngle(time);
        return Vector3.Normalize(new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0.35f));
    }

    /// <summary>0 at night to 1 in full day, eased across the horizon from the sun's
    /// <paramref name="elevation"/> (<see cref="SunDirection"/>'s Y).</summary>
    public static float Dayness(float elevation) => MathUtils.SmoothStep((elevation + 0.08f) / 0.33f);

    /// <summary>How deep into the night the lighting clock is: 0 in full day, 1 in full night.</summary>
    public static float NightFactor(double time) => 1f - Dayness(SunDirection(time).Y);
}
