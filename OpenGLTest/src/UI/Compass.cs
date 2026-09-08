using System;

namespace silkgltriangle;

/// <summary>
/// Pure heading math behind the HUD compass (see <see cref="UIRenderer.DrawCompass"/>):
/// yaw-to-heading mapping, spawn bearings and cardinal names. No GL calls, so headless
/// tests cover it here instead of through a window.
/// </summary>
public static class Compass
{
    /// <summary>World XZ the spawn marker points at: the centre of the origin column,
    /// matching the eye XZ of <c>Camera.SpawnPosition</c>.</summary>
    public const float SpawnX = 0.5f;
    public const float SpawnZ = 0.5f;

    private static readonly string[] Cardinals = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    /// <summary>
    /// Compass heading in degrees, 0 = North (-Z) running clockwise, so 90 = East (+X).
    /// Yaw 0 looks down +X, so the heading leads the yaw by 90 degrees.
    /// </summary>
    public static float HeadingFromYaw(float yaw) => ((yaw + 90f) % 360f + 360f) % 360f;

    /// <summary>Bearing from one XZ point to another in the same 0 = North, clockwise convention.</summary>
    public static float BearingTo(float fromX, float fromZ, float toX, float toZ)
    {
        var bearing = MathF.Atan2(toX - fromX, -(toZ - fromZ)) * (180f / MathF.PI);
        return ((bearing % 360f) + 360f) % 360f;
    }

    /// <summary>Signed shortest turn between two headings, in -180..180 degrees.</summary>
    public static float WrapDelta(float degrees) => ((degrees + 540f) % 360f) - 180f;

    /// <summary>Nearest of N/NE/E/SE/S/SW/W/NW for a heading in degrees.</summary>
    public static string CardinalName(float heading)
    {
        var index = ((int)MathF.Round(heading / 45f) % 8 + 8) % 8;
        return Cardinals[index];
    }
}
