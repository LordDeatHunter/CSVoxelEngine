using System;
using System.Numerics;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using VoxelEngine.Effects.Post;

namespace VoxelEngine.Rendering.ScreenSpace;

/// <summary>
/// The sun dazzle: the blinding bloom of looking straight at the sun, over the whole frame, and
/// the lens flare (<see cref="LensFlare"/>): the row of tinted circles the sun throws across the
/// picture when it is off to one side. Two draws of one triangle. The first (dazzle-visibility-fragment.glsl) is a single fragment into
/// this renderer's own 1x1 target: it counts how much of the sun's disc is in open sky from the
/// scene depth and blends that into the target, which therefore holds the eased share with no
/// readback. The second (dazzle-fragment.glsl) adds the glow (<see cref="SunDazzle.Glow"/>) and
/// the flare's circles times that share into the caller's color-only HDR framebuffer, in linear light.
/// Texture units read: 4 (the scene depth) by the first draw, bound by the caller, and 5 (the 1x1
/// target), bound here around the second. Owns its blend and depth state and puts the world's back
/// on the way out.
/// </summary>
public sealed class SunDazzleRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _visibility;
    private readonly Shader _glow;
    // The 1x1 target holding the eased share of the sun's disc in open sky
    private readonly uint _visibilityFbo;
    private readonly uint _visibilityTex;
    private readonly uint _vao;
    private readonly uint _vbo;
    private bool _disposed;

    public SunDazzleRenderer(GL gl)
    {
        _gl = gl;

        _visibility = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/rays-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/dazzle-visibility-fragment.glsl"));
        // Sampler units and the tuning are program state, set once
        _visibility.Use();
        _visibility.SetUniform("uSceneDepth", 4);
        _visibility.SetUniform("uTaps", PostProcessConfig.SunDazzleTaps);

        _glow = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/rays-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/dazzle-fragment.glsl"));
        _glow.Use();
        _glow.SetUniform("uVisibility", 5);
        _glow.SetUniform("uCoreRadius", PostProcessConfig.SunDazzleCoreRadius);
        _glow.SetUniform("uVeilShare", PostProcessConfig.SunDazzleVeilShare);
        _glow.SetUniform("uVeilReach", PostProcessConfig.SunDazzleVeilReach);
        _glow.SetUniform("uRayShare", PostProcessConfig.SunDazzleRayShare);
        _glow.SetUniform("uRayReach", PostProcessConfig.SunDazzleRayReach);
        // The lens flare's circles (LensFlare), drawn by the same shader
        var ghosts = Math.Min(PostProcessConfig.LensFlareGhosts.Length, LensFlare.MaxGhosts);
        _glow.SetUniform("uGhostCount", ghosts);
        _glow.SetUniformArray4("uGhostShape", LensFlare.MaxGhosts, LensFlare.PackShapes());
        _glow.SetUniformArray4("uGhostColor", LensFlare.MaxGhosts, LensFlare.PackColors());

        // One float, starting hidden; the first draw replaces it whole (SunDazzle.EaseShare)
        _visibilityTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _visibilityTex);
        ReadOnlySpan<float> hidden = [0f];
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R16f, 1, 1, 0, PixelFormat.Red, PixelType.Float, hidden);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.BindTexture(TextureTarget.Texture2D, 0);

        _visibilityFbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _visibilityFbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _visibilityTex, 0);
        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new Exception($"Sun dazzle visibility framebuffer incomplete: {status}");
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

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
    /// Measures the sun's disc against the scene depth on unit 4, then adds the dazzle into
    /// <paramref name="sceneFbo"/> (the color-only HDR framebuffer) at <paramref name="viewportSize"/>.
    /// <paramref name="sunUv"/> is the sun's place on screen (0..1, possibly off screen) and
    /// <paramref name="discRadius"/> its disc's radius as a share of the screen height
    /// (<see cref="SunDazzle.DiscRadius"/>); <paramref name="color"/> is the linear sun color times
    /// <see cref="SunDazzle.Intensity"/>, <paramref name="flare"/> the same color times
    /// <see cref="LensFlare.Intensity"/> and <see cref="PostProcessConfig.LensFlareLight"/> and
    /// <paramref name="flareCover"/> the flare's <see cref="LensFlare.Cover"/>; <paramref name="ease"/> is how far the eased share moves
    /// toward this frame's measurement (<see cref="SunDazzle.EaseShare"/>).
    /// </summary>
    public void Render(uint sceneFbo, Vector2D<int> viewportSize, Vector2 sunUv, float discRadius, Vector3 color, Vector3 flare,
        float flareCover, float ease)
    {
        var aspect = viewportSize.X / (float)Math.Max(1, viewportSize.Y);

        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.Enable(EnableCap.Blend);
        // dst + (src - dst) * ease: the target eases toward this frame's count
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _visibilityFbo);
        _gl.Viewport(0, 0, 1, 1);

        _visibility.Use();
        _visibility.SetUniform("uSunUv", sunUv);
        _visibility.SetUniform("uDiscUv", new Vector2(discRadius / aspect, discRadius));
        _visibility.SetUniform("uEase", ease);

        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, sceneFbo);
        _gl.Viewport(0, 0, (uint)viewportSize.X, (uint)viewportSize.Y);
        // Premultiplied: the dazzle writes alpha 0, so its light simply adds, and the flare's circles
        // take a little of the picture behind them away; the target's own alpha is left alone
        _gl.BlendFuncSeparate(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.Zero, BlendingFactor.One);

        // Only now is the 1x1 target safe to sample
        _gl.ActiveTexture(TextureUnit.Texture5);
        _gl.BindTexture(TextureTarget.Texture2D, _visibilityTex);

        _glow.Use();
        _glow.SetUniform("uSunUv", sunUv);
        _glow.SetUniform("uAspect", aspect);
        _glow.SetUniform("uColor", color);
        _glow.SetUniform("uFlare", flare);
        _glow.SetUniform("uFlareCover", flareCover);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        // Leave no unit holding a render target
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.ActiveTexture(TextureUnit.Texture0);

        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _visibility.Dispose();
        _glow.Dispose();
        _gl.DeleteFramebuffer(_visibilityFbo);
        _gl.DeleteTexture(_visibilityTex);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        GC.SuppressFinalize(this);
    }
}
