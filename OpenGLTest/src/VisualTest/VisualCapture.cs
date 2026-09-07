using System;
using System.IO;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// Reads the finished frame back from the GPU and writes it as a PNG. Called at the end of
/// the render callback, so HUD shots include the UI and hidden-HUD shots skip it.
/// </summary>
public static class VisualCapture
{
    public static unsafe void SaveScreenshot(GL gl, int width, int height, string path)
    {
        var pixels = new byte[width * height * 4];
        fixed (byte* p = pixels)
            gl.ReadPixels(0, 0, (uint)width, (uint)height,
                PixelFormat.Rgba, PixelType.UnsignedByte, p);

        // GL rows run bottom-first; PNG rows run top-first.
        var rowBytes = width * 4;
        var row = new byte[rowBytes];
        for (var y = 0; y < height / 2; y++)
        {
            System.Buffer.BlockCopy(pixels, y * rowBytes, row, 0, rowBytes);
            System.Buffer.BlockCopy(pixels, (height - 1 - y) * rowBytes, pixels, y * rowBytes, rowBytes);
            System.Buffer.BlockCopy(row, 0, pixels, (height - 1 - y) * rowBytes, rowBytes);
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        fixed (byte* data = pixels)
        {
            using var stream = File.OpenWrite(path);
            new StbImageWriteSharp.ImageWriter().WritePng(data, width, height,
                StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, stream);
        }
        Console.WriteLine($"[visual] saved {path}");
    }
}
