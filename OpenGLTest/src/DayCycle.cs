using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// GL-free day/night clock: where the sun is at a given lighting time (<see cref="World.LightingTime"/>)
/// and how much daylight that gives. <see cref="RenderManager.SetLighting"/> lights the world from
/// it and gameplay that depends on the hour (the slime size roll) reads the same numbers, so the
/// two can never disagree about whether it is night.
/// </summary>
public static class DayCycle
{
    /// <summary>The unit direction toward the sun: one orbit per
    /// <see cref="WorldGenerationConfig.DayLengthSeconds"/>, phased to start mid-morning.</summary>
    public static Vector3 SunDirection(double time)
    {
        var angle = (float)(time / WorldGenerationConfig.DayLengthSeconds) * MathF.Tau + 0.8f;
        return Vector3.Normalize(new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0.35f));
    }

    /// <summary>0 at night to 1 in full day, eased across the horizon from the sun's
    /// <paramref name="elevation"/> (<see cref="SunDirection"/>'s Y).</summary>
    public static float Dayness(float elevation) => MathUtils.SmoothStep((elevation + 0.08f) / 0.33f);

    /// <summary>How deep into the night the lighting clock is: 0 in full day, 1 in full night.</summary>
    public static float NightFactor(double time) => 1f - Dayness(SunDirection(time).Y);
}
