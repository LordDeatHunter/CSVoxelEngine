using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// The sky dome: one oversized full-screen triangle (the same shape
/// <see cref="UnderwaterRaysRenderer"/> draws) shaded by sky-fragment.glsl from the frame's
/// <see cref="SkyPalette"/>. It is drawn first, straight after the clear and before the sun
/// disc, with the depth test and depth writes off, so everything after it occludes it, which is
/// also what keeps its stars behind every piece of terrain.
/// Binds no textures, so <see cref="RenderManager"/>'s texture-unit cache stays valid: the star
/// occlusion samples the cloud-shadow map on the unit given to the constructor, which the caller
/// keeps bound for the whole run.
/// </summary>
public sealed class SkyRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private bool _disposed;

    /// <param name="gl">The context.</param>
    /// <param name="cloudShadowUnit">The texture unit the cloud-shadow coverage map stays bound on.</param>
    public SkyRenderer(GL gl, int cloudShadowUnit)
    {
        _gl = gl;

        _shader = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/rays-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/sky-fragment.glsl"));

        // The star and cloud-cover tuning never changes at run time, so it goes up once.
        _shader.Use();
        _shader.SetUniform("uStarCells", (float)WorldGenerationConfig.StarCellsPerFace);
        _shader.SetUniform("uStarDensity", WorldGenerationConfig.StarDensity);
        _shader.SetUniform("uStarMargin", StarField.CellMargin);
        _shader.SetUniform("uStarBrightness", WorldGenerationConfig.StarBrightness);
        _shader.SetUniform("uStarFaintest", WorldGenerationConfig.StarFaintest);
        _shader.SetUniform("uStarCurve", WorldGenerationConfig.StarBrightnessCurve);
        _shader.SetUniform("uStarRadiusPx", WorldGenerationConfig.StarRadiusPixels);
        _shader.SetUniform("uStarBrightRadius", WorldGenerationConfig.StarBrightRadiusScale);
        _shader.SetUniform("uStarHorizonFade", WorldGenerationConfig.StarHorizonFade);
        _shader.SetUniform("uStarTwinkleDepth", WorldGenerationConfig.StarTwinkleDepth);
        _shader.SetUniform("uStarTwinkleRate",
            new Vector2(WorldGenerationConfig.StarTwinkleRateMin, WorldGenerationConfig.StarTwinkleRateMax));
        _shader.SetUniform("uCloudShadowMap", cloudShadowUnit);
        _shader.SetUniform("uCloudCells", CloudField.Cells);
        _shader.SetUniform("uCloudMaxLevels", WorldGenerationConfig.CloudMaxLevels);
        _shader.SetUniform("uCloudHeight", WorldGenerationConfig.CloudHeight);
        _shader.SetUniform("uCloudLayer", WorldGenerationConfig.CloudLayerThickness);
        _shader.SetUniform("uCloudField", WorldGenerationConfig.CloudFieldSize);
        _shader.SetUniform("uCloudFog", new Vector2(WorldGenerationConfig.FogNearBlocks, WorldGenerationConfig.FogFarBlocks));
        _shader.SetUniform("uGlareExponent", WorldGenerationConfig.SunGlareExponent);

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
    /// <paramref name="tintAmount"/> (lava); it stays dry underwater so the Snell window shows sky,
    /// stars included. <paramref name="frame"/> carries the night sky: the stars are a function of
    /// the view direction alone, so the mirror's dome shows each one where its reflection belongs.
    /// </summary>
    public void Render(Matrix4x4 invViewRotProj, in SkyPalette sky, Vector3 sunDir, Vector3 tint, float tintAmount,
        in SkyDomeFrame frame)
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

        _shader.SetUniform("uStarVisibility", frame.StarVisibility);
        _shader.SetUniform("uStarSpin", new Vector2(MathF.Cos(frame.StarSpin), MathF.Sin(frame.StarSpin)));
        _shader.SetUniform("uStarTime", frame.StarTime);
        // No star inside the sun's glow, feathered back in over half the radius again; the
        // glow follows the player's sun-size setting, so the mask does too.
        var maskDeg = GameSettings.SunSize * WorldGenerationConfig.SunGlowScale * WorldGenerationConfig.StarSunMaskScale;
        _shader.SetUniform("uStarSunMask", new Vector2(MathUtils.CosDeg(maskDeg * 1.5f), MathUtils.CosDeg(maskDeg)));
        _shader.SetUniform("uEye", frame.Eye);
        _shader.SetUniform("uCloudDrift", frame.CloudDrift);
        _shader.SetUniform("uCloudBlocking", CloudCover.Blocking(GameSettings.CloudOpacity));
        _shader.SetUniform("uGlareColor", frame.GlareColor);

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
