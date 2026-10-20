using System;
using System.Collections.Generic;
using System.IO;

namespace VoxelEngine.Core;

/// <summary>
/// Finds the engine's <c>assets</c> directory for the programs that do not run from the game
/// project's folder: the dedicated server, the item debugger and the test fixture. Each keeps its
/// own search rule. The game itself opens assets relative to its working directory and never
/// calls this.
/// </summary>
public static class AssetRoot
{
    private const int SearchDepth = 8;

    /// <summary>
    /// Locates the game's <c>assets/</c> directory for the dedicated server and the test fixture.
    /// Both are separate projects run from their own folder or build output, so the assets live a
    /// few levels up beside the game project. Walks up from <see cref="AppContext.BaseDirectory"/>
    /// and at each level returns <c>OpenGLTest/assets</c>, or an <c>assets</c> folder holding
    /// <c>data</c> or <c>models</c>. Throws <see cref="DirectoryNotFoundException"/> when no level
    /// matches.
    /// </summary>
    public static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var nextToGame = Path.Combine(dir.FullName, "OpenGLTest", "assets");
            if (Directory.Exists(nextToGame))
                return nextToGame;
            if (Directory.Exists(Path.Combine(dir.FullName, "assets", "data")))
                return Path.Combine(dir.FullName, "assets");
            if (Directory.Exists(Path.Combine(dir.FullName, "assets", "models")))
                return Path.Combine(dir.FullName, "assets");
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate assets/ walking up from " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Points the process at the engine's <c>assets</c> directory before any content loads (the
    /// item debugger). The engine opens every asset by the relative path <c>assets/...</c>, so the
    /// working directory has to contain it. Searches the working directory, then the base
    /// directory, each up to <c>SearchDepth</c> levels up. The engine source tree is preferred over
    /// a copied build output, because <c>[SAVE]</c> writes item JSON back through that same
    /// relative path and a bin copy would be overwritten by the next build. Leaves the working
    /// directory unchanged when nothing matches.
    /// </summary>
    public static void UseAssetsDirectory()
    {
        foreach (var directory in DirectoriesWithAssets())
        {
            Directory.SetCurrentDirectory(directory);
            return;
        }
        // Left as-is: ContentRegistry.Load names the missing folder and fails loudly.
    }

    private static IEnumerable<string> DirectoriesWithAssets()
    {
        // Source tree first (it owns VoxelEngine.csproj), so saves persist across builds.
        foreach (var candidate in Candidates())
        {
            if (IsEngineSource(candidate)) yield return candidate;
            var engine = Path.Combine(candidate, "OpenGLTest");
            if (IsEngineSource(engine)) yield return engine;
        }

        // Fall back to any copied assets (e.g. a build output run without the source nearby).
        foreach (var candidate in Candidates())
        {
            if (HasAssets(candidate)) yield return candidate;
            var engine = Path.Combine(candidate, "OpenGLTest");
            if (HasAssets(engine)) yield return engine;
        }
    }

    private static bool IsEngineSource(string directory) =>
        File.Exists(Path.Combine(directory, "VoxelEngine.csproj")) && HasAssets(directory);

    private static bool HasAssets(string directory) =>
        File.Exists(Path.Combine(directory, "assets", "data", "items", "bucket.json"));

    private static IEnumerable<string> Candidates()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = start;
            for (var i = 0; i < SearchDepth && !string.IsNullOrEmpty(dir); i++)
            {
                yield return dir;
                dir = Path.GetDirectoryName(dir);
            }
        }
    }
}
