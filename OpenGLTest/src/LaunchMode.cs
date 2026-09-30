using System;

namespace silkgltriangle;

/// <summary>
/// Decides whether a launch goes to the main menu or straight into a storeless world.
/// <see cref="Camera"/> reads VOXEL_SPAWN and VOXEL_LOOK on every construction, and a store-backed
/// world marks its spawn settled from a VOXEL_SPAWN eye, so a dev override must never reach a
/// persisted world: any of the three dev variables skips the menu and builds the storeless world
/// exactly as before (no persistence, no seed change, byte-identical scripted runs). VOXEL_TIME
/// alone does not bypass, because the per-world lighting-clock rebase carries it into a menu world.
/// Camera keeps its own private copies of the variable names on purpose: touching it would risk
/// the byte-identical scripted path.
/// </summary>
public static class LaunchMode
{
    public const string ScriptVariable = "VOXEL_SCRIPT";
    public const string SpawnVariable = "VOXEL_SPAWN";
    public const string LookVariable = "VOXEL_LOOK";

    /// <summary>
    /// True when any dev override is set to something other than whitespace, the same rule
    /// Program and Camera apply when they read those variables.
    /// </summary>
    public static bool BypassesMenu(Func<string, string?> env) =>
        !string.IsNullOrWhiteSpace(env(ScriptVariable))
        || !string.IsNullOrWhiteSpace(env(SpawnVariable))
        || !string.IsNullOrWhiteSpace(env(LookVariable));
}
