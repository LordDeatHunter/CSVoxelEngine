using System;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using VoxelEngine.Effects.Post;
using VoxelEngine.Rendering.Atmosphere;

namespace VoxelEngine.Rendering.ScreenSpace;

/// <summary>
/// The frame's final full-screen pass: one oversized triangle (the shape <see cref="SkyRenderer"/>
/// draws) shaded by post-fragment.glsl, which reads the linear HDR scene target and writes the
/// tonemapped, sRGB-encoded, FXAA-smoothed, dithered image. It binds no framebuffer and no texture - the caller
/// (<see cref="ScreenPasses.RenderPostProcess"/>) binds the destination and puts the scene
/// texture on unit 0 - so the texture-unit cache (<see cref="TextureUnits"/>) stays its own business.
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
        // Nothing else ever writes these, so they are set once here (the exposure is the default
        // until the first Render sets the frame's).
        _shader.Use();
        _shader.SetUniform("uScene", 0);
        _shader.SetUniform("uDepth", 1);
        _shader.SetUniform("uWavePushBlocks", JuiceConfig.FloorConqueredShockwavePushBlocks);
        _shader.SetUniform("uWaveCount", 0);
        _shader.SetUniform("uExposure", LightingConfig.TonemapExposure);
        _shader.SetUniform("uGrade", System.Numerics.Vector3.One);
        _shader.SetUniform("uDamageFringe", 0f);
        _shader.SetUniform("uFringeShare", PostProcessConfig.DamageFringeScreenShare);
        _shader.SetUniform("uUnderwater", 0f);
        _shader.SetUniform("uWobbleShare", PostProcessConfig.UnderwaterWobbleScreenShare);
        _shader.SetUniform("uWobbleSpeed", PostProcessConfig.UnderwaterWobbleSpeed);
        _shader.SetUniform("uWobbleWaves", UnderwaterWobble.WavesAcross);
        _shader.SetUniform("uFxaaReduceMin", PostProcessConfig.FxaaReduceMin);
        _shader.SetUniform("uFxaaReduceMul", PostProcessConfig.FxaaReduceMul);
        _shader.SetUniform("uFxaaSpanMax", PostProcessConfig.FxaaSpanMax);
        _shader.SetUniform("uFxaaEdgeThreshold", PostProcessConfig.FxaaEdgeThreshold);
        _shader.SetUniform("uFxaaEdgeThresholdMin", PostProcessConfig.FxaaEdgeThresholdMin);

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
    /// is the scene texture's size in pixels, which FXAA clamps its taps to. <paramref name="effects"/>
    /// are the frame's camera effects: the exposure (eye adaptation) and the color grade.
    /// </summary>
    public void Render(Vector2D<int> viewport, in PostEffects effects, in System.Numerics.Matrix4x4 viewProj,
        in System.Numerics.Matrix4x4 invViewProj)
    {
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.Blend);

        _shader.Use();
        _shader.SetUniform("uResolution", new System.Numerics.Vector2(viewport.X, viewport.Y));
        _shader.SetUniform("uExposure", effects.Exposure);
        _shader.SetUniform("uGrade", effects.Grade);
        _shader.SetUniform("uDamageFringe", effects.DamageFringe);
        _shader.SetUniform("uUnderwater", effects.Underwater);
        _shader.SetUniform("uTime", effects.Time);
        SetShockwaves(effects.Shockwaves, viewProj, invViewProj);
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
    }

    // Uploads the shells (the shader's arrays: center and radius, then bottom, top, strength and half thickness)
    private void SetShockwaves(System.Collections.Generic.IReadOnlyList<Shockwave>? waves, in System.Numerics.Matrix4x4 viewProj,
        in System.Numerics.Matrix4x4 invViewProj)
    {
        var count = Math.Min(waves?.Count ?? 0, Shockwave.Max);
        _shader.SetUniform("uWaveCount", count);
        if (count == 0) return;
        _shader.SetUniform("uViewProj", viewProj);
        _shader.SetUniform("uInvViewProj", invViewProj);
        for (var i = 0; i < count; i++)
        {
            var w = waves![i];
            _shader.SetUniform($"uWaveA[{i}]", new System.Numerics.Vector4(w.Center, w.Radius));
            _shader.SetUniform($"uWaveB[{i}]", new System.Numerics.Vector4(w.BottomY, w.TopY, w.Strength, w.HalfThickness));
        }
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
