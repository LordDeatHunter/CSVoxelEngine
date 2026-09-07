using System;
using System.IO;
using silkgltriangle;

namespace silkgltriangle.Tests;

/// <summary>
/// One-time headless content load shared by every test that names a block or item.
/// Thread-safe and idempotent, so parallel test collections can all call it.
/// BakeModels is GL-free (it only bakes quads), but it settles the occlusion and
/// light-passing flags the mesher reads, so tests need it too.
/// </summary>
internal static class TestContent
{
    private static readonly object Gate = new();
    private static bool _loaded;

    internal static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded) return;
            var root = FindAssetsRoot();
            ContentRegistry.Load(root);
            TextureAtlas.RegisterTilesWithoutUpload(Path.Combine(root, "textures", "blocks"));
            ContentRegistry.BakeModels();
            _loaded = true;
        }
    }

    internal static string FindAssetsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var nextToProject = Path.Combine(dir.FullName, "OpenGLTest", "assets");
            if (Directory.Exists(nextToProject)) return nextToProject;
            if (Directory.Exists(Path.Combine(dir.FullName, "assets", "data")))
                return Path.Combine(dir.FullName, "assets");
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate assets/ walking up from " + AppContext.BaseDirectory);
    }
}
