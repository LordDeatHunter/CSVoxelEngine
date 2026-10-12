using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Data;
using VoxelEngine.Graphics;

namespace VoxelEngine.Particles;

/// <summary>
/// Draws every live <see cref="BubbleParticle"/> as a camera-facing billboard in one dynamic
/// draw, sampling <c>assets/textures/particles/bubble.png</c> and fading it per particle.
/// Back-to-front so the blend reads correctly. Main thread only.
/// </summary>
public sealed class BubbleParticleRenderer : IDisposable
{
    /// <summary>Billboard sprite, as a single-slice array texture like every other sprite.</summary>
    public const string SpritePath = "assets/textures/particles/bubble.png";

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;
    private readonly uint _texture;
    private readonly int _max;
    private bool _disposed;

    private readonly Vertex[] _verts;
    private readonly float[] _depths;
    private readonly int[] _order;

    private static readonly (float X, float Y)[] CornerSigns =
        [(-1f, -1f), (-1f, 1f), (1f, 1f), (1f, -1f)];

    public BubbleParticleRenderer(GL gl)
    {
        _gl = gl;
        _max = WorldGenerationConfig.BubbleParticleMax;
        _shader = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/bubble-particle-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/bubble-particle-fragment.glsl"));
        _shader.Use();
        _shader.SetUniform("uSprite", 0);
        _texture = TextureLoader.LoadFromFile(gl, SpritePath, srgb: true);

        _verts = new Vertex[_max * 4];
        _depths = new float[_max];
        _order = new int[_max];

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

    public void Render(IReadOnlyList<BubbleParticle> particles, Matrix4x4 viewProj, Vector3 cameraPos,
        Vector3 forward, Vector3 fogColor, float fogNear, float fogFar)
    {
        var count = Math.Min(particles.Count, _max);
        if (count == 0) return;

        forward.BillboardBasis(out var right, out var up);

        for (var i = 0; i < count; i++)
        {
            _depths[i] = Vector3.DistanceSquared(particles[i].Position, cameraPos);
            _order[i] = i;
        }
        Array.Sort(_depths, _order, 0, count);

        for (var slot = 0; slot < count; slot++)
        {
            var p = particles[_order[count - 1 - slot]];
            var half = p.Size * 0.5f;
            var alpha = (byte)(MathUtils.Clamp01(p.Alpha) * 255f + 0.5f);

            for (var c = 0; c < 4; c++)
            {
                var (sx, sy) = CornerSigns[c];
                var offset = right * (sx * half) + up * (sy * half);
                // V=0 is the image top (rows upload top-first), so the quad top takes V=0.
                _verts[slot * 4 + c] = new Vertex(p.Position + offset,
                    255, 255, 255, alpha, new Vector2(sx * 0.5f + 0.5f, 0.5f - sy * 0.5f),
                    0f, 0f, Vector3.UnitY);
            }
        }

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
        _gl.DeleteTexture(_texture);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
        GC.SuppressFinalize(this);
    }
}
