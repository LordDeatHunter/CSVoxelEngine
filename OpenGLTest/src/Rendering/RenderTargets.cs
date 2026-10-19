using System;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using VoxelEngine.Rendering.ScreenSpace;

namespace VoxelEngine.Rendering;

/// <summary>
/// The viewport size and the render targets sized to it: the linear HDR scene target every
/// world pass renders into (and its color-only view), the scene copies the water pass
/// refracts through, and the half-resolution target of the underwater ray march and the
/// godrays. Creates, recreates on a resize and deletes them, binds the scene target back
/// after every mid-frame detour and snapshots it for the water. Render thread only.
/// </summary>
internal sealed class RenderTargets
{
    private readonly GL _gl;
    private readonly RenderManager _owner;

    // The linear HDR scene target every world pass renders into (BindSceneTarget): an RGBA16F
    // color texture plus a DepthComponent24 depth *texture* (sampleable, compare mode off), sized
    // to the viewport by CreateHdrTarget. Only the post pass and the UI touch framebuffer 0.
    private uint _hdrFbo;
    private uint _hdrColorTex;
    private uint _hdrDepthTex;
    // The same _hdrColorTex on ColorAttachment0 with no depth attachment: a full-screen pass can
    // blend into the scene color here while sampling _hdrDepthTex, with no feedback loop,
    // because the depth texture it reads is not attached to the framebuffer it writes.
    private uint _hdrColorOnlyFbo;
    private Vector2D<int> _hdrSize;

    // The window's viewport, which every target here is sized to (RenderManager.SetViewport keeps
    // it current).
    private Vector2D<int> _viewportSize;

    // The scene copies the water pass refracts through: the HDR scene target's color on
    // unit 3 and its depth on unit 4, snapshotted by CopyScene just before the water draws.
    private uint _sceneColorTex;
    private uint _sceneDepthTex;
    private Vector2D<int> _sceneSize;

    // The half-resolution target the underwater sun-shaft march renders into before a
    // full-res depth-aware upsample composites it back (see CreateRaysTarget). Layout is
    // RGBA16F at half the viewport per axis: rgb is the additive shaft color and a is the
    // world-space distance that pixel marched, capped at
    // WaterRenderConfig.WaterRayMaxDistance, which the upsample keys its depth-aware
    // weights off. No depth attachment - the march is a full-screen triangle with the depth
    // test off, so there is nothing to test against.
    private uint _raysFbo;
    private uint _raysTex;
    private Vector2D<int> _raysSize;

    // Live views of the targets for the passes that bind, sample or size against them.
    internal Vector2D<int> ViewportSize { get => _viewportSize; set => _viewportSize = value; }
    internal uint HdrFbo => _hdrFbo;
    internal uint HdrColorTex => _hdrColorTex;
    internal uint HdrDepthTex => _hdrDepthTex;
    internal uint HdrColorOnlyFbo => _hdrColorOnlyFbo;
    internal uint SceneColorTex => _sceneColorTex;
    internal uint SceneDepthTex => _sceneDepthTex;
    internal uint RaysFbo => _raysFbo;
    internal uint RaysTex => _raysTex;
    internal Vector2D<int> RaysSize => _raysSize;

    private TextureUnits Units => _owner.Units;

    internal RenderTargets(GL gl, RenderManager owner)
    {
        _gl = gl;
        _owner = owner;
    }

    /// <summary>
    /// Allocates the two full-resolution textures <see cref="CopyScene"/> snapshots the HDR scene
    /// target into. No FBO and no depth attachment: these are copy destinations only, read
    /// back by the water fragment shader on units 3 and 4. Called from
    /// <see cref="RenderManager.LoadResources"/> and <see cref="RenderManager.SetViewport"/>,
    /// recreating only on a resize.
    /// </summary>
    internal void CreateSceneCopy()
    {
        var w = Math.Max(2, _viewportSize.X);
        var h = Math.Max(2, _viewportSize.Y);
        if (_sceneColorTex != 0 && _sceneSize.X == w && _sceneSize.Y == h) return;

        DeleteSceneCopy();
        _sceneSize = new Vector2D<int>(w, h);

        _sceneColorTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _sceneColorTex);
        unsafe
        {
            // Half-float like the HDR target it copies, so the refraction carries linear values.
            _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba16f,
                (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.Float, null);
        }
        // Linear: the refraction offset lands between texels, and a nearest fetch there
        // turns the ripple into a staircase.
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _sceneDepthTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _sceneDepthTex);
        unsafe
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.DepthComponent24,
                (uint)w, (uint)h, 0, PixelFormat.DepthComponent, PixelType.Float, null);
        }
        // Nearest, and deliberately no compare mode: unlike the shadow map this one is read
        // as a plain sampler2D for its raw depth value, not as a hardware depth test.
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _gl.BindTexture(TextureTarget.Texture2D, 0);
        // Recreate path only (the size-unchanged early return above binds nothing).
        Units.InvalidateTextureUnits();
    }

    internal void DeleteSceneCopy()
    {
        if (_sceneColorTex == 0) return;
        _gl.DeleteTexture(_sceneColorTex);
        _gl.DeleteTexture(_sceneDepthTex);
        _sceneColorTex = 0;
        _sceneDepthTex = 0;
        // Deleting a texture reverts its bindings to 0 on every unit of the current context.
        Units.InvalidateTextureUnits();
    }

    /// <summary>
    /// Builds the half-resolution color target the underwater ray march writes into, sized
    /// from the viewport by <see cref="HalfResTarget"/>. Called from
    /// <see cref="RenderManager.LoadResources"/> and <see cref="RenderManager.SetViewport"/>,
    /// recreating only when the half-res size actually changed - two adjacent viewport widths
    /// often share one, which is what keeps a drag-resize from churning GL objects on every
    /// pixel of drag.
    /// <para>
    /// Unlike <see cref="ReflectionPass.CreateReflectionTarget"/> this one needs no validity flag
    /// and cannot flash on a resize: the march covers the target with a full-screen triangle, blending
    /// off and no clear, on every frame it runs, and nothing samples the texture before that
    /// draw. The undefined contents of a fresh allocation are therefore never observable.
    /// </para>
    /// Like its two siblings this assumes <c>ActiveTexture</c> is unit 0 on entry - the
    /// convention <see cref="CopyScene"/> restores before it returns.
    /// </summary>
    internal void CreateRaysTarget()
    {
        var desired = HalfResTarget.For(_viewportSize.X, _viewportSize.Y);
        if (!HalfResTarget.ShouldRecreate(_raysFbo != 0, new HalfResTarget.Size(_raysSize.X, _raysSize.Y), desired))
            return;

        DeleteRaysTarget();
        _raysSize = new Vector2D<int>(desired.Width, desired.Height);

        _raysTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _raysTex);
        unsafe
        {
            // Half-float, not Rgba8: alpha carries a marched distance in blocks (up to
            // WaterRayMaxDistance), which a normalized 8-bit channel cannot hold, and 8-bit
            // rgb would re-band the deliberately dithered, dim additive term.
            _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba16f,
                (uint)desired.Width, (uint)desired.Height, 0, PixelFormat.Rgba, PixelType.Float, null);
        }
        // The upsample fetches texels explicitly and clamps the coordinates itself, so the
        // filter and wrap modes here are belt-and-braces.
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _raysFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _raysFbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _raysTex, 0);

        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new Exception($"Underwater rays framebuffer incomplete: {status}");

        // Back to the scene target (the HDR FBO once it exists, framebuffer 0 before that).
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _hdrFbo);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        // Recreate path only (the size-unchanged early return above binds nothing).
        Units.InvalidateTextureUnits();
    }

    internal void DeleteRaysTarget()
    {
        if (_raysFbo == 0) return;
        _gl.DeleteFramebuffer(_raysFbo);
        _gl.DeleteTexture(_raysTex);
        _raysFbo = 0;
        // Deleting a texture reverts its bindings to 0 on every unit of the current context.
        Units.InvalidateTextureUnits();
    }

    /// <summary>
    /// Builds the linear HDR scene target every world pass renders into: an RGBA16F color
    /// texture and a sampleable DepthComponent24 depth texture (nothing uses stencil), plus a
    /// color-only FBO over the same color texture, all sized to the viewport.
    /// Called first from <see cref="RenderManager.LoadResources"/> and
    /// <see cref="RenderManager.SetViewport"/>, so the other target creators restore to it, and
    /// recreating only on a resize. Nearest filtering: the post pass reads it with
    /// <c>texelFetch</c> only. Leaves framebuffer 0 bound, like the window state it was called
    /// from; <see cref="RenderManager.BeginFrame"/> binds the target each frame.
    /// </summary>
    internal void CreateHdrTarget()
    {
        var w = Math.Max(2, _viewportSize.X);
        var h = Math.Max(2, _viewportSize.Y);
        if (_hdrFbo != 0 && _hdrSize.X == w && _hdrSize.Y == h) return;

        DeleteHdrTarget();
        _hdrSize = new Vector2D<int>(w, h);

        _hdrColorTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _hdrColorTex);
        unsafe
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba16f,
                (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.Float, null);
        }
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        // A texture rather than a renderbuffer so a full-screen pass can sample the scene depth
        // (with texelFetch; compare mode off, or sampler2D reads would be undefined).
        _hdrDepthTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _hdrDepthTex);
        unsafe
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.DepthComponent24,
                (uint)w, (uint)h, 0, PixelFormat.DepthComponent, PixelType.UnsignedInt, null);
        }
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)GLEnum.None);

        _hdrFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _hdrFbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _hdrColorTex, 0);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, _hdrDepthTex, 0);

        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new Exception($"HDR framebuffer incomplete: {status}");

        // Color-only view of the same target (see _hdrColorOnlyFbo).
        _hdrColorOnlyFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _hdrColorOnlyFbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _hdrColorTex, 0);

        status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new Exception($"HDR color-only framebuffer incomplete: {status}");

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        // Recreate path only (the size-unchanged early return above binds nothing).
        Units.InvalidateTextureUnits();
    }

    internal void DeleteHdrTarget()
    {
        if (_hdrFbo == 0) return;
        // Never leave a deleted FBO as the draw target.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.DeleteFramebuffer(_hdrFbo);
        _gl.DeleteFramebuffer(_hdrColorOnlyFbo);
        _gl.DeleteTexture(_hdrColorTex);
        _gl.DeleteTexture(_hdrDepthTex);
        _hdrFbo = 0;
        _hdrColorOnlyFbo = 0;
        _hdrColorTex = 0;
        _hdrDepthTex = 0;
        // Deleting a texture reverts its bindings to 0 on every unit of the current context.
        Units.InvalidateTextureUnits();
    }

    /// <summary>Binds the HDR scene target at the full viewport: where every world pass draws and
    /// every mid-frame detour (shadows, mirror, rays) returns to - never framebuffer 0.</summary>
    internal void BindSceneTarget()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _hdrFbo);
        _gl.Viewport(_viewportSize);
    }

    /// <summary>Snapshots the HDR scene target's color and depth into the scene textures so the
    /// water pass can refract what is behind the surface and measure how thick the water is. Two
    /// texture copies, no extra FBO: CopyTexSubImage2D reads the bound read framebuffer, which is
    /// the HDR FBO (read buffer ColorAttachment0, the default for an FBO) during the world passes.</summary>
    internal void CopyScene(bool color)
    {
        if (color)
        {
            Units.BindUnit(3, TextureTarget.Texture2D, _sceneColorTex);
            _gl.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, (uint)_sceneSize.X, (uint)_sceneSize.Y);
        }
        Units.BindUnit(4, TextureTarget.Texture2D, _sceneDepthTex);
        _gl.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, (uint)_sceneSize.X, (uint)_sceneSize.Y);
        Units.ActiveUnit(0);
    }
}
