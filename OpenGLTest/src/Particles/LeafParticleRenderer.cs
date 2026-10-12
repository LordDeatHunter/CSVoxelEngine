using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Data;
using VoxelEngine.Graphics;

namespace VoxelEngine.Particles;

/// <summary>
/// Draws every live <see cref="LeafParticle"/> as a camera-facing billboard in a single
/// dynamic draw: one quad per leaf sampling the centred crop of the leaves tile, faded
/// per vertex through its own shader (the world shader reserves vertex alpha for water).
/// Back-to-front so the blend reads correctly. Main thread only.
/// </summary>
public sealed class LeafParticleRenderer : IDisposable
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

    private ushort _leavesTile;
    private UvRect _crop;
    private bool _cropResolved;

    // Corner signs in the winding Primitives.CreateRectangle and UvRect.CornersTo share:
    // bottom-left, top-left, top-right, bottom-right.
    private static readonly (float X, float Y)[] CornerSigns =
        [(-1f, -1f), (-1f, 1f), (1f, 1f), (1f, -1f)];

    public LeafParticleRenderer(GL gl)
    {
        _gl = gl;
        _max = WorldGenerationConfig.LeafParticleMax;
        _shader = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/leaf-particle-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/leaf-particle-fragment.glsl"));
        _shader.Use();
        _shader.SetUniform("uAtlas", 0);

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

    /// <summary>
    /// The centred <c>cropPixels × cropPixels</c> square of a <c>tilePixels</c> tile in 0..1
    /// UV space, on <paramref name="tile"/>. With the atlas on Nearest filtering the crop
    /// edge lands exactly between texels, so neighbouring tiles never bleed in.
    /// </summary>
    public static UvRect CenterCrop(ushort tile, int tilePixels, int cropPixels)
    {
        var min = (tilePixels - cropPixels) / 2f / tilePixels;
        var max = (tilePixels + cropPixels) / 2f / tilePixels;
        return new UvRect(new Vector2(min, min), new Vector2(max, max), tile);
    }

    /// <summary>
    /// Draws <paramref name="particles"/> (clamped to the configured max) as one blended,
    /// depth-tested batch with no depth writes, so fading leaves never punch holes in the
    /// depth buffer. Leaves the atlas bound on unit 0, like the world draws.
    /// </summary>
    public void Render(IReadOnlyList<LeafParticle> particles, Matrix4x4 viewProj, Vector3 cameraPos,
        Vector3 forward, uint atlasTexture, int tilePixels, Vector3 sunDir, Vector3 sunColor,
        float sunIntensity, Vector3 ambient, Vector3 fogColor, float fogNear, float fogFar)
    {
        var count = Math.Min(particles.Count, _max);
        if (count == 0) return;

        if (!_cropResolved)
        {
            _leavesTile = TextureAtlas.TileOf("oak_leaves");
            _crop = CenterCrop(_leavesTile, tilePixels, WorldGenerationConfig.LeafParticleCropPixels);
            _cropResolved = true;
        }

        forward.BillboardBasis(out var right, out var up);

        for (var i = 0; i < count; i++)
        {
            _depths[i] = Vector3.DistanceSquared(particles[i].Position, cameraPos);
            _order[i] = i;
        }
        Array.Sort(_depths, _order, 0, count);

        Span<Vector2> uvCorners = stackalloc Vector2[4];
        _crop.CornersTo(uvCorners);

        for (var slot = 0; slot < count; slot++)
        {
            var p = particles[_order[count - 1 - slot]];
            var half = p.Size * 0.5f;
            var cos = MathF.Cos(p.Rotation);
            var sin = MathF.Sin(p.Rotation);
            var alpha = (byte)(MathUtils.Clamp01(p.Alpha) * 255f + 0.5f);

            for (var c = 0; c < 4; c++)
            {
                var (sx, sy) = CornerSigns[c];
                var offset = right * ((sx * cos - sy * sin) * half)
                           + up * ((sx * sin + sy * cos) * half);
                _verts[slot * 4 + c] = new Vertex(p.Position + offset,
                    255, 255, 255, alpha, uvCorners[c], _leavesTile,
                    1f, 0f, Vector3.UnitY);
            }
        }

        _shader.Use();
        _shader.SetUniform("uVP", viewProj);
        _shader.SetUniform("uCameraPos", cameraPos);
        _shader.SetUniform("uSunDir", sunDir);
        _shader.SetUniform("uSunColor", sunColor);
        _shader.SetUniform("uSunIntensity", sunIntensity);
        _shader.SetUniform("uAmbient", ambient);
        _shader.SetUniform("uFogColor", fogColor);
        _shader.SetUniform("uFogNear", fogNear);
        _shader.SetUniform("uFogFar", fogFar);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2DArray, atlasTexture);
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
