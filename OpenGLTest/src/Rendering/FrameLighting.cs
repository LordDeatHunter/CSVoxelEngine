using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Effects.Lighting;
using VoxelEngine.Effects.Post;
using VoxelEngine.Effects.Sky;
using VoxelEngine.Entities;
using VoxelEngine.Particles;
using VoxelEngine.Player;
using VoxelEngine.Rendering.Atmosphere;
using VoxelEngine.UI;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Rendering;

/// <summary>
/// The frame's light, sky, fog and eye state: the key light and the sky's sun, the ambient,
/// fog and sky palette, the shadow strengths, the point lights, the underwater and lava fades,
/// the eye's exposure and grade and the clock and wind. Computed once a frame by
/// <see cref="SetLighting"/> and read live by every pass. Issues no GL call.
/// </summary>
internal sealed class FrameLighting
{
    private readonly RenderManager _owner;

    // The sun disc's visibility: SkyPasses.RenderSun fades it out below the horizon and underwater.
    private float _sunVisibility;
    // The moon disc's (SkyPasses.RenderMoon) visibility, faded with the stars and out in lava, and
    // its phase (MoonPhase).
    private float _moonVisibility;
    private float _moonPhase = MoonPhase.FirstNightPhase;

    // This frame's godray strength (Godrays.Intensity), set by SetLighting; 0 skips the pass
    private float _godrayStrength;

    // Per-frame lighting state, pushed once per frame via SetLighting and uploaded to the
    // world shader once per frame by RenderManager.ApplyFrameUniforms
    // _sunDir is the world's key light (Moonlight): the sun by day, the moon (the sun negated) once
    // the sun is well down. Everything that lights or shadows the world reads it; the sky (the dome,
    // the sun and moon discs, the stars' turn, the glare) reads the true sun, _skySunDir.
    private Vector3 _sunDir = Vector3.UnitY;
    private Vector3 _skySunDir = Vector3.UnitY;
    // The sun disc's own color (linear); _sunColor is the key light's, the moonlight's at night
    private Vector3 _sunDiscColor = Vector3.One;
    private Vector3 _sunColor = Vector3.One;
    private float _sunIntensity = 1f;
    private Vector3 _ambient = new(0.42f, 0.45f, 0.52f);
    // Linear-light curve on the direct sun (SH-10): intensity = gain * (old ramp)^exponent. The
    // sun term was tuned in display space, where a sunlit top came out at ~1.6x albedo; linear
    // light needs roughly that gain raised to 2.2 at noon (gain 2.0 keeps noon tops within a few
    // percent of the old shots), while the exponent pulls the low end down so dusk does not turn
    // brighter and redder than before. The ambient (what shaded faces see) only needed decoding;
    // its night end is lifted a touch because the decoded blue night sun no longer props it up.
    private const float SunLinearGain = 2.0f;
    private const float SunLinearExponent = 1.4f;
    private Vector3 _fogColor = new(0.3f, 0.6f, 0.9f);
    private Vector3 _skyColor = new(0.3f, 0.6f, 0.9f);
    // The sky dome's palette for this frame (SkyPalette), plus what the dome fades toward in
    // lava (_skyTint by _skyTintAmount; it stays dry in water so the Snell window shows sky),
    // and how far the fog has closed in to the underwater tint.
    private SkyPalette _sky = SkyPalette.Compute(1f, 0f);
    // The dome's night sky this frame (stars and the eye their cloud cover is measured from) and
    // the sun's lens glare, plus the eased cloud cover over the sun the glare fades with
    private SkyDomeFrame _skyFrame;
    private float _glareCloud;
    // How cold it is at the eye, eased (Aurora.Ease): what fades the aurora in and out
    private float _eyeColdness;
    // The eye adaptation's exposure multiplier (EyeAdaptation), eased toward the voxel light at the
    // eye each frame by SetLighting and applied by ScreenPasses.RenderPostProcess; 1 in any lit place
    private float _eyeExposure = 1f;
    // The biome color grade (BiomeGrade), eased toward the climate at the eye; (1, 1, 1) is neutral
    private Vector3 _grade = Vector3.One;
    // Display-space (sRGB) light at the pre-SH-10 values, set by SetLighting: the sun color
    // (the cloud layer and the underwater shafts), and the cloud layer's sun intensity and
    // ambient (see CloudRenderer.Render and ScreenPasses.RenderUnderwaterRays).
    private Vector3 _sunSrgb = Vector3.One;
    private float _cloudSunIntensity = 1f;
    private Vector3 _cloudAmbientSrgb = Vector3.One;
    private Vector3 _skyTint;
    private float _skyTintAmount;
    private float _fogWet;
    private float _fogNear = LightingConfig.FogNearBlocks;
    private float _fogFar = LightingConfig.FogFarBlocks;
    private Vector3 _cameraPos;
    private float _time;
    // The same lighting clock in double, for the cloud drift: Wind.Offset needs the precision a
    // large VOXEL_TIME would lose in float.
    private double _timeD;
    // The shared wind (Wind.cs) at the lighting clock, refreshed in SetLighting: the unit
    // downwind direction and how far the leaf gusts have rolled along it.
    private Vector2 _wind = new(1f, 0f);
    private Vector2 _windOffset;

    /// <summary>Unit downwind direction (x, z) this frame, for wind consumers such as future rain or snow.</summary>
    internal Vector2 WindDirection => _wind;

    // 0 at night (and fully underwater), 1 with the sun well up: how much of the direct
    // sun term the shadow lookup is allowed to take away
    private float _shadowStrength;

    // The same value before the underwater fade. Caustics and the sun shafts are exactly the
    // effects a submerged eye is supposed to see, so they (and the shadow map itself) keep
    // using this while the world diffuse fades with _shadowStrength.
    private float _shadowStrengthDry;

    // How high the sun is, 0 at night and 1 well into the day; the ray pass scales with it
    private float _dayness;
    /// <summary>0 at night, 1 in full day; drives the ambience crossfade.</summary>
    internal float Dayness => _dayness;

    // Inverse of the current view-projection, for the shaders that reconstruct a world
    // position out of the depth texture (see the depth-convention note in test-fragment.glsl)
    private Matrix4x4 _invViewProj = Matrix4x4.Identity;
    // The forward twin of _invViewProj: the water's SSR march projects its steps through it
    // into the scene copies.
    private Matrix4x4 _sceneViewProj = Matrix4x4.Identity;

    // Underwater tint: the eye state is a hard flag, so it is eased into _underwaterT
    // (0 = above water, 1 = fully submerged) over UnderwaterFadeSeconds
    internal static readonly Vector3 UnderwaterFogColor = new(0.05f, 0.22f, 0.42f);
    private static readonly Vector3 UnderwaterAmbientTint = new(0.55f, 0.75f, 1f);
    internal static readonly Vector3 LavaFogColor = new(0.55f, 0.12f, 0.02f);
    private static readonly Vector3 LavaAmbientTint = new(1.2f, 0.6f, 0.4f);
    // Linear twins for SetLighting, which feeds pre-tonemap shaders. The gamma fog colors above
    // stay for UiPasses.RenderUI's wash rect, which draws after the tonemap onto the gamma
    // framebuffer.
    private static readonly Vector3 UnderwaterFogColorLinear = ColorSpace.SrgbToLinear(UnderwaterFogColor);
    private static readonly Vector3 UnderwaterAmbientTintLinear = ColorSpace.SrgbToLinear(UnderwaterAmbientTint);
    private static readonly Vector3 LavaFogColorLinear = ColorSpace.SrgbToLinear(LavaFogColor);
    private static readonly Vector3 LavaAmbientTintLinear = ColorSpace.SrgbToLinear(LavaAmbientTint);

    // Point-light hues (sRGB-authored, decoded once); SetLighting scales them as linear intensities
    private static readonly Vector3 TntLightColor = ColorSpace.SrgbToLinear(new Vector3(1f, 0.45f, 0.15f));
    private static readonly Vector3 TntFlashColor = ColorSpace.SrgbToLinear(new Vector3(1f, 0.9f, 0.8f));
    private static readonly Vector3 CampfireLightColor = ColorSpace.SrgbToLinear(new Vector3(1f, 0.5f, 0.15f));
    private double _prevTime;
    private float _underwaterT;
    private bool _eyeInLava;
    // Eases the lava-only fades (sky clear color, sun disc) exactly as _underwaterT eases the
    // water ones: _eyeInLava is a hard flag that drops the frame the eye leaves lava while
    // _underwaterT is still fading out, which would otherwise pop the sky back in one frame.
    private float _lavaT;

    private readonly Vector3[] _pointPos = new Vector3[LightingConfig.MaxPointLights];
    private readonly Vector3[] _pointColors = new Vector3[LightingConfig.MaxPointLights];
    private int _pointCount;
    /// <summary>Index of the first fire point light; the TNT strobes fill the slots before it.</summary>
    private int _fireLightStart;

    /// <summary>
    /// Where a fire cell's warm point light sits inside it: a block above the cell origin, so a
    /// campfire lights what stands around it rather than only its own pile. Deliberately higher
    /// than <see cref="FireParticleSystem.SpawnOffset"/>, which is where the flame itself is —
    /// a caller passing an off-cell flame (the kiln) gets its light that same 0.65 higher.
    /// </summary>
    internal static readonly Vector3 FireLightOffset = new(0.5f, 1.0f, 0.5f);

    // Live views of the frame's state for the passes that upload or draw with it.
    internal Vector3 SunDir => _sunDir;
    internal Vector3 SkySunDir => _skySunDir;
    internal Vector3 SunDiscColor => _sunDiscColor;
    internal Vector3 SunColor => _sunColor;
    internal float SunIntensity => _sunIntensity;
    internal Vector3 Ambient => _ambient;
    internal Vector3 FogColor => _fogColor;
    internal Vector3 SkyColor => _skyColor;
    internal SkyPalette Sky => _sky;
    internal SkyDomeFrame SkyFrame => _skyFrame;
    internal float EyeExposure => _eyeExposure;
    internal Vector3 Grade => _grade;
    internal Vector3 SunSrgb => _sunSrgb;
    internal float CloudSunIntensity => _cloudSunIntensity;
    internal Vector3 CloudAmbientSrgb => _cloudAmbientSrgb;
    internal Vector3 SkyTint => _skyTint;
    internal float SkyTintAmount => _skyTintAmount;
    internal float FogWet => _fogWet;
    internal float FogNear => _fogNear;
    internal float FogFar => _fogFar;
    // Settable: the water mirror swaps in its mirrored eye around its own frame uniforms.
    internal Vector3 CameraPos { get => _cameraPos; set => _cameraPos = value; }
    internal float Time => _time;
    internal double TimeD => _timeD;
    internal Vector2 WindOffset => _windOffset;
    internal float ShadowStrength => _shadowStrength;
    internal float ShadowStrengthDry => _shadowStrengthDry;
    internal float SunVisibility => _sunVisibility;
    internal float MoonVisibility => _moonVisibility;
    internal float MoonPhaseFraction => _moonPhase;
    internal float GodrayStrength => _godrayStrength;
    internal Matrix4x4 InvViewProj => _invViewProj;
    internal Matrix4x4 SceneViewProj => _sceneViewProj;
    internal float UnderwaterT => _underwaterT;
    internal bool EyeInLava => _eyeInLava;
    internal Vector3[] PointPos => _pointPos;
    internal Vector3[] PointColors => _pointColors;
    internal int PointCount => _pointCount;
    internal int FireLightStart => _fireLightStart;

    internal FrameLighting(RenderManager owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// Advances the day/night cycle and pushes the resulting sun, sky and point-light
    /// state into the per-frame fields. Called once per frame before <see cref="RenderManager.BeginFrame"/>,
    /// which uploads the result to the world shader through <see cref="RenderManager.ApplyFrameUniforms"/>.
    /// <paramref name="underwater"/> is whether the camera's eye sits below a fluid surface;
    /// it fades the underwater tint in and out. <paramref name="eyeInLava"/> picks the lava
    /// variant of that tint, so a submerged view reads red instead of blue.
    /// </summary>
    internal void SetLighting(double time, Camera? camera, IReadOnlyList<PrimedTnt>? primed, bool underwater, bool eyeInLava,
        IReadOnlyList<Vector3>? campfires = null)
    {
        // The window clock is the only time source this pass has; clamp the step so a hitch
        // (or the very first frame) cannot jump the fade
        var dt = (float)MathUtils.Clamp(time - _prevTime, 0.0, 0.25);
        _prevTime = time;

        _time = (float)time;
        _timeD = time;
        // Both from the double clock: the offset is bounded but its phase needs the precision
        _wind = Wind.Direction(time);
        _windOffset = Wind.Offset(time, SkyConfig.WindGustSpeed);
        if (camera != null)
        {
            _cameraPos = camera.RenderEye;
            // Inverting the exact matrix the depth was written with is what makes the shaders'
            // position reconstruction independent of the clip-depth convention.
            _sceneViewProj = camera.GetViewProjection();
            if (!Matrix4x4.Invert(_sceneViewProj, out _invViewProj))
                _invViewProj = Matrix4x4.Identity;
        }

        // The sun orbits once per DayLengthSeconds; the phase starts it mid-morning (DayCycle,
        // which gameplay keyed to the hour reads too)
        _skySunDir = DayCycle.SunDirection(time);
        var elevation = _skySunDir.Y;
        // The world's key light: the sun, or the moon once the sun is well down (Moonlight). The
        // handover happens with both at zero strength, so neither the light nor its shadows pop.
        var moonLit = Moonlight.IsMoonLit(elevation);
        _sunDir = Moonlight.Direction(_skySunDir);

        // The shadow camera fades itself out as the key light sets: at a grazing elevation the
        // shadows stretch to the horizon and the ortho box cannot hold them. The moon, the key
        // light at night, climbs from the handover on, so its shadows fade in as it does.
        _shadowStrength = GameSettings.Shadows
            ? Smooth01((_sunDir.Y - 0.02f) / 0.12f)
            : 0f;

        // The disc itself fades over a much narrower band than the light does, so it sinks
        // out of sight shortly after the horizon instead of hanging in the night sky
        _sunVisibility = Smooth01((elevation + 0.08f) / 0.14f);

        // The moon stands opposite the sun and fades in and out with the stars; its phase steps
        // once a day, at noon, while it is below the horizon (MoonPhase)
        _moonVisibility = MoonPhase.Visibility(elevation);
        _moonPhase = MoonPhase.Fraction(time);

        var dayness = DayCycle.Dayness(elevation);
        _dayness = dayness;
        var duskAmount = MathUtils.Clamp01(1f - MathF.Abs(elevation) * 5f);

        _sunColor = Mix(new Vector3(0.3f, 0.4f, 0.7f), new Vector3(1f, 0.96f, 0.88f), dayness);
        _sunColor = Mix(_sunColor, new Vector3(1f, 0.5f, 0.25f), duskAmount * 0.55f);
        // Every palette color here is authored in sRGB (display) space and decoded once, where
        // it is computed, so fog, the clear color, clouds, the mirror and the rays all see linear
        // light (SH-10).
        // The cloud layer and the underwater shafts are still lit in display space
        // (cloud-fragment.glsl, rays-upsample-fragment.glsl), with the exact pre-SH-10 sun,
        // intensity and ambient: the world's retuned linear constants below would light them
        // differently than they were.
        _sunDiscColor = ColorSpace.SrgbToLinear(_sunColor);
        _cloudAmbientSrgb = Mix(new Vector3(0.06f, 0.07f, 0.11f), new Vector3(0.42f, 0.45f, 0.52f), dayness);
        if (moonLit)
        {
            // Moonlight: a faint cool key light from the moon, eased in as it climbs from the
            // handover and fuller with a fuller moon (Moonlight.Intensity). The clouds take the
            // same share of their own night floor.
            _sunSrgb = Moonlight.ColorSrgb;
            _sunColor = Moonlight.ColorLinear;
            _sunIntensity = Moonlight.Intensity(elevation, _moonPhase);
            _cloudSunIntensity = (0.05f + 1.2f * dayness) * Moonlight.Rise(elevation) * Moonlight.PhaseShare(_moonPhase);
        }
        else
        {
            // The sun's light, easing out below the horizon to nothing at the handover
            // (Moonlight.SunFade), instead of lighting the night from under the ground
            var fade = Moonlight.SunFade(elevation);
            _sunSrgb = _sunColor;
            _sunColor = _sunDiscColor;
            _sunIntensity = SunLinearGain * MathF.Pow(0.05f + 1.2f * dayness, SunLinearExponent) * fade;
            _cloudSunIntensity = (0.05f + 1.2f * dayness) * fade;
        }
        // Thin cloud edges near the sun light up, never with the sun at or below the horizon
        if (_owner.Clouds != null)
            _owner.Clouds.SilverLining = CloudSilverLining.Strength(elevation);

        // The night end is a touch bluer than it was: the moonlit sky the shaded faces see
        _ambient = ColorSpace.SrgbToLinear(
            Mix(new Vector3(0.09f, 0.105f, 0.165f), new Vector3(0.42f, 0.45f, 0.52f), dayness));

        // The warm glow toward the sun only while it crosses the horizon (the day sky stays flat)
        _sky = SkyPalette.Compute(dayness, duskAmount, SkyPalette.GlowAmount(elevation));
        // SkyPalette stays authored (and tested) in sRGB; the dome and fog want it linear
        _sky = _sky with
        {
            Zenith = ColorSpace.SrgbToLinear(_sky.Zenith),
            Horizon = ColorSpace.SrgbToLinear(_sky.Horizon),
            Glow = ColorSpace.SrgbToLinear(_sky.Glow),
        };
        // The stars twinkle on the lighting clock, wrapped hourly so the float keeps its
        // precision; their cloud cover follows the drawn clouds' drift
        _skyFrame = new SkyDomeFrame(StarField.Visibility(elevation), (float)(_timeD % 3600.0), _cameraPos,
            CloudCopies.Drift(_timeD, SkyConfig.CloudFieldSize), Vector3.Zero);
        // The flat sky/fog color is the dome's no-glow horizon sample
        _skyColor = _sky.HazedHorizon;
        // Fog melts the far terrain (and the LOD edge) into the sky, day and night
        _fogColor = _skyColor;

        // Primed TNT strobes feed the dynamic point lights; lamp glow is baked voxel light
        _pointCount = 0;
        if (primed != null)
        {
            foreach (var tnt in primed)
            {
                if (_pointCount >= LightingConfig.MaxPointLights) break;
                _pointPos[_pointCount] = tnt.Position;
                var flash = tnt.WhiteFlash;
                // Linear intensities: the hues are the old sRGB ones decoded, the gains retuned
                _pointColors[_pointCount] = TntLightColor * 1.4f + TntFlashColor * (flash * 3f);
                _pointCount++;
            }
        }
        // Lit campfires each add a warm flickering point light above the pile, each on its own
        // phase hashed from its cell so neighboring fires do not pulse in unison
        _fireLightStart = _pointCount;
        if (campfires != null)
        {
            foreach (var cell in campfires)
            {
                if (_pointCount >= LightingConfig.MaxPointLights) break;
                var flicker = LightFlicker.Campfire(_time, LightFlicker.CellPhase(cell));
                _pointPos[_pointCount] = cell + FireLightOffset;
                _pointColors[_pointCount] = CampfireLightColor * (2.0f * flicker);
                _pointCount++;
            }
        }
        for (var i = _pointCount; i < LightingConfig.MaxPointLights; i++)
        {
            _pointPos[i] = Vector3.Zero;
            _pointColors[i] = Vector3.Zero;
        }

        // Underwater the fog closes in to a few blocks and goes blue-green and the light dims
        // and cools, so a submerged view reads as being inside the water rather than looking
        // at a blue filter. Scaled by dayness so night water stays dark. The clear color and
        // the sun disc stay dry in water: they are what the surface's Snell window shows from
        // below, so only the fog closes in. Lava is an opaque glow, so it still fades both to
        // red (eased through _lavaT) and stays bright at night.
        _eyeInLava = eyeInLava && underwater;
        _underwaterT = MathUtils.Clamp01(
            _underwaterT + (underwater ? dt : -dt) / WaterRenderConfig.UnderwaterFadeSeconds);
        var wet = MathUtils.SmoothStep(_underwaterT);
        _lavaT = MathUtils.Clamp01(_lavaT + (_eyeInLava ? dt : -dt) / WaterRenderConfig.UnderwaterFadeSeconds);
        var lavaWet = wet * MathUtils.SmoothStep(_lavaT);

        // Underwater the sun is a diffuse glow, so the crisp shadow edges go with the tint.
        // This has to run before the early return below, or a submerged view would keep them.
        // _shadowStrengthDry keeps the unfaded value: the caustics and the underwater sun
        // shafts are exactly what a submerged eye should see, and both need the shadow map
        // to still be rendered and sampled while the world diffuse fades.
        _shadowStrengthDry = _shadowStrength;
        _shadowStrength *= 1f - wet;
        // The disc stays in water: the surface's Snell window shows it refracted from below
        // (the scene copy captures it). Only lava, an opaque glow, still hides it.
        _sunVisibility *= 1f - lavaWet;
        _moonVisibility *= 1f - lavaWet;
        UpdateShadowCamera(camera);

        // The lens glare (SunGlare) is a camera effect, so it fades out with the eye underwater or
        // in lava: the dome stays dry there for the Snell window, and a lens flare seen through the
        // surface from below would read as a bug. The cloud over the sun's disc is eased, so a
        // drifting edge fades it smoothly. The mirror draws the dome without it
        // (ReflectionPass.RenderReflection).
        var glareCover = 0f;
        if (_owner.CloudShadow != null && elevation > 0f)
            glareCover = CloudCover.AcrossDisc(_owner.CloudShadow.Coverage, CloudField.Cells, _cameraPos, _skySunDir,
                MathUtils.ToRadians(GameSettings.SunSize), _skyFrame.CloudDrift, CloudCover.Blocking(GameSettings.CloudOpacity));
        _glareCloud = MathUtils.Damp(_glareCloud, glareCover, SkyConfig.SunGlareCloudEaseRate, dt);
        var glare = camera == null
            ? 0f
            : SunGlare.Intensity(Vector3.Dot(camera.ViewDirection, _skySunDir), elevation, _glareCloud) * (1f - wet);
        // Godrays: strongest facing a low sun, a fraction at noon, dimmed by the cloud over it and
        // gone under water, where the underwater shafts take over (Godrays.Intensity)
        _godrayStrength = camera == null || !PostProcessConfig.GodraysEnabled
            ? 0f
            : Godrays.Intensity(Vector3.Dot(camera.ViewDirection, _skySunDir), elevation, _glareCloud, wet);
        // The aurora shows only from cold lands: the climate at the eye's column, read from the stored
        // corner climate (never worldgen noise, so a client reads what its server streamed), eased so
        // walking across a border fades it; an unknown column eases it out
        var eyeClimateKnown = false;
        var eyeWarmth = 0.5f;
        var eyeWetness = 0.5f;
        var columns = _owner.LightSource?.ColumnSource;
        if (columns != null && camera != null)
            eyeClimateKnown = EyeClimate.TryAt(columns, camera.Position.X, camera.Position.Z, out eyeWarmth, out eyeWetness);
        _eyeColdness = Aurora.Ease(_eyeColdness, Aurora.TargetColdness(eyeClimateKnown, eyeWarmth), dt);
        // The biome color grade reads the same climate (neutral, eased back, when it is unknown)
        _grade = BiomeGrade.Ease(_grade, BiomeGrade.Target(eyeClimateKnown, eyeWarmth, eyeWetness), dt);
        // Eye adaptation: the exposure eases up in the dark, read from the voxel light at the eye's
        // cell (no GPU readback); open sky and lit rooms keep it at 1
        var eyeLight = camera != null ? EntityLight.Sample(_owner.LightSource, camera.Position) : EntityLightSample.Default;
        _eyeExposure = EyeAdaptation.Step(_eyeExposure, EyeAdaptation.Target(eyeLight.Sun, eyeLight.Block), dt);
        _skyFrame = _skyFrame with
        {
            GlareColor = _sunDiscColor * glare,
            MoonVisibility = _moonVisibility,
            Aurora = Aurora.Visibility(_eyeColdness, StarField.Visibility(elevation)),
            AuroraDrift = Aurora.Drift(time),
        };

        _fogNear = MathUtils.Lerp(LightingConfig.FogNearBlocks,
            WaterRenderConfig.UnderwaterFogNearBlocks, wet);
        _fogFar = MathUtils.Lerp(LightingConfig.FogFarBlocks,
            WaterRenderConfig.UnderwaterFogFarBlocks, wet);

        // Reset before the dry early-out, or a tint would stick after leaving water or lava
        _fogWet = wet;
        _skyTint = Vector3.Zero;
        _skyTintAmount = 0f;

        if (wet <= 0f) return;

        var underwaterColor = _eyeInLava
            // The dayness dims were tuned as display-space factors; raised to 1.6 (matched against the
            // old night shots) they dim the linear colors about as much on screen as they used to
            ? LavaFogColorLinear * MathF.Pow(0.6f + 0.4f * dayness, 1.6f)
            : UnderwaterFogColorLinear * MathF.Pow(0.15f + 0.85f * dayness, 1.6f);
        _skyTint = underwaterColor;
        _skyTintAmount = lavaWet;
        _fogColor = Mix(_fogColor, underwaterColor, wet);
        // _fogColor was copied from the dry _skyColor earlier, so this leaves the fog tint alone;
        // afterwards _skyColor only feeds the clear color, which the window shows from below.
        _skyColor = Mix(_skyColor, underwaterColor, lavaWet);
        _ambient = Mix(_ambient, _ambient * (_eyeInLava ? LavaAmbientTintLinear : UnderwaterAmbientTintLinear), wet);
        // 0.25 in linear light is the old display-space 0.5 raised to ~2.2 (SH-10): the sun is
        // what lights the sea floor, so a larger factor turned it warm and bright
        _sunIntensity = MathUtils.Lerp(_sunIntensity, _sunIntensity * 0.25f, wet);
    }

    /// <summary>
    /// Builds the light-space matrices for this frame: the near cascade's ortho box of half-extent
    /// <see cref="LightingConfig.ShadowRadiusBlocks"/> and the far cascade's of
    /// <see cref="LightingConfig.FarShadowRadiusBlocks"/>, each centered a little ahead of
    /// the camera and looking down the sun direction. <see cref="ShadowCascade.StepCenter"/> carries
    /// that center forward in whole shadow texels, or the sub-texel drift as the player walks
    /// makes every shadow edge crawl. Each cascade steps its own center on its own texel lattice.
    /// </summary>
    private void UpdateShadowCamera(Camera? camera)
    {
        if (camera == null || _shadowStrengthDry <= 0f) return;

        _owner.NearShadow.Update(camera.RenderEye, camera.ViewDirection, _sunDir);
        _owner.FarShadow.Update(camera.RenderEye, camera.ViewDirection, _sunDir);
    }

    private static float Smooth01(float t) => MathUtils.SmoothStep(t);

    private static Vector3 Mix(Vector3 a, Vector3 b, float t) => a.Lerp(b, t);

    /// <summary>Drops the eased eye state with the world it belongs to (RenderManager.ForgetWorld).</summary>
    internal void ForgetWorld()
    {
        // The eased eye state belongs to the world just released: without this, a quit from
        // underwater would wash the next world's first frames in the fading tint.
        _underwaterT = 0f;
        _lavaT = 0f;
        _eyeInLava = false;
    }
}
