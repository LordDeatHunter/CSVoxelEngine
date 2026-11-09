using System;
using System.IO;
using System.Numerics;
using Silk.NET.OpenGL;
using StbTrueTypeSharp;
using VoxelEngine.Data;
using VoxelEngine.Graphics;
using VoxelEngine.Rendering.Overlays;

namespace VoxelEngine.UI;

/// <summary>
/// One screen-space draw of a single glyph: its atlas UV rect and where to place it. Produced by
/// <see cref="BitmapFont.Layout"/>; the caller batches every glyph of a run into one dynamic
/// vertex buffer and draws it through the UI shader in a single call (see
/// <see cref="UIRenderer.DrawText(string, Vector2, float, Rgba)"/>).
/// </summary>
public readonly record struct GlyphInstance(Vector2 Center, Vector2 Size, UvRect Uv);

/// <summary>
/// A TrueType font baked once into a GL atlas, for crisp screen-space text. The ASCII
/// printable range is rasterized with <c>stbtt_BakeFontBitmap</c> and each non-empty glyph's
/// atlas UV rect is cached, so laying out text is pure CPU work — the caller turns the result
/// into one batched draw instead of a per-glyph mesh and draw call.
///
/// The atlas holds white texels with coverage in alpha, so the draw tint fully controls the
/// text color (see the ui-fragment shader). Owned and drawn by <see cref="UIRenderer"/>.
/// </summary>
public sealed class BitmapFont : IDisposable
{
    public const int FirstChar = 32;
    public const int NumChars = 96;

    private const float BakePixelHeight = 32f;
    private const int MinAtlasSide = 512;
    private const int MaxAtlasSide = 2048;

    private readonly GL _gl;
    private readonly StbTrueType.stbtt_bakedchar[] _baked = new StbTrueType.stbtt_bakedchar[NumChars];
    // Null for glyphs with no ink (space and friends); Layout skips them.
    private readonly UvRect?[] _glyphUv = new UvRect?[NumChars];
    private readonly int _atlasSide;
    private readonly uint _texture;
    private bool _disposed;

    /// <summary>The baked atlas; passed to the UI draw as the text texture.</summary>
    public uint Texture => _texture;

    public BitmapFont(GL gl, string path)
    {
        _gl = gl;

        var ttf = File.ReadAllBytes(path);
        var side = MinAtlasSide;
        while (true)
        {
            var bitmap = new byte[side * side];
            Array.Clear(_baked, 0, _baked.Length);
            if (TryBake(ttf, side, bitmap))
            {
                _atlasSide = side;
                _texture = Upload(gl, bitmap, side);
                ComputeGlyphUvs(side);
                return;
            }

            side *= 2;
            if (side > MaxAtlasSide)
                throw new InvalidOperationException(
                    $"Font '{path}' does not fit in a {MaxAtlasSide}x{MaxAtlasSide} atlas at {BakePixelHeight}px.");
        }
    }

    /// <summary>
    /// Draw scale mapping the digit cap-height to <paramref name="pixels"/> on screen, so
    /// callers size text by its actual digit height instead of the font's em size.
    /// </summary>
    public float ScaleForCapHeight(float pixels)
    {
        var digit = _baked['0' - FirstChar];
        return pixels / Math.Max(1, digit.y1 - digit.y0);
    }

    /// <summary>Pixel size of <paramref name="text"/> at <paramref name="scale"/>, for anchoring.</summary>
    public Vector2 MeasureSize(ReadOnlySpan<char> text, float scale)
    {
        var n = Scan(text, scale, default, out var width, out var minY, out var maxY);
        return n == 0 ? Vector2.Zero : new Vector2(width, maxY - minY);
    }

    /// <summary>
    /// Lays <paramref name="text"/> out with its top-left corner at <paramref name="topLeft"/>
    /// (screen pixels, Y down). Writes up to <paramref name="outInstances"/>'s length and
    /// returns how many were written (size it to the text length to draw the text whole).
    /// Unknown and empty glyphs (like space) advance the pen but emit nothing.
    /// </summary>
    public int Layout(ReadOnlySpan<char> text, Vector2 topLeft, float scale, Span<GlyphInstance> outInstances)
    {
        // The ink top anchors the run, so a first pass finds it and the second writes the instances.
        Scan(text, scale, default, out _, out var minY, out _);
        return Scan(text, scale, outInstances, out _, out _, out _, topLeft, minY);
    }

    /// <summary>
    /// Lays <paramref name="text"/> out starting at <paramref name="left"/> with the digit cap box
    /// centered on <paramref name="centerY"/>. Unlike <see cref="Layout"/> the vertical place does not
    /// depend on which glyphs the string holds (no ascender or descender shifts it), so one-line
    /// labels in a box all center by the same rule.
    /// </summary>
    public int LayoutCentered(ReadOnlySpan<char> text, float left, float centerY, float scale,
        Span<GlyphInstance> outInstances)
    {
        var digit = _baked['0' - FirstChar];
        var capCenter = (digit.yoff + (digit.y1 - digit.y0) * 0.5f) * scale;
        return Scan(text, scale, outInstances, out _, out _, out _, new Vector2(left, centerY), capCenter);
    }

    // Bakes the printable ASCII range into bitmap (top-first coverage bytes). The bake
    // wrapper reports success on partial overflows (a negative row count still compares
    // nonzero), so every core glyph must be confirmed present — anything missing means the
    // atlas is too small and the caller retries larger. Space is legitimately empty.
    private bool TryBake(byte[] ttf, int side, byte[] bitmap)
    {
        if (!StbTrueType.stbtt_BakeFontBitmap(ttf, 0, BakePixelHeight, bitmap, side, side,
                FirstChar, NumChars, _baked))
            return false;

        foreach (var c in "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz")
        {
            var b = _baked[c - FirstChar];
            if (b.x1 <= b.x0 || b.y1 <= b.y0)
                return false;
        }

        return true;
    }

    // White texels with coverage in alpha; clamped with plain linear filtering and no
    // mipmaps, so small pixel text stays smooth instead of bleeding into empty atlas space.
    private static uint Upload(GL gl, byte[] bitmap, int side)
    {
        var rgba = new byte[side * side * 4];
        for (var i = 0; i < bitmap.Length; i++)
        {
            var o = i * 4;
            rgba[o] = rgba[o + 1] = rgba[o + 2] = 255;
            rgba[o + 3] = bitmap[i];
        }

        // Every texture the shared Vertex layout samples is an array texture (see
        // TextureLoader); the glyph atlas is a single slice with its own smooth, clamped sampling
        var texture = TextureLoader.Upload(gl, rgba, side, side);
        gl.BindTexture(TextureTarget.Texture2DArray, texture);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.BindTexture(TextureTarget.Texture2DArray, 0);

        return texture;
    }

    // One atlas UV rect per non-empty glyph, in the same convention the chunk mesher uses
    // (Min is the top-left of the tile), so the existing UI transform draws them upright.
    private void ComputeGlyphUvs(int side)
    {
        for (var i = 0; i < NumChars; i++)
        {
            var b = _baked[i];
            if (b.x1 <= b.x0 || b.y1 <= b.y0)
                continue;

            _glyphUv[i] = new UvRect(
                new Vector2(b.x0 / (float)side, b.y0 / (float)side),
                new Vector2(b.x1 / (float)side, b.y1 / (float)side));
        }
    }

    // Walks the text with the pen starting at the origin and Y growing down from the baseline
    // (stbtt_GetBakedQuad mutates only the pen's X; there are no line breaks here), counting every
    // inked glyph and tracking the ink's vertical extent. With a non-empty `instances` it also
    // writes each glyph (at most that many) placed from topLeft, shifted up by anchorY; the return
    // value is the number of glyphs counted or written.
    private unsafe int Scan(ReadOnlySpan<char> text, float scale, Span<GlyphInstance> instances,
        out float width, out float minY, out float maxY, Vector2 topLeft = default, float anchorY = 0f)
    {
        minY = 0f;
        maxY = 0f;

        var n = 0;
        var x = 0f;
        var write = !instances.IsEmpty;

        fixed (StbTrueType.stbtt_bakedchar* baked = _baked)
        {
            for (var i = 0; i < text.Length; i++)
            {
                var index = text[i] - FirstChar;
                if (index < 0 || index >= NumChars)
                    continue;

                StbTrueType.stbtt_aligned_quad q;
                var qx = x;
                var qy = 0f;
                StbTrueType.stbtt_GetBakedQuad(baked, _atlasSide, _atlasSide, index, &qx, &qy, &q, 1);
                x = qx;

                if (_glyphUv[index] == null)
                    continue;

                var x0 = q.x0 * scale;
                var x1 = q.x1 * scale;
                var y0 = q.y0 * scale;
                var y1 = q.y1 * scale;
                if (n == 0)
                {
                    minY = y0;
                    maxY = y1;
                }
                else
                {
                    minY = Math.Min(minY, y0);
                    maxY = Math.Max(maxY, y1);
                }

                if (write)
                {
                    if (n >= instances.Length)
                        break;
                    instances[n] = new GlyphInstance(
                        new Vector2(topLeft.X + (x0 + x1) * 0.5f, topLeft.Y + (y0 + y1) * 0.5f - anchorY),
                        new Vector2(x1 - x0, y1 - y0), _glyphUv[index]!.Value);
                }

                n++;
            }
        }

        width = x * scale;
        return n;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _gl.DeleteTexture(_texture);

        GC.SuppressFinalize(this);
    }
}
