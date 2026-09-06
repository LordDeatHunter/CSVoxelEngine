using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Silk.NET.OpenGL;
using StbImageSharp;

namespace silkgltriangle;

/// <summary>
/// Stitches the individual block textures in <c>assets/textures/blocks/*.png</c> into a
/// single grid atlas at startup and uploads it as the world texture. Block types reference
/// textures by file name (see <see cref="BlockType"/>) and resolve to tile coordinates
/// through <see cref="TileOf"/> once the atlas is built.
///
/// Files are packed row-major in sorted file-name order; the grid grows to
/// <c>ceil(sqrt(count))</c> tiles per side, so adding a texture never requires touching
/// coordinates by hand. All source textures must share one size (currently 32x32).
/// Rows are blitted top-first exactly as decoded, so the stitched atlas samples identically
/// to a pre-combined PNG laid out the same way.
///
/// Startup-owned: <see cref="Build"/> runs once on the main thread (GL upload) before the
/// <see cref="World"/> and its generation workers exist; afterwards the layout is read-only
/// and safe to sample from meshing threads.
/// </summary>
public static class TextureAtlas
{
    /// <summary>Tiles per atlas side from the last <see cref="Build"/>; mesh UV math reads this.</summary>
    public static int TilesPerSide { get; private set; } = 4;

    /// <summary>UV span of one tile; kept as a property so it tracks <see cref="TilesPerSide"/>.</summary>
    public static float TileUVSize => 1f / TilesPerSide;

    private static readonly Dictionary<string, BlockType.Point> Tiles = new();

    /// <summary>
    /// Builds the atlas from every <c>*.png</c> in <paramref name="folder"/>, uploads it,
    /// and records each file's tile coordinate under its name without extension.
    /// </summary>
    /// <returns>The uploaded GL texture handle.</returns>
    public static uint Build(GL gl, string folder)
    {
        var files = Directory.GetFiles(folder, "*.png").OrderBy(Path.GetFileName).ToArray();
        if (files.Length == 0)
            throw new InvalidOperationException($"No block textures found in '{folder}'.");

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

        var tilesPerSide = (int)Math.Ceiling(Math.Sqrt(images.Count));
        var atlasWidth = tilesPerSide * tileWidth;
        var atlasHeight = tilesPerSide * tileHeight;
        var atlas = new byte[atlasWidth * atlasHeight * 4];

        Tiles.Clear();
        for (var i = 0; i < images.Count; i++)
        {
            var (name, image) = images[i];
            var tx = i % tilesPerSide;
            var ty = i / tilesPerSide;
            Blit(image.Data, tileWidth, tileHeight, atlas, atlasWidth, tx, ty);
            Tiles[name] = new BlockType.Point(tx, ty);
        }

        TilesPerSide = tilesPerSide;
        return TextureLoader.Upload(gl, atlas, atlasWidth, atlasHeight);
    }

    /// <summary>Tile coordinate of a stitched texture, by file name without extension.</summary>
    public static BlockType.Point TileOf(string name)
    {
        if (!Tiles.TryGetValue(name, out var tile))
            throw new KeyNotFoundException(
                $"Block texture '{name}.png' is not in the stitched atlas. Add the file to assets/textures/blocks/.");
        return tile;
    }

    // Copies one tile's rows into the atlas at grid cell (tx, ty). Both buffers hold
    // top-first RGBA rows, so row r of the source lands on the matching atlas row.
    private static void Blit(byte[] src, int tileWidth, int tileHeight,
        byte[] dst, int atlasWidth, int tx, int ty)
    {
        var rowBytes = tileWidth * 4;
        for (var r = 0; r < tileHeight; r++)
            System.Buffer.BlockCopy(src, r * rowBytes, dst, ((ty * tileHeight + r) * atlasWidth + tx * tileWidth) * 4, rowBytes);
    }
}
