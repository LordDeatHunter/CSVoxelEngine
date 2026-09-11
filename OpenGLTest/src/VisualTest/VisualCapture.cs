using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// Reads the finished frame back from the GPU and writes it as a PNG. Called at the end of
/// the render callback, so HUD shots include the UI and hidden-HUD shots skip it.
/// Only the readback runs on the render thread: the row flip, PNG encode and file write
/// go to a worker, so a shot costs the frame a pipeline stall instead of the full encode.
/// <see cref="WaitForPending"/> drains in-flight encodes at exit, since the last scripted
/// step is usually a screenshot.
/// </summary>
public static class VisualCapture
{
    private static int _pending;

    /// <summary>Encodes still running on workers; the exit path waits for zero.</summary>
    public static int Pending => Volatile.Read(ref _pending);

    public static unsafe byte[] ReadPixels(GL gl, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        fixed (byte* p = pixels)
            gl.ReadPixels(0, 0, (uint)width, (uint)height,
                PixelFormat.Rgba, PixelType.UnsignedByte, p);
        return pixels;
    }

    public static void QueueSave(byte[] pixels, int width, int height, string path)
    {
        Interlocked.Increment(ref _pending);
        Task.Run(() =>
        {
            try
            {
                EncodeAndSave(pixels, width, height, path);
            }
            catch (Exception e)
            {
                Log.WriteLine($"[visual] screenshot failed ({path}): {e.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        });
    }

    public static void WaitForPending()
    {
        while (Volatile.Read(ref _pending) > 0)
            Thread.Sleep(1);
    }

    internal static unsafe void EncodeAndSave(byte[] pixels, int width, int height, string path)
    {
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
        Log.WriteLine($"[visual] saved {path}");
    }
}
