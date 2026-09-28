using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// The sky dome: one oversized full-screen triangle (the same shape
/// <see cref="UnderwaterRaysRenderer"/> draws) shaded by sky-fragment.glsl from the frame's
/// <see cref="SkyPalette"/>. It is drawn first, straight after the clear and before the sun
/// disc, with the depth test and depth writes off, so everything after it occludes it.
/// Binds no textures, so <see cref="RenderManager"/>'s texture-unit cache stays valid.
/// </summary>
public sealed class SkyRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private bool _disposed;

    public SkyRenderer(GL gl)
    {
        _gl = gl;

        _shader = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/rays-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/sky-fragment.glsl"));

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
    /// Paints the dome over the whole current target. <paramref name="invViewRotProj"/> is the
    /// inverse of a translation-free view-projection, so the shader rebuilds each pixel's view
    /// ray from the origin. The dome fades toward <paramref name="tint"/> by
    /// <paramref name="tintAmount"/> (lava); it stays dry underwater so the Snell window shows sky.
    /// </summary>
    public void Render(Matrix4x4 invViewRotProj, in SkyPalette sky, Vector3 sunDir, Vector3 tint, float tintAmount)
    {
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.Blend);

        _shader.Use();
        _shader.SetUniform("uInvViewRotProj", invViewRotProj);
        _shader.SetUniform("uSkyZenith", sky.Zenith);
        _shader.SetUniform("uSkyHorizon", sky.Horizon);
        _shader.SetUniform("uSkyGlow", sky.Glow);
        _shader.SetUniform("uSkyShape", SkyPalette.Shape);
        _shader.SetUniform("uSunDir", sunDir);
        _shader.SetUniform("uSkyTint", tint);
        _shader.SetUniform("uSkyTintAmount", tintAmount);

        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
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
