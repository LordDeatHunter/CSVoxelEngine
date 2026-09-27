using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;
using System.IO;
using StbImageSharp;

namespace silkgltriangle;

/// <summary>
/// Uploads textures for the shared <see cref="Vertex"/> layout. Every one of them is a
/// <c>TEXTURE_2D_ARRAY</c> — the block atlas with one slice per tile, everything else with a
/// single slice — so all the shaders sample one sampler type and a merged chunk quad can
/// tile its texture through the sampler's REPEAT instead of atlas arithmetic.
/// </summary>
public static class TextureLoader
{
    public static uint LoadFromFile(GL gl, string path)
    {
        using var stream = File.OpenRead(path);
        var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        return Upload(gl, image.Data, image.Width, image.Height);
    }

    /// <summary>Uploads a top-first RGBA buffer as a single-slice array texture, with the same
    /// sampling state every world/UI texture shares (nearest filtering, mipmapped).</summary>
    public static uint Upload(GL gl, byte[] rgba, int width, int height) =>
        UploadArray(gl, [rgba], width, height);

    /// <summary>
    /// Uploads a grid spritesheet as one slice per frame (row-major from the top-left), so each
    /// frame mipmaps in isolation instead of bleeding into its neighbours. Every failure names
    /// <paramref name="path"/>, like the content errors in <c>ContentRegistry</c>.
    /// </summary>
    public static uint LoadSpritesheet(GL gl, string path, int cols, int rows, int frames)
    {
        using var stream = File.OpenRead(path);
        var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        if (cols < 1 || rows < 1 || frames < 1 || frames > cols * rows)
            throw new InvalidOperationException(
                $"Item '{path}' has a {cols}x{rows} spritesheet using {frames} frames; expected 1..{cols * rows} frames of a positive grid.");
        if (image.Width % cols != 0 || image.Height % rows != 0)
            throw new InvalidOperationException(
                $"Item '{path}' is {image.Width}x{image.Height} but its spritesheet is {cols}x{rows}: cells must divide the texture evenly.");

        var layers = new byte[frames][];
        for (var f = 0; f < frames; f++)
            layers[f] = CropFrame(image.Data, image.Width, image.Height, cols, rows, f);
        return UploadArray(gl, layers, image.Width / cols, image.Height / rows);
    }

    /// <summary>
    /// Copies one row-major cell out of a top-first RGBA buffer: the same crop the UI sheet
    /// split and the item-drop fallback below share.
    /// </summary>
    public static byte[] CropFrame(byte[] rgba, int width, int height, int cols, int rows, int frame)
    {
        var cellWidth = width / cols;
        var cellHeight = height / rows;
        var col = frame % cols;
        var row = frame / cols;
        var cell = new byte[cellWidth * cellHeight * 4];
        for (var y = 0; y < cellHeight; y++)
        {
            var src = ((row * cellHeight + y) * width + col * cellWidth) * 4;
            System.Buffer.BlockCopy(rgba, src, cell, y * cellWidth * 4, cellWidth * 4);
        }
        return cell;
    }

    /// <summary>
    /// Uploads one array texture with a slice per entry of <paramref name="layers"/> (each a
    /// top-first RGBA buffer of <paramref name="width"/> × <paramref name="height"/>), slice
    /// <c>i</c> being <c>layers[i]</c>. Mipmaps are generated per slice, so neighbouring tiles
    /// never bleed into each other at any level.
    ///
    /// With <paramref name="sampleMips"/> false (every sprite, the font, the UI) minification
    /// stays plain <c>Nearest</c>, so the generated chain is never read. The block and material
    /// atlases pass true: minification picks the nearest texel from the two nearest levels and
    /// blends them (<c>NearestMipmapLinear</c>) with anisotropic filtering on top, which stops
    /// distant ground from sparkling while magnification stays <c>Nearest</c> and close blocks
    /// keep their crisp pixels.
    /// </summary>
    public static uint UploadArray(GL gl, IReadOnlyList<byte[]> layers, int width, int height, bool sampleMips = false)
    {
        if (layers.Count == 0)
            throw new ArgumentException("An array texture needs at least one slice.", nameof(layers));

        var sliceBytes = width * height * 4;
        var all = new byte[sliceBytes * layers.Count];
        for (var i = 0; i < layers.Count; i++)
        {
            if (layers[i].Length < sliceBytes)
                throw new ArgumentException($"Slice {i} holds {layers[i].Length} bytes; expected {sliceBytes}.", nameof(layers));
            System.Buffer.BlockCopy(layers[i], 0, all, i * sliceBytes, sliceBytes);
        }

        var tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2DArray, tex);

        unsafe
        {
            fixed (byte* ptr = all)
            {
                gl.TexImage3D(TextureTarget.Texture2DArray,
                    level: 0,
                    internalformat: (int)InternalFormat.Rgba,
                    (uint)width,
                    (uint)height,
                    (uint)layers.Count,
                    border: 0,
                    format: PixelFormat.Rgba,
                    type: PixelType.UnsignedByte,
                    pixels: ptr);
            }
        }

        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter,
            sampleMips ? (int)GLEnum.NearestMipmapLinear : (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        if (sampleMips)
        {
            // Core since GL 4.6, so no extension check; clamp to what the driver supports.
            gl.GetFloat(GLEnum.MaxTextureMaxAnisotropy, out float maxAniso);
            gl.TexParameter(TextureTarget.Texture2DArray, (TextureParameterName)GLEnum.TextureMaxAnisotropy,
                MathF.Min(WorldGenerationConfig.AtlasAnisotropy, maxAniso));
        }

        gl.GenerateMipmap(TextureTarget.Texture2DArray);
        gl.BindTexture(TextureTarget.Texture2DArray, 0);

        return tex;
    }
}
