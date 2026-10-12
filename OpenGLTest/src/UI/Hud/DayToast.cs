namespace VoxelEngine.UI.Hud;

/// <summary>
/// The day watch behind the "Day 4" toast, GL-free: it reports the day number once each time the
/// sun's elevation rises through dawn, so the line appears at daybreak (not at the midnight the
/// day number itself rolls over) and exactly once per day however the clock is jumped.
/// </summary>
public static class DayToast
{
    /// <summary>The sun elevation (its Y) dawn is read at: rising through this shows the line.</summary>
    public const float DawnElevation = -0.02f;

    /// <summary>
    /// Whether a step from <paramref name="previousElevation"/> to <paramref name="elevation"/>
    /// crossed dawn upward: the caller then shows "Day &lt;number&gt;". A jump that lands already
    /// above the line without crossing it (a teleport in time) does not count.
    /// </summary>
    public static bool CrossedDawn(float previousElevation, float elevation) =>
        previousElevation < DawnElevation && elevation >= DawnElevation;
}
