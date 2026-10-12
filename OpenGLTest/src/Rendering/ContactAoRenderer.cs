using System;
using System.Numerics;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using VoxelEngine.Effects.Post;

namespace VoxelEngine.Rendering;

/// <summary>
/// The screen-space contact AO pass (SH-28, contact-ao-fragment.glsl, twin <see cref="ContactAo"/>):
/// one oversized full-screen triangle, the shape <see cref="PostProcessRenderer"/> draws, that
/// reads the HDR scene depth on unit 0 and outputs a darkening multiplier. It binds no framebuffer,
/// no texture and no blend state - <see cref="RenderManager.RenderContactAo"/> owns all of that - so
/// the render manager's texture-unit cache stays its own business.
/// </summary>
public sealed class ContactAoRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private bool _disposed;

    public ContactAoRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/rays-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/contact-ao-fragment.glsl"));
        _shader.Use();
        _shader.SetUniform("uDepth", 0);

        // One triangle covering NDC: the two off-screen corners keep the visible area seamless
        Span<float> vertices = [-1f, -1f, 3f, -1f, -1f, 3f];
        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);
        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, vertices, BufferUsageARB.StaticDraw);
        unsafe
        {
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        }
        gl.EnableVertexAttribArray(0);
        gl.BindVertexArray(0);
    }

    /// <summary>
    /// Draws the AO multiplier over every pixel of the bound framebuffer. The caller binds the
    /// target, puts the scene depth texture on unit 0
    /// and sets the multiplicative blend and depth state. <paramref name="pxPerBlock"/> is
    /// <see cref="ContactAo.PxPerBlockAtUnitDist"/> for the current viewport and field of view.
    /// </summary>
    public void Render(Matrix4x4 invViewProj, Vector3 eye, Vector2D<int> size, float pxPerBlock,
        in ContactAoSettings s)
    {
        _shader.Use();
        _shader.SetUniform("uInvViewProj", invViewProj);
        _shader.SetUniform("uCameraPos", eye);
        _shader.SetUniform("uResolution", new Vector2(size.X, size.Y));
        _shader.SetUniform("uPxPerBlock", pxPerBlock);
        _shader.SetUniform("uStrength", s.Strength);
        _shader.SetUniform("uMinBlocks", s.MinBlocks);
        _shader.SetUniform("uPeakBlocks", s.PeakBlocks);
        _shader.SetUniform("uRangeBlocks", s.RangeBlocks);
        _shader.SetUniform("uFadeNear", s.FadeNear);
        _shader.SetUniform("uFadeFar", s.FadeFar);
        _shader.SetUniform("uRadiusBlocks", s.RadiusBlocks);
        _shader.SetUniform("uMaxPx", s.MaxPx);
        _shader.SetUniform("uReachNear", s.ReachNear);
        _shader.SetUniform("uReachFar", s.ReachFar);
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shader.Dispose();
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        GC.SuppressFinalize(this);
    }
}
