namespace VoxelEngine.Config;

/// <summary>
/// Lighting tuning: the cutout alpha test and atlas filtering, voxel light spread, the sun shadow
/// cascades, point and fire lights, the world fog, the tonemap exposure and eye adaptation.
/// </summary>
public static class LightingConfig
{
    // Alpha-test threshold of cutout blocks (leaves, flowers): the world pass's uAlphaCutoff and
    // the target CutoutMipmap preserves coverage at. Must match the `.a < 0.5` literals in
    // shadow-fragment.glsl and the lodLeaves test in test-fragment.glsl.
    public const float AlphaCutoff = 0.5f;

    // Anisotropic filtering of the block and material atlases, which sample their mip chains
    // (TextureLoader.UploadArray's sampleMips). Clamped to the driver's maximum at upload.
    public const float AtlasAnisotropy = 8f;

    // Directional shadow map resolution (depth texture is Size x Size)
    public const int ShadowMapSize = 2048;

    // Half-extent in blocks of the shadow camera's ortho box around the view target
    public const float ShadowRadiusBlocks = 90f;

    // Half-extent in blocks of the far shadow cascade: a coarse, silhouette-only box sampled
    // where the near box fades out. The near box's fade band must lie inside the far box's
    // unfaded core, which ShadowCascadeTests checks.
    public const float FarShadowRadiusBlocks = 352f;

    // Far cascade map resolution (~0.34 block per texel). This is the lever if the far pass's
    // fill cost ever shows up: halving it keeps silhouettes at about 0.7 block per texel.
    public const int FarShadowMapSize = 2048;

    // The far map is cached and re-rendered only when FarShadowReuse asks (PERF_TODO F3).
    // Half-extent, as a share of FarShadowRadiusBlocks, of the light-space square around the held
    // far center in which the look-following target may wander before the box re-centers. It must
    // keep the near box's fade band inside the far core while the center is held (ShadowCascadeTests
    // checks it), and a re-center moves the far box's own fade band by up to this much in one frame,
    // so the far-shadow pop check bounds it from above.
    public const float FarShadowReuseInnerFraction = 0.25f;

    // Eye travel along the key light, as a share of the far radius, before the far map re-renders.
    // The box is 4r deep along the light, so a quarter radius keeps every caster above a held box
    // well inside its depth range.
    public const float FarShadowReuseDepthMarginFraction = 0.25f;

    // Key-light turn, in far texels at the box edge, a cached far map may lag the sun by. Half a
    // texel keeps the stale silhouettes under the texel the map already quantizes them to.
    public const float FarShadowReuseMaxTurnTexels = 0.5f;

    // The oldest a far map may get, in frames, whatever else holds still: bounds how long the
    // frozen leaf sway and any change the other causes miss can stay visible.
    public const int FarShadowReuseMaxFrames = 30;

    // Frames since the last far render before a caster geometry change (an opaque chunk or LOD
    // upload or free, a LOD burial flip) forces another, so the bumps of a streaming burst batch
    // into one render. Measured on perf-fly (PERF_TODO F3): the version moves on about two frames in
    // three while streaming, so 1 re-rendered the full far box on two frames in three (shadowFar
    // GPU ~1.6x the culled every-frame pass) and 2 broke even; 4 halves it. A new chunk's far
    // shadow arrives at most three frames late, far outside the near cascade.
    public const int FarShadowReuseGeometryMinFrames = 4;

    // Whether the depth-only shadow pass runs at all (false leaves the world fully lit)
    public const bool ShadowsEnabled = true;

    // Spread of the 4-tap PCF kernel in shadow-map texels. The four taps sit half a texel
    // times this value from the lookup on each diagonal, and the shadow sampler filters
    // linearly, so every tap is itself a hardware 2x2 comparison: at 1 the four overlap into
    // a smooth ~3x3 tent for 4 fetches instead of 9. 0 or less means a single tap.
    public const float ShadowSoftnessTexels = 1f;

    // How far along its normal a fragment is pushed before the depth lookup, in shadow
    // texels — kills the self-shadowing acne on grazing-lit voxel faces
    public const float ShadowNormalOffsetTexels = 1.5f;

    // Constant part of the depth bias, in shadow texels, added on top of the slope-scaled
    // term the shader derives from the fragment's own normal. It only has to cover the
    // float error of a receiver that faces the sun square on, so it stays small: a bias big
    // enough to stand in for the slope at every sun angle is what lifted shadows off their
    // casters once the sun climbed.
    public const float ShadowBiasTexels = 0.5f;

    // Dynamic point lights in the world shader (primed TNT strobes); lamp glow is baked
    public const int MaxPointLights = 8;

    // Radius in blocks of the smooth window on fire point lights (campfires, lit kilns): about a
    // lamp's flood-fill reach. It stands in for occlusion until fires bake their light (SH-2).
    public const float FireLightRange = 8f;

    // Distance fog that blends far terrain into the sky (also hides the LOD edge at night)
    public const float FogNearBlocks = 200f;
    public const float FogFarBlocks = 1800f;

    /// <summary>Linear multiplier applied to the HDR scene before the tonemap
    /// (post-fragment.glsl's uExposure). Left at 1: the brightness retune lives in
    /// FrameLighting's sun/ambient constants instead, where each term could be matched against
    /// the pre-SH-10 shots separately, and a global exposure would have moved them all at once.</summary>
    public const float TonemapExposure = 1.0f;

    // --- Eye adaptation (EyeAdaptation, ScreenPasses.RenderPostProcess) ---
    /// <summary>Most the exposure rises in total darkness (a multiplier on TonemapExposure).</summary>
    public const float EyeAdaptMaxExposure = 1.8f;
    /// <summary>Voxel light at the eye (0..1, the brighter of sky and block light) from which the exposure stays at 1, so a lamp-lit room is never brightened.</summary>
    public const float EyeAdaptFullLight = 0.6f;
    /// <summary>How fast the exposure rises in the dark (MathUtils.Damp rate, per second): the eye adapts slowly.</summary>
    public const float EyeAdaptToDarkRate = 0.5f;
    /// <summary>How fast the exposure falls back in the light (per second): much quicker than adapting to the dark.</summary>
    public const float EyeAdaptToLightRate = 2.5f;

    // Sunlight and lamp light spread through air losing one level per block from a max
    // of 15, so one edited block can change light up to this far away. Block edits must
    // remesh every chunk in this radius, not just face-adjacent ones.
    public const int LightPropagationRadius = 15;
}
