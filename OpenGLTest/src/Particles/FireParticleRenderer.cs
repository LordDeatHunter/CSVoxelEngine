using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Data;
using VoxelEngine.Graphics;

namespace VoxelEngine.Particles;

/// <summary>
/// Draws every live <see cref="FireParticle"/> as a camera-facing billboard: plain
/// vertex-colored quads (embers glow orange, smoke drifts gray), fogged like the world.
/// One buffer upload feeds two draws. Embers go first and <b>additively</b>, so a cluster of
/// them builds up light instead of stacking into an opaque orange blot, and additive light
/// needs no sort. Smoke follows <b>alpha-blended</b> and back-to-front, drawn over the embers
/// so it veils the ones inside or behind it (the common case). An ember in front of smoke is
/// slightly veiled too, which is accepted to keep it at two draws. Main thread only.
/// </summary>
public sealed class FireParticleRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;
    private readonly int _max;
    private bool _disposed;

    private readonly Vertex[] _verts;
    private readonly float[] _depths;
    private readonly int[] _order;
    private readonly FireParticleKind[] _kinds;

    private static readonly (float X, float Y)[] CornerSigns =
        [(-1f, -1f), (-1f, 1f), (1f, 1f), (1f, -1f)];

    public FireParticleRenderer(GL gl)
    {
        _gl = gl;
        _max = WorldGenerationConfig.FireParticleMax;
        _shader = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/fire-particle-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/fire-particle-fragment.glsl"));

        _verts = new Vertex[_max * 4];
        _depths = new float[_max];
        _order = new int[_max];
        _kinds = new FireParticleKind[_max];

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

    public void Render(IReadOnlyList<FireParticle> particles, Matrix4x4 viewProj, Vector3 cameraPos,
        Vector3 forward, Vector3 fogColor, float fogNear, float fogFar)
    {
        var count = Math.Min(particles.Count, _max);
        if (count == 0) return;

        forward.BillboardBasis(out var right, out var up);

        for (var i = 0; i < count; i++)
        {
            _depths[i] = Vector3.DistanceSquared(particles[i].Position, cameraPos);
            _kinds[i] = particles[i].Kind;
        }
        var embers = OrderForDraw(new ReadOnlySpan<FireParticleKind>(_kinds, 0, count),
            new ReadOnlySpan<float>(_depths, 0, count), new Span<int>(_order, 0, count));

        for (var slot = 0; slot < count; slot++)
        {
            var p = particles[_order[slot]];
            var half = p.Size * 0.5f;
            var alpha = (byte)(MathUtils.Clamp01(p.Alpha) * 255f + 0.5f);
            var (r, g, b) = p.Color;
            var emissive = p.Kind == FireParticleKind.Ember ? 1f : 0f;

            for (var c = 0; c < 4; c++)
            {
                var (sx, sy) = CornerSigns[c];
                var offset = right * (sx * half) + up * (sy * half);
                _verts[slot * 4 + c] = new Vertex(p.Position + offset,
                    r, g, b, alpha, new Vector2(sx * 0.5f + 0.5f, sy * 0.5f + 0.5f),
                    emissive, 0f, Vector3.UnitY);
            }
        }

        _shader.Use();
        _shader.SetUniform("uVP", viewProj);
        _shader.SetUniform("uCameraPos", cameraPos);
        _shader.SetUniform("uFogColor", fogColor);
        _shader.SetUniform("uFogNear", fogNear);
        _shader.SetUniform("uFogFar", fogFar);

        _gl.Enable(EnableCap.Blend);
        _gl.DepthMask(false);

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferSubData<Vertex>(BufferTargetARB.ArrayBuffer, 0,
            new ReadOnlySpan<Vertex>(_verts, 0, count * 4));
        unsafe
        {
            if (embers > 0)
            {
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
                _gl.DrawElements(PrimitiveType.Triangles, (uint)(embers * 6),
                    DrawElementsType.UnsignedShort, null);
            }
            if (count > embers)
            {
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                _gl.DrawElements(PrimitiveType.Triangles, (uint)((count - embers) * 6),
                    DrawElementsType.UnsignedShort, (void*)(embers * 6 * sizeof(ushort)));
            }
        }
        _gl.BindVertexArray(0);

        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(true);
    }

    /// <summary>
    /// Writes the draw order for one batch into <paramref name="order"/> (a permutation of
    /// 0..n-1): every ember first, in no particular order since additive light commutes, then
    /// every smoke particle sorted far-to-near by <paramref name="distSq"/> so the alpha blend
    /// composites correctly. Returns the number of embers, i.e. where the smoke run starts.
    /// GL-free and allocation-free (an in-place heap sort over the index span).
    /// </summary>
    public static int OrderForDraw(ReadOnlySpan<FireParticleKind> kinds, ReadOnlySpan<float> distSq,
        Span<int> order)
    {
        var n = kinds.Length;
        var head = 0;
        var tail = n;
        for (var i = 0; i < n; i++)
        {
            if (kinds[i] == FireParticleKind.Ember) order[head++] = i;
            else order[--tail] = i;
        }

        var smoke = order.Slice(head, n - head);
        // Min-heap on distance, popping the nearest to the back, leaves the run far-to-near.
        for (var i = smoke.Length / 2 - 1; i >= 0; i--) SiftDown(smoke, distSq, i, smoke.Length);
        for (var end = smoke.Length - 1; end > 0; end--)
        {
            (smoke[0], smoke[end]) = (smoke[end], smoke[0]);
            SiftDown(smoke, distSq, 0, end);
        }
        return head;
    }

    private static void SiftDown(Span<int> heap, ReadOnlySpan<float> key, int i, int size)
    {
        while (true)
        {
            var l = 2 * i + 1;
            if (l >= size) return;
            var m = l;
            if (l + 1 < size && key[heap[l + 1]] < key[heap[l]]) m = l + 1;
            if (key[heap[i]] <= key[heap[m]]) return;
            (heap[i], heap[m]) = (heap[m], heap[i]);
            i = m;
        }
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
