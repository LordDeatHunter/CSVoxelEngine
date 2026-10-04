using System;
using System.IO;

namespace silkgltriangle;

/// <summary>
/// Locates the game's <c>assets/</c> directory from the server's working directory. The server is
/// a separate project whose working directory is its own folder (or its build output), so the
/// assets live a few levels up beside the game project.
/// </summary>
public static class AssetRoot
{
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
}
