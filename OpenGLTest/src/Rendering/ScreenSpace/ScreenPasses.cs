using System;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Effects.Post;
using VoxelEngine.Juice;
using VoxelEngine.Player;
using VoxelEngine.Rendering.Atmosphere;
using VoxelEngine.UI;

namespace VoxelEngine.Rendering.ScreenSpace;

/// <summary>
/// The full-screen passes over the HDR scene: the tonemap resolve into the default framebuffer,
/// the screen-space contact AO, the underwater sun shafts, the godrays and the sun dazzle. Owns their five
/// renderers (built in its constructor, which <see cref="RenderManager"/> runs from LoadResources,
/// and freed by <see cref="Dispose"/> at the same point of RenderManager's teardown); the targets,
/// the texture units and the frame's light are read live through the owner. Render thread only.
/// </summary>
internal sealed class ScreenPasses : IDisposable
{
    private readonly GL _gl;
    private readonly RenderManager _owner;

    // Underwater sun shafts, one additive full-screen pass. Built by the constructor.
    private UnderwaterRaysRenderer? _rays;

    // Godrays: the same march over dry air (RenderGodrays). Built by the constructor.
    private GodraysRenderer? _godrays;

    // The sun dazzle: the bloom of looking straight at the sun (RenderSunDazzle), and the lighting
    // clock at its last draw, which its eased visibility steps from. Built by the constructor.
    private SunDazzleRenderer? _dazzle;
    private double _dazzleDrawnAt = double.NegativeInfinity;

    // The frame's resolve pass: tonemaps the HDR scene target into the default framebuffer at
    // the end of WorldRenderer.Render, before the UI (RenderPostProcess). Built by the constructor.
    private PostProcessRenderer? _post;
    private ContactAoRenderer? _contactAo;

    private FrameLighting Lighting => _owner.Lighting;
    private TextureUnits Units => _owner.Units;
    private RenderTargets Targets => _owner.Targets;

    internal ScreenPasses(GL gl, RenderManager owner)
    {
        _gl = gl;
        _owner = owner;
        _post = new PostProcessRenderer(gl);
        _contactAo = new ContactAoRenderer(gl);
        _rays = new UnderwaterRaysRenderer(gl);
        _godrays = new GodraysRenderer(gl, RenderManager.CloudShadowUnit);
        _dazzle = new SunDazzleRenderer(gl);
    }

    /// <summary>
    /// Resolves the HDR scene target into the default framebuffer (exposure, clamp tonemap,
    /// sRGB encode and dither in post-fragment.glsl). Runs at the end of WorldRenderer.Render; the UI then
    /// draws on top of the result in gamma space. The scene texture is unbound from unit 0 again
    /// afterwards so next frame's render target never sits on a sampler unit.
    /// <paramref name="damageFlash"/> is the player's damage flash (<see cref="PlayerHealth.DamageFlash"/>),
    /// which drives the damage chromatic fringe.
    /// </summary>
    internal void RenderPostProcess(float damageFlash = 0f)
    {
        if (_post == null || Targets.HdrFbo == 0) return;
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(Targets.ViewportSize);
        Units.BindUnit(0, TextureTarget.Texture2D, Targets.HdrColorTex);
        Units.ActiveUnit(0);
        // The eye adaptation (FrameLighting.SetLighting) scales the exposure up in the dark; the
        // biome grade tints the finished image faintly warm over the desert and cool over the snow;
        // the damage flash (the vignette's, PlayerHealth.DamageFlash) splits the screen edges for a
        // moment after a hit; the eye under water (not lava, an opaque glow) sways the image
        // (UnderwaterWobble)
        _post.Render(Targets.ViewportSize, new PostEffects(LightingConfig.TonemapExposure * Lighting.EyeExposure, Lighting.Grade,
            DamageFringe.Strength(damageFlash), Lighting.EyeInLava ? 0f : MathUtils.SmoothStep(Lighting.UnderwaterT),
            UnderwaterWobble.WrapTime(Lighting.TimeD)));
        Units.BindUnit(0, TextureTarget.Texture2D, 0);
    }

    /// <summary>
    /// Multiplies the screen-space contact AO (SH-28, <see cref="ContactAoRenderer"/>) into the
    /// HDR scene color. WorldRenderer.Render calls it mid entity pass: after everything that writes
    /// honest depth (terrain, drops, stations, TNT, falling blocks, slimes) and before the
    /// depth-less or blended draws (healthbars, the first-person head, debris, player, held item),
    /// the selection outline, water, clouds and particles, none of which own the depth it reads.
    /// It draws through <see cref="RenderTargets.HdrColorOnlyFbo"/>, which shares the color texture but not the
    /// depth, so sampling <see cref="RenderTargets.HdrDepthTex"/> is no feedback loop, and puts the world's
    /// depth, blend and target state back on the way out.
    /// </summary>
    internal void RenderContactAo(Camera camera)
    {
        if (!PostProcessConfig.ContactAoEnabled || _contactAo == null || Targets.HdrColorOnlyFbo == 0) return;
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, Targets.HdrColorOnlyFbo);
        _gl.Viewport(Targets.ViewportSize);
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.Enable(EnableCap.Blend);
        // dst * src: the pass outputs a 0..1 multiplier
        _gl.BlendFunc(BlendingFactor.Zero, BlendingFactor.SrcColor);
        Units.BindUnit(0, TextureTarget.Texture2D, Targets.HdrDepthTex);
        Units.ActiveUnit(0);

        var pxPerBlock = ContactAo.PxPerBlockAtUnitDist(Targets.ViewportSize.Y, MathUtils.ToRadians(camera.CurrentFov));
        _contactAo.Render(Lighting.InvViewProj, camera.RenderEye, Targets.ViewportSize, pxPerBlock, ContactAoSettings.Default);

        Units.BindUnit(0, TextureTarget.Texture2D, 0);
        Units.ActiveUnit(0);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        Targets.BindSceneTarget();
    }

    /// <summary>Sun shafts through the water, drawn while the eye is submerged. Needs the depth of
    /// everything drawn so far (water surface included), so the depth texture is re-copied here
    /// before the march.
    /// The march itself runs at HALF resolution into <see cref="RenderTargets.RaysFbo"/> and is composited back
    /// to full resolution by a depth-aware upsample (PERF_TODO R2) - a quarter of the marched
    /// pixels, with the loop's step count, sample count and per-sample formula untouched.
    /// The depth copy stays FULL resolution deliberately: the composite runs at full-res pixels and
    /// weighs each half-res tap against the distance the full-res pixel marches, so it needs a
    /// full-res depth reference, and the default framebuffer's own depth is unsamplable while it is
    /// the draw target (which is why <see cref="RenderTargets.CopyScene"/> exists). A half-res depth
    /// alongside this one would be additive cost, not a saving - see R3 in PERF_TODO.md, which
    /// shares this machinery.
    /// Both draws sit inside this one method, so the RaysPass GPU bracket around the call in
    /// WorldRenderer.Render still measures the whole pass and FrameProfiler needs no change.</summary>
    internal void RenderUnderwaterRays(Camera? camera)
    {
        if (camera == null || _rays == null || Lighting.EyeInLava) return;
        var wet = MathUtils.SmoothStep(Lighting.UnderwaterT);
        // The shafts are added in display space (rays-upsample-fragment.glsl), so they take the
        // display-space sun color and the pre-SH-10 strength, as they did before the scene went
        // linear.
        var strength = wet * Lighting.ShadowStrengthDry * Lighting.Dayness * GameSettings.WaterSunShafts;
        if (strength <= 0.0005f) return;
        if (Targets.RaysFbo == 0) return;             // no target to march into (never happens after LoadResources)
        Targets.CopyScene(color: false);
        Units.BindUnit(1, TextureTarget.Texture2D, _owner.NearShadow.DepthTex);
        Units.BindUnit(4, TextureTarget.Texture2D, Targets.SceneDepthTex);
        Units.ActiveUnit(0);
        _rays.Render(Targets.RaysFbo, Targets.RaysTex, Targets.RaysSize, Targets.HdrFbo, Targets.HdrColorTex, Targets.ViewportSize, Lighting.InvViewProj, Lighting.CameraPos, Lighting.SunDir,
            Lighting.SunSrgb, strength, camera.EyeWaterSurfaceY, WaterRenderConfig.WaterAbsorb, _owner.NearShadow.ViewProj, Lighting.Time);
        // UnderwaterRaysRenderer selects unit 5 for the upsample and puts unit 0 back. Unit 5 is
        // untracked so the records happen to survive, but the renderer does not promise that.
        Units.InvalidateTextureUnits();
    }

    /// <summary>
    /// Godrays: light shafts through trees, clouds and gaps in dry air when the sun is in view
    /// (<see cref="GodraysRenderer"/>), drawn after everything else in the world, like the underwater
    /// shafts, whose half-resolution target it borrows (the two never need it at once: the godrays
    /// fade out as the eye goes under). It reads the HDR scene's own depth texture, marching it
    /// through the near shadow cascade, so it needs no scene copy, and composites through the
    /// color-only HDR framebuffer so that read is no feedback loop. Skipped when the frame's strength
    /// (<see cref="Godrays.Intensity"/>, set by <see cref="FrameLighting.SetLighting"/>) is negligible, when the
    /// shadow passes did not run (shadows off, or the sun too low for them), and when
    /// <see cref="PostProcessConfig.GodraysEnabled"/> is off.
    /// </summary>
    internal void RenderGodrays(Camera? camera)
    {
        if (camera == null || _godrays == null || !PostProcessConfig.GodraysEnabled) return;
        if (Lighting.GodrayStrength <= 0.0005f || Lighting.ShadowStrengthDry <= 0f) return;
        if (Targets.RaysFbo == 0 || Targets.HdrColorOnlyFbo == 0) return;
        // The sun's place on screen, which the composite blurs toward
        if (!TrySunUv(out var sunUv)) return;
        Units.BindUnit(1, TextureTarget.Texture2D, _owner.NearShadow.DepthTex);
        Units.BindUnit(4, TextureTarget.Texture2D, Targets.HdrDepthTex);
        Units.ActiveUnit(0);
        var cloudShadow = GameSettings.CloudOpacity > 0f ? PostProcessConfig.GodraysCloudShadow : 0f;
        _godrays.Render(Targets.RaysFbo, Targets.RaysTex, Targets.RaysSize, Targets.HdrColorOnlyFbo, Targets.HdrColorTex, Targets.ViewportSize, Lighting.InvViewProj,
            Lighting.CameraPos, Lighting.SunDir, Lighting.SunSrgb, Lighting.GodrayStrength,
            _owner.NearShadow.ViewProj, CloudCopies.Drift(Lighting.TimeD, SkyConfig.CloudFieldSize), cloudShadow, sunUv);
        // The renderer bound units 5 and 9 behind the cache's back, and unit 4 must not keep the
        // scene depth (the next frame renders into it)
        Units.InvalidateTextureUnits();
        Units.BindUnit(4, TextureTarget.Texture2D, 0);
        Units.ActiveUnit(0);
        Targets.BindSceneTarget();
    }

    /// <summary>
    /// The sun dazzle: the blinding bloom of looking straight at the sun
    /// (<see cref="SunDazzleRenderer"/>), added over the finished world right after the godrays, so
    /// it lies over the foreground too. The renderer measures how much of the sun's disc is in open
    /// sky from the HDR scene's own depth texture, so like the godrays it needs no scene copy and
    /// draws through the color-only HDR framebuffer. Skipped when the frame's strength
    /// (<see cref="SunDazzle.Intensity"/>, set by <see cref="FrameLighting.SetLighting"/>) is
    /// negligible, with the sun behind the eye, and when
    /// <see cref="PostProcessConfig.SunDazzleEnabled"/> is off.
    /// </summary>
    internal void RenderSunDazzle(Camera? camera)
    {
        if (camera == null || _dazzle == null || !PostProcessConfig.SunDazzleEnabled) return;
        if (Lighting.DazzleStrength <= 0.0005f || Targets.HdrColorOnlyFbo == 0) return;
        if (!TrySunUv(out var sunUv)) return;
        // The eased visibility steps by the time since the pass last drew, so after a pause it
        // takes this frame's measurement whole
        var ease = SunDazzle.EaseShare((float)(Lighting.TimeD - _dazzleDrawnAt));
        _dazzleDrawnAt = Lighting.TimeD;
        Units.BindUnit(4, TextureTarget.Texture2D, Targets.HdrDepthTex);
        Units.ActiveUnit(0);
        _dazzle.Render(Targets.HdrColorOnlyFbo, Targets.ViewportSize, sunUv,
            SunDazzle.DiscRadius(GameSettings.SunSize, camera.CurrentFov), Lighting.SunDiscColor * Lighting.DazzleStrength, ease);
        // The renderer bound unit 5 behind the cache's back, and unit 4 must not keep the scene
        // depth (the next frame renders into it)
        Units.InvalidateTextureUnits();
        Units.BindUnit(4, TextureTarget.Texture2D, 0);
        Units.ActiveUnit(0);
        Targets.BindSceneTarget();
    }

    /// <summary>
    /// The sun's place on screen (0..1, possibly off screen) under the matrix the scene was drawn
    /// with; false with the sun behind the eye, where it has none.
    /// </summary>
    private bool TrySunUv(out Vector2 sunUv)
    {
        var sunClip = Vector4.Transform(new Vector4(Lighting.CameraPos + Lighting.SkySunDir * 1000f, 1f), Lighting.SceneViewProj);
        sunUv = default;
        if (sunClip.W <= 1e-4f) return false;
        sunUv = new Vector2(sunClip.X / sunClip.W, sunClip.Y / sunClip.W) * 0.5f + new Vector2(0.5f);
        return true;
    }

    /// <summary>Frees the five renderers, in the order RenderManager.Dispose always freed them.</summary>
    public void Dispose()
    {
        _post?.Dispose();
        _contactAo?.Dispose();
        _rays?.Dispose();
        _godrays?.Dispose();
        _dazzle?.Dispose();
    }
}
