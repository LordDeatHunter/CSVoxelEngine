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

        var tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);

        unsafe
        {
            fixed (byte* ptr = image.Data)
            {
                gl.TexImage2D(TextureTarget.Texture2D,
                    level: 0,
                    internalformat: (int)InternalFormat.Rgba,
                    (uint)image.Width,
                    (uint)image.Height,
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