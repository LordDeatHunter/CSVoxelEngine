using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Data;
using VoxelEngine.Graphics;

namespace VoxelEngine.Particles;

/// <summary>
/// Draws every live <see cref="WaterRipple"/> as flat quads lying on the water top, in one dynamic
/// draw: the bubble billboards' vertex stage and a procedural ring fragment
/// (<c>ripple-fragment.glsl</c>), blended, depth-tested and depth-write-free. A ripple is cut into one
/// quad per water cell it covers (<see cref="WaterRipple.Cells"/>, each at its own surface height and
/// clipped to the cell), all sharing the ripple's center in their UVs, so the ring stops at the water's
/// edge. The quad's tint carries the wave train: r = the leading edge's radius, g = the wavelength and
/// b = a per-ripple seed (all but the seed as shares of the ripple square's half-size), a = opacity.
/// Main thread only.
/// </summary>
public sealed class RippleRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;
    private readonly int _maxQuads;
    private bool _disposed;

    private readonly Vertex[] _verts;

    public RippleRenderer(GL gl)
    {
        _gl = gl;
        _maxQuads = ParticleConfig.RippleMax * ParticleConfig.RippleQuadsPerRipple;
        _shader = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/bubble-particle-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/ripple-fragment.glsl"));
        _shader.Use();
        _shader.SetUniform("uTrainWaves", ParticleConfig.RippleTrainWaves);

        _verts = new Vertex[_maxQuads * 4];

        var indices = new ushort[_maxQuads * 6];
        for (var i = 0; i < _maxQuads; i++)
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
        var quads = 0;
        foreach (var r in ripples)
        {
            var half = r.Half;
            var radiusByte = (byte)(MathUtils.Clamp01(r.Radius / half) * 255f + 0.5f);
            var waveByte = (byte)(MathUtils.Clamp01(ParticleConfig.RippleWavelength / half) * 255f + 0.5f);
            var seedByte = (byte)(MathUtils.Clamp01(r.Seed) * 255f + 0.5f);
            var alpha = (byte)(MathUtils.Clamp01(r.Opacity * ParticleConfig.RippleAlpha) * 255f + 0.5f);

            var tint = (radiusByte, waveByte, seedByte, alpha);

            if (r.Cells == null)
            {
                AddQuad(ref quads, r, tint, r.Position.Y,
                    r.Position.X - half, r.Position.X + half, r.Position.Z - half, r.Position.Z + half);
                continue;
            }

            foreach (var cell in r.Cells)
            {
                var x0 = MathF.Max(cell.X, r.Position.X - half);
                var x1 = MathF.Min(cell.X + 1, r.Position.X + half);
                var z0 = MathF.Max(cell.Z, r.Position.Z - half);
                var z1 = MathF.Min(cell.Z + 1, r.Position.Z + half);
                if (x1 <= x0 || z1 <= z0) continue;
                AddQuad(ref quads, r, tint, cell.Top + ParticleConfig.RippleLift, x0, x1, z0, z1);
            }
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

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferSubData<Vertex>(BufferTargetARB.ArrayBuffer, 0,
            new ReadOnlySpan<Vertex>(_verts, 0, quads * 4));
        unsafe
        {
            _gl.DrawElements(PrimitiveType.Triangles, (uint)(quads * 6),
                DrawElementsType.UnsignedShort, null);
        }
        _gl.BindVertexArray(0);

        _gl.DepthMask(true);
    }

    // One quad over the rectangle [x0, x1] x [z0, z1] at height y; its UVs are the ripple's own square
    // (center = the ripple's position), so every piece of a ripple draws the same ring. A full buffer
    // drops the quad.
    private void AddQuad(ref int quads, WaterRipple r, (byte Radius, byte Wave, byte Seed, byte Alpha) tint,
        float y, float x0, float x1, float z0, float z1)
    {
        if (quads >= _maxQuads) return;
        var half = r.Half;
        Span<(float X, float Z)> corners = [(x0, z0), (x0, z1), (x1, z1), (x1, z0)];
        for (var c = 0; c < 4; c++)
        {
            var (x, z) = corners[c];
            var uv = new Vector2((x - r.Position.X) / half * 0.5f + 0.5f, (z - r.Position.Z) / half * 0.5f + 0.5f);
            _verts[quads * 4 + c] = new Vertex(new Vector3(x, y, z), tint.Radius, tint.Wave, tint.Seed, tint.Alpha,
                uv, 0f, 0f, Vector3.UnitY);
        }

        quads++;
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
