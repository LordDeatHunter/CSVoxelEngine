using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Silk.NET.OpenGL;
using StbImageSharp;

namespace silkgltriangle;

/// <summary>
/// Loads the individual block textures in <c>assets/textures/blocks/*.png</c> into one
/// <c>TEXTURE_2D_ARRAY</c> at startup — one slice per file, in sorted file-name order — and
/// uploads it as the world texture. Block types reference textures by file name (see
/// <see cref="BlockType"/>) and resolve to a slice through <see cref="TileOf"/> once the atlas
/// is built. Because every tile is its own slice, UVs are plain 0..1 (or 0..N for a quad that
/// tiles) and the sampler's REPEAT wraps them; nothing ever bleeds across tiles, at any mip
/// level. All source textures must share one size (currently 32x32). Rows are uploaded
/// top-first exactly as decoded.
///
/// Startup-owned: <see cref="Build"/> runs once on the main thread (GL upload) before the
/// <see cref="World"/> and its generation workers exist; afterwards the layout is read-only
/// and safe to read from meshing threads.
/// </summary>
public static class TextureAtlas
{
    /// <summary>Number of slices in the last built atlas (tests, diagnostics).</summary>
    public static int LayerCount { get; private set; }

    private static readonly Dictionary<string, ushort> Tiles = new();

    /// <summary>
    /// Builds the atlas from every <c>*.png</c> in <paramref name="folder"/>, uploads it,
    /// and records each file's slice under its name without extension.
    /// </summary>
    /// <returns>The uploaded GL texture handle.</returns>
    public static uint Build(GL gl, string folder)
    {
        var (layers, width, height) = Load(folder);
        return TextureLoader.UploadArray(gl, layers, width, height);
    }

    /// <summary>
    /// Test seam: decodes the tiles and records the slice layout without uploading, so
    /// headless tests bake models against the exact layout the game uses.
    /// </summary>
    public static void RegisterTilesWithoutUpload(string folder) => Load(folder);

    private static (List<byte[]> Layers, int Width, int Height) Load(string folder)
    {
        var files = Directory.GetFiles(folder, "*.png").OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
        if (files.Length == 0)
            throw new InvalidOperationException($"No block textures found in '{folder}'.");
        if (files.Length > ushort.MaxValue)
            throw new InvalidOperationException($"{files.Length} block textures exceed the {ushort.MaxValue} slices a Vertex.Layer can address.");

        var images = new List<(string Name, ImageResult Image)>(files.Length);
        foreach (var file in files)
        {
            using var stream = File.OpenRead(file);
            var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            images.Add((Path.GetFileNameWithoutExtension(file), image));
        }

        var tileWidth = images[0].Image.Width;
        var tileHeight = images[0].Image.Height;
        foreach (var (name, image) in images)
            if (image.Width != tileWidth || image.Height != tileHeight)
                throw new InvalidOperationException(
                    $"Block texture '{name}.png' is {image.Width}x{image.Height}, expected {tileWidth}x{tileHeight}: all atlas tiles must share one size.");

        Tiles.Clear();
        var layers = new List<byte[]>(images.Count);
        for (var i = 0; i < images.Count; i++)
        {
            var (name, image) = images[i];
            layers.Add(image.Data);
            Tiles[name] = (ushort)i;
        }

        LayerCount = layers.Count;
        return (layers, tileWidth, tileHeight);
    }

    /// <summary>Slice of a block texture, by file name without extension.</summary>
    public static ushort TileOf(string name)
    {
        if (!Tiles.TryGetValue(name, out var tile))
            throw new KeyNotFoundException(
                $"Block texture '{name}.png' is not in the atlas. Add the file to assets/textures/blocks/.");
        return tile;
    }
}
