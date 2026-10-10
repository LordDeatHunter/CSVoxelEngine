using System;
using System.Numerics;
using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// Full-screen damage vignette: one oversized triangle with a radial fragment gradient,
/// drawn inside the UI pass. A single primitive is what fixes the old corner artefacts —
/// overlapping bars multiplied their alpha where they met — and the shader's grain and pulse
/// keep the reddening from reading as a flat fill. The UI pass already provides the GL state
/// this needs (depth test off, straight alpha blending).
/// </summary>
public sealed class DamageVignetteRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private bool _disposed;

    public DamageVignetteRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/vignette-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/vignette-fragment.glsl"));

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
    /// Draws the vignette over the current framebuffer. <paramref name="strength"/> is the 0..1
    /// damage flash and <paramref name="lowHealth"/> the 0..1 low-health pulse
    /// (<see cref="LowHealthVignette.Intensity"/>), a second, broader edge term of the same pass;
    /// <paramref name="time"/> advances the grain and the flash's flicker. When both are zero the
    /// draw is skipped entirely.
    /// </summary>
    public void Render(Vector2D<int> size, float strength, float lowHealth, float time)
    {
        var s = MathUtils.Clamp01(strength);
        var low = MathUtils.Clamp01(lowHealth);
        if ((s <= 0.001f && low <= 0.001f) || size.X <= 0 || size.Y <= 0) return;

        _shader.Use();
        _shader.SetUniform("uResolution", new Vector2(size.X, size.Y));
        _shader.SetUniform("uStrength", s);
        _shader.SetUniform("uLowHealth", low);
        _shader.SetUniform("uTime", time);
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
