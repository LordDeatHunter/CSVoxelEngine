using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Silk.NET.OpenGL;
using StbImageSharp;
using VoxelEngine.Content;

namespace VoxelEngine.Graphics;

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

    /// <summary>Pixel size of one tile; every tile shares it (tests, particle UV crops).</summary>
    public static int TileWidth { get; private set; }
    public static int TileHeight { get; private set; }

    /// <summary>Tile names in slice order, so a parallel array texture (see
    /// <see cref="BuildMaterial"/>) can be built against the exact same layout.</summary>
    public static IReadOnlyList<string> TileNames { get; private set; } = [];

    private static readonly Dictionary<string, ushort> Tiles = new();

    /// <summary>The decoded level-0 tiles of the last <see cref="Build"/>, kept only until
    /// <see cref="ApplyCutoutMips"/> has rebuilt the cutout tiles' mip chains from them.</summary>
    private static List<byte[]>? _levelZero;

    /// <summary>
    /// Builds the atlas from every <c>*.png</c> in <paramref name="folder"/>, uploads it,
    /// and records each file's slice under its name without extension. <paramref name="srgb"/>
    /// uploads it as <c>SRGB8_ALPHA8</c> so the world shader samples linear albedo; the game
    /// passes true (it tonemaps), a tool with no post pass passes false.
    /// </summary>
    /// <returns>The uploaded GL texture handle.</returns>
    public static uint Build(GL gl, string folder, bool srgb)
    {
        var (layers, width, height) = Load(folder);
        _levelZero = layers;
        return TextureLoader.UploadArray(gl, layers, width, height, sampleMips: true, srgb: srgb);
    }

    /// <summary>
    /// Overwrites mip levels 1..n of each slice in <paramref name="slices"/> with a
    /// <see cref="CutoutMipmap"/> chain, whose alpha is rescaled per level so the fraction of
    /// texels passing <paramref name="cutoff"/> matches level 0. The box-filtered chain
    /// <c>GenerateMipmap</c> wrote averages leaf alpha below the cutoff, which thins canopies
    /// out with distance now that the atlas samples its mips.
    ///
    /// Main thread only (GL upload); must run after <see cref="Build"/>, and releases the
    /// level-0 copies it kept, so it runs once.
    /// </summary>
    public static void ApplyCutoutMips(GL gl, uint texture, IReadOnlyList<ushort> slices, float cutoff)
    {
        if (_levelZero == null)
            throw new InvalidOperationException("Build the block atlas (once) before applying its cutout mips.");

        gl.BindTexture(TextureTarget.Texture2DArray, texture);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        foreach (var slice in slices)
        {
            var chain = CutoutMipmap.BuildChain(_levelZero[slice], TileWidth, TileHeight, cutoff);
            for (var i = 0; i < chain.Length; i++)
            {
                var level = i + 1;
                var w = Math.Max(1, TileWidth >> level);
                var h = Math.Max(1, TileHeight >> level);
                unsafe
                {
                    fixed (byte* ptr = chain[i])
                    {
                        gl.TexSubImage3D(TextureTarget.Texture2DArray, level, 0, 0, slice,
                            (uint)w, (uint)h, 1, PixelFormat.Rgba, PixelType.UnsignedByte, ptr);
                    }
                }
            }
        }
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4); // GL's default, for every later upload
        gl.BindTexture(TextureTarget.Texture2DArray, 0);
        _levelZero = null;
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
        foreach (var sheet in TextureSheetRegistry.All)
            if (!images.Exists(i => i.Name == sheet.Texture))
                throw new InvalidOperationException($"Spritesheet texture '{sheet.Texture}.png' is not in '{folder}'.");

        // Plain tiles set the atlas tile size; a spritesheet is read as a grid of cells that size.
        var (tileWidth, tileHeight) = TileSizeOf(images);
        var layers = new List<byte[]>(images.Count);
        var names = new List<string>(images.Count);
        Tiles.Clear();
        foreach (var (name, image) in images)
        {
            var sheet = TextureSheetRegistry.TryGet(name);
            if (sheet == null)
            {
                if (image.Width != tileWidth || image.Height != tileHeight)
                    throw new InvalidOperationException(
                        $"Block texture '{name}.png' is {image.Width}x{image.Height}, expected {tileWidth}x{tileHeight}: all atlas tiles must share one size.");
                AddLayer(name, image.Data, layers, names);
                continue;
            }
            if (image.Width != sheet.Cols * tileWidth || image.Height != sheet.Rows * tileHeight)
                throw new InvalidOperationException(
                    $"Spritesheet '{name}.png' is {image.Width}x{image.Height}, expected {sheet.Cols}x{sheet.Rows} cells of {tileWidth}x{tileHeight}.");
            for (var frame = 0; frame < sheet.Frames; frame++)
            {
                // Frame 0 keeps the texture's own name, so a block's face names the first frame; the rest
                // follow it in consecutive layers, which is what the fluid shader steps through.
                var frameName = frame == 0 ? name : $"{name}_frame_{frame:00}";
                AddLayer(frameName, CropCell(image, frame % sheet.Cols, frame / sheet.Cols, tileWidth, tileHeight),
                    layers, names);
            }
        }

        TileNames = names;
        LayerCount = layers.Count;
        TileWidth = tileWidth;
        TileHeight = tileHeight;
        return (layers, tileWidth, tileHeight);
    }

    /// <summary>The atlas tile size: the first plain (non-spritesheet) texture's size, which every plain tile must share.</summary>
    private static (int Width, int Height) TileSizeOf(List<(string Name, ImageResult Image)> images)
    {
        foreach (var (name, image) in images)
            if (TextureSheetRegistry.TryGet(name) == null)
                return (image.Width, image.Height);
        throw new InvalidOperationException("The block textures are all spritesheets: at least one plain tile sets the atlas tile size.");
    }

    private static void AddLayer(string name, byte[] data, List<byte[]> layers, List<string> names)
    {
        Tiles[name] = (ushort)layers.Count;
        layers.Add(data);
        names.Add(name);
    }

    /// <summary>Copies one cell of a spritesheet out as its own tile-sized RGBA layer.</summary>
    private static byte[] CropCell(ImageResult sheet, int col, int row, int tileWidth, int tileHeight)
    {
        var rowBytes = tileWidth * 4;
        var sheetRowBytes = sheet.Width * 4;
        var cell = new byte[rowBytes * tileHeight];
        for (var y = 0; y < tileHeight; y++)
        {
            var source = (row * tileHeight + y) * sheetRowBytes + col * rowBytes;
            System.Buffer.BlockCopy(sheet.Data, source, cell, y * rowBytes, rowBytes);
        }
        return cell;
    }

    /// <summary>
    /// Builds the material array that sits index-parallel to the block atlas: slice <c>i</c>
    /// describes the same tile as atlas slice <c>i</c>, so the world shader's existing
    /// <c>fLayer</c> addresses both and no per-block plumbing is needed to find it.
    ///
    /// A tile is described by <c>&lt;folder&gt;/&lt;tile name&gt;.png</c> when that file exists and by a
    /// flat default otherwise, which is why only the handful of reflective tiles need art.
    /// RGB is a tangent-space normal and A is a metallic multiplier, so the default
    /// <c>(128, 128, 255, 255)</c> means "flat, and take the block's metallic as written".
    ///
    /// Must run after <see cref="Build"/>, whose slice order and tile size it mirrors.
    /// </summary>
    /// <returns>The uploaded GL texture handle.</returns>
    public static uint BuildMaterial(GL gl, string folder)
    {
        if (LayerCount == 0)
            throw new InvalidOperationException("Build the block atlas before its material array.");

        var sliceBytes = TileWidth * TileHeight * 4;
        var layers = new List<byte[]>(LayerCount);

        foreach (var name in TileNames)
        {
            var path = Path.Combine(folder, name + ".png");
            if (!File.Exists(path))
            {
                var flat = new byte[sliceBytes];
                for (var i = 0; i < sliceBytes; i += 4)
                {
                    flat[i] = 128;       // +X of the tangent-space normal, i.e. none
                    flat[i + 1] = 128;   // +Y, none
                    flat[i + 2] = 255;   // +Z, straight out of the face
                    flat[i + 3] = 255;   // full metallic multiplier
                }
                layers.Add(flat);
                continue;
            }

            using var stream = File.OpenRead(path);
            var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            if (image.Width != TileWidth || image.Height != TileHeight)
                throw new InvalidOperationException(
                    $"Material texture '{name}.png' is {image.Width}x{image.Height}, expected " +
                    $"{TileWidth}x{TileHeight}: it must match the block tile it describes.");
            layers.Add(image.Data);
        }

        // Plain RGBA, never sRGB: a tangent-space normal plus a metallic multiplier is data,
        // not color, and an sRGB decode would bend every normal and the metallic scale.
        return TextureLoader.UploadArray(gl, layers, TileWidth, TileHeight, sampleMips: true);
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
