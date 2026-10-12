using System;
using System.IO;
using VoxelEngine.Saves;

namespace VoxelEngine.UI.Hud;

/// <summary>
/// Where F2's screenshots land, GL-free so the naming is testable: a PNG named for the moment it
/// was taken, under <c>%APPDATA%/CSVoxelEngine/screenshots/</c> — the saves root's own parent, so a
/// shot lives beside the worlds it was taken in.
/// </summary>
public static class ScreenshotPaths
{
    /// <summary>The screenshots folder: beside the saves' own root (its parent plus
    /// <c>screenshots</c>).</summary>
    public static string Directory => Path.Combine(Path.GetDirectoryName(WorldStore.DefaultRoot)!, "screenshots");

    /// <summary>
    /// The file name of a shot taken at <paramref name="now"/>: the local timestamp to the
    /// millisecond, so two shots in the same second never collide.
    /// </summary>
    public static string NameFor(DateTime now) => $"screenshot-{now:yyyyMMdd-HHmmss-fff}.png";
}
