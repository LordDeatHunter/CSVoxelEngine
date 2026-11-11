using System;
using System.IO;
using VoxelEngine.Content;
using VoxelEngine.Graphics;

namespace VoxelEngine.WikiExport;

/// <summary>
/// Headless exporter for the wiki: <c>dotnet run --project OpenGLTest.WikiExport -- outDir</c>
/// writes one growth file per tree species into <c>outDir</c>.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: VoxelEngine.WikiExport <outDir>");
            return 2;
        }

        var outDir = Path.GetFullPath(args[0]);
        var assets = AssetRoot.Find();
        // Content is opened by the relative path "assets/...", exactly like the game and the server.
        Directory.SetCurrentDirectory(Path.GetDirectoryName(assets)!);
        ContentRegistry.Load(assets);
        TextureAtlas.RegisterTilesWithoutUpload(Path.Combine(assets, "textures", "blocks"));
        ContentRegistry.BakeModels();

        var count = TreeExporter.WriteAll(outDir, WorldSeed.Default);
        Console.WriteLine($"Wrote {count} tree growth files to {outDir}");
        return 0;
    }
}
