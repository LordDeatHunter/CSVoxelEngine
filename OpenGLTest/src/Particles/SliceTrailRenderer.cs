using System;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Data;
using VoxelEngine.Graphics;

namespace VoxelEngine.Particles;

/// <summary>
/// Draws one <see cref="SliceTrail"/> as an additive glowing ribbon: the geometry comes from
/// <see cref="SliceTrailRibbon.Build"/> and the profile (soft colored body, white core near the
/// tip edge, wispy streaks) from <c>slice-trail-fragment.glsl</c>, whose GL-free twin is
/// <see cref="SliceTrailGlow"/>. Additive light needs no sort, depth is tested but never written,
/// and back-face culling is off because the ribbon is seen from both sides. Binds no texture.
/// With <c>linearOutput</c> the sRGB trail color is decoded for the game's HDR target; the item
/// debugger, which renders in display space, passes false. Main thread only.
/// </summary>
public sealed class SliceTrailRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;
    private readonly Vertex[] _verts;
    private bool _disposed;

    public SliceTrailRenderer(GL gl, bool linearOutput)
    {
        _gl = gl;
        _shader = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/slice-trail-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/slice-trail-fragment.glsl"));

        _verts = new Vertex[SliceTrailRibbon.MaxVertices];

        var indices = new ushort[SliceTrailRibbon.MaxQuads * 6];
        for (var i = 0; i < SliceTrailRibbon.MaxQuads; i++)
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

        // Shader.SetUniform writes to the bound program, so bind it before the constant uniforms.
        _shader.Use();
        _shader.SetUniform("uCoreCenter", ParticleConfig.SliceTrailCoreCenter);
        _shader.SetUniform("uCoreWidth", ParticleConfig.SliceTrailCoreWidth);
        _shader.SetUniform("uCoreBoost", ParticleConfig.SliceTrailCoreBoost);
        _shader.SetUniform("uGlowPower", ParticleConfig.SliceTrailGlowPower);
        _shader.SetUniform("uStreakDepth", ParticleConfig.SliceTrailStreakDepth);
        _shader.SetUniform("uStreakSpan", ParticleConfig.SliceTrailStreakSpanBlocks);
        _shader.SetUniform("uStreakLanes", ParticleConfig.SliceTrailStreakLanes);
        _shader.SetUniform("uLinearOutput", linearOutput ? 1 : 0);
    }

    /// <summary>
    /// Builds <paramref name="trail"/>'s ribbon as it stands at <paramref name="now"/> and draws
    /// it additively, faded out between <paramref name="fadeNear"/> and <paramref name="fadeFar"/>
    /// blocks from the camera. Restores the blend function, depth mask and cull state it changes.
    /// </summary>
    public void Render(SliceTrail trail, double now, in Matrix4x4 viewProj, Vector3 cameraPos,
        float fadeNear, float fadeFar)
    {
        var count = SliceTrailRibbon.Build(trail, now, _verts);
        if (count == 0) return;

        _shader.Use();
        _shader.SetUniform("uVP", viewProj);
        _shader.SetUniform("uCameraPos", cameraPos);
        _shader.SetUniform("uFadeNear", fadeNear);
        _shader.SetUniform("uFadeFar", fadeFar);

        var cull = _gl.IsEnabled(EnableCap.CullFace);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);
        _gl.DepthMask(false);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferSubData<Vertex>(BufferTargetARB.ArrayBuffer, 0,
            new ReadOnlySpan<Vertex>(_verts, 0, count));
        unsafe
        {
            _gl.DrawElements(PrimitiveType.Triangles, (uint)(count / 4 * 6),
                DrawElementsType.UnsignedShort, null);
        }
        _gl.BindVertexArray(0);

        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(true);
        if (cull) _gl.Enable(EnableCap.CullFace);
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
