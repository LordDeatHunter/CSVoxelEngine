using System;
using System.Numerics;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using VoxelEngine.Effects.Post;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Rendering;

/// <summary>
/// Godrays: light shafts through trees, clouds and gaps in dry air when the sun is in view. Two
/// draws, like <see cref="UnderwaterRaysRenderer"/>: the same march run over air
/// (godrays-fragment.glsl) into the caller's HALF-RESOLUTION target, accumulating the sunlight that
/// survives the near shadow cascade and the cloud shadows along each pixel's ray, then the composite
/// (godrays-composite-fragment.glsl) at full resolution, which averages the march along the line
/// from each pixel toward the sun's place on screen, so the contrast between long sunlit rays (open
/// sky past the trees, cloud gaps) and short or shadowed ones (trunks, crowns, clouds) streaks
/// outward from the sun, and adds it in display space over the scene.
/// Unlike the underwater pass it reads the HDR scene's own depth texture rather than a copy: the
/// march draws into the half-res target, which does not have that depth attached, so sampling it is
/// no feedback loop; the composite draws into the color-only HDR framebuffer.
/// Texture units read: 1 (the near shadow map) and 4 (the scene depth) by the march, bound by the
/// caller, 8 (the cloud-shadow coverage, bound for the whole run), and 5 and <see cref="SceneUnit"/>,
/// bound here around the composite. Owns its blend and depth state and puts the world's back on the
/// way out.
/// </summary>
public sealed class GodraysRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _march;
    private readonly Shader _composite;
    // Unit the composite reads the HDR scene color from; bound only around that draw
    private const int SceneUnit = 9;
    private readonly uint _vao;
    private readonly uint _vbo;
    private bool _disposed;

    /// <param name="gl">The context.</param>
    /// <param name="cloudShadowUnit">The texture unit the cloud-shadow coverage map stays bound on.</param>
    public GodraysRenderer(GL gl, int cloudShadowUnit)
    {
        _gl = gl;

        _march = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/rays-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/godrays-fragment.glsl"));
        // Sampler units and the tuning are program state, set once
        _march.Use();
        _march.SetUniform("uSceneDepth", 4);
        _march.SetUniform("uShadowMap", 1);
        _march.SetUniform("uCloudShadowMap", cloudShadowUnit);
        _march.SetUniform("uCloudCells", CloudField.Cells);
        _march.SetUniform("uCloudHeight", WorldGenerationConfig.CloudHeight);
        _march.SetUniform("uCloudField", WorldGenerationConfig.CloudFieldSize);
        _march.SetUniform("uMaxDistance", WorldGenerationConfig.GodraysMaxDistance);
        _march.SetUniform("uSteps", WorldGenerationConfig.GodraysSteps);
        _march.SetUniform("uPhaseExponent", WorldGenerationConfig.GodraysPhaseExponent);
        _march.SetUniform("uPhaseFloor", WorldGenerationConfig.GodraysPhaseFloor);
        _march.SetUniform("uDensity", WorldGenerationConfig.GodraysDensityPerBlock);
        _march.SetUniform("uMaxAdd", WorldGenerationConfig.GodraysMaxAdd);
        _march.SetUniform("uSkyBlocks", WorldGenerationConfig.GodraysSkyBlocks);

        // The composite blurs the march radially toward the sun and adds it in display space
        _composite = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/rays-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/godrays-composite-fragment.glsl"));
        _composite.Use();
        _composite.SetUniform("uRays", 5);
        _composite.SetUniform("uScene", SceneUnit);
        _composite.SetUniform("uBlurLength", WorldGenerationConfig.GodraysBlurLength);
        _composite.SetUniform("uDecay", WorldGenerationConfig.GodraysBlurDecay);
        _composite.SetUniform("uSamples", WorldGenerationConfig.GodraysBlurSamples);

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
    /// Marches into <paramref name="halfResFbo"/> at <paramref name="halfResSize"/>, then composites
    /// <paramref name="halfResTex"/> into <paramref name="sceneFbo"/> (the color-only HDR framebuffer)
    /// at <paramref name="viewportSize"/>, reading the scene color <paramref name="sceneTex"/> it draws
    /// into after a texture barrier. <paramref name="invViewProj"/> inverts exactly the matrix that
    /// wrote the depth on unit 4; <paramref name="lightViewProj"/> is the near cascade's, whose map is
    /// on unit 1. <paramref name="sunColor"/> is display-space; <paramref name="strength"/> is
    /// <see cref="Godrays.Intensity"/>; <paramref name="cloudDrift"/> the
    /// cloud layer's drift and <paramref name="cloudShadow"/> how much a full cloud's shadow removes;
    /// <paramref name="sunUv"/> is the sun's place on screen (0..1, possibly off screen), which the
    /// composite blurs toward.
    /// </summary>
    public void Render(uint halfResFbo, uint halfResTex, Vector2D<int> halfResSize,
        uint sceneFbo, uint sceneTex, Vector2D<int> viewportSize, Matrix4x4 invViewProj, Vector3 cameraPos,
        Vector3 sunDir, Vector3 sunColor, float strength, Matrix4x4 lightViewProj, Vector2 cloudDrift, float cloudShadow,
        Vector2 sunUv)
    {
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        // The march owns every texel of its target (one oversized triangle), so nothing to blend
        _gl.Disable(EnableCap.Blend);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, halfResFbo);
        _gl.Viewport(0, 0, (uint)halfResSize.X, (uint)halfResSize.Y);

        _march.Use();
        _march.SetUniform("uInvViewProj", invViewProj);
        _march.SetUniform("uLightViewProj", lightViewProj);
        _march.SetUniform("uCameraPos", cameraPos);
        _march.SetUniform("uSunDir", sunDir);
        _march.SetUniform("uSunColor", sunColor);
        _march.SetUniform("uStrength", strength);
        _march.SetUniform("uCloudDrift", cloudDrift);
        _march.SetUniform("uCloudShadow", cloudShadow);

        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, sceneFbo);
        _gl.Viewport(0, 0, (uint)viewportSize.X, (uint)viewportSize.Y);

        // Only now is the march output safe to sample; the scene color is read where it is written,
        // texel for texel, after the barrier makes this frame's earlier writes visible
        _gl.ActiveTexture(TextureUnit.Texture5);
        _gl.BindTexture(TextureTarget.Texture2D, halfResTex);
        _gl.ActiveTexture(TextureUnit.Texture0 + SceneUnit);
        _gl.BindTexture(TextureTarget.Texture2D, sceneTex);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.TextureBarrier();

        _composite.Use();
        _composite.SetUniform("uSunUv", sunUv);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        // Leave no unit holding a render target
        _gl.ActiveTexture(TextureUnit.Texture5);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.ActiveTexture(TextureUnit.Texture0 + SceneUnit);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.ActiveTexture(TextureUnit.Texture0);

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _march.Dispose();
        _composite.Dispose();
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        GC.SuppressFinalize(this);
    }
}
