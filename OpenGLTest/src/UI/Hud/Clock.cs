using System;
using VoxelEngine.Effects.Sky;
using VoxelEngine.Rendering;

namespace VoxelEngine.UI.Hud;

/// <summary>
/// Pure clock math behind the HUD's held-clock readout (see <see cref="UIRenderer.DrawClock"/>):
/// the lighting clock's time of day and day number. The day is the game day
/// (<see cref="WorldGenerationConfig.DayLengthSeconds"/> seconds, one sun orbit); its first
/// midnight is half a cycle after <see cref="DayCycle.NoonTime"/>, and day 1 runs from the
/// world's first morning (lighting time 0) to that midnight. No GL calls, so headless tests
/// cover it here instead of through a window.
/// </summary>
public static class Clock
{
    /// <summary>The first lighting time at which the sun stands lowest: midnight, half a cycle
    /// after the first noon.</summary>
    public static double MidnightTime => DayCycle.NoonTime + WorldGenerationConfig.DayLengthSeconds * 0.5;

    /// <summary>Seconds into the day, 0 at midnight, wrapping every day length.</summary>
    public static double SecondsSinceMidnight(double time)
    {
        var day = WorldGenerationConfig.DayLengthSeconds;
        var shifted = (time - MidnightTime) % day;
        if (shifted < 0.0) shifted += day;
        return shifted;
    }

    /// <summary>Whole days since the world's first morning: day 1 from lighting time 0 to the
    /// first midnight, then one per cycle, rolling over at every midnight.</summary>
    public static int DayNumber(double time) =>
        time < MidnightTime
            ? 1
            : 2 + (int)Math.Floor((time - MidnightTime) / WorldGenerationConfig.DayLengthSeconds);

    /// <summary>The 24-hour readout of a lighting time, "HH:MM", over the game day (one real
    /// second is 1.2 game minutes at the default 1200-second day).</summary>
    public static string Readout(double time)
    {
        var fraction = SecondsSinceMidnight(time) / WorldGenerationConfig.DayLengthSeconds;
        var totalMinutes = (int)(fraction * 24.0 * 60.0 + 1e-9);
        return $"{totalMinutes / 60:D2}:{totalMinutes % 60:D2}";
    }
}
