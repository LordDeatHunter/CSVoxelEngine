using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.WorldGen.Sky;

namespace VoxelEngine.Tests.Support;

/// <summary>
/// Finds sky island zones under the current seed for the sky mob tests, so they never pin a coordinate:
/// a zone's center (where its weight is full), or a point of open sky far from every zone.
/// </summary>
internal static class SkyZoneFinder
{
    private const int Step = 160;
    private const int MaxRings = 300;

    /// <summary>The (x, z) center of the nearest zone to the origin, scanning outward, that
    /// <paramref name="accept"/> takes.</summary>
    public static Vector2 Center(Func<SkyZone, bool> accept)
    {
        for (var ring = 0; ring < MaxRings; ring++)
        foreach (var (x, z) in Ring(ring))
        {
            var zone = SkyIslands.ZoneAt(x, z);
            if (!zone.InZone) continue;
            var core = SkyIslands.ZoneAt(zone.CenterX, zone.CenterZ);
            if (core.Weight >= 1f && accept(core)) return new Vector2(core.CenterX, core.CenterZ);
        }
        throw new InvalidOperationException("no sky zone matched");
    }

    /// <summary>A point from which every column within <paramref name="clear"/> blocks lies outside every zone.</summary>
    public static Vector2 OpenSky(float clear)
    {
        for (var ring = 0; ring < MaxRings; ring++)
        foreach (var (x, z) in Ring(ring))
        {
            if (IsOpen(x, z, clear)) return new Vector2(x, z);
        }
        throw new InvalidOperationException("no open sky found");
    }

    private static bool IsOpen(float x, float z, float clear)
    {
        for (var dx = -clear; dx <= clear; dx += 8f)
        for (var dz = -clear; dz <= clear; dz += 8f)
            if (SkyIslands.ZoneAt(x + dx, z + dz).InZone)
                return false;
        return true;
    }

    // The points of the square ring `ring` steps out from the origin
    private static IEnumerable<(float X, float Z)> Ring(int ring)
    {
        if (ring == 0)
        {
            yield return (0f, 0f);
            yield break;
        }
        for (var i = -ring; i < ring; i++)
        {
            yield return (i * Step, -ring * Step);
            yield return (ring * Step, i * Step);
            yield return (-i * Step, ring * Step);
            yield return (-ring * Step, -i * Step);
        }
    }
}
