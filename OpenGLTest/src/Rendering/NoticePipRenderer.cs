using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Data;
using VoxelEngine.Entities;
using VoxelEngine.Graphics;
using VoxelEngine.UI;

namespace VoxelEngine.Rendering;

/// <summary>
/// Draws the "!" a goblin pops over its head the moment it first notices a player, through the
/// healthbar billboard shader and the HUD's own <see cref="BitmapFont"/> atlas (like the damage
/// numbers): a dark drop shadow then a bright face, scaling in with an <see cref="MathUtils.EaseOutBack"/>
/// pop and fading out, depth-tested without writing depth and fogged with distance. One draw,
/// fixed buffers sized by <see cref="WorldGenerationConfig.GoblinNoticePipMax"/>. Main thread only.
/// </summary>
public sealed class NoticePipRenderer : IDisposable
{
    private const string Pip = "!";

    // The drop shadow sits this many font pixels right and down of the face, like the HUD text's
    private const float ShadowOffsetPx = 1.5f;
    private const float ShadowAlpha = 0.75f;

    // Authored in sRGB and drawn into the linear HDR target, so the face goes through
    // ColorSpace.SrgbToLinear once here
    private static readonly Vector3 FaceSrgb = new(1f, 0.92f, 0.3f);

    // Where the pop ends (its EaseOutBack scale) and where the fade starts, as shares of the pip
    private const float PopShare = 0.3f;
    private const float FadeFrom = 0.55f;

    private readonly GL _gl;
    private readonly BitmapFont _font;
    private readonly Shader _shader;
    private readonly uint _vao, _vbo, _ebo;
    private readonly Vertex[] _verts;
    private readonly int _maxQuads;
    private readonly GlyphInstance[] _glyphs = new GlyphInstance[4];
    private readonly Rgba _face;
    // Height of the glyph in the font's baked pixels at scale 1
    private readonly float _glyphHeightPx;
    private bool _disposed;

    public NoticePipRenderer(GL gl, BitmapFont font)
    {
        _gl = gl;
        _font = font;
        _shader = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/entity-healthbar-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/entity-healthbar-fragment.glsl"));
        _shader.Use();
        _shader.SetUniform("uSprite", 0);

        _maxQuads = WorldGenerationConfig.GoblinNoticePipMax * 2;
        _verts = new Vertex[_maxQuads * 4];
        var linear = ColorSpace.SrgbToLinear(FaceSrgb);
        _face = Rgba.FromFloat(linear.X, linear.Y, linear.Z);
        _glyphHeightPx = MathF.Max(1f, font.MeasureSize(Pip.AsSpan(), 1f).Y);

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

    /// <summary>Draws a pip over every goblin still inside its pip window; nothing when none is.</summary>
    public void Render(IReadOnlyList<Goblin> goblins, Matrix4x4 viewProj, Vector3 cameraPos, Vector3 forward,
        Vector3 fogColor, float fogNear, float fogFar)
    {
        if (goblins.Count == 0) return;
        var n = _font.Layout(Pip.AsSpan(), Vector2.Zero, 1f, _glyphs);
        if (n == 0) return;
        var size = _font.MeasureSize(Pip.AsSpan(), 1f);

        forward.BillboardBasis(out var right, out var up);
        var quads = 0;
        for (var i = 0; i < goblins.Count; i++)
        {
            var goblin = goblins[i];
            var progress = goblin.NoticePipProgress;
            if (progress >= 1f || goblin.IsDying) continue;
            if (quads + n * 2 > _maxQuads) break;
            quads += WritePip(goblin, progress, size, right, up, quads);
        }
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

    // One goblin's pip: the pop scale and the fade from its own progress, anchored above the head.
    // Returns how many quads it wrote at firstQuad.
    private int WritePip(Goblin goblin, float progress, Vector2 runSize, Vector3 right, Vector3 up, int firstQuad)
    {
        var scale = MathUtils.EaseOutBack(progress / PopShare);
        var alpha = 1f - MathUtils.SmoothStep(MathUtils.InverseLerp(FadeFrom, 1f, progress));
        var k = WorldGenerationConfig.GoblinNoticePipCapHeightBlocks * scale / _glyphHeightPx;
        var anchor = new Vector3(goblin.Position.X,
            goblin.Position.Y + goblin.HeadAboveCentre + WorldGenerationConfig.GoblinNoticePipRiseBlocks
            + (scale - 1f) * 0.15f, goblin.Position.Z);
        var shadow = new Rgba(0, 0, 0, (byte)(MathUtils.Clamp01(alpha * ShadowAlpha) * 255f + 0.5f));
        var face = _face.WithAlpha(alpha);
        var shadowShift = (right - up) * (ShadowOffsetPx * k);

        var glyph = _glyphs[0];
        WriteGlyph(firstQuad, glyph, runSize, anchor + shadowShift, right, up, k, shadow);
        WriteGlyph(firstQuad + 1, glyph, runSize, anchor, right, up, k, face);
        return 2;
    }

    // One glyph quad: the layout's pixel space (Y down from the run's top-left) centred on the anchor
    // and mapped onto the camera-facing right/up basis, corners in the shared bottom-left, top-left,
    // top-right, bottom-right winding.
    private void WriteGlyph(int quad, GlyphInstance glyph, Vector2 runSize, Vector3 anchor, Vector3 right,
        Vector3 up, float k, Rgba color)
    {
        var centre = anchor
                     + right * ((glyph.Centre.X - runSize.X * 0.5f) * k)
                     - up * ((glyph.Centre.Y - runSize.Y * 0.5f) * k);
        var halfRight = right * (glyph.Size.X * 0.5f * k);
        var halfUp = up * (glyph.Size.Y * 0.5f * k);
        Span<Vector2> uv = stackalloc Vector2[4];
        glyph.Uv.CornersTo(uv);
        var v = quad * 4;
        _verts[v + 0] = new Vertex(centre - halfRight - halfUp, color, uv[0], 1f, 0f, Vector3.UnitY);
        _verts[v + 1] = new Vertex(centre - halfRight + halfUp, color, uv[1], 1f, 0f, Vector3.UnitY);
        _verts[v + 2] = new Vertex(centre + halfRight + halfUp, color, uv[2], 1f, 0f, Vector3.UnitY);
        _verts[v + 3] = new Vertex(centre + halfRight - halfUp, color, uv[3], 1f, 0f, Vector3.UnitY);
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
