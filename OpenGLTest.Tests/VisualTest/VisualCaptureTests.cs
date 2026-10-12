using System;
using System.IO;
using StbImageSharp;
using VoxelEngine.VisualTest;
using Xunit;

namespace VoxelEngine.Tests.VisualTest;

/// <summary>Encode path is GL-free, so the flip + PNG round-trip and the worker drain
/// are covered headless; only the readback needs a context.</summary>
public sealed class VisualCaptureTests
{
    [Fact]
    public void EncodeAndSaveFlipsRowsIntoValidPng()
    {
        // Bottom-first 2x2: red, green on the bottom row; blue, white on top.
        var pixels = new byte[]
        {
            255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 255, 255, 255, 255,
        };
        var path = Path.Combine(Path.GetTempPath(), $"vcap-{Guid.NewGuid():N}.png");
        try
        {
            VisualCapture.EncodeAndSave(pixels, 2, 2, path);

            using var stream = File.OpenRead(path);
            var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            Assert.Equal(2, image.Width);
            Assert.Equal(2, image.Height);
            // PNG rows run top-first: blue, white, red, green.
            Assert.Equal(new byte[]
            {
                0, 0, 255, 255, 255, 255, 255, 255,
                255, 0, 0, 255, 0, 255, 0, 255,
            }, image.Data);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void QueuedSaveDrainsBeforeReturn()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"vcap-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "shot.png");
        try
        {
            VisualCapture.QueueSave(new byte[2 * 2 * 4], 2, 2, path);
            VisualCapture.WaitForPending();
            Assert.Equal(0, VisualCapture.Pending);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(dir)) Directory.Delete(dir);
        }
    }
}
