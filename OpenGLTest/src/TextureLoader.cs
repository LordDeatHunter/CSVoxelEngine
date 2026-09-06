using Silk.NET.OpenGL;
using System.IO;
using StbImageSharp;

namespace silkgltriangle;

public static class TextureLoader
{
    public static uint LoadFromFile(GL gl, string path)
    {
        using var stream = File.OpenRead(path);
        var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        return Upload(gl, image.Data, image.Width, image.Height);
    }

    /// <summary>Uploads a top-first RGBA buffer as a texture, with the same sampling state
    /// every world/UI texture shares (nearest filtering, mipmapped).</summary>
    public static uint Upload(GL gl, byte[] rgba, int width, int height)
    {
        var tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);

        unsafe
        {
            fixed (byte* ptr = rgba)
            {
                gl.TexImage2D(TextureTarget.Texture2D,
                    level: 0,
                    internalformat: (int)InternalFormat.Rgba,
                    (uint)width,
                    (uint)height,
                    border: 0,
                    format: PixelFormat.Rgba,
                    type: PixelType.UnsignedByte,
                    pixels: ptr);
            }
        }

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);

        gl.GenerateMipmap(TextureTarget.Texture2D);
        gl.BindTexture(TextureTarget.Texture2D, 0);

        return tex;
    }
}