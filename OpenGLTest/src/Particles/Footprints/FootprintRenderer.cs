using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Data;
using VoxelEngine.Graphics;

namespace VoxelEngine.Particles.Footprints;

/// <summary>
/// Draws every live <see cref="Footprint"/> as a flat quad lying on the ground in one dynamic draw,
/// sampling <c>assets/textures/particles/footprint.png</c> (the shape is its alpha) tinted dark and faded per
/// print. Depth-tested with a slight polygon offset and no depth write. Main thread only.
/// </summary>
public sealed class FootprintRenderer : IDisposable
{
    /// <summary>The print sprite, a single-slice array texture like every other sprite.</summary>
    public const string SpritePath = "assets/textures/particles/footprint.png";

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;
    private readonly uint _texture;
    private readonly Vertex[] _verts = new Vertex[FootprintConfig.Max * 4];
    private bool _disposed;

    // The config's sRGB tint as linear bytes: the scene target is linear light, so a dark sRGB value
    // written raw would come out far too bright
    private static readonly (byte R, byte G, byte B) Tint = LinearTint();

    private static (byte, byte, byte) LinearTint()
    {
        var linear = ColorSpace.SrgbToLinear(new Vector3(FootprintConfig.TintR, FootprintConfig.TintG,
            FootprintConfig.TintB) / 255f);
        return ((byte)(linear.X * 255f + 0.5f), (byte)(linear.Y * 255f + 0.5f), (byte)(linear.Z * 255f + 0.5f));
    }

    // Back-left, front-left, front-right, back-right (FootprintRules.Corners); V=0 is the sprite's top, its toe
    private static readonly Vector2[] Uvs = [new(0f, 1f), new(0f, 0f), new(1f, 0f), new(1f, 1f)];

    public FootprintRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/footprint-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/footprint-fragment.glsl"));
        _shader.Use();
        _shader.SetUniform("uSprite", 0);
        _texture = TextureLoader.LoadFromFile(gl, SpritePath, srgb: true);

        var indices = new ushort[FootprintConfig.Max * 6];
        for (var i = 0; i < FootprintConfig.Max; i++)
        {
            var v = (ushort)(i * 4);
            var o = i * 6;
            indices[o + 0] = v;
            indices[o + 1] = (ushort)(v + 1);
            indices[o + 2] = (ushort)(v + 2);
            indices[o + 3] = v;
            indices[o + 4] = (ushort)(v + 2);
            indices[o + 5] = (ushort)(v + 3);
        }

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

    public void Render(IReadOnlyList<Footprint> prints, Matrix4x4 viewProj, Vector3 cameraPos,
        Vector3 fogColor, float fogNear, float fogFar)
    {
        Span<Vector3> corners = stackalloc Vector3[4];
        var range2 = FootprintConfig.DrawRangeBlocks * FootprintConfig.DrawRangeBlocks;
        var count = 0;
        for (var i = 0; i < prints.Count && count < FootprintConfig.Max; i++)
        {
            var print = prints[i];
            var alpha = print.Alpha;
            if (alpha <= 0.002f || Vector3.DistanceSquared(print.Feet, cameraPos) > range2) continue;

            FootprintRules.Corners(print.Feet, print.Heading, print.Side, corners);
            var a = (byte)(alpha * 255f + 0.5f);
            for (var c = 0; c < 4; c++)
            {
                // The right foot mirrors the sprite
                var uv = print.Side < 0 ? Uvs[c] : new Vector2(1f - Uvs[c].X, Uvs[c].Y);
                _verts[count * 4 + c] = new Vertex(corners[c], Tint.R, Tint.G, Tint.B, a, uv, 1f, 0f, Vector3.UnitY);
            }
            count++;
        }
        if (count == 0) return;

        _shader.Use();
        _shader.SetUniform("uVP", viewProj);
        _shader.SetUniform("uCameraPos", cameraPos);
        _shader.SetUniform("uFogColor", fogColor);
        _shader.SetUniform("uFogNear", fogNear);
        _shader.SetUniform("uFogFar", fogFar);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2DArray, _texture);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        _gl.Enable(EnableCap.PolygonOffsetFill);
        _gl.PolygonOffset(-1f, -2f);

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferSubData<Vertex>(BufferTargetARB.ArrayBuffer, 0, new ReadOnlySpan<Vertex>(_verts, 0, count * 4));
        unsafe
        {
            _gl.DrawElements(PrimitiveType.Triangles, (uint)(count * 6), DrawElementsType.UnsignedShort, null);
        }
        _gl.BindVertexArray(0);

        _gl.Disable(EnableCap.PolygonOffsetFill);
        _gl.DepthMask(true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shader.Dispose();
        _gl.DeleteTexture(_texture);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
        GC.SuppressFinalize(this);
    }
}
