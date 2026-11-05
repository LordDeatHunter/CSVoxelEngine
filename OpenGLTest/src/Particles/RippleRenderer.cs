using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Data;
using VoxelEngine.Graphics;

namespace VoxelEngine.Particles;

/// <summary>
/// Draws every live <see cref="WaterRipple"/> as a flat quad lying on the water top, in one dynamic
/// draw: the bubble billboards' vertex stage and a procedural ring fragment
/// (<c>ripple-fragment.glsl</c>), blended, depth-tested and depth-write-free. The quad's tint carries
/// the ring: r = the leading ring's radius and g = its width, both as shares of the quad's half-size,
/// a = opacity. Main thread only.
/// </summary>
public sealed class RippleRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;
    private readonly int _max;
    private bool _disposed;

    private readonly Vertex[] _verts;

    private static readonly (float X, float Z)[] CornerSigns =
        [(-1f, -1f), (-1f, 1f), (1f, 1f), (1f, -1f)];

    public RippleRenderer(GL gl)
    {
        _gl = gl;
        _max = ParticleConfig.RippleMax;
        _shader = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/bubble-particle-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/ripple-fragment.glsl"));
        _shader.Use();
        _shader.SetUniform("uTrailShare", ParticleConfig.RippleTrailShare);

        _verts = new Vertex[_max * 4];

        var indices = new ushort[_max * 6];
        for (var i = 0; i < _max; i++)
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

    public void Render(IReadOnlyList<WaterRipple> ripples, Matrix4x4 viewProj, Vector3 cameraPos,
        Vector3 fogColor, float fogNear, float fogFar)
    {
        var count = Math.Min(ripples.Count, _max);
        if (count == 0) return;

        // The quad reaches the ring's final radius plus a ring width of soft edge on each side
        var width = ParticleConfig.RippleRingWidth;
        var widthByte = (byte)(width * 255f + 0.5f);
        for (var slot = 0; slot < count; slot++)
        {
            var r = ripples[slot];
            var half = r.MaxRadius / (1f - 2f * width);
            var radiusByte = (byte)(MathUtils.Clamp01(r.Radius / half) * 255f + 0.5f);
            var alpha = (byte)(MathUtils.Clamp01(r.Opacity * ParticleConfig.RippleAlpha) * 255f + 0.5f);

            for (var c = 0; c < 4; c++)
            {
                var (sx, sz) = CornerSigns[c];
                _verts[slot * 4 + c] = new Vertex(r.Position + new Vector3(sx * half, 0f, sz * half),
                    radiusByte, widthByte, 0, alpha, new Vector2(sx * 0.5f + 0.5f, sz * 0.5f + 0.5f),
                    0f, 0f, Vector3.UnitY);
            }
        }

        _shader.Use();
        _shader.SetUniform("uVP", viewProj);
        _shader.SetUniform("uCameraPos", cameraPos);
        _shader.SetUniform("uFogColor", fogColor);
        _shader.SetUniform("uFogNear", fogNear);
        _shader.SetUniform("uFogFar", fogFar);

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferSubData<Vertex>(BufferTargetARB.ArrayBuffer, 0,
            new ReadOnlySpan<Vertex>(_verts, 0, count * 4));
        unsafe
        {
            _gl.DrawElements(PrimitiveType.Triangles, (uint)(count * 6),
                DrawElementsType.UnsignedShort, null);
        }
        _gl.BindVertexArray(0);

        _gl.DepthMask(true);
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
