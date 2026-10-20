using System;
using System.Globalization;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Player;

/// <summary>
/// The dev launch overrides of the camera's start pose (VOXEL_SPAWN, VOXEL_LOOK) and the surface spawn
/// search they fall back to; parsed by Camera once at startup.
/// </summary>
internal static class SpawnOverrides
{
    // Set to "x,y,z" to start the eye at an exact spot instead of the surface spawn
    private const string SpawnOverrideVariable = "VOXEL_SPAWN";

    // Set to "yaw,pitch" in degrees to start facing an exact direction
    private const string LookOverrideVariable = "VOXEL_LOOK";

    // Yaw 0 looks down +X, so the default -90 faces -Z, level with the horizon
    private static readonly Vector2 DefaultLook = new(-90f, 0f);

    // Eye position standing on the terrain at the origin, so the player never starts buried
    internal static Vector3 SpawnPosition()
    {
        // Dev override: VOXEL_SPAWN="x,y,z" drops the eye exactly there, so a spot worth
        // inspecting (a cave, a ravine) can be looked at without flying out to it first.
        var spawnOverride = Environment.GetEnvironmentVariable(SpawnOverrideVariable);
        if (!string.IsNullOrWhiteSpace(spawnOverride))
        {
            if (TryParseSpawnOverride(spawnOverride, out var overridePosition))
                return overridePosition;

            Log.WriteLine(
                $"Ignoring {SpawnOverrideVariable}=\"{spawnOverride}\": expected three comma-separated numbers, e.g. -40,-107,-75");
        }

        // On the water surface where the origin column is flooded, so the player never starts
        // submerged (or, once swimming exists, sinking)
        var surface = Math.Max(ColumnCache.GetHeights(0, 0)[0], WorldGenerationConfig.SeaLevel) + 1;
        return new Vector3(0.5f, surface + Camera.EyeHeight, 0.5f);
    }

    private static bool TryParseSpawnOverride(string value, out Vector3 position)
    {
        position = Vector3.Zero;

        var parts = value.Split(',');
        if (parts.Length != 3)
            return false;

        Span<float> components = stackalloc float[3];
        for (var i = 0; i < 3; i++)
        {
            if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out components[i]))
                return false;
        }

        position = new Vector3(components[0], components[1], components[2]);
        return true;
    }

    // Facing at startup, as (yaw, pitch) in degrees
    internal static Vector2 SpawnOrientation()
    {
        // Dev override: VOXEL_LOOK="yaw,pitch" pins the view direction, so a scripted
        // verification run frames the same thing every time instead of whatever the mouse
        // happened to be doing.
        var lookOverride = Environment.GetEnvironmentVariable(LookOverrideVariable);
        if (!string.IsNullOrWhiteSpace(lookOverride))
        {
            if (TryParseLookOverride(lookOverride, out var look))
                return look;

            Log.WriteLine(
                $"Ignoring {LookOverrideVariable}=\"{lookOverride}\": expected two comma-separated numbers, e.g. -90,-20");
        }

        return DefaultLook;
    }

    private static bool TryParseLookOverride(string value, out Vector2 look)
    {
        look = DefaultLook;

        var parts = value.Split(',');
        if (parts.Length != 2)
            return false;

        if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var yaw)
            || !float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var pitch))
            return false;

        // Same clamp the mouse look uses, so an override can never start the view past vertical
        look = new Vector2(yaw, MathUtils.ClampPitch(pitch));
        return true;
    }
}
