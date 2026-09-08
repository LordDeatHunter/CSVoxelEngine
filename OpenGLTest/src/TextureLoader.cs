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
    /// Uploads one array texture with a slice per entry of <paramref name="layers"/> (each a
    /// top-first RGBA buffer of <paramref name="width"/> × <paramref name="height"/>), slice
    /// <c>i</c> being <c>layers[i]</c>. Mipmaps are generated per slice, so neighbouring tiles
    /// never bleed into each other at any level.
    /// </summary>
    public static uint UploadArray(GL gl, IReadOnlyList<byte[]> layers, int width, int height)
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
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);

        gl.GenerateMipmap(TextureTarget.Texture2DArray);
        gl.BindTexture(TextureTarget.Texture2DArray, 0);

        return tex;
    }
}
