using System;
using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// The frame's final full-screen pass: one oversized triangle (the shape <see cref="SkyRenderer"/>
/// draws) shaded by post-fragment.glsl, which reads the linear HDR scene target and writes the
/// tonemapped, sRGB-encoded, FXAA-smoothed, dithered image. It binds no framebuffer and no texture - the caller
/// (<see cref="RenderManager.RenderPostProcess"/>) binds the destination and puts the scene
/// texture on unit 0 - so <see cref="RenderManager"/>'s texture-unit cache stays its own business.
/// </summary>
public sealed class PostProcessRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private bool _disposed;

    public PostProcessRenderer(GL gl)
    {
        _gl = gl;

        _shader = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/rays-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/post-fragment.glsl"));
        // Nothing else ever writes these, so they are set once here.
        _shader.Use();
        _shader.SetUniform("uScene", 0);
        _shader.SetUniform("uExposure", WorldGenerationConfig.TonemapExposure);
        _shader.SetUniform("uFxaaReduceMin", WorldGenerationConfig.FxaaReduceMin);
        _shader.SetUniform("uFxaaReduceMul", WorldGenerationConfig.FxaaReduceMul);
        _shader.SetUniform("uFxaaSpanMax", WorldGenerationConfig.FxaaSpanMax);
        _shader.SetUniform("uFxaaEdgeThreshold", WorldGenerationConfig.FxaaEdgeThreshold);
        _shader.SetUniform("uFxaaEdgeThresholdMin", WorldGenerationConfig.FxaaEdgeThresholdMin);

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
    /// Resolves the scene texture bound on unit 0 into the currently bound framebuffer, covering
    /// every pixel with blending and the depth test off, then restores the state the UI pass
    /// expects (depth on and writable, SrcAlpha/OneMinusSrcAlpha blending). <paramref name="viewport"/>
    /// is the scene texture's size in pixels, which FXAA clamps its taps to.
    /// </summary>
    public void Render(Vector2D<int> viewport)
    {
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.Blend);

        _shader.Use();
        _shader.SetUniform("uResolution", new System.Numerics.Vector2(viewport.X, viewport.Y));
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
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
