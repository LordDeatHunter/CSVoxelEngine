using System;
using System.Numerics;
using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// Underwater sun shafts, drawn after everything else while the eye is submerged. Two draws,
/// both a single oversized triangle rather than a quad so every pixel is covered by one
/// primitive and no diagonal seam runs through the screen:
/// <list type="number">
/// <item>the march (rays-fragment.glsl) into a caller-owned HALF-RESOLUTION target, blending
/// off - it marches from the eye toward whatever the depth copy saw and accumulates the
/// sunlight that survives the shadow map and the water above it, writing the distance it
/// marched into alpha;</item>
/// <item>the composite (rays-upsample-fragment.glsl) at full resolution into the default
/// framebuffer, additively, weighting each half-res tap by how closely the distance it
/// marched matches this pixel's own so the shafts do not bleed across a silhouette.</item>
/// </list>
/// Quartering the marched pixels is the whole point (PERF_TODO R2); the loop's step count,
/// sample count and per-sample formula are untouched.
/// Texture units read: 1 (shadow map) and 4 (full-res scene depth) by the march, 4 and 5 (its
/// own half-res output, bound here) by the composite.
/// The draws are additive, so this owns its own blend state and puts the world's back on the
/// way out (<see cref="RenderManager.RenderUnderwaterRays"/> runs right before the selection
/// pass, which needs the depth test).
/// </summary>
public sealed class UnderwaterRaysRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly Shader _upsample;
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

        // Same vertex shader - the composite covers the screen with the same triangle.
        _upsample = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/rays-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/rays-upsample-fragment.glsl"));
        _upsample.Use();
        _upsample.SetUniform("uRays", 5);
        _upsample.SetUniform("uSceneDepth", 4);

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
    /// Draws the shafts in two passes. <paramref name="invViewProj"/> inverts exactly the matrix
    /// that wrote the depth texture, which is how both shaders unproject it without knowing the
    /// near/far planes or the clip-depth convention; <paramref name="lightViewProj"/> is the same
    /// light-space matrix the world shader samples the shadow map with.
    /// The march goes into <paramref name="halfResFbo"/> at <paramref name="halfResSize"/>; the
    /// composite reads it back out of <paramref name="halfResTex"/> and draws to the default
    /// framebuffer at <paramref name="viewportSize"/>. Both shaders must unproject with the SAME
    /// matrix in the same frame - the composite weighs each tap by comparing marched distances,
    /// and that comparison is only meaningful because both sides computed them the same way.
    /// <paramref name="waterAbsorb"/> is the per-channel absorption per block of depth below
    /// <paramref name="surfaceY"/>, the same coefficient the world shader tints water with.
    /// <paramref name="surfaceY"/> is the world Y of the eye's own water surface
    /// (<see cref="Camera.EyeWaterSurfaceY"/>), not the sea plane, so a lake above sea level
    /// gets shafts as well.
    /// </summary>
    public void Render(uint halfResFbo, uint halfResTex, Vector2D<int> halfResSize,
        Vector2D<int> viewportSize, Matrix4x4 invViewProj, Vector3 cameraPos, Vector3 sunDir,
        Vector3 sunColor, float strength, float surfaceY, Vector3 waterAbsorb, Matrix4x4 lightViewProj, float time)
    {
        _gl.Disable(EnableCap.DepthTest);      // full-screen: nothing to test against
        _gl.DepthMask(false);
        // The march's triangle covers every texel of its target and it owns all of them, so there
        // is nothing to blend with and deliberately nothing to clear - the previous contents are
        // fully overwritten, which is also why a resize of that target cannot flash.
        _gl.Disable(EnableCap.Blend);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, halfResFbo);
        _gl.Viewport(0, 0, (uint)halfResSize.X, (uint)halfResSize.Y);

        _shader.Use();
        _shader.SetUniform("uInvViewProj", invViewProj);
        _shader.SetUniform("uLightViewProj", lightViewProj);
        _shader.SetUniform("uCameraPos", cameraPos);
        _shader.SetUniform("uSunDir", sunDir);
        _shader.SetUniform("uSunColor", sunColor);
        _shader.SetUniform("uStrength", strength);
        _shader.SetUniform("uSurfaceY", surfaceY);
        _shader.SetUniform("uWaterAbsorb", waterAbsorb);
        _shader.SetUniform("uMaxDistance", WorldGenerationConfig.WaterRayMaxDistance);
        _shader.SetUniform("uSteps", WorldGenerationConfig.WaterRaySteps);
        _shader.SetUniform("uTime", time);

        // One VAO, bound once and used by both draws.
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        // Back to the window before the composite.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)viewportSize.X, (uint)viewportSize.Y);

        // Only NOW is the march output safe to sample: binding it to a unit while it was still
        // the draw target would be a feedback loop, the hazard RenderReflection documents for
        // unit 2. Unit 4 stays bound to the full-res scene depth across both draws - the
        // composite reconstructs its own marched distance from it.
        _gl.ActiveTexture(TextureUnit.Texture5);
        _gl.BindTexture(TextureTarget.Texture2D, halfResTex);
        _gl.ActiveTexture(TextureUnit.Texture0);

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);   // light adds, never darkens
        _upsample.Use();
        _upsample.SetUniform("uInvViewProj", invViewProj);
        _upsample.SetUniform("uCameraPos", cameraPos);
        _upsample.SetUniform("uMaxDistance", WorldGenerationConfig.WaterRayMaxDistance);
        _upsample.SetUniform("uTolerance", WorldGenerationConfig.WaterRayUpsampleToleranceBlocks);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        // Leave no unit holding this pass's own render target, so next frame's march never runs
        // with it bound - defined behaviour either way (the march reads only units 1 and 4), but
        // it matches the convention CopyScene and every other pass here follows.
        _gl.ActiveTexture(TextureUnit.Texture5);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.ActiveTexture(TextureUnit.Texture0);

        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);       // the selection pass right after depends on it
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shader.Dispose();
        _upsample.Dispose();
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        GC.SuppressFinalize(this);
    }
}
