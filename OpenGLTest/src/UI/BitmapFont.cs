using System;
using System.IO;
using System.Numerics;
using Silk.NET.OpenGL;
using StbTrueTypeSharp;

namespace silkgltriangle;

/// <summary>
/// One screen-space draw of a single glyph: which baked mesh to draw and where. Produced by
/// <see cref="BitmapFont.Layout"/>; the caller draws each through the UI shader.
/// </summary>
public readonly record struct GlyphInstance(Mesh Mesh, Vector2 Centre, Vector2 Size);

/// <summary>
/// A TrueType font baked once into a GL atlas, for crisp screen-space text. The ASCII
/// printable range is rasterized with <c>stbtt_BakeFontBitmap</c> and each non-empty glyph
/// gets a unit-rect mesh carrying its atlas UVs, so drawing text is just one textured quad
/// per character through the existing UI shader — no per-frame buffer work.
///
/// The atlas holds white texels with coverage in alpha, so the draw tint fully controls the
/// text colour (see the ui-fragment shader). Owned and drawn by <see cref="UIRenderer"/>.
/// </summary>
public sealed class BitmapFont : IDisposable
{
    public const int FirstChar = 32;
    public const int NumChars = 96;

    private const float BakePixelHeight = 32f;
    private const int MinAtlasSide = 512;
    private const int MaxAtlasSide = 2048;
    private const int MaxLayoutChars = 64;

    private readonly GL _gl;
    private readonly StbTrueType.stbtt_bakedchar[] _baked = new StbTrueType.stbtt_bakedchar[NumChars];
    private readonly Mesh?[] _meshes = new Mesh?[NumChars];
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
                BuildGlyphMeshes(gl, side);
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
        Span<CharQuad> quads = stackalloc CharQuad[MaxLayoutChars];
        var n = LayoutQuads(text, scale, quads, out var width, out var minY, out var maxY);
        return n == 0 ? Vector2.Zero : new Vector2(width, maxY - minY);
    }

    /// <summary>
    /// Lays <paramref name="text"/> out with its top-left corner at <paramref name="topLeft"/>
    /// (screen pixels, Y down). Writes up to <paramref name="outInstances"/>'s length and
    /// returns how many were written. Unknown and empty glyphs (like space) advance the pen
    /// but emit nothing.
    /// </summary>
    public int Layout(ReadOnlySpan<char> text, Vector2 topLeft, float scale, Span<GlyphInstance> outInstances)
    {
        Span<CharQuad> quads = stackalloc CharQuad[MaxLayoutChars];
        var n = LayoutQuads(text, scale, quads, out _, out var minY, out _);
        var count = Math.Min(n, outInstances.Length);

        for (var i = 0; i < count; i++)
        {
            var q = quads[i];
            var centre = new Vector2(
                topLeft.X + (q.X0 + q.X1) * 0.5f,
                topLeft.Y + (q.Y0 + q.Y1) * 0.5f - minY);
            outInstances[i] = new GlyphInstance(_meshes[q.Index]!, centre, new Vector2(q.X1 - q.X0, q.Y1 - q.Y0));
        }

        return count;
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

    // One unit rect per non-empty glyph in the same UV convention the chunk mesher uses
    // (Min is the top-left of the tile), so the existing UI transform draws them upright.
    private void BuildGlyphMeshes(GL gl, int side)
    {
        for (var i = 0; i < NumChars; i++)
        {
            var b = _baked[i];
            if (b.x1 <= b.x0 || b.y1 <= b.y0)
                continue;

            var uv = new UvRect(
                new Vector2(b.x0 / (float)side, b.y0 / (float)side),
                new Vector2(b.x1 / (float)side, b.y1 / (float)side));
            _meshes[i] = Primitives.CreateRectangle(gl, 1f, 1f, Rgba.White, uv);
        }
    }

    // Quads in baked pixels at scale, with the pen starting at the origin and Y growing
    // down from the baseline. stbtt_GetBakedQuad mutates only the pen's X (no line breaks
    // here); the caller anchors vertically from minY/maxY.
    private unsafe int LayoutQuads(ReadOnlySpan<char> text, float scale, Span<CharQuad> quads,
        out float width, out float minY, out float maxY)
    {
        width = 0f;
        minY = 0f;
        maxY = 0f;

        var n = 0;
        var x = 0f;
        const float y = 0f;
        var limit = Math.Min(text.Length, quads.Length);

        fixed (StbTrueType.stbtt_bakedchar* baked = _baked)
        {
            for (var i = 0; i < limit; i++)
            {
                var index = text[i] - FirstChar;
                if (index < 0 || index >= NumChars)
                    continue;

                StbTrueType.stbtt_aligned_quad q;
                var qx = x;
                var qy = y;
                StbTrueType.stbtt_GetBakedQuad(baked, _atlasSide, _atlasSide, index, &qx, &qy, &q, 1);
                x = qx;

                if (_meshes[index] == null)
                    continue;

                var quad = new CharQuad(q.x0 * scale, q.y0 * scale, q.x1 * scale, q.y1 * scale, index);
                if (n == 0)
                {
                    minY = quad.Y0;
                    maxY = quad.Y1;
                }
                else
                {
                    minY = Math.Min(minY, quad.Y0);
                    maxY = Math.Max(maxY, quad.Y1);
                }

                quads[n++] = quad;
            }
        }

        width = x * scale;
        return n;
    }

    private readonly record struct CharQuad(float X0, float Y0, float X1, float Y1, int Index);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var mesh in _meshes)
            mesh?.Dispose();
        _gl.DeleteTexture(_texture);

        GC.SuppressFinalize(this);
    }
}
