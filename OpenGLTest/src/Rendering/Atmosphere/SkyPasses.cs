using System;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Effects.Sky;
using VoxelEngine.Player;
using VoxelEngine.UI;

namespace VoxelEngine.Rendering.Atmosphere;

/// <summary>
/// The sky passes: the dome and the sun and moon discs, painted onto the cleared scene target
/// (and into the water mirror) before any terrain, and the translucent cloud layer drawn after
/// the water. Owns no GL resource: the programs, the sprites, the quad, the dome and the clouds
/// are <see cref="RenderManager"/>'s, read live through it. Render thread only.
/// </summary>
internal sealed class SkyPasses
{
    private readonly GL _gl;
    private readonly RenderManager _owner;

    // The sun disc's HDR intensity, scaling its color in RenderSun.
    private const float SunDiscIntensity = 2.5f;

    private FrameLighting Lighting => _owner.Lighting;
    private TextureUnits Units => _owner.Units;
    private RenderTargets Targets => _owner.Targets;

    internal SkyPasses(GL gl, RenderManager owner)
    {
        _gl = gl;
        _owner = owner;
    }

    /// <summary>
    /// Draws the sky dome (<see cref="SkyRenderer"/>) and then the sun disc over it
    /// (<see cref="RenderSun"/>). Follows RenderSun's parameter shape so the reflection pass can
    /// pass its own mirrored view. <paramref name="viewRotation"/> must be translation-free and
    /// keep the camera's roll, since the world is drawn with that roll too. <paramref name="frame"/>
    /// is the frame's night sky, seen from the pass's own eye.
    /// </summary>
    internal void RenderSky(Matrix4x4 viewRotation, Matrix4x4 projection, in SkyDomeFrame frame)
    {
        if (!Matrix4x4.Invert(viewRotation * projection, out var inv))
            inv = Matrix4x4.Identity;
        // The dome reads the true sun, never the key light: its glow, stars and moon mask follow it
        _owner.SkyDome?.Render(inv, Lighting.Sky, Lighting.SkySunDir, Lighting.SkyTint, Lighting.SkyTintAmount, frame);
        RenderSun(viewRotation, projection);
        RenderMoon(viewRotation, projection);
    }

    /// <summary>
    /// Draws the moon: <see cref="RenderSun"/>'s quad and vertex shader placed opposite the sun, with
    /// the moon sprite and moon-fragment.glsl, which shades this day's phase (<see cref="MoonPhase"/>)
    /// over the full disc and adds a faint halo that grows with how much of it is lit. Additive and
    /// depth-free like the sun, so the terrain drawn after it occludes it; RenderSky draws it for the
    /// mirror as well, and the Snell window shows it through the scene copy.
    /// </summary>
    private void RenderMoon(Matrix4x4 viewRotation, Matrix4x4 projection)
    {
        if (Lighting.MoonVisibility <= 0f) return;

        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);

        _owner.MoonShader.Use();
        Units.BindUnit(0, TextureTarget.Texture2DArray, _owner.MoonTexture);

        _owner.MoonShader.SetUniform("uViewProj", viewRotation * projection);
        _owner.MoonShader.SetUniform("uSunDir", -Lighting.SkySunDir);
        _owner.MoonShader.SetUniform("uHalfSize", 2f * 100f * MathF.Tan(MathUtils.ToRadians(
            SkyConfig.MoonAngularRadiusDeg * SkyConfig.MoonGlowScale)));
        _owner.MoonShader.SetUniform("uDiscFraction", 1f / SkyConfig.MoonGlowScale);
        _owner.MoonShader.SetUniform("uPhaseLight", MoonPhase.LightDirection(Lighting.MoonPhaseFraction));
        _owner.MoonShader.SetUniform("uLitFraction", MoonPhase.IlluminatedFraction(Lighting.MoonPhaseFraction));
        _owner.MoonShader.SetUniform("uTerminator", SkyConfig.MoonTerminatorSoftness);
        _owner.MoonShader.SetUniform("uEarthshine", SkyConfig.MoonEarthshine);
        _owner.MoonShader.SetUniform("uGlowIntensity", SkyConfig.MoonGlowIntensity);
        _owner.MoonShader.SetUniform("uGlowExponent", SkyConfig.MoonGlowExponent);
        _owner.MoonShader.SetUniform("uMoonColor", MoonColorLinear * SkyConfig.MoonIntensity);
        _owner.MoonShader.SetUniform("uVisibility", Lighting.MoonVisibility);

        _owner.SunQuad.Render();

        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
    }

    // The moon's cool white tint, authored in sRGB like every palette color and decoded once
    private static readonly Vector3 MoonColorLinear = ColorSpace.SrgbToLinear(new Vector3(0.9f, 0.94f, 1f));

    /// <summary>
    /// Draws the sun: a textured disc surrounded by a radial glow, on one screen-facing quad.
    /// Three things make it cheap. It is drawn first, with the depth test and depth writes
    /// off, so everything drawn afterwards occludes it for free; the dome under it is
    /// <see cref="SkyRenderer"/>'s.
    /// The quad is placed 100 blocks along <see cref="FrameLighting.SkySunDir"/> under a view matrix with the
    /// translation removed, so it effectively sits at infinity and never parallaxes as the
    /// player walks. And it blends additively (One, One), so the shader's black background
    /// contributes nothing and the glow brightens the sky rather than graying it.
    /// Takes the translation-free view (with the camera's roll, so the disc wobbles with the sprint
    /// like the dome) and projection rather than a camera, so the reflection pass can draw
    /// the same disc through its mirrored view.
    /// </summary>
    private void RenderSun(Matrix4x4 viewRotation, Matrix4x4 projection)
    {
        if (Lighting.SunVisibility <= 0f) return;

        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);

        _owner.SunShader.Use();
        Units.BindUnit(0, TextureTarget.Texture2DArray, _owner.SunTexture);

        // Rotation only: dropping the camera position is what pins the quad at infinity
        _owner.SunShader.SetUniform("uViewProj", viewRotation * projection);
        _owner.SunShader.SetUniform("uSunDir", Lighting.SkySunDir);
        // The quad has to hold the whole glow, so it is sized to the glow's angular radius and
        // the shader keeps the textured disc inside the inner uDiscFraction of it. The unit
        // square's corners sit at +-0.5, hence the factor 2 to make uHalfSize the true radius.
        _owner.SunShader.SetUniform("uHalfSize", 2f * 100f * MathF.Tan(GameSettings.SunSize
            * SkyConfig.SunGlowScale * MathF.PI / 180f));
        _owner.SunShader.SetUniform("uDiscFraction", 1f / SkyConfig.SunGlowScale);
        _owner.SunShader.SetUniform("uGlowIntensity", GameSettings.SunGlow);
        _owner.SunShader.SetUniform("uGlowExponent", SkyConfig.SunGlowExponent);
        // An HDR intensity: the disc plus the sky behind it has to clear the tonemap knee by a
        // wide margin to read as display white, and the additive glow needs the same lift to show
        // over a bright linear sky as it did added in display space.
        _owner.SunShader.SetUniform("uSunColor", Lighting.SunDiscColor * SunDiscIntensity);
        _owner.SunShader.SetUniform("uVisibility", Lighting.SunVisibility);

        _owner.SunQuad.Render();

        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
    }

    /// <summary>
    /// Draws the cloud field once, translucent, after every opaque pass and the water pass so the
    /// terrain and the sea surface both occlude it properly. Skipped with the eye underwater, where
    /// the water fog already hides the sky. Only the cull state has to be restored afterwards:
    /// blending is the state the later passes expect anyway.
    /// </summary>
    internal void RenderClouds(Camera? camera, bool eyeInWater)
    {
        if (camera == null || eyeInWater || _owner.Clouds == null) return;

        // Translucent and depth-tested: terrain and water (already drawn) occlude the clouds, and a
        // depth pre-pass inside CloudRenderer picks the nearest surface so overlapping rounded pieces
        // blend exactly once whatever their draw order. Back-face culling keeps bottoms from bleeding
        // through tops; DepthMask goes back on below because the pre-pass leaves it off.
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        // The clouds mix over the scene in display space by reading the HDR target
        // (CloudRenderer.Render); never leave it sitting on a sampler unit afterwards.
        Units.BindUnit(RenderManager.SceneReadUnit, TextureTarget.Texture2D, Targets.HdrColorTex);
        _owner.Clouds.Render(camera.GetViewProjection(), Lighting.CameraPos, Lighting.TimeD,
            Lighting.SunDir, Lighting.SunSrgb, Lighting.CloudSunIntensity, Lighting.CloudAmbientSrgb, Lighting.FogColor, Lighting.FogNear, Lighting.FogFar, Lighting.Sky, Lighting.FogWet, RenderManager.SceneReadUnit);
        Units.BindUnit(RenderManager.SceneReadUnit, TextureTarget.Texture2D, 0);
        Units.ActiveUnit(0);
        _gl.Disable(EnableCap.CullFace);
        _gl.DepthMask(true);
    }
}
