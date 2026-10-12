using System;
using System.Collections.Generic;
using System.IO;

namespace silkgltriangle;

/// <summary>
/// Points the process at the engine's <c>assets</c> directory before any content loads. The
/// engine opens every asset by the relative path <c>assets/...</c>, so the working directory
/// has to contain it. The engine source tree is preferred over a copied build output, because
/// <c>[SAVE]</c> writes item JSON back through that same relative path and a bin copy would be
/// overwritten by the next build.
/// </summary>
public static class AssetRoot
{
    private const int SearchDepth = 8;

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
