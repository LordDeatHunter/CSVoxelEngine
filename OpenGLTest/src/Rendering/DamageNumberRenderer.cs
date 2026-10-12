using System;
using System.Globalization;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Data;
using VoxelEngine.Entities;
using VoxelEngine.Graphics;
using VoxelEngine.UI;

namespace VoxelEngine.Rendering;

/// <summary>
/// Draws the floating damage numbers (<see cref="DamageNumberPool"/>) as world-space text that
/// always faces the camera, in the entity pass right after the healthbars. The glyphs are quads
/// laid out from the HUD's own <see cref="BitmapFont"/> atlas (white texels, coverage in alpha),
/// so the healthbar billboard shader draws them as is: tinted per vertex, depth-tested against the
/// terrain without writing depth, and fogged with distance. Each number is a dark drop shadow then
/// its face, far numbers first, all in one draw. Fixed buffers sized by the pool's capacity, so a
/// frame allocates nothing. Main thread only.
/// </summary>
public sealed class DamageNumberRenderer : IDisposable
{
    // Digits drawn per number; a longer amount is cut (no hit comes near it)
    private const int MaxGlyphs = 4;

    // Amounts with a cached string; anything larger is formatted on the spot
    private const int CachedTexts = 1000;

    // The drop shadow sits this many font pixels right and down of the face, like the HUD text's
    private const float ShadowOffsetPx = 1.5f;

    // Authored in sRGB and drawn into the linear HDR target, so the face goes through
    // ColorSpace.SrgbToLinear once here
    private static readonly Vector3 FaceSrgb = new(255f / 255f, 236f / 255f, 150f / 255f);
    private const float ShadowAlpha = 0.75f;

    private readonly GL _gl;
    private readonly BitmapFont _font;
    private readonly Shader _shader;
    private readonly uint _vao, _vbo, _ebo;
    private readonly Vertex[] _verts;
    private readonly int _maxQuads;
    private readonly GlyphInstance[] _glyphs = new GlyphInstance[MaxGlyphs];
    private readonly string?[] _texts = new string?[CachedTexts];
    private readonly float[] _depths;
    private readonly int[] _order;
    private readonly Rgba _face;
    // Height of a digit in the font's baked pixels at scale 1: what DamageNumberCapHeightBlocks maps to
    private readonly float _digitHeightPx;
    private bool _disposed;

    public DamageNumberRenderer(GL gl, BitmapFont font)
    {
        _gl = gl;
        _font = font;
        _shader = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/entity-healthbar-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/entity-healthbar-fragment.glsl"));
        _shader.Use();
        _shader.SetUniform("uSprite", 0);

        var capacity = WorldGenerationConfig.DamageNumberCapacity;
        _maxQuads = capacity * MaxGlyphs * 2;
        _verts = new Vertex[_maxQuads * 4];
        _depths = new float[capacity];
        _order = new int[capacity];
        var linear = ColorSpace.SrgbToLinear(FaceSrgb);
        _face = Rgba.FromFloat(linear.X, linear.Y, linear.Z);
        _digitHeightPx = MathF.Max(1f, font.MeasureSize("0".AsSpan(), 1f).Y);

        var indices = new ushort[_maxQuads * 6];
        UIRenderer.BuildQuadIndices(indices);

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);
        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BufferData<Vertex>(BufferTargetARB.ArrayBuffer,
            (nuint)(_verts.Length * Vertex.Size), _verts, BufferUsageARB.DynamicDraw);
        _ebo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        gl.BufferData<ushort>(BufferTargetARB.ElementArrayBuffer,
            (nuint)(indices.Length * sizeof(ushort)), indices, BufferUsageARB.StaticDraw);
        Mesh.ConfigureVertexAttribs(gl);
        gl.BindVertexArray(0);
    }

    /// <summary>Draws every active number of <paramref name="pool"/>; nothing when it is empty.</summary>
    public void Render(DamageNumberPool pool, Matrix4x4 viewProj, Vector3 cameraPos, Vector3 forward,
        Vector3 fogColor, float fogNear, float fogFar)
    {
        if (pool.Count == 0) return;
        var items = pool.Items;

        // Far to near, so nearer numbers blend over farther ones
        var count = 0;
        for (var i = 0; i < items.Length && count < _order.Length; i++)
        {
            if (!items[i].Active) continue;
            _depths[count] = -Vector3.DistanceSquared(DamageNumberPool.PositionOf(items[i]), cameraPos);
            _order[count] = i;
            count++;
        }
        if (count == 0) return;
        Array.Sort(_depths, _order, 0, count);

        forward.BillboardBasis(out var right, out var up);
        var quads = 0;
        for (var n = 0; n < count; n++)
            quads += WriteNumber(items[_order[n]], right, up, quads);
        if (quads == 0) return;

        _shader.Use();
        _shader.SetUniform("uVP", viewProj);
        _shader.SetUniform("uCameraPos", cameraPos);
        _shader.SetUniform("uFogColor", fogColor);
        _shader.SetUniform("uFogNear", fogNear);
        _shader.SetUniform("uFogFar", fogFar);

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2DArray, _font.Texture);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferSubData<Vertex>(BufferTargetARB.ArrayBuffer, 0, new ReadOnlySpan<Vertex>(_verts, 0, quads * 4));
        unsafe
        {
            _gl.DrawElements(PrimitiveType.Triangles, (uint)(quads * 6), DrawElementsType.UnsignedShort, null);
        }
        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
    }

    // Lays one number out centered on its current position: its shadow quads, then its face quads.
    // Returns how many quads it wrote at firstQuad.
    private int WriteNumber(in DamageNumber number, Vector3 right, Vector3 up, int firstQuad)
    {
        var text = TextFor(number.Amount);
        var n = _font.Layout(text.AsSpan(), Vector2.Zero, 1f, _glyphs);
        if (n == 0 || firstQuad + n * 2 > _maxQuads) return 0;

        var size = _font.MeasureSize(text.AsSpan(), 1f);
        // Blocks per baked font pixel for this number right now
        var k = WorldGenerationConfig.DamageNumberCapHeightBlocks * DamageNumberPool.ScaleOf(number) / _digitHeightPx;
        var anchor = DamageNumberPool.PositionOf(number);
        var alpha = DamageNumberPool.AlphaOf(number);
        var shadow = new Rgba(0, 0, 0, (byte)(MathUtils.Clamp01(alpha * ShadowAlpha) * 255f + 0.5f));
        var face = _face.WithAlpha(alpha);
        var shadowShift = (right - up) * (ShadowOffsetPx * k);

        for (var i = 0; i < n; i++)
            WriteGlyph(firstQuad + i, _glyphs[i], size, anchor + shadowShift, right, up, k, shadow);
        for (var i = 0; i < n; i++)
            WriteGlyph(firstQuad + n + i, _glyphs[i], size, anchor, right, up, k, face);
        return n * 2;
    }

    // One glyph quad: the layout's pixel space (Y down from the run's top-left) centered on the anchor
    // and mapped onto the camera-facing right/up basis, corners in the shared bottom-left, top-left,
    // top-right, bottom-right winding.
    private void WriteGlyph(int quad, GlyphInstance glyph, Vector2 runSize, Vector3 anchor, Vector3 right,
        Vector3 up, float k, Rgba color)
    {
        var center = anchor
                     + right * ((glyph.Center.X - runSize.X * 0.5f) * k)
                     - up * ((glyph.Center.Y - runSize.Y * 0.5f) * k);
        var halfRight = right * (glyph.Size.X * 0.5f * k);
        var halfUp = up * (glyph.Size.Y * 0.5f * k);
        Span<Vector2> uv = stackalloc Vector2[4];
        glyph.Uv.CornersTo(uv);
        var v = quad * 4;
        _verts[v + 0] = new Vertex(center - halfRight - halfUp, color, uv[0], 1f, 0f, Vector3.UnitY);
        _verts[v + 1] = new Vertex(center - halfRight + halfUp, color, uv[1], 1f, 0f, Vector3.UnitY);
        _verts[v + 2] = new Vertex(center + halfRight + halfUp, color, uv[2], 1f, 0f, Vector3.UnitY);
        _verts[v + 3] = new Vertex(center + halfRight - halfUp, color, uv[3], 1f, 0f, Vector3.UnitY);
    }

    private string TextFor(int amount)
    {
        if (amount < 0 || amount >= CachedTexts)
            return amount.ToString(CultureInfo.InvariantCulture);
        return _texts[amount] ??= amount.ToString(CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shader.Dispose();
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
        GC.SuppressFinalize(this);
    }
}
