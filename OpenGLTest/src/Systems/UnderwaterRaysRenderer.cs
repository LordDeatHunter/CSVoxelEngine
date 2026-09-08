using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// Underwater sun shafts: one additive full-screen pass, drawn after everything else while the
/// eye is submerged. The geometry is a single oversized triangle rather than a quad, so every
/// pixel is covered by one primitive and no diagonal seam runs through the screen. All the work
/// is in rays-fragment.glsl, which marches from the eye toward whatever the depth copy saw and
/// accumulates the sunlight that survives the shadow map and the water above it.
/// The draw is additive, so this owns its own blend state and puts the world's back on the way
/// out (<see cref="RenderManager.RenderUnderwaterRays"/> runs right before the selection pass,
/// which needs the depth test).
/// </summary>
public sealed class UnderwaterRaysRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private bool _disposed;

    public UnderwaterRaysRenderer(GL gl)
    {
        _gl = gl;

        _shader = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/rays-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/rays-fragment.glsl"));

        // Sampler units are program state, so they only have to be set once, with it bound
        _shader.Use();
        _shader.SetUniform("uSceneDepth", 4);
        _shader.SetUniform("uShadowMap", 1);

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
    /// Draws the shafts. <paramref name="invViewProj"/> inverts exactly the matrix that wrote
    /// the depth texture, which is how the shader unprojects it without knowing the near/far
    /// planes or the clip-depth convention; <paramref name="lightViewProj"/> is the same
    /// light-space matrix the world shader samples the shadow map with.
    /// </summary>
    public void Render(Matrix4x4 invViewProj, Vector3 cameraPos, Vector3 sunDir, Vector3 sunColor,
        float strength, float waterPlaneY, Matrix4x4 lightViewProj, float time)
    {
        _gl.Disable(EnableCap.DepthTest);      // full-screen: nothing to test against
        _gl.DepthMask(false);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);   // light adds, never darkens

        _shader.Use();
        _shader.SetUniform("uInvViewProj", invViewProj);
        _shader.SetUniform("uLightViewProj", lightViewProj);
        _shader.SetUniform("uCameraPos", cameraPos);
        _shader.SetUniform("uSunDir", sunDir);
        _shader.SetUniform("uSunColor", sunColor);
        _shader.SetUniform("uStrength", strength);
        _shader.SetUniform("uWaterPlaneY", waterPlaneY);
        _shader.SetUniform("uMaxDistance", WorldGenerationConfig.WaterRayMaxDistance);
        _shader.SetUniform("uSteps", WorldGenerationConfig.WaterRaySteps);
        _shader.SetUniform("uTime", time);

        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);       // the selection pass right after depends on it
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
