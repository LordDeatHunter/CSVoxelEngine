using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Configuration for world generation and chunk management. Distances and frequencies are in
/// blocks (one block is 0.5 m; a frequency of 1/64 means features ~64 blocks across).
/// </summary>
public static class WorldGenerationConfig
{
    // Alpha-test threshold of cutout blocks (leaves, flowers): the world pass's uAlphaCutoff and
    // the target CutoutMipmap preserves coverage at. Must match the `.a < 0.5` literals in
    // shadow-fragment.glsl and the lodLeaves test in test-fragment.glsl.
    public const float AlphaCutoff = 0.5f;

    // Anisotropic filtering of the block and material atlases, which sample their mip chains
    // (TextureLoader.UploadArray's sampleMips). Clamped to the driver's maximum at upload.
    public const float AtlasAnisotropy = 8f;

    // View distance
    public const int ChunkRadius = 16;
    public const int ChunkRadiusSq = ChunkRadius * ChunkRadius;

    // How many completed meshes to upload to GPU per frame
    public const int MaxMeshUploadsPerFrame = 32;

    // How many dirty chunks to process per frame (kick off mesh gen)
    public const int MaxDirtyChunksPerFrame = 64;

    // How many dirty-set entries one frame scans for mesh candidates. The set holds
    // thousands of entries mid-stream, so an uncapped scan is milliseconds by itself;
    // leftovers simply wait for the next frames (item 19).
    public const int MaxDirtyScanPerFrame = 1024;

    // How many mesh generation tasks can run concurrently on the thread pool
    public const int MaxConcurrentMeshGenerations = 4;

    // How many dedicated threads for block data generation
    public const int MaxConcurrentBlockDataGenerations = 4;

    // How many dedicated threads build LOD region meshes (LodManager's LodGen pool).
    //
    // SHIPS AT 1: a pool was built, measured and declined. The thread census said 3 (12 hardware
    // threads on the reference machine, minus 1 render thread, minus MaxConcurrentMeshGenerations
    // = 4 ThreadPool mesh runners at the pool's default Normal priority, minus
    // MaxConcurrentBlockDataGenerations = 4 dedicated BelowNormal chunk-gen threads), and the
    // capture said otherwise: three BelowNormal workers reach a fixed region count 1.31x sooner
    // but slow chunk generation DISJOINTLY (chunks loaded at the 4th perf line 11156-12485
    // baseline against 8427-10253, p = 0.0143; seconds to the full 17077-chunk ring 5.0 -> 6.1)
    // and cost 30 % of burst frame rate (median 108.0 -> 75.3 fps); two workers clear the
    // chunk-gen and GC guards but still cost 18 % (110.3 -> 90.7) for 1.26x. Decision gate 2's
    // branch 4 applies -- neither size clears -- so the pool goes and everything around it stays.
    //
    // Why so far below linear, and the reason a bigger pool must not be re-proposed: a LOD build
    // is terrain generation in disguise. LodRegion.BuildMeshData prefetches 36 / 100 / 324 chunk
    // columns at levels 0 / 1 / 2 through ColumnCache.GetHeights, and a column that misses
    // Chunk.ColumnCache is generated on the spot (measured medians 17.7 / 17.6 / 65.3 ms per
    // build, tracking the column count, not the mesh). The four chunk-gen threads are doing that
    // same work on that same cache at that same moment, so LodGen workers do not add throughput to
    // an idle resource -- they contend for the one the burst already saturates. The lever the
    // measurement pointed at was cutting the work, not more threads, and that lever HAS NOW BEEN
    // TAKEN (item D2, landed): the cap is unchanged at 16384 entries, but the eviction is no
    // longer a wholesale Clear(). ColumnCache runs on GenerationalCache, so a level-2 ring sweep
    // (roughly 51k distinct columns) can only age out the OLDEST generation at a time instead of
    // dropping chunk generation's hot working set along with its own one-shot columns (that set is
    // a radius-16 disc: ~800 columns, 933 once each build's 3x3 heightmap neighbourhood is counted).
    // Measured on the replayed key sequences: chunk-gen's own misses during the mixed burst fell
    // 1219 -> 733. See PERF_TODO's "Measurement: D2".
    //
    // Everything the pool needed is still here and still correct at N = 1 -- the TryAdd-gated
    // _building claim with both rollbacks, MaxLodBuildsInFlight, the Idle/Throttled split and the
    // raised MeshBufferPool ceilings -- so raising this constant is a one-token experiment. The
    // priority claim, if it is: the workers run at BelowNormal, so they cannot PREEMPT the
    // Normal-priority render thread or the mesh runners. That claim is deliberately narrow because
    // the stronger one is false. Priority bounds preemption only, and two channels still reach the
    // frame regardless of who allocated: (1) GC -- a MeshBufferPool miss falls through to a fresh
    // ~786 KB List<Vertex>(LodRegion.MeshVertexCapacity) on the large object heap, and Workstation
    // concurrent GC suspends the main thread whichever thread allocated; (2) shared last-level
    // cache and memory bandwidth, which is the channel the measurement above actually caught
    // (item C5).
    public const int MaxConcurrentLodGenerations = 1;

    // Delay when no chunks need generating (ms)
    public const int GenerationDelayIdleMs = 200;

    // --- Lighting ---
// Full day/night cycle length in seconds (sun orbit + sky + light modulation). Matches
// Minecraft's 20 real minutes for the whole cycle, so the sun crosses the sky at the same
// 0.3 degrees per second.
public const float DayLengthSeconds = 1200f;


    // --- Ambience ---
    // Looping day/night ambience, crossfaded by the lighting pass's dayness (0 at night,
    // 1 in full day). Dayness itself ramps over ~30 s at dawn/dusk, so the crossfade is
    // inherently slow; FadeRate only smooths the per-frame volume steps.
    public const string DayAmbiencePath = "assets/sfx/ambience/forest_ambience.mp3";
    public const string NightAmbiencePath = "assets/sfx/ambience/night_ambience.mp3";
    // Peak volume of each loop when fully faded in (0..1)
    public const float AmbienceVolume = 0.35f;
    // Exponential approach rate per second the day/night crossfade eases toward the current
    // dayness; the master volume is applied directly so a slider change is instant
    public const float AmbienceFadeRate = 0.8f;
    // Both loops rise from silence over this many seconds at startup
    public const float AmbienceStartupFadeSeconds = 8f;
    // Peak volume of one-shot gameplay sfx (block hits, pickups) mixed by SfxManager
    public const float SfxVolume = 0.7f;
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

    /// <summary>
    /// Selection outline beam thickness per block of camera distance, so the outline keeps a
    /// roughly constant on-screen width (about 2 px at 1080p with the default FOV).
    /// </summary>
    public const float SelectionOutlinePixelScale = 0.0025f;

    /// <summary>Floor on the selection outline beam thickness, in blocks, for a box right at the eye.</summary>
    public const float SelectionOutlineMinThickness = 0.002f;

    /// <summary>
    /// Lowest opacity the selection outline breathes down to (SelectionPulse, selection-fragment.glsl's
    /// uPulseDepth = 1 - this); the top of each breath is full opacity. 1 switches the pulse off.
    /// </summary>
    public const float SelectionPulseMinOpacity = 0.7f;

    /// <summary>Seconds per breath of the selection outline (uPulsePeriod).</summary>
    public const float SelectionPulsePeriodSeconds = 1.6f;

    // Distance fog that blends far terrain into the sky (also hides the LOD edge at night)
    public const float FogNearBlocks = 200f;
    public const float FogFarBlocks = 1800f;

    /// <summary>Linear multiplier applied to the HDR scene before the tonemap
    /// (post-fragment.glsl's uExposure). Left at 1: the brightness retune lives in
    /// RenderManager's sun/ambient constants instead, where each term could be matched against
    /// the pre-SH-10 shots separately, and a global exposure would have moved them all at once.</summary>
    public const float TonemapExposure = 1.0f;

    // --- Eye adaptation (EyeAdaptation, RenderManager.RenderPostProcess) ---
    /// <summary>Most the exposure rises in total darkness (a multiplier on TonemapExposure).</summary>
    public const float EyeAdaptMaxExposure = 1.8f;
    /// <summary>Voxel light at the eye (0..1, the brighter of sky and block light) from which the exposure stays at 1, so a lamp-lit room is never brightened.</summary>
    public const float EyeAdaptFullLight = 0.6f;
    /// <summary>How fast the exposure rises in the dark (MathUtils.Damp rate, per second): the eye adapts slowly.</summary>
    public const float EyeAdaptToDarkRate = 0.5f;
    /// <summary>How fast the exposure falls back in the light (per second): much quicker than adapting to the dark.</summary>
    public const float EyeAdaptToLightRate = 2.5f;

    // --- Biome colour grade (BiomeGrade, post-fragment.glsl's uGrade) ---
    /// <summary>Most any channel of the final image moves under the desert's warm or the snow's cool grade (0.04 = 4 %).</summary>
    public const float BiomeGradeStrength = 0.04f;
    /// <summary>Coldness at the eye (EyeClimate.Coldness) from which the cool grade starts: about where snow begins to settle.</summary>
    public const float BiomeGradeColdStart = 0.45f;
    /// <summary>Coldness at the eye at which the cool grade is full.</summary>
    public const float BiomeGradeColdFull = 0.85f;
    /// <summary>How fast the grade follows the eye's climate (MathUtils.Damp rate, per second): a slow drift across a border.</summary>
    public const float BiomeGradeEaseRate = 0.5f;

    // --- Damage chromatic fringe (DamageFringe, post-fragment.glsl's uDamageFringe) ---
    /// <summary>How long after a hit the screen edges stay split red and blue, in seconds.</summary>
    public const float DamageFringeSeconds = 0.3f;
    /// <summary>Largest red/blue shift at the screen corners right at the hit, as a share of the screen height.</summary>
    public const float DamageFringeScreenShare = 0.006f;

    // --- Underwater wobble (UnderwaterWobble, post-fragment.glsl's uUnderwater) ---
    /// <summary>How far the image sways on each axis with the eye under water, as a share of the screen height.</summary>
    public const float UnderwaterWobbleScreenShare = 0.003f;
    /// <summary>How fast the sway waves roll, in radians per second.</summary>
    public const float UnderwaterWobbleSpeed = 1.6f;

    /// <summary>FXAA's floor on the edge-direction reduction (post-fragment.glsl's
    /// uFxaaReduceMin): keeps the direction from blowing up on near-flat luma, so faint
    /// gradients are not smeared across <see cref="FxaaSpanMax"/> pixels.</summary>
    public const float FxaaReduceMin = 1f / 128f;

    /// <summary>FXAA's luma-proportional direction reduction (uFxaaReduceMul): brighter
    /// neighbourhoods shorten the search span a little, which keeps high-contrast texture detail
    /// (ore specks, leaf cutouts) from being blurred along a false edge.</summary>
    public const float FxaaReduceMul = 1f / 8f;

    /// <summary>FXAA's longest blur span in pixels along the detected edge (uFxaaSpanMax); the
    /// classic 8 smooths long near-horizontal block silhouettes without reaching across
    /// unrelated geometry.</summary>
    public const float FxaaSpanMax = 8f;

    /// <summary>FXAA's relative contrast gate (uFxaaEdgeThreshold, FXAA 3.11's "quality"
    /// value): a pixel whose 3x3 luma range is below this fraction of its brightest neighbour is
    /// left as it is. Skipping those flat pixels is most of the pass's cost saving.</summary>
    public const float FxaaEdgeThreshold = 0.125f;

    /// <summary>FXAA's absolute contrast floor (uFxaaEdgeThresholdMin, FXAA 3.11's default):
    /// dark neighbourhoods need at least this luma range before they are smoothed, so noise in
    /// caves and at night is not smeared.</summary>
    public const float FxaaEdgeThresholdMin = 0.0312f;

    // --- Contact AO (ContactAo, contact-ao-fragment.glsl, SH-28) ---

    /// <summary>Master switch for the screen-space contact AO pass run mid entity pass
    /// (RenderManager.RenderContactAo). Off, the frame is exactly what it was without it.</summary>
    public const bool ContactAoEnabled = true;

    /// <summary>Most a pixel is darkened by the contact AO (1 - this is the darkest multiplier);
    /// kept subtle, since the baked vertex AO already shades every block corner.</summary>
    public const float ContactAoStrength = 0.22f;

    /// <summary>Crease depth, in blocks, below which a pixel is left alone: depth noise and
    /// near-planar surfaces never reach it.</summary>
    public const float ContactAoMinBlocks = 0.03f;

    /// <summary>Crease depth, in blocks, at which the AO is strongest.</summary>
    public const float ContactAoPeakBlocks = 0.25f;

    /// <summary>Crease depth, in blocks, past which the AO has faded out again, so a silhouette
    /// in front of distant terrain is not mistaken for a crevice.</summary>
    public const float ContactAoRangeBlocks = 1.2f;

    /// <summary>Distance, in blocks, where the contact AO starts fading out.</summary>
    public const float ContactAoFadeNear = 24f;

    /// <summary>Distance, in blocks, past which there is no contact AO at all.</summary>
    public const float ContactAoFadeFar = 64f;

    /// <summary>World-space tap radius, in blocks, converted to pixels per fragment and clamped
    /// to 1..<see cref="ContactAoMaxPx"/>.</summary>
    public const float ContactAoRadiusBlocks = 0.2f;

    /// <summary>Largest tap radius in pixels, which bounds the pass's texture-cache footprint. Large
    /// enough that the taps keep their <see cref="ContactAoRadiusBlocks"/> world size down to a few
    /// blocks away at 1080p: a smaller cap shrinks near creases below <see cref="ContactAoMinBlocks"/>
    /// and the AO vanishes exactly where it is most visible.</summary>
    public const float ContactAoMaxPx = 32f;

    /// <summary>Distance from the shaded point, in tap radii, within which a tap that sees a
    /// surface in front of the shaded one counts fully as an occluder. A wall or pebble rising
    /// beside the point is about one tap radius away.</summary>
    public const float ContactAoReachNear = 1.5f;

    /// <summary>Distance from the shaded point, in tap radii, past which such a tap no longer
    /// counts: the upper part of a torch, drop or mob stands that far in front of the ground beside
    /// it, which would otherwise darken a displaced copy of its silhouette onto that ground.</summary>
    public const float ContactAoReachFar = 2.5f;

    // Sunlight and lamp light spread through air losing one level per block from a max
    // of 15, so one edited block can change light up to this far away. Block edits must
    // remesh every chunk in this radius, not just face-adjacent ones.
    public const int LightPropagationRadius = 15;

    // --- Mining ---
    // The block-breaking crack sprite (assets/textures/environment/break_progress.png) is a
    // vertical strip of this many square frames, top frame first and blank (0 % damage). The
    // progress 0..1 picks the frame; uBreakProgress's alpha is only the crack shape, so the
    // opacity below is what keeps the black cracks from fully hiding the block underneath.
    public const int BreakOverlayFrames = 8;
    public const float BreakOverlayOpacity = 0.6f;

    // --- Camera and swing feel (Camera, SprintFov, StrafeTilt, SwingHitStop) ---
    // Degrees the view widens by at a full on-foot sprint, added to the base FOV and the punch
    // pinch. The 10x flight sprint widens exactly as far, never further (SprintFov clamps the
    // speed ramp at the on-foot sprint speed).
    public const float SprintFovExtraDegrees = 5f;
    // How fast the widening eases in and back out (MathUtils.Damp rate, per second)
    public const float SprintFovEaseRate = 6f;
    // Degrees the view rolls toward a full sideways strafe (a lean into the move), added to the run
    // sway, the hit kick and the death roll. Off while flying and while dead.
    public const float StrafeTiltDegrees = 1.5f;
    // How fast the tilt eases in and back to level (MathUtils.Damp rate, per second)
    public const float StrafeTiltEaseRate = 8f;
    // Hit-stop: a punch or slice that connects holds the swing (and the punch pinch) at its impact
    // pose for a beat, a light swing for HitStopSeconds and the heaviest punch for
    // HitStopHeavySeconds, never past HitStopMaxSeconds. Cosmetic only: the world, the mobs, their
    // recoil, the player's movement and attack pacing never stop.
    public const float HitStopSeconds = 0.15f;
    public const float HitStopHeavySeconds = 0.2f;
    public const float HitStopMaxSeconds = 0.25f;
    // The most of a swing's remaining post-impact time a hold may take. The rest of the swing replays
    // faster after the hold, so a frozen swing still ends exactly when it would have and nothing that
    // waits on it (the slice combo's next swing, the next jab) is ever delayed. A punch, whose
    // follow-through is short, therefore holds for less than HitStopSeconds.
    public const float HitStopMaxRecoveryShare = 0.7f;
    // Throw charge zoom (ThrowZoom): degrees the view narrows by at a full throw charge, added to the
    // base FOV, the sprint widening and the punch pinch. A spring follows the charge and springs back
    // on release; its rate in Hz and damping ratio (below 1 overshoots a touch wider on the release).
    public const float ThrowZoomDegrees = 4f;
    public const float ThrowZoomSpringHz = 2.2f;
    public const float ThrowZoomSpringDamping = 0.55f;

    // --- Water depth states (WaterImmersion, PlayerPhysics) ---
    // The share of the body (PlayerHeight) under the fluid surface from which a body with its eye
    // still above it floats rather than wades; the eye going under is always a swim.
    public const float WaterFloatShare = 0.5f;
    // Wading (under WaterFloatShare) walks, at this share of the walk speed: a lighter slowdown than
    // swimming's, with the normal jump.
    public const float WadeSpeedFactor = 0.75f;
    // Floating: a body in deep water with its eye above the surface (and one that has not dived,
    // even with the eye under) settles where this share of it is under. Its vertical speed eases at
    // FloatSettleRate (per second) toward FloatBuoyancy (per second) times its height error, capped
    // by the swim sink and rise speeds; the pair is near critically damped, so it settles without
    // bobbing through the surface.
    public const float WaterFloatRestShare = 0.7f;
    public const float FloatBuoyancy = 2.5f;
    public const float FloatSettleRate = 6.4f;

    // --- Sky ---
    // Angular radius of the visible sun disc in degrees. The real sun is 0.27 degrees across
    // its radius, but at that size it reads as a pinprick, so games exaggerate it.
    public const float SunAngularRadiusDeg = 3.5f;

    // Radius of the glow quad as a multiple of the disc radius: the quad is drawn this much
    // bigger than the disc and the extra ring is pure falloff around it.
    public const float SunGlowScale = 4f;

    // Peak brightness the glow adds at the disc's edge, before the sun colour tints it
    public const float SunGlowIntensity = 0.55f;

    // Falloff exponent of the glow from the disc outwards; higher means a tighter halo
    public const float SunGlowExponent = 2.5f;

    // Lens glare (SunGlare): a much wider, fainter glow around the sun that grows as the camera
    // faces it. Linear HDR brightness it adds right at the sun, times the sun colour, at its peak.
    // Kept below the point where the sky round the disc washes out to white, so the disc stays crisp.
    public const float SunGlareStrength = 0.28f;

    // Falloff of the glare across the sky: cos(angle to the sun) raised to this. Lower is wider.
    public const float SunGlareExponent = 8f;

    // Angle between the view and the sun beyond which there is no glare at all
    public const float SunGlareFacingDegrees = 65f;

    // Sine of the sun's elevation from which the glare is at full strength; it fades to nothing
    // as the sun reaches the horizon
    public const float SunGlareFullElevation = 0.15f;

    // Rate (per second) at which the glare follows the cloud cover over the sun, so a drifting
    // cloud edge fades it instead of stepping it
    public const float SunGlareCloudEaseRate = 2.5f;

    // --- Sky dome (SkyPalette, SkySample in the sky/world/cloud shaders) ---
    /// <summary>Sine of the view elevation at which the zenith colour has fully taken over from the horizon.</summary>
    public const float SkyHorizonBand = 0.35f;

    /// <summary>Sine of the view elevation over which the brighter horizon haze band fades out.</summary>
    public const float SkyHazeWidth = 0.08f;

    /// <summary>
    /// How much brighter the haze band makes the horizon right at elevation 0 (0.2 = 20 %).
    /// 0 while the palette is the flat 28198cc sky colour: the haze multiplies the (linearised)
    /// horizon, which would push the low sky and every fogged fragment away from that colour.
    /// </summary>
    public const float SkyHazeStrength = 0f;

    /// <summary>Azimuthal falloff exponent of the dusk glow toward the sun; higher is a narrower lobe.</summary>
    public const float SkyGlowExponent = 4f;

    /// <summary>How far the flat sky colour moves toward the dusk orange at full dusk (the 28198cc mix).</summary>
    public const float SkyDuskMix = 0.5f;

    /// <summary>Brightness of the sunset glow band toward the sun at its peak (a scale on its sRGB hue in SkyPalette).</summary>
    public const float SkyDuskGlowStrength = 0.5f;

    /// <summary>Sine of the sun's elevation at and above which there is no sunset glow (the flat day sky).</summary>
    public const float SkyGlowAboveSunElevation = 0.2f;

    /// <summary>Sine of the sun's elevation at which the sunset glow is strongest (the sun on the horizon).</summary>
    public const float SkyGlowPeakSunElevation = 0f;

    /// <summary>
    /// Sine of the sun's elevation at and below which the afterglow is gone. Must stay at or above
    /// MoonlightSwitchSunElevation, where the moon takes over the world's light (MoonlightTests).
    /// </summary>
    public const float SkyGlowBelowSunElevation = -0.1f;

    // --- Stars (StarField, Stars in sky-fragment.glsl) ---
    /// <summary>Star cells along each side of one face of the cube of directions (6 faces); a cell holds at most one star.</summary>
    public const int StarCellsPerFace = 72;

    /// <summary>
    /// Chance a cell at a cube face's centre holds a star (StarField.Probability scales it per cell
    /// by solid angle), so about this share of 6 * StarCellsPerFace^2 stars cover the whole sphere.
    /// </summary>
    public const float StarDensity = 0.16f;

    /// <summary>
    /// Linear HDR brightness of the brightest star, before its colour tint and twinkle (which dims
    /// a star by StarTwinkleDepth / 2 on average). The night dome is near black and the post pass
    /// only clamps and encodes, so 1 already reads as a display-white point.
    /// </summary>
    public const float StarBrightness = 1.35f;

    /// <summary>Brightness fraction of the faintest star; the rest spread up to 1 along the power law below.</summary>
    public const float StarFaintest = 0.04f;

    /// <summary>Exponent of the star brightness power law: higher makes bright stars rarer.</summary>
    public const float StarBrightnessCurve = 4f;

    /// <summary>
    /// Gaussian radius of a faint star in screen pixels (pixel-relative, so stars stay pin-sharp
    /// at any resolution and FOV and never vanish between pixel centres while the view turns).
    /// </summary>
    public const float StarRadiusPixels = 0.85f;

    /// <summary>How much wider than a faint star the brightest star's point is.</summary>
    public const float StarBrightRadiusScale = 1.6f;

    /// <summary>Sine of the sun's elevation above which no star shows; below it they fade in over dusk.</summary>
    public const float StarsGoneAboveSunElevation = -0.02f;

    /// <summary>Sine of the sun's elevation below which the stars are at full brightness.</summary>
    public const float StarsFullBelowSunElevation = -0.22f;

    /// <summary>Sine of the view elevation over which the stars fade in above the horizon haze.</summary>
    public const float StarHorizonFade = 0.2f;

    /// <summary>
    /// Radius of the starless disc around the sun as a multiple of the sun's glow radius
    /// (SunSize * SunGlowScale); the stars feather back in over the next half of that radius.
    /// </summary>
    public const float StarSunMaskScale = 1.2f;

    /// <summary>How deep a star's twinkle dips: its brightness swings between 1 - this and 1.</summary>
    public const float StarTwinkleDepth = 0.45f;

    /// <summary>
    /// Slowest and fastest twinkle, in cycles per second; each star draws its own rate between them
    /// from its cell hash. Slow on purpose: a real scintillation rate would flicker at the frame rate.
    /// </summary>
    public const float StarTwinkleRateMin = 0.25f;
    public const float StarTwinkleRateMax = 1.2f;

    // --- Moon (MoonPhase, RenderManager.RenderMoon, moon-fragment.glsl) ---
    /// <summary>Days from one full moon to the next; the phase steps once a day, at noon.</summary>
    public const int MoonCycleDays = 8;

    /// <summary>Angular radius of the moon disc in degrees (exaggerated like the sun's).</summary>
    public const float MoonAngularRadiusDeg = 2.6f;

    /// <summary>Radius of the moon quad as a multiple of the disc: the extra ring holds its faint halo.</summary>
    public const float MoonGlowScale = 2.2f;

    /// <summary>Peak brightness of the halo at the disc's edge, scaled by how much of the disc is lit.</summary>
    public const float MoonGlowIntensity = 0.1f;

    /// <summary>Falloff exponent of the halo from the disc outwards.</summary>
    public const float MoonGlowExponent = 3f;

    /// <summary>Linear HDR brightness of the lit disc (times its sprite and its cool tint).</summary>
    public const float MoonIntensity = 1.6f;

    /// <summary>Share of the lit brightness the unlit part keeps (earthshine), so a crescent still shows its whole disc faintly.</summary>
    public const float MoonEarthshine = 0.025f;

    /// <summary>Half-width of the soft terminator between the lit and dark parts, in units of the sphere normal's dot with the light.</summary>
    public const float MoonTerminatorSoftness = 0.06f;

    // --- Aurora (Aurora, AuroraCurtains in sky-fragment.glsl) ---
    /// <summary>Coldness at the eye (EyeClimate.Coldness, the cold ramp) at and below which there is no aurora: the snow line.</summary>
    public const float AuroraColdStart = 0.55f;

    /// <summary>Coldness at the eye from which the aurora is at full strength (past the taiga/frozen line).</summary>
    public const float AuroraColdFull = 0.85f;

    /// <summary>How fast the eased coldness follows the eye's climate (MathUtils.Damp rate, per second): a slow fade across a border.</summary>
    public const float AuroraColdEaseRate = 0.35f;

    /// <summary>Linear HDR brightness of the curtains' bright lower edge at full strength.</summary>
    public const float AuroraBrightness = 0.5f;

    /// <summary>How fast the curtains drift and ripple along the sky (noise cells per second).</summary>
    public const float AuroraDriftSpeed = 0.035f;

    // --- Moonlight (Moonlight, RenderManager.SetLighting) ---
    /// <summary>
    /// Sine of the sun's elevation at and below which the moon lights the world instead of the sun.
    /// Must stay at or below SkyGlowBelowSunElevation, so the sunset glow is gone by then
    /// (MoonlightTests asserts it).
    /// </summary>
    public const float MoonlightSwitchSunElevation = -0.1f;

    /// <summary>How much further the sun sinks (the moon climbs), in sine of elevation, while the moonlight eases in.</summary>
    public const float MoonlightRiseBand = 0.25f;

    /// <summary>The full moon's light in the world shader's sun-intensity units (the noon sun is about 2.6).</summary>
    public const float MoonlightIntensity = 0.07f;

    /// <summary>Share of the full moon's light a new moon still gives, so a moonless night keeps faint shadows.</summary>
    public const float MoonlightNewMoonShare = 0.35f;

    // --- Clouds ---
    // One static field of CloudCellSize-block cells (CloudField) drawn 3x3 around the camera by
    // CloudRenderer. Cell levels come from periodic value noise, so the field tiles with no seam.
    // Heights clear the tallest terrain (~112) plus trees.
    public const float CloudHeight = 160f;
    // Drawn as whole copies 3x3 around the camera, so this must be >= FogFarBlocks for the
    // field edge to stay hidden in fog. Must be a power-of-two multiple of CloudCellSize.
    public const float CloudFieldSize = 2048f;
    public const float CloudCellSize = 16f;
    public const float CloudLayerThickness = 4f;
    public const int CloudMaxLevels = 2;
    // Density above which a cell holds a cloud (tuned for 30-45 % coverage; CloudTests asserts it),
    // and how much more density each extra stacked layer needs.
    public const float CloudThreshold = 0.54f;
    public const float CloudLevelStep = 0.09f;
    public const float CloudOpacity = 0.8f;
    // Fillet radius on every convex cloud edge, in blocks. Must stay below half the layer thickness
    // and half the cell size so the flat parts of a one-layer cell keep a positive size.
    public const float CloudCornerRadius = 1.5f;
    // Quads per quarter turn of a fillet; 3 is plenty with smooth per-vertex normals.
    public const int CloudFilletSegments = 3;
    public const int CloudSeed = 9241;
    public const float CloudDriftBlocksPerSecond = 1.5f;
    /// <summary>
    /// Fraction of direct sun a full-thickness cloud removes from the terrain under its shadow
    /// (test-fragment.glsl CloudShadow); a one-layer cloud removes half of it.
    /// </summary>
    public const float CloudShadowStrength = 0.4f;

    // Silver lining (CloudSilverLining, cloud-fragment.glsl): thin cloud edges near the sun light up.
    /// <summary>Peak brightness of the lining, as a share of the cloud's own display-space sun term.</summary>
    public const float CloudSilverLiningStrength = 0.9f;
    /// <summary>Forward-scatter falloff: cos(angle to the sun) raised to this. Higher keeps it closer to the sun.</summary>
    public const float CloudSilverLiningExponent = 10f;
    /// <summary>Sine of the sun's elevation from which the lining is at full strength (zero at the horizon).</summary>
    public const float CloudSilverLiningFullElevation = 0.1f;
    /// <summary>Blocks between the points the thinness test reads along the view ray behind a cloud surface.</summary>
    public const float CloudSilverLiningStepBlocks = 2.5f;

    // --- Far-terrain LOD ---
    // Level definitions (cell sizes, per-level radii) live in LodRegion.Levels.

    // How many completed LOD region meshes to upload to GPU per frame. This is the MAIN
    // THREAD's per-frame GL ceiling (VAO setup, buffer uploads) and C5 does NOT raise it. It is
    // also nowhere near the limiter: C5's capture measured the single builder producing 53-55
    // regions/s against this cap's 4 x 108 fps = ~433 regions/s, i.e. 7.8-8.2x headroom, which is
    // why MaxConcurrentLodGenerations could be raised at all. MaxConcurrentLodGenerations builders
    // would exceed this cap during the streaming burst -- that is what MaxLodBuildsInFlight bounds
    // -- but the pool ships at 1, so today the drain stays ahead of the builder. Halving it from 8
    // cost no convergence (item 26).
    public const int MaxLodUploadsPerFrame = 4;

    // How many finished region builds may wait for the main thread before the LodGen workers
    // stop claiming. Outstanding rents on LodManager.MeshBuffers are therefore bounded by
    // MaxLodBuildsInFlight (waiting) + MaxConcurrentLodGenerations (building), which is what the
    // pool's retention ceilings are sized from. 8 is two frames of uploads at
    // MaxLodUploadsPerFrame = 4, so a worker can run a frame ahead of the drain without the
    // queue turning into an unbounded backlog of pinned vertex lists (item C5).
    public const int MaxLodBuildsInFlight = 8;

    // How long a THROTTLED LodGen worker backs off, in ms. A throttled worker is not idle: work
    // is available and it is only waiting for the main thread to drain the completed queue, so it
    // must not sleep GenerationDelayIdleMs = 200 (that would stall the burst for a fifth of a
    // second at a time). This is a COARSE backoff whose real granularity is the OS timer: a 1 ms
    // Thread.Sleep on Windows lands anywhere from ~1 to ~15 ms unless the timer resolution was
    // raised, so it is not "about a frame" and nothing promises it is. Correctness does not depend
    // on the value at all -- the next spin simply re-checks the cap and retries (item C5).
    public const int LodBuildThrottleDelayMs = 1;

    // How many unburied level-0 regions one buried-flag pass cover-tests. Level-0 tests
    // walk 16 columns of chunk lookups each, so the uncapped pass is ~10 ms mid-stream;
    // new regions, coarser levels and still-buried regions are always tested (item 19).
    public const int MaxLodCoverTestsPerFrame = 64;

    // --- Terrain ---
    // Every noise channel and worldgen hash derives its seed from this one (TerrainGenerator,
    // NoiseCaves, WormCarver, the feature generators). It is runtime state, not a constant: a world
    // sets it through WorldSeed.Apply between worlds (never while one is live), and it defaults to
    // WorldSeed.Default, so tests, the item debugger and scripted runs see the old fixed seed.
    public static int Seed => WorldSeed.Value;

    // --- Saves ---
    // Seconds between autosaves of the loaded, edited chunks while a world with a save store runs.
    public const double AutosaveIntervalSeconds = 60.0;
    // Chunk snapshots one autosave call takes, so a large burst of edits spreads over frames.
    public const int MaxAutosaveSnapshotsPerCall = 256;
    // How long closing a world waits for the save writer thread to land its queued files.
    public const double SaveWriterShutdownTimeoutSeconds = 10.0;
    // Attempts to move a written temp file into place, since a reader or scanner may hold it briefly.
    public const int SaveMoveRetries = 3;
    // Pause between those move attempts.
    public const int SaveMoveRetryDelayMs = 50;
    // How long stopping the chunk-generation and LOD workers waits to join each thread.
    public const int WorkerStopJoinTimeoutMs = 5000;
    // How long closing a world waits for in-flight mesh builds (they read the worldgen caches).
    public const int MeshDrainTimeoutMs = 5000;
    // Cells a removal of a column's top block scans down for the new LOD surface before giving up.
    public const int LodSurfaceRescanDepth = 64;
    // Chunks around the camera that must be generated and meshed before a loaded world starts
    // playing; the sphere is ~123 chunks, kept far below ChunkRadius so meshing always has its neighbours.
    public const int LoadReadyRadiusChunks = 3;
    // A loading world starts playing anyway after this much accumulated update time.
    public const double LoadTimeoutSeconds = 30.0;
    // The per-frame caps while loading: larger than play, still bounded so no frame spikes.
    public const int LoadingMeshUploadsPerFrame = 64;
    public const int LoadingDirtyChunksPerFrame = 128;
    public const int LoadingLodUploadsPerFrame = 8;
    // Saved mobs and drops whose chunk never loads are dropped after this many seconds of play.
    public const double EntityRestoreTimeoutSeconds = 30.0;
    // Nearest-first cap on the item drops one save writes.
    public const int MaxSavedDrops = 512;
    // Per-second rate the loading screen's shown bar eases toward the real load progress (MathUtils.Damp).
    public const float LoadingProgressDampRate = 6f;
    // Seconds of wall time for one revolution of the loading screen's spinner.
    public const double LoadingSpinnerPeriodSeconds = 1.2;
    // The loading overlay fades out over this many seconds once the world plays.
    public const float LoadingFadeSeconds = 0.5f;
    // Dots around the loading screen's spinner ring.
    public const int LoadingSpinnerDots = 8;

    // --- Deterministic worldgen caches (GenerationalCache) ---
    // ColumnCache.Get's cache. Column data depends only on (x, z), so one entry serves the whole
    // vertical chunk stack and the far LOD alike.
    //
    // CAPACITY is unchanged from the wholesale-Clear() era on purpose: the residency ceiling does not
    // move, only the eviction policy does. A ColumnData is seven 256-element arrays plus scalars
    // (int[] Heights/Bed/WaterLevel, float[] EntranceWeight/CaveIntensity, byte[] DirtDepth,
    // byte-backed Biome[]), which is about 5.9 KB, so 16384 entries is roughly 96 MB -- NOT the
    // "~40 MB" the old Chunk.cs comment claimed, a figure that predated Bed, WaterLevel and Biomes.
    // That arithmetic is exactly why item D2's fix is more GENERATIONS at the same capacity rather
    // than a bigger cache.
    //
    // GENERATIONS is the one-token re-pricing lever a later round can move: at G the sweep can age
    // out only the oldest 1/G of the cache at a time, and anything read while it sits in that oldest
    // generation is promoted forward instead of dropped. Higher G retains more of the hot set but
    // narrows each generation's insert budget; see GenerationalCache's numbered invariants (7a/7b)
    // and PERF_TODO's D2 measurement table.
    public const int ColumnCacheCapacity = 16384;
    public const int ColumnCacheGenerations = 4;

    // Blocks of dirt under the grass: DirtDepthBase +- DirtDepthVariation, varying slowly over the map
    public const float DirtDepthBase = 3f;
    public const float DirtDepthVariation = 1.5f;
    public const float DirtDepthFrequency = 1f / 32f;

    // Far LOD side faces switch from dirt to stone below this depth
    public const int NominalDirtDepth = 4;

    // --- Water ---
    // Sea level: every column whose terrain height is below it is flooded with water up to
    // SeaLevel, after the carvers and before trees (see Chunk.FillWater). Measured over a
    // 1024x1024 area around the origin, terrain heights run -65..112 with a mean near +15, so
    // 0 floods about a quarter of the map (basins up to ~65 deep) while the origin column
    // (height 0) stays dry as a shoreline. Coverage: -8 -> 8%, 0 -> 26%, 4 -> 45%, 8 -> 53%.
    public const bool WaterEnabled = true;
    public const int SeaLevel = 0;

    // Planar water reflections render the opaque world mirrored across the sea surface
    // into a texture this fraction of the viewport (half res ≈ quarter cost).
    public const float WaterReflectionScale = 0.5f;

    // Reflection temporal reuse: the planar mirror is re-rendered at most every other frame and
    // the finished texture (with the view-projection that produced it) is reused in between.
    // These are the per-frame deltas that force an immediate refresh instead, so a hard turn,
    // a fast move or a changed projection never samples a stale mirror. They are per-FRAME and
    // not per-second: at 30 fps the effective limits are half the 60 fps figures quoted below.
    // The gate therefore refreshes more often on slow frames, which is the safe direction.
    // View-direction change since the frame that produced the cached mirror, in degrees. The
    // water shader clamps its projected mirror lookup to [0.001,0.999] (test-fragment.glsl:289)
    // after a wave offset of nW.xz * 0.08 (:288), so rotating past this smears the clamped
    // screen edge; 1 deg/frame is ~60 deg/s at 60 fps, i.e. any real pan refreshes every frame.
    public const float ReflectionReuseMaxDegrees = 1.0f;
    // Eye movement in blocks. BaseSpeed 8 with SprintMultiplier 1.5 (Camera.cs) is <= 0.2
    // blocks/frame at 60 fps and still reuses; FlyBoostMultiplier 10 is ~1.3 and always refreshes.
    public const float ReflectionReuseMaxMoveBlocks = 0.35f;
    // Vertical FOV change, in degrees. Camera.GetProjectionMatrix builds from _currentFov, which
    // eases toward the sprint boost via MathUtils.Damp, and that projection is baked into the
    // cached reflection view-projection.
    public const float ReflectionReuseMaxFovDegrees = 0.1f;
    // Viewport aspect change, the other GetProjectionMatrix input. A tiny resize can round to the
    // same reflection texture size, so the FBO-recreation trigger alone does not cover it.
    public const float ReflectionReuseMaxAspect = 0.001f;
    /// <summary>
    /// How far (in blocks) any mirrored entity may drift from where the cached mirror drew it
    /// before the reflection has to refresh (<c>ReflectionReuse.EntitiesMoved</c>). Small, so a
    /// hopping slime or a bobbing drop never visibly lags its own reflection.
    /// </summary>
    public const float ReflectionReuseMaxEntityMoveBlocks = 0.02f;
    /// <summary>
    /// Only entities within this horizontal distance (blocks) of the eye, and above the water
    /// plane, both feed the reflection's entity-motion signal and are drawn in the mirror.
    /// </summary>
    public const float ReflectionEntityRadius = 64f;

    // --- GPU timer queries (RenderManager.BeginGpuFrame / BeginGpuPass, item R6) ---
    // One frame in this many still issues its TIME_ELAPSED queries while the X overlay is down,
    // so the once-a-second snapshot standing when X is pressed already carries GPU figures
    // instead of "--". Chosen, not measured: ~12 samples per publish window at 100 fps for an
    // eighth of the always-on GL query traffic. Moving it owes a counted capture, not a guess.
    public const int GpuTimingIdleInterval = 8;

    // Wave normal steepness multiplier for the water shading (geometry moves less than it looks)
    public const float WaterNormalScale = 2.0f;
    // Lava swell normal steepness: LavaWaveHeight's slopes are gentle (broad, low-frequency
    // blobs), so the gain is higher than water's to keep the catch-light visibly riding the heave
    public const float LavaNormalScale = 6f;
    // Strength of the point-light (fire, kiln, TNT) catch-light and sky sheen on the lava crust;
    // backed off over the molten texels, which already glow past it
    public const float LavaCatchLightStrength = 0.6f;
    // Blinn exponent of that catch-light: high, so a nearby fire reads as a small glint
    public const float LavaCatchLightPower = 48f;
    // Lava glow pulse (test-fragment.glsl, LavaPulse): how far the lava's brightness breathes above
    // and below itself on a slow noise field, as a fraction (0.2 = 80..120 %); 0 switches it off
    public const float LavaPulseStrength = 0.2f;
    // Screen-space refraction offset at the camera, in uv units; shrinks with distance in the shader
    public const float WaterRefractStrength = 0.06f;
    // Seen from below, the surface is a Snell window: the whole sky above is squeezed into a
    // ~97 degree cone, so the same wave tilt swings the view through it further than it does
    // looking down. Multiplies WaterRefractStrength (and the WaterRefraction slider) for
    // fragments whose face is viewed from behind.
    public const float WaterWindowRefractScale = 1.75f;
    // Beer-Lambert absorption per block of water, per channel (red dies first, so depth reads teal)
    public static readonly Vector3 WaterAbsorb = new(0.20f, 0.07f, 0.04f);
    // In-scattered water colour that replaces the seabed as the water thickens
    public static readonly Vector3 WaterScatter = new(0.05f, 0.30f, 0.40f);
    // Brightness of the sunlight caustics on submerged faces
    public const float WaterCausticStrength = 0.6f;
    // Past WaterFancyFarBlocks water skips its wave/reflection/refraction model for a
    // geometric-normal fresnel between saturated scatter and the sky mirror (no texture
    // fetches); the Near->Far band blends the two so the switch never pops. Near sits at
    // the full-detail ring (ChunkRadius * 16) so LOD water is always cheap, and CullWater
    // skips the reflection pass and the scene copy when no water is nearer than Far.
    public const float WaterFancyNearBlocks = 256f;
    public const float WaterFancyFarBlocks = 384f;
    // Underwater sun shafts: brightness, how far the ray marches and how many samples it takes
    public const float WaterRayStrength = 0.08f;
    public const float WaterRayMaxDistance = 40f;
    public const int WaterRaySteps = 20;
    // How far apart two marched distances have to be, in blocks, before the half-res sun-shaft
    // upsample (rays-upsample-fragment.glsl) stops letting one bleed into the other. The falloff
    // is exponential, so ~2 blocks of mismatch keeps about a third of a tap's weight and a
    // silhouette several blocks deep rejects its neighbours outright.
    public const float WaterRayUpsampleToleranceBlocks = 2f;

    // Godrays (Godrays, GodraysRenderer, godrays-fragment.glsl): light shafts through trees, clouds
    // and gaps in dry air when the sun is in view, the underwater shafts' march run over air.
    /// <summary>Master switch for the godrays pass; off, it costs nothing and the frame is exactly what it was.</summary>
    public const bool GodraysEnabled = true;
    /// <summary>Peak strength (Godrays.Intensity's cap), a multiplier on what the shafts add.</summary>
    public const float GodraysStrength = 1f;
    /// <summary>Scattering per block of sunlit air marched (Godrays.Scatter): how fast the shafts saturate along a ray.</summary>
    public const float GodraysDensityPerBlock = 0.006f;
    /// <summary>The most the shafts add to a display value, at full strength and phase with a long sunlit path.</summary>
    public const float GodraysMaxAdd = 0.4f;
    /// <summary>Blocks of sunlit air counted past the march for a ray that reaches open sky (no terrain, tree or cloud in the way).</summary>
    public const float GodraysSkyBlocks = 250f;
    /// <summary>How far the march reaches, in blocks: inside the near shadow cascade (ShadowRadiusBlocks).</summary>
    public const float GodraysMaxDistance = 72f;
    /// <summary>Samples per marched pixel (the march runs at half resolution).</summary>
    public const int GodraysSteps = 24;
    /// <summary>Angle between the view and the sun beyond which there are no godrays.</summary>
    public const float GodraysFacingDegrees = 80f;
    /// <summary>Sine of the sun's elevation at which the shafts are strongest (a low sun).</summary>
    public const float GodraysPeakElevation = 0.2f;
    /// <summary>Share of their peak strength the shafts keep with the sun straight overhead.</summary>
    public const float GodraysNoonShare = 0.3f;
    /// <summary>How much a full cloud over the sun's disc dims the shafts (they never vanish: cloud gaps still cast them).</summary>
    public const float GodraysCloudDim = 0.7f;
    /// <summary>How much a full-thickness cloud's shadow removes from the sunlit air under it, in the march.</summary>
    public const float GodraysCloudShadow = 0.8f;
    /// <summary>Forward-scatter falloff of the shafts: cos(view, sun) raised to this.</summary>
    public const float GodraysPhaseExponent = 8f;
    /// <summary>Share of the forward peak the shafts keep looking well away from the sun.</summary>
    public const float GodraysPhaseFloor = 0.02f;
    /// <summary>How much of the way from a pixel to the sun's place on screen the composite's radial blur reaches.</summary>
    public const float GodraysBlurLength = 0.85f;
    /// <summary>Each radial blur sample's weight relative to the one before it (nearer samples count more).</summary>
    public const float GodraysBlurDecay = 0.95f;
    /// <summary>Radial blur samples per full-resolution pixel.</summary>
    public const int GodraysBlurSamples = 24;
    // Flow-aware water (SH-14): the mesher bakes each fluid corner's FluidFlow.Corner vector into
    // the vertex and the world shaders stretch and scroll the wave normal along it.
    /// <summary>Corner flow magnitude (<see cref="FluidFlow.Corner{T}"/>) that reads as full
    /// strength; weaker corners scale their baked flow down linearly.</summary>
    public const float WaterFlowFullStrength = 0.5f;
    /// <summary>Fraction of the along-flow wave slope damped at full strength, which stretches
    /// the crests along the flow.</summary>
    public const float WaterFlowStretch = 0.6f;
    /// <summary>How fast the flow map scrolls the wave pattern at full strength, in blocks per second.</summary>
    public const float WaterFlowScrollSpeed = 1.5f;
    /// <summary>Seconds per flow-map phase; the two crossfaded phases each restart this often.</summary>
    public const float WaterFlowPeriod = 2f;
    /// <summary>Vertical scroll speed of the streaks on falling-water side faces, in blocks per second.</summary>
    public const float WaterFallScrollSpeed = 5f;
    /// <summary>How strongly the falling streaks tilt the side-face normal.</summary>
    public const float WaterFallStreakStrength = 0.5f;

    // Shore foam (test-fragment.glsl): a thin pale band where the water surface meets a block,
    // a bank or a thin trickle, i.e. where the water the eye looks through (the refraction
    // path's depth from the surface to the scene behind it) thins to nothing.
    /// <summary>
    /// Water depth along the view ray, in blocks, at which the foam band ends. Must stay under a
    /// full cell's surface height (<see cref="FluidState.SourceHeight"/>) less the wave swell, so
    /// open water over a block floor never foams; only a wall, a bank or a thin flow does.
    /// </summary>
    public const float WaterFoamWidthBlocks = 0.5f;
    /// <summary>Opacity of the foam at the contact line, 0..1 (the band breaks up away from it).</summary>
    public const float WaterFoamStrength = 0.7f;
    /// <summary>Albedo of the foam film, authored in sRGB (pale, slightly blue-white).</summary>
    public static readonly Vector3 WaterFoamColor = new(0.86f, 0.92f, 0.94f);

    // Sun glitter (test-fragment.glsl, WaterGlitter): hashed sparkles of mirrored sun on the waves
    // along the sun's reflection path.
    /// <summary>Brightness of a glitter sparkle as a multiple of the direct sun (uWaterGlitter): one
    /// mirrored sun on a tiny facet, meant to reach white. 0 switches the term off.</summary>
    public const float WaterGlitterStrength = 2f;
    /// <summary>Glitter cells per block along each axis of the water plane; each holds at most one sparkle.</summary>
    public const float WaterGlitterCellsPerBlock = 5f;
    /// <summary>Share of the glitter cells that hold a sparkle facet at all.</summary>
    public const float WaterGlitterDensity = 0.8f;
    /// <summary>Largest tilt of a sparkle facet off the wave normal, in radians: how far around the
    /// wave's own mirror direction the sparkles spread, i.e. how wide the glitter path is.</summary>
    public const float WaterGlitterTiltRadians = 0.12f;
    /// <summary>How many times a second each cell re-rolls its facet (fading out and in between).</summary>
    public const float WaterGlitterRate = 3f;
    /// <summary>Distances from the eye, in blocks, over which the glitter fades out before a cell
    /// turns subpixel and a sparkle would only shimmer.</summary>
    public const float WaterGlitterFadeNearBlocks = 48f;
    public const float WaterGlitterFadeFarBlocks = 128f;

    /// <summary>
    /// Steps of the screen-space reflection march that stands in for the planar mirror on water
    /// off the sea plane (a pond above sea level). The shader's loop bound is 8, so more is clamped.
    /// </summary>
    public const int WaterSsrSteps = 6;
    /// <summary>Length of the first SSR step, in blocks.</summary>
    public const float WaterSsrStepStart = 0.75f;
    /// <summary>Factor each SSR step grows by, so a few steps still reach a far bank.</summary>
    public const float WaterSsrStepGrowth = 1.6f;
    /// <summary>
    /// How far behind the depth buffer a march point may land and still count as a hit, as a
    /// multiple of the current step length; farther means the ray passed behind an occluder.
    /// </summary>
    public const float WaterSsrThickness = 1.5f;
    /// <summary>
    /// Distance (blocks) of a water surface from the mirror plane at which SSR starts to blend
    /// in. Above the lava/water wave amplitude (~0.09), so sea-level water never pays for it.
    /// </summary>
    public const float WaterSsrPlaneNear = 0.25f;
    /// <summary>Plane distance (blocks) at which SSR fully replaces the planar mirror.</summary>
    public const float WaterSsrPlaneFar = 1.0f;

    // Beaches ring the sea: dry columns up to BeachAboveSeaLevel over the waterline and
    // submerged columns down to BeachBelowSeaLevel under it build their top and soil band
    // from sand instead of grass/dirt. Ravine bare-stone walls still win over the beach.
    public const int BeachAboveSeaLevel = 2;
    public const int BeachBelowSeaLevel = 3;

    // Gravel patches cap some beach columns: where the gravel noise passes its threshold
    public const float GravelFrequency = 1f / 48f;
    public const float GravelThreshold = 0.45f;

    // --- Ore highlight (test-fragment.glsl) ---
    /// <summary>
    /// Scale on the ore sun highlight (uOreSpecScale). The highlight is added in linear light,
    /// where the sRGB encode lifts a small add on the near-black ore body far more than the same
    /// add did in display space before linear lighting; at full strength the broad lobe washed
    /// coal's specks into grey. 0.1 matches the pre-linear look side by side.
    /// </summary>
    public const float OreSpecLinearScale = 0.1f;

    // --- Ore twinkle (test-fragment.glsl, OreTwinkle) ---
    /// <summary>
    /// Brightness of an ore twinkle at its peak, as a multiple of the direct sun (uOreTwinkle): a
    /// single texel, so like a snow glint it is meant to reach white. 0 switches the term off.
    /// </summary>
    public const float OreTwinkleStrength = 1.5f;
    /// <summary>Share of an ore tile's texels that twinkle at all (uOreTwinkleDensity).</summary>
    public const float OreTwinkleDensity = 0.04f;
    /// <summary>Slowest and fastest twinkle, in flashes per second; each texel hashes its own rate between.</summary>
    public const float OreTwinkleRateMin = 0.2f;
    public const float OreTwinkleRateMax = 0.5f;
    /// <summary>Half-width of a flash as a fraction of its texel's cycle: the flash lights
    /// 2 * this of the cycle, so twinkles stay occasional.</summary>
    public const float OreTwinkleWidth = 0.04f;
    /// <summary>Distances from the eye, in blocks, over which the twinkle fades out, before a 32 px
    /// texel turns subpixel and a flash would only shimmer.</summary>
    public const float OreTwinkleFadeNearBlocks = 12f;
    public const float OreTwinkleFadeFarBlocks = 28f;

    // --- Snow sparkle (test-fragment.glsl) ---
    /// <summary>
    /// Brightness of a snow glint (uSnowSparkle), as a multiple of the direct sun: the sun
    /// mirrored by one ice-crystal facet, added in linear light. A glint is a single texel, so
    /// unlike the ore highlight's broad lobe it cannot wash a face grey, and it is meant to reach
    /// white. Sunlit snow already sits past the tonemap clip for most of the day (the sun's linear
    /// gain is ~2.7 at noon), so the glints read only where it does not: a low sun, whose n.l on
    /// the flat tops is small. 0 switches the term off.
    /// </summary>
    public const float SnowSparkleStrength = 1.5f;
    /// <summary>Share of the snow tile's texels that carry a glinting facet at all (uSnowSparkleDensity).</summary>
    public const float SnowSparkleDensity = 0.15f;

    // --- Leaf backlight (test-fragment.glsl, LeafBacklight) ---
    /// <summary>
    /// Brightness of the light a leaf lets through when it stands between the eye and the sun, as a
    /// multiple of the direct sun on its tinted albedo (uLeafBacklight). 0 switches the term off.
    /// </summary>
    public const float LeafBacklightStrength = 0.9f;
    /// <summary>Exponent of the backlight's lobe around the sun: higher keeps the glow closer to the
    /// sun's direction (6 is about half strength 25 degrees off it).</summary>
    public const float LeafBacklightPower = 6f;
    /// <summary>How far toward the sun, in blocks, the shadow map is read to decide whether sunlight
    /// reaches a leaf from behind: a crown thinner than this along the sun glows, a thicker one stays dark.</summary>
    public const float LeafBacklightReachBlocks = 1.5f;
    /// <summary>Tint of the transmitted light over the leaf's own albedo, authored in sRGB (warm).</summary>
    public static readonly Vector3 LeafBacklightTint = new(1f, 0.9f, 0.6f);

    // --- Grass wind shimmer (test-fragment.glsl, GrassShimmer) ---
    /// <summary>
    /// How much a shimmer band brightens the grass tops, as a share of their own light at a band's
    /// crest and a full gust (uGrassShimmer). 0 switches the term off.
    /// </summary>
    public const float GrassShimmerStrength = 0.5f;
    /// <summary>Distance between two shimmer bands along the wind, in blocks.</summary>
    public const float GrassShimmerWavelengthBlocks = 7f;
    /// <summary>Distance from the eye, in blocks, at which the shimmer starts to fade out.</summary>
    public const float GrassShimmerFadeNearBlocks = 48f;
    /// <summary>Distance from the eye, in blocks, past which there is no shimmer: a band a few blocks
    /// wide turns into shimmering pixels well inside the full-detail ring.</summary>
    public const float GrassShimmerFadeFarBlocks = 112f;

    // --- Ice glint (test-fragment.glsl, IceGlint) ---
    /// <summary>
    /// Brightness of the sun highlight on ice, as a multiple of the direct sun (uIceGlint), added in
    /// linear light. 0 switches the whole ice term off, the sky sheen included.
    /// </summary>
    public const float IceGlintStrength = 1f;
    /// <summary>Blinn exponent of the ice sun highlight: high, since ice is a smooth, flat face.</summary>
    public const float IceGlintShine = 96f;
    /// <summary>Share of the mirrored sky an ice face shows at a full graze (scaled by the Schlick fresnel).</summary>
    public const float IceSheenStrength = 0.6f;

    // --- Glass sheen (test-fragment.glsl, GlassSheen) ---
    /// <summary>
    /// Share of the mirrored sky a glass (or pane) face shows at a full graze, scaled by the Schlick
    /// fresnel and folded into the blend (uGlassSheen). 0 switches the term off.
    /// </summary>
    public const float GlassSheenStrength = 0.6f;

    // --- Wet albedo (test-fragment.glsl, SH-22) ---
    // Albedo multiplier on a submerged face below its own baked water surface. Dry faces beside or
    // above the water are never darkened: the old wet band tinted dry beach tops and banks.
    public const float WetDarken = 0.62f;

    // --- Surface water (lakes and rivers) ---
    // The forest floor sits above sea level, so an un-carved column is always the dry rim of any
    // carved water: a lake is a bowl and a river a channel, and water fills only the part cut
    // below the waterline, which keeps it enclosed instead of floating on a slope. The carve and
    // the flood test read the same LakeWeight/RiverWeight functions (TerrainGenerator). Forest
    // only: a desert stays dry, and sand/gravel show only where these weights are present.
    public const bool LakesAndRiversEnabled = true;
    // Below this water-body weight a below-waterline column is left dry.
    public const float WaterBodyThreshold = 0.05f;
    // The lake/river carve and the flood test are scaled by 1 - smoothstep of desertWeight over
    // [WaterDesertFadeStart, WaterDesertFadeEnd], so a bowl or channel tapers out and closes on
    // forest ground just before the hard forest/desert line (0.5). That line is exact on both
    // borders, the humid one and the temperate one: desertWeight = min(warm, dry) * (1 - ocean) and
    // desert classifies at min(warm, dry) >= 0.5, so the fade has fully closed on every inland
    // desert column. Only the last stretch before the desert is affected, so lakes elsewhere in the
    // forest keep their full depth. Without it a pond that straddled the border was carved only on
    // the forest side and ended in a vertical wall against the uncarved desert (see
    // TerrainGenerator.SurfaceWaterGate).
    public const float WaterDesertFadeStart = 0.3f;
    public const float WaterDesertFadeEnd = 0.5f;

// Lakes: one hashed candidate per cell, which keeps them small and scattered instead of the
// broad sheets a raw noise threshold carves. Each candidate chooses its own water surface from
// the ground it stands on - LakeBankDepth under the lowest point of its rim - so a pond sits in
// the landscape that holds it instead of at the bottom of a crater dug to one global sea level,
// and its size, depth and outline all vary. A site whose rim climbs more than LakeSiteFlatness is
// a hillside and grows no lake, which is what keeps ponds out of the slopes.
public const float LakeMinDepth = 3f;       // a small pond is a shallow scrape...
public const float LakeMaxDepth = 13f;      // ...a broad lake is a real basin
public const float LakeRadiusSkew = 2.2f;   // exponent on the size roll: many puddles, rare broad lakes
public const float LakeBankDepth = 2f;      // the water surface sits this far under the lowest rim
public const float LakeSiteFlatness = 7f;   // reject a site whose rim climbs more than this
public const float LakeShapeFrequency = 1f / 26f; // shoreline wobble: a few lobes across a lake
public const float LakeShapeAmplitude = 0.3f;     // fraction of the radius the wobble moves
public const int LakeCellBlocks = 192;      // candidate grid, one possible lake per cell
public const float LakeChance = 0.35f;      // fraction of cells that grow a lake
public const float LakeMinRadius = 11f;
public const float LakeMaxRadius = 46f;
public const int LakeSpawnClearRadius = 72; // no lake this close to the origin spawn

    // Rivers: warped ridge lines (|noise| near zero) gated by a rarity mask. The channel is cut
    // RiverBedDepth below the waterline, then faded out as the local land climbs above RiverLowEnd,
    // so a river runs through the lowlands and dries into a wash as it climbs.
    public const float RiverFrequency = 1f / 520f;
    public const float RiverWarpFrequency = 1f / 220f;
    public const float RiverWarpAmplitude = 26f;
    public const float RiverMaskFrequency = 1f / 700f;
    public const float RiverMaskStart = 0.4f;
    public const float RiverMaskEnd = 0.6f;
    public const float RiverWidth = 0.045f;
    public const float RiverBedDepth = 3f;
    public const float RiverLowStart = 14f;    // at or below: full channel carve
    public const float RiverLowEnd = 30f;      // at or above: no carve (dry wash)

    // --- Snow and ice ---
    // Cold columns freeze their surface water into a walkable ice sheet and lay snow on dry ground.
    // Both read TerrainGenerator.ColdWeight (a biome-generation input), so the snow line and the
    // freeze line fade in as a column cools instead of snapping at a biome border.
    public const bool SnowAndIceEnabled = true;
    public const float SnowColdThreshold = 0.55f;   // ColdWeight above which snow starts to settle
    public const float FreezeColdThreshold = 0.72f; // ColdWeight above which surface water freezes
    public const float SnowFrequency = 1f / 140f;   // drift noise: where snow piles deeper
    public const int SnowMaxDepth = 8;              // deepest natural drift, in layers (block holds 16)

    // Ice spikes: rare tall ice columns standing on the barren frozen plains.
    public const float IceSpikeChance = 0.0005f;    // per candidate column
    public const int IceSpikeSpacingRadius = 3;     // lowest-hash spacing, like trees
    public const int IceSpikeMinHeight = 4;
    public const int IceSpikeMaxHeight = 9;
    public const int IceSpikeReachBlocks = 6;       // chunk border margin, like the other generators

    // --- Biomes ---
    // Biome is a pure function of (x, z): continentalness (low = ocean) splits ocean from land; on
    // land temperature and humidity form a 2D climate grid (warm+wet forest, warm+dry desert,
    // cold+wet taiga, cold+dry barren frozen). Every field is a smooth 0..1 weight, so the height
    // profile blends across a border instead of stepping, and a per-column hash breaks the
    // forest/desert material line into a ragged patchwork.
    public const bool BiomesEnabled = true;

    // Continentalness: a ~2800-block wavelength keeps ocean basins 1-2k blocks across.
    public const float ContinentFrequency = 1f / 2800f;
    public const float ContinentOceanStart = -0.30f;   // at or below: full ocean
    public const float ContinentOceanEnd = -0.04f;     // at or above: full land

    // Ocean weight at which the ocean biome, its water and its shore materials begin. The land
    // clamp fades out across [OceanShoreWeight, 0.5], so the coast is a continuous slope into the
    // water instead of a cliff where the hard biome flip used to be.
    public const float OceanShoreWeight = 0.35f;

    // Temperature: a three-band axis. The cold ramp (TemperatureCold*, read by
    // TerrainGenerator.ColdWeight) holds the snow, the ice and the taiga/frozen biomes; the warm
    // ramp (TemperatureWarm*, read by TerrainGenerator.WarmWeight) holds the desert; the raw-noise
    // gap between them is the temperate band. Cold land is taiga (with humidity) or the barren
    // frozen biome (without it). Temperature and humidity are read only while choosing a biome and
    // the worldgen features that follow from it, and by the grass/foliage tint through
    // TerrainGenerator.Climate, so nothing else in the game sees them.
    // Low frequency (one octave, see TerrainGenerator's ctor) keeps the temperature gradient shallow,
    // so the raw-noise gap between the two ramps spans a wide band of world: at this frequency it
    // keeps every hot column at least a full-detail load radius from snow
    // (BiomeTests.HotAirKeepsAFullLoadRadiusFromSnow). Changing it re-lays every temperature
    // value, the origin's included.
    public const float TemperatureFrequency = 1f / 4500f;
    public const float TemperatureColdStart = -0.35f;  // at or below: fully cold
    public const float TemperatureColdEnd = -0.05f;    // at or above: no cold at all
    // The warm ramp must start at or after the cold ramp ends, so no column is ever both cold and
    // warm (a test rule: TemperatureRampsAreExclusiveWithATemperateBandBetween). Desert begins at
    // the ramp's midpoint (WarmWeight 0.5), where the dune profile is half blended in.
    public const float TemperatureWarmStart = 0.05f;   // at or below: no warmth
    public const float TemperatureWarmEnd = 0.35f;     // at or above: fully warm

    // Humidity: dry where the noise runs low. Dry land is desert (warm) or barren frozen (cold).
    public const float HumidityFrequency = 1f / 1700f;
    public const float HumidityDryStart = -0.35f;      // at or below: fully dry
    public const float HumidityDryEnd = -0.05f;        // at or above: fully wet

    // Temperate band between hot and cold biomes: taiga/frozen live on the cold ramp (at
    // ColdBiomeMinCold and colder), desert on the warm ramp (from its midpoint up), and the whole
    // raw-noise gap between those two lines always classifies as forest. Without it the warm/cold
    // isoline puts dunes directly against snow whenever the humidity stays dry across the
    // temperature line. Widen the band by raising both TemperatureWarm constants together (which
    // keeps the dune fade width), never by moving the cold ramp.
    public const float ColdBiomeMinCold = 0.70f; // below: too warm for taiga/frozen, forest instead

    // Ocean depth ramp: the shelf sits just over the waterline and the basin falls to the floor.
    public const float OceanShoreHeight = 3f;       // blocks, the shallowest shelf
    public const float OceanFloorHeight = -44f;     // blocks, the deepest basin centre
    public const float OceanDepthExponent = 1.35f;  // >1 keeps a wide shelf and a steep drop

    // Ocean floor materials by depth below sea level: sand near the beach, gravel down the
    // slope, bare stone in the deep basin.
    public const int OceanSandDepth = 4;
    public const int OceanGravelDepth = 16;

    // Trenches: rare narrow gashes in the ocean floor, cut like a ravine but only underwater.
    public const float TrenchFrequency = 1f / 420f;
    public const float TrenchWarpFrequency = 1f / 160f;
    public const float TrenchWarpAmplitude = 18f;
    public const float TrenchMaskFrequency = 1f / 900f;
    public const float TrenchMaskStart = 0.25f;
    public const float TrenchMaskEnd = 0.5f;
    public const float TrenchWidth = 0.05f;
    public const float TrenchDepth = 34f;

    // Desert dunes: smooth rolling sand hills over the base terrain, spaced well apart so the
    // dunes read as broad swells rather than choppy lumps.
    public const float DuneFrequency = 1f / 170f;
    public const float DuneAmplitude = 8f;
    // Sandstone strata: sand on top, this many sandstone blocks under it, then stone.
    public const int SandstoneDepth = 4;
    // Width of the grass/desert blend band where the surface material dithers, 0 disables it.
    public const float BiomeBorderDither = 0.16f;
    // Wavelength of the coherent border dither, in blocks. The material flip follows this noise,
    // so the grass/sand line is ragged in whole patches instead of a per-column checkerboard.
    public const float BiomeDitherFrequency = 1f / 28f;

    // Fluid simulation: one tick every FluidTickSeconds, at most MaxFluidTicksPerFrame of them
    // in a frame so a hitch never turns into a spiral of catch-up ticks
    public const float FluidTickSeconds = 0.25f;
    public const int MaxFluidTicksPerFrame = 4;

    // Cap on how many cells up the eye's column the underwater rays look for the water top
    // (ChunkManager.GetFluidColumnTopGlobal); a deeper column reads its surface at the cap
    public const int FluidSurfaceSearchCells = 64;

    // Cells one fluid tick processes per frame at most. A tick's cells are spread over the
    // frames until the next tick, so per-frame work stays flat while the simulation still keeps
    // up with a chunk-load wake storm (a per-tick cap alone leaves the storm queued, which
    // makes fluid appear frozen).
    public const int MaxFluidCellsPerFrame = 2048;

    // Ceiling on fluid cells queued for the next tick. Chunk wakes stop scheduling once it is
    // reached, so the queue can never grow past what the tick can work through.
    public const int MaxFluidBacklogCells = 8192;

    // Chunks whose worldgen water is woken per frame (FluidManager.WakeLoadedChunks). Each wake
    // schedules the chunk's fluid frontier and scans its six borders, so a streaming burst is
    // spread over frames; leftovers simply stay queued for the next one.
    public const int MaxWakeChunksPerFrame = 16;

    // How far a spreading fluid looks for a hole to fall into before spreading evenly
    public const int FluidSlopeSearchDistance = 4;

    // --- Biome tint (BiomeTint: grass and foliage colour from climate) ---
    // sRGB target colours of a flat, fully lit tinted face, one palette per tint kind. The four
    // corners sit on the biome lines of TerrainGenerator.Climate (ColdDry = frozen, ColdWet =
    // taiga, WarmDry = desert, WarmWet = warm forest); the temperate band is the midpoint of the
    // two rows, and at full wetness the grass midpoint (70, 132, 77) lands near grass_top's
    // measured mean, so near-spawn grass matches the inventory icon. Every channel must stay
    // under the ceiling BiomeTint.Gain reaches against the grey tiles (about sRGB 183), which
    // BiomeTintTests enforces.
    public static readonly Vector3 GrassColorColdDry = new Vector3(112f, 138f, 104f) / 255f;
    public static readonly Vector3 GrassColorColdWet = new Vector3(74f, 124f, 88f) / 255f;
    public static readonly Vector3 GrassColorWarmDry = new Vector3(142f, 146f, 70f) / 255f;
    public static readonly Vector3 GrassColorWarmWet = new Vector3(66f, 140f, 66f) / 255f;
    // Foliage (leaves, spruce leaves): the same climate axes, a little darker than the grass.
    public static readonly Vector3 FoliageColorColdDry = new Vector3(92f, 118f, 86f) / 255f;
    public static readonly Vector3 FoliageColorColdWet = new Vector3(58f, 106f, 70f) / 255f;
    public static readonly Vector3 FoliageColorWarmDry = new Vector3(120f, 126f, 58f) / 255f;
    public static readonly Vector3 FoliageColorWarmWet = new Vector3(52f, 120f, 52f) / 255f;
    // The sRGB grey every textureless tile averages to (the two supplied grass tiles measure 98.4
    // and 97.4, and tools/generate-leaf-textureless.ps1 normalises the leaf tiles to it), so the
    // one BiomeTint.Gain fits them all.
    public const float BiomeTintTextureGrey = 98f / 255f;

    // --- Mud ---
    // Dirt with water directly above it and at least this many water cells in its
    // 26-neighbourhood (faces, edges and corners alike) rots to mud the moment a
    // neighbouring fluid ticks and notices it.
    public const int MudWaterNeighbours = 2;

    // Blocks the far LOD water sheet sits below the chunk water surface, so the two never
    // z-fight where they meet at the full-detail ring. Depth resolution there is ~0.02 blocks;
    // the step is also visible edge-on as a thin line at the ring, so keep it as small as safe
    public const float LodWaterSink = 0.05f;

    // --- LOD sky exposure ---
    // LOD vertices bake a coarse skylight term from the column heights (LodSkyExposure), so a
    // far notch or the underside of a canopy bump is not lit like open ground. Open ground,
    // open walls and water keep 1, matching the full skylight near chunks give open sky.
    // How many cells a column must rise above a vertex to fully block that direction. The rise
    // is measured against a one-cell horizontal step, so it is a slope and holds at every level.
    public const float LodSkyRiseCells = 4f;
    // How much a fully blocked horizon (all sampled columns saturated) darkens a vertex
    public const float LodValleyShade = 0.45f;
    // How much the base of a canopy bump is darkened relative to its top face
    public const float LodCanopyShade = 0.55f;
    // Lowest sky exposure any LOD vertex can bake, so no far face goes pitch black
    public const float LodSkyExposureFloor = 0.35f;

    // Underwater view: the fog closes in to a few blocks and everything turns blue-green.    // The fade is deliberately short, so breaking the surface reads as a hard boundary
    // rather than a slow filter, but is still eased instead of snapped.
    public const float UnderwaterFadeSeconds = 0.15f;
    public const float UnderwaterFogNearBlocks = 1f;
    public const float UnderwaterFogFarBlocks = 48f;
    // Deep-water darkness (DeepWaterFog, test-fragment.glsl's uDeepWaterDim): the underwater fog
    // darkens and loses red the deeper the eye is below the surface. The dim approaches
    // DeepWaterFogMaxDim (never pitch black) over a depth scale; each channel loses at its own rate,
    // red first. MaxDim 0 switches it off.
    public const float DeepWaterFogMaxDim = 0.85f;
    public const float DeepWaterFogScaleBlocks = 16f;
    public static readonly Vector3 DeepWaterFogLoss = new(1.6f, 1.0f, 0.8f);

    // --- Lava ---
    // Deep cave floors pond over with lava sources where a patch mask and a per-cell roll
    // agree (see LavaGenerator). Pockets sit at or below LavaMaxY and buried under the
    // terrain, so the surface, ravines and entrances stay dry and sea caves stay water.
    public const bool LavaEnabled = true;
    public const int LavaMaxY = -24;
    public const int LavaBurial = 2;
    public const float LavaPatchFrequency = 1f / 128f;
    public const float LavaPatchThreshold = 0.2f;
    public const float LavaPoolChance = 0.3f;

    // --- Malachite ore (MalachiteGenerator) ---
    // Common coal-like blobs replacing stone at any depth: cells where the vein field
    // exceeds MalachiteThreshold become ore. Deposits average ~50 ores each and most
    // chunk columns hold several, so casual mining runs into ore without a dedicated
    // hunt. Frequency sets the blob diameter; the threshold sets deposit size and
    // spacing (measured in MalachiteTests, not guessed).
    public const bool MalachiteEnabled = true;
    public const float MalachiteFrequency = 1f / 18f;
    public const float MalachiteThreshold = 0.88f;

    // --- Coal ore (CoalGenerator) ---
    // Same threshold-on-noise deposits as malachite, but hostable in sandstone as well as stone,
    // so desert columns carry shallow coal where malachite only ever shows in rock. A slightly
    // longer wavelength packs each blob over more cells (the threshold alone fixes the ore
    // fraction), which is what makes coal veins a touch larger than malachite's without making
    // the world any richer in ore. Measured relationally in CoalTests, not guessed.
    public const bool CoalEnabled = true;
    public const float CoalFrequency = 1f / 20f;
    public const float CoalThreshold = 0.88f;

    // --- Forest terrain shape (smooth rolling hills with flat valleys between them) ---
    // The floor is a broad swell plus a positive-only hill field gated by a low-frequency
    // hill-region mask, so hill masses rise in patches and flat valleys run between them instead
    // of one continuous bumpy mass. The floor sits well above sea level, which is what lets a
    // lake or river be contained: an un-carved forest column is the dry rim, so carved water can
    // only sit in its own bowl or channel.
    public const float ForestBaseHeight = 12f;           // valley floor, blocks above sea level
    public const float ForestSwellFrequency = 1f / 1400f; // continent-scale undulation the hills ride on
    public const float ForestSwellAmplitude = 6f;        // blocks
    public const float HillFrequency = 1f / 260f;        // hill wavelength
    public const float HillAmplitude = 32f;              // blocks a fully-masked hill adds
    public const float HillMaskFrequency = 1f / 700f;    // where hill masses sit versus open flats
    public const float HillMaskStart = 0.28f;            // fully flat below this...
    public const float HillMaskEnd = 0.62f;              // ...fully hilly above this (soft blend between)
    public const float HillWarpFrequency = 1f / 520f;    // makes hill outlines wind instead of blobbing
    public const float HillWarpAmplitude = 40f;          // blocks of sideways displacement
    public const float ValleyRoughFrequency = 1f / 90f;  // small roughness on the valley floors
    public const float ValleyRoughAmplitude = 2.5f;      // blocks

    // --- Forest mountains (occasional, not common) ---
    // Rare tall ranges: a warped ridged field (peaks where |noise| is near zero) gated by a
    // low-frequency, high-threshold mask, so an occasional mountain rises out of the rolling
    // hills. The ridge is raised to MountainSharpness so only the crest climbs, leaving the
    // hills around it intact.
    public const bool MountainsEnabled = true;
    public const float MountainFrequency = 1f / 240f;     // ridge line spacing
    public const float MountainWarpFrequency = 1f / 420f;  // how tightly a range winds
    public const float MountainWarpAmplitude = 30f;        // blocks of sideways displacement
    public const float MountainMaskFrequency = 1f / 1600f; // where ranges sit versus plain hills
    public const float MountainMaskStart = 0.38f;          // no mountain below this...
    public const float MountainMaskEnd = 0.62f;            // ...a full range above it
    public const float MountainAmplitude = 60f;            // blocks a fully-masked crest adds
    public const float MountainSharpness = 3f;             // >1 sharpens the crest into a peak
    // Above this altitude a forest column turns to bare rock, so a high crest reads as a rocky
    // mountain instead of another tree-covered hill (trees also stop at TreeMaxGroundHeight).
    public const float MountainRockHeight = 56f;
    // Height at which a peak wears full snow whatever its climate (SnowGenerator.ColdWeightAt)
    public const int MountainSnowFullHeight = 96;
    // No crests this close to the origin (faded in by the second radius), so the spawn view
    // stays a forest valley rather than a random summit.
    public const float MountainSpawnClearStart = 48f;
    public const float MountainSpawnClearEnd = 120f;

    // --- Forest openness (woodland versus open grass) ---
    // A low-frequency mask that says how open the forest is at (x, z). Trees thin out to none
    // as it rises and the hill mass flattens a little, so the forest alternates between rolling
    // woodland and open grassy regions instead of one uniform canopy.
    public const float OpennessFrequency = 1f / 900f;
    public const float OpennessStart = 0.0f;               // below this: fully wooded...
    public const float OpennessEnd = 0.45f;                // ...above it: fully open
    public const float OpennessHillReduction = 0.5f;       // fraction of hill height an open region drops

    // --- Ravines (2D, cut into the heightmap so the far LOD shows them too) ---
    // The cut fades out across the shore band (OceanShoreWeight..0.5) and across every lake's and
    // river's footprint (TerrainGenerator.RavineCutFade), so a ravine never slices the edge of any
    // water body, sea or pond; the ocean's own gashes are trenches.
    public const bool RavinesEnabled = true;
    public const float RavineRidgeFrequency = 1f / 900f;   // spacing of candidate canyon lines
    public const float RavineWarpFrequency = 1f / 150f;    // how tightly the lines wind
    public const float RavineWarpAmplitude = 14f;          // blocks of sideways displacement
    public const float RavineMaskFrequency = 1f / 600f;    // rarity: ravines exist where mask noise is high
    public const float RavineMaskStart = 0.6f;
    public const float RavineMaskEnd = 0.74f;
    public const float RavineWidth = 0.035f;               // ridge noise band; ~7-9 blocks half-width
    public const float RavineDepth = 26f;                  // blocks, at full mask
    // Columns a ravine lowers by at least this many blocks are bare stone (no grass/dirt): the
    // soil layer thins from the rim inward so steep walls read as exposed rock, not as terraces
    public const int RavineBareStoneDepth = 6;

    // --- Cave surface rule ---
    // Caves fade out this close (in blocks) below the surface...
    public const float CaveSurfaceFadeStart = 2f;
    public const float CaveSurfaceFadeEnd = 8f;
    // ...except in entrance zones, sparse patches where the fade is lifted and caves reach the sky
    public const float EntranceFrequency = 1f / 128f;
    public const float EntranceStart = 0.5f;
    public const float EntranceEnd = 0.6f;
    // Entrances fade out over this width of ocean weight before the shore band, so a cave never
    // breaches a beach or the sea floor into a water-filled hole.
    public const float EntranceShoreFade = 0.1f;
    // Slow 2D variation added to the cave thresholds: riddled regions vs nearly solid ones
    public const float CaveIntensityFrequency = 1f / 512f;
    public const float CaveIntensityRange = 0.1f;

    // --- Noise caves (NoiseCaves) ---
    public const bool NoiseCavesEnabled = true;
    // Noise is sampled every this many blocks and interpolated; must divide Chunk.Size
    public const int CaveNoiseStride = 4;

    // Cheese: blobby rooms where a low-frequency field exceeds a threshold that loosens with depth
    public const float CheeseFrequency = 1f / 80f;
    public const float CheeseYScale = 1.5f;
    public const float CheeseThresholdShallow = 0.75f;
    public const float CheeseThresholdDeep = 0.6f;
    public const float CheeseDepthStart = 8f;
    public const float CheeseDepthEnd = 60f;

    // Spaghetti: tunnels along the curves where two independent fields are both near zero.
    // Radii are in noise units; the tunnel radius in blocks is roughly radius * wavelength / 30.
    public const float SpaghettiFrequency = 1f / 64f;
    public const float SpaghettiYScale = 1.6f;
    public const float SpaghettiRadius = 0.07f;            // ~2.5 blocks
    public const float SpaghettiRadiusVariation = 0.025f;
    public const float SpaghettiThicknessFrequency = 1f / 200f;

    // Narrow spaghetti: tighter, more frequent tunnels, still walkable, only in patches
    public const float NarrowSpaghettiFrequency = 1f / 32f;
    public const float NarrowSpaghettiRadius = 0.10f;       // ~2 blocks
    public const float NarrowSpaghettiMaskFrequency = 1f / 160f;
    public const float NarrowSpaghettiMaskStart = -0.1f;    // patches exist where mask noise exceeds this...
    public const float NarrowSpaghettiMaskEnd = 0.3f;       // ...and are fully developed above this

    // Caverns: rare, wide, flat open spaces that only exist deep down
    public const float CavernFrequency = 1f / 256f;
    public const float CavernYScale = 3f;
    public const float CavernThreshold = 0.62f;
    public const float CavernDepthStart = 40f;
    public const float CavernDepthEnd = 60f;

    // --- Worm carvers (WormCarver) ---
    public const bool WormCavesEnabled = true;
    // The world is split into square XZ cells; each cell deterministically spawns its worms
    public const int CarverCellShift = 6;                   // 64-block cells
    public const int CarverCellBlocks = 1 << CarverCellShift;
    public const float WormChanceOne = 0.35f;               // P(1 worm); P(2) below; P(0) is the rest
    public const float WormChanceTwo = 0.15f;
    public const int WormMinLength = 64;
    public const int WormMaxLength = 160;
    // A worm ends once this far (blocks) from its start, which bounds how many cells a chunk must check
    public const float WormExcursionCap = 112f;
    public const int WormReachCells = 2;
    public const float WormMinRadius = 2f;
    public const float WormMaxRadius = 3.5f;
    public const float WormRoomRadius = 5f;                 // the opening chamber at the start
    public const float WormVerticalSquash = 0.85f;          // tunnels are a bit wider than tall
    public const int WormStartDepthMin = 10;                // blocks below the surface
    public const int WormStartDepthMax = 60;
    public const float WormSurfaceMargin = 6f;              // stay this far under the surface outside entrances
    public const float WormBranchChance = 0.1f;             // per 20 nodes

    // WormCarver.GetCellWorms' cache, on the same GenerationalCache as the column cache above.
    // CAPACITY is unchanged from the wholesale-Clear() era and sits one to two orders of magnitude
    // ABOVE this cache's working set: cells are 64 blocks (CarverCellShift = 6), Carve asks for a
    // (2 * WormReachCells + 1)^2 = 25-cell window per chunk, and the whole chunk load sphere
    // (ChunkRadius = 16 chunks = 256 blocks) spans only on the order of 150 cells. The LOD builder
    // never touches this cache at all -- it reads heightmaps only. So the old Clear() essentially
    // never fired in play and this conversion buys UNIFORMITY plus removal of the all-locks
    // ConcurrentDictionary.Count from the miss path, not a hit rate; the measurable half of item D2
    // is Chunk.ColumnCache. GENERATIONS matches the column cache so the two sites read alike.
    public const int CellCacheCapacity = 4096;
    public const int CellCacheGenerations = 4;

    // --- Trees (TreeGenerator) ---
    // Species, placement rules and growth all live in assets/data/trees/*.json; these are the
    // engine-wide switches and cache knobs only. TreesEnabled covers every species (cacti too).
    public const bool TreesEnabled = true;
    // No trunk roots this close (Chebyshev) to the origin column, so the surface spawn the
    // camera picks there is always in the open; a species opts out with worldgen.spawnClear false
    public const int TreeSpawnClearRadius = 3;
    // Buttress fins (ButtressFeature): each cardinal side of a wide root (TreeRasterizer.BlockRadius
    // >= 1) grows a fin with this chance, reaching 1..TreeButtressMaxReach blocks past the trunk's
    // shell footprint along the ground. TreeRasterizer.ReachOf widens a buttress species' horizontal
    // reach by that maximum
    public const float TreeButtressFinChance = 0.75f;
    public const int TreeButtressMaxReach = 2;
    // Pixel-rim bevel (TreeWoodShape.RimBoxes): the quarter-round radius in pixels a rim:"pixel"
    // species carves into each exposed corner of a trunk shell. Valid 2..8: at least 2 so the
    // corner row is carved, at most half a cell so two rounds on one edge never meet
    public const int TreeRimBevelPx = 8;

    // TreeGenerator.ShapeCache and TreeGenerator.CanopyCache, both on the same GenerationalCache as
    // the column and worm-path caches above (item D6).
    //
    // The SHAPE cache holds one grown, rasterised tree per accepted trunk column, keyed by (x, z):
    // one entry is a whole tree of roughly a thousand TreeCells, so its capacity is far below the
    // old 16384 small-shape entries. It is genuinely shared: TreeGenerator.Stamp asks it on the
    // four generation workers, and BuildCanopyTops asks it for every leafy trunk in a
    // (2 * ReachHorizontal + 16)^2 footprint, which the far-LOD canopy sweep drives. Re-price it
    // only against the tree-perf.json numbers in TREES_PLAN.md.
    //
    // The CANOPY cache holds one CanopyColumns (the 16x16 tops and leaf blocks of a chunk column).
    // Its single engine caller is LodRegion.BuildMeshData, which asks TryGetCanopy for every FINE
    // column of a region -- that is what makes the 4096 cap bite at the outer rings.
    //
    // GENERATIONS is 4 at both sites, matching ColumnCacheGenerations and CellCacheGenerations so
    // all GenerationalCache sites read alike. G is the one-token re-pricing lever, and PERF_TODO's
    // lesson 4 allows moving it only against a real capture.
    public const int TreeShapeCacheCapacity = 4096;
    public const int TreeShapeCacheGenerations = 4;
    public const int TreeCanopyCacheCapacity = 4096;
    public const int TreeCanopyCacheGenerations = 4;

    // The tree batch cell: every tree rooted in one 64x64 XZ cell grows together (TreeBatch), the
    // same 64-block cell size as CarverCellShift's worm cells. TreeGenerator's static constructor
    // requires 2 * ReachHorizontal <= TreeCellBlocks, so a batch's neighbour obstacles always come
    // from the 8 adjacent cells.
    public const int TreeCellShift = 6;
    public const int TreeCellBlocks = 1 << TreeCellShift;

    // TreeGenerator.BatchCache, on the same GenerationalCache as the sites above. One entry holds a
    // whole 64x64 cell of grown trees, measured over the 3x3 cells around tree-perf.json's vantages:
    // forest ~21 trees x ~250 cells x 16 B per entry (~82 KB mean, max ~70 trees / ~250 KB), taiga
    // ~79 trees x ~230 cells x 16 B per entry (~288 KB mean, max ~350 KB). It is shared by Stamp on
    // the generation workers and the first LOD ring's canopy sweep on the LOD builder, whose working
    // set is the generation disc (~70 cells) plus the first LOD ring (~17x17 regions, each reading up
    // to 4 cells, ~320 cells). Lowered from 384 so even an all-taiga cache stays under ~100 MB
    // (336 x ~288 KB ~ 95 MB); residency floor 252 at G = 4.
    public const int TreeBatchCacheCapacity = 336;
    public const int TreeBatchCacheGenerations = 4;

    // TreeGenerator.ProxyCanopyCache: the proxy CanopyColumns of a chunk column that the far LOD
    // rings read instead of the grown canopy. Cheap to rebuild (no growth, only placement and the
    // species' lod crown), and sized like the real canopy cache because the same per-fine-column
    // sweep in LodRegion.BuildMeshData drives it. G = 4 like every site; re-price only against a
    // capture (PERF_TODO lesson 4).
    public const int TreeProxyCanopyCacheCapacity = 4096;
    public const int TreeProxyCanopyCacheGenerations = 4;

    // Felled-canopy markers kept before TreeGenerator.CanopyDisturbed is wiped wholesale (it only
    // bounds memory). Sized so a fell's square, (2 * FellPoisonRadius + 1)^2 ~ 1000 columns, wipes
    // the set no more often than the old 289-column square did against 16384.
    public const int CanopyDisturbedLimit = 65536;

    // --- Canopy wind (leaf-wave vertex shader) ---
    // Master strength of the leaf sway, 0 disables it; wavelengths and gusts live in
    // test-vertex.glsl next to the wave itself
    public const float LeafSwayStrength = 1f;

    // --- Falling leaves (LeafParticleSystem + LeafParticleRenderer) ---
    // Ambient leaf fall around the camera plus a burst whenever canopy is disturbed
    // (mined, felled, blasted). One batched billboard draw covers all of them.
    public const int LeafParticleMax = 512;
    public const float LeafParticleSpawnPerSecond = 30f;
    public const float LeafParticleSpawnRadius = 24f;
    public const int LeafParticleSpawnTries = 6;
    public const float LeafParticleLifetime = 9f;
    // Leaves flutter down at a near-constant sink instead of accelerating like drops
    public const float LeafParticleFallSpeed = 0.9f;
    public const float LeafParticleFallDrag = 2.5f;
    // A 6x6-texel crop of a 32px tile spans 6/32 of a block, rounded up a touch
    public const float LeafParticleSize = 0.2f;
    // Seconds a landed leaf takes to fade out before it is removed
    public const float LeafParticleFadeSeconds = 1.2f;
    // Texels of the centred square cropped out of the leaves tile for the sprite
    public const int LeafParticleCropPixels = 6;
    public const int LeafParticleBurstMined = 6;
    public const int LeafParticleBurstFell = 24;
    public const int LeafParticleBurstBlast = 16;
    // A leaf dissolving through LeafDecayManager: a light flutter, many fall in a row
    public const int LeafParticleBurstDecay = 2;

    // --- Mining debris (BlockDebris + BlockDebrisSystem) ---
    // Small block-textured cubes chipped off on every mining hit plus a bigger burst when
    // the block breaks. Visual only: they fall, bounce and fade, never collect.
    public const int BlockDebrisMax = 512;
    public const int BlockDebrisPerHit = 4;
    public const int BlockDebrisOnBreak = 20;
    public const float BlockDebrisLifetime = 2.0f;
    public const float BlockDebrisSize = 0.12f;
    // Seconds a cube takes to fade out at the end of its life (or after landing)
    public const float BlockDebrisFadeSeconds = 0.4f;

    // --- Landing dust (LandingDust rules, GroundDust spawner, drawn as BlockDebris chips) ---
    // A body landing from a real fall kicks up chips of the block it landed on: none below the
    // minimum fall, then more and faster ones the longer the drop (MathUtils.ImpactStrength),
    // reaching the caps at the full fall. Cosmetic: never on a headless server.
    public const float LandingDustMinFallBlocks = 3f;
    public const float LandingDustFullFallBlocks = 14f;
    public const int LandingDustMinCount = 4;
    public const int LandingDustMaxCount = 18;
    // Blocks per second the chips fly out at, mostly sideways so the burst spreads along the ground
    public const float LandingDustMinSpeed = 1.6f;
    public const float LandingDustMaxSpeed = 4.5f;
    // Edge length of a dust chip, smaller than a mining chip so it reads as grit
    public const float LandingDustSize = 0.08f;

    // --- Slime splat (LandingDust.Splat*, GroundDust): a landing slime throws chips of its own
    // green instead of the ground's dust. Every hop's landing splats, growing with the impact like
    // its squash kick: nothing under the minimum (a bob in water, the hair a resting body settles),
    // full at the kick's own clamp (Slime's 20 blocks/s). ---
    public const float SlimeSplatMinImpact = 3f;
    public const float SlimeSplatFullImpact = 20f;
    public const int SlimeSplatMinCount = 3;
    public const int SlimeSplatMaxCount = 10;
    // Blocks per second the goo flies out at
    public const float SlimeSplatMinLaunch = 1.2f;
    public const float SlimeSplatMaxLaunch = 3.2f;
    public const float SlimeSplatSize = 0.1f;
    // sRGB colour of the goo when the slime's model has no skin to read its green from
    public static readonly Vector3 SlimeSplatFallbackColor = new(0.45f, 0.78f, 0.35f);

    // --- Mob hit sparks (BlockDebrisSystem.Spark): a punch, slice or thrown hit that connects
    // throws a few pale chips off the mob's body where the striker faces it. Cosmetic:
    // singleplayer and a client (its predicted connect), never a server. ---
    public const int HitSparkCount = 6;
    // Blocks per second the sparks fly out at
    public const float HitSparkSpeed = 2.8f;
    public const float HitSparkSize = 0.07f;
    public static readonly Vector3 HitSparkColor = new(1f, 0.96f, 0.8f);

    // --- Mob death poof (GroundDust.DeathPoof): a mob whose death animation just ended bursts
    // into chips of its own skin colour (LandingDust.SkinColour of its model), so a goblin throws
    // goblin-coloured chips and a slime its green. Cosmetic: singleplayer and a client, never a server. ---
    public const int MobDeathPoofCount = 12;
    // Blocks per second the chips fly out at, between a landing splat and a mining burst
    public const float MobDeathPoofSpeed = 2.4f;
    public const float MobDeathPoofSize = 0.1f;
    // sRGB colour of the chips when the mob's model has no skin to read one from
    public static readonly Vector3 MobDeathPoofFallbackColor = new(0.62f, 0.5f, 0.42f);
    // A grey textureless block tile (its mean grey is BiomeTintTextureGrey) that a plain-colour chip
    // wears on every face, so BiomeTint.VertexTint lands the chip on its target colour
    public const string ColourChipTile = "grass_top_textureless";

    // --- Sprint footstep dust (FootstepDust rules, GroundDust): while sprinting on foot over loose
    // ground (a block with "footstepDust"), every footfall of the gait kicks up a tiny puff ---
    public const int FootstepDustCount = 5;
    // Blocks per second the grit flies out at, well under a landing's
    public const float FootstepDustSpeed = 1.3f;
    // On top of that a footfall kicks its grit up and back, against the run (blocks per second)
    public const float FootstepDustLift = 2.2f;
    public const float FootstepDustKickBack = 1.5f;
    public const float FootstepDustSize = 0.085f;
    // Horizontal blocks per second the body must really be covering: a sprint held against a wall
    // never puffs
    public const float FootstepDustMinSpeed = 4f;

    // --- TNT blast shake (ShakeFalloff): a blast shakes the local camera with this much trauma
    // inside TntManager.BlastRadius, falling off as radius / distance and gone at the range.
    // Cosmetic: singleplayer and a client, never a headless server. ---
    public const float BlastShakeStrength = 1f;
    public const float BlastShakeRangeBlocks = 48f;
    // A client sees a blast as a primed-TNT mirror leaving the server's snapshot with at most this
    // much of its locally burnt-down fuse left (the server only drops a cube when it detonates)
    public const float BlastMirrorFuseSlackSeconds = 0.5f;

    // --- TNT blast push (BlastImpulse): a blast shoves every loose thing within TntManager.BlastRadius
    // away from its centre, this many blocks per second at the centre, falling linearly to nothing at
    // the radius. The direction is tipped up by the bias so things are tossed, not skated. Gameplay:
    // singleplayer and the server (a player's share goes through its queued knockback). ---
    public const float BlastPushMaxSpeed = 14f;
    public const float BlastPushUpBias = 0.5f;

    // --- Falling block landing dust (LandingDust.FallingBlock*, GroundDust.LandBlock): a sand or gravel
    // cube that settles throws chips of itself out from under its rim, more and faster the longer it
    // fell, none under the minimum (a lip slide) and none under water. A client, which never sees the
    // fall, puffs as if the cube fell the client fall. Cosmetic: never on a headless server. ---
    public const float FallingBlockDustMinFallBlocks = 0.75f;
    public const float FallingBlockDustFullFallBlocks = 10f;
    public const int FallingBlockDustMinCount = 6;
    public const int FallingBlockDustMaxCount = 20;
    public const float FallingBlockDustMinSpeed = 1.6f;
    public const float FallingBlockDustMaxSpeed = 4.4f;
    public const float FallingBlockDustSize = 0.1f;
    public const float FallingBlockDustClientFallBlocks = 3f;

    // --- Steam on lava hardening (FluidManager.HardenedCells -> FireParticleSystem.Puff of
    // FireParticleKind.Steam on the campfire instance): a cell contact hardening turns solid puffs
    // this many pale steam particles off its top, scattered across the spread; at most the cap of
    // cells (within FireParticleRadius of the camera) puff in one frame, so a lake meeting the sea
    // never floods the particle ring. ---
    public const int SteamPuffSmoke = 8;
    public const float SteamPuffSpread = 0.9f;
    public const int SteamPuffMaxCellsPerFrame = 12;
    public const float SteamLifetime = 2.2f;
    public const float SteamRise = 1.1f;
    public const float SteamSize = 0.3f;
    public const float SteamGrow = 0.35f;
    public const float SteamAlpha = 0.6f;

    // --- Tree fall thud (TopplingTree.TryTakeLanding -> World.ThudTree): the moment a felled tree
    // swings flat it shakes the local camera (ShakeFalloff: this strength, scaled by how much of
    // TreeThudFullWoodCells of wood fell, full within the near radius, gone at the range), bursts
    // leaves off its landed canopy and kicks the ground's dust up under the trunk at a few points.
    // Cosmetic: singleplayer only (a client sees no toppling tree), never a server. ---
    public const float TreeThudShakeStrength = 0.6f;
    public const int TreeThudFullWoodCells = 12;
    public const float TreeThudShakeNearBlocks = 6f;
    public const float TreeThudShakeRangeBlocks = 36f;
    public const int LeafParticleBurstThud = 20;
    public const int TreeThudDustPoints = 8;
    public const int TreeThudDustCount = 5;
    public const float TreeThudDustSpeed = 2.6f;

    // --- Water splash (WaterSplash, WaterSplashTracker, BlockDebrisSystem.Splash): anything that
    // crosses a water surface throws pale droplets up from the water top, none under the minimum
    // speed, more and higher the faster it crossed (MathUtils.ImpactStrength), capped at the full
    // speed; the count scales with the body (1 for the player). A step farther than the teleport
    // distance splashes nothing, and a body still for the stale time counts as still. Cosmetic:
    // singleplayer and a client, never a headless server. ---
    public const float WaterSplashMinSpeed = 2.5f;
    public const float WaterSplashFullSpeed = 16f;
    public const int WaterSplashMinCount = 4;
    public const int WaterSplashMaxCount = 18;
    public const float WaterSplashMinLaunch = 1.8f;
    public const float WaterSplashMaxLaunch = 4.5f;
    public const float WaterSplashSize = 0.07f;
    public const float WaterSplashTeleportBlocks = 4f;
    public const float WaterSplashStaleSeconds = 0.3f;
    // How big a splash each kind of body throws, against the player's 1 (a mob's comes from its width)
    public const float WaterSplashSmallScale = 0.35f;
    public const float WaterSplashCubeScale = 1.4f;
    // sRGB colour of a droplet: a pale water-blue
    public static readonly Vector3 WaterSplashColor = new(0.72f, 0.86f, 1f);

    // --- Wind (Wind.cs, SH-27) ---
    // One slowly turning wind shared by leaf sway, fire smoke and the cloud drift.
    /// <summary>Wind heading at lighting time 0, degrees from +X toward +Z.</summary>
    public const float WindStartDegrees = 30f;
    /// <summary>How fast the wind heading turns: a full circle every 2400 s (two day cycles).</summary>
    public const float WindTurnDegreesPerSecond = 360f / 2400f;
    /// <summary>Blocks per second the leaf gust pattern rolls downwind (Wind.Offset -> uWindOffset).</summary>
    public const float WindGustSpeed = 3f;
    /// <summary>Downwind lean of swaying leaves at full gust, in blocks (uploaded as the length of uWind).</summary>
    public const float WindLean = 0.02f;
    /// <summary>Blocks per second a smoke puff drifts downwind once it has risen clear of the fire.</summary>
    public const float WindSmokeDrift = 0.35f;
    /// <summary>Blocks per second an ember drifts downwind.</summary>
    public const float WindEmberDrift = 0.15f;
    /// <summary>Blocks per second a falling leaf drifts downwind at full gust (scaled by Wind.Gust).</summary>
    public const float WindLeafDrift = 1.5f;
    /// <summary>How fast (1/s) a falling leaf's sideways motion settles into the wind's, so a burst's
    /// toss is carried off downwind within a second or so.</summary>
    public const float WindLeafDrag = 1.2f;

    // --- Campfire particles (FireParticleSystem + FireParticleRenderer) ---
    // Embers and smoke rising off lit firepits, spawned per lit cell near the camera.
    public const float FireEmberLifetime = 1.8f;
    public const float FireSmokeLifetime = 5.0f;
    public const float FireEmberRise = 0.8f;
    public const float FireSmokeRise = 0.5f;
    public const float FireEmberSize = 0.09f;
    public const float FireSmokeSize = 0.22f;
    public const float FireSmokeGrow = 0.12f;
    public const int FireParticleMax = 512;
    public const float FireEmberPerSecond = 36f;
    public const float FireSmokePerSecond = 8f;
    public const float FireParticleRadius = 48f;

    // --- Lava embers and smoke (a second FireParticleSystem instance in World) ---
    // Exposed lava cells ("hot fluid vents", recorded per chunk by the mesher) emit sparse
    // embers and smoke. Their own instance keeps them from competing with campfires.
    /// <summary>Embers per second from each lava vent in range; a pool has many vents, so this is far sparser than a campfire.</summary>
    public const float LavaEmberPerSecond = 0.6f;
    /// <summary>Smoke puffs per second from each lava vent in range.</summary>
    public const float LavaSmokePerSecond = 0.25f;
    /// <summary>Ring capacity of the lava particle instance; the oldest particle is evicted past it.</summary>
    public const int LavaParticleMax = 256;
    /// <summary>Only vents within this distance (blocks) of the eye emit.</summary>
    public const float LavaVentRadius = 32f;
    /// <summary>Cap on emitting vents per frame, filled nearest chunk first so the pool beside the player always wins.</summary>
    public const int LavaVentMaxEmitters = 96;

    // --- Torch flames (a third FireParticleSystem instance in World) ---
    // Every block with a JSON "flame" point (the torches; recorded per chunk by the mesher in
    // Chunk.FlamePoints) gives off a small flicker of embers and a thin wisp of smoke from exactly
    // that point. Cosmetic: never on a headless server.
    /// <summary>Embers per second off each torch in range.</summary>
    public const float TorchEmberPerSecond = 7f;
    /// <summary>Smoke puffs per second off each torch in range.</summary>
    public const float TorchSmokePerSecond = 1.2f;
    /// <summary>Ring capacity of the torch particle instance.</summary>
    public const int TorchParticleMax = 256;
    /// <summary>Only flames within this distance (blocks) of the eye burn.</summary>
    public const float TorchFlameRadius = 24f;
    /// <summary>Cap on burning flames per frame, filled nearest chunk first.</summary>
    public const int TorchFlameMaxEmitters = 48;
    /// <summary>Spawn scatter across a flame point, in blocks: a pixel or so, since the point is exact.</summary>
    public const float TorchFlameJitter = 0.06f;
    /// <summary>Particle scale of a torch flame against a campfire's 1 (size, lifetime, speed, sway).</summary>
    public const float TorchFlameScale = 0.45f;

    // --- Exhaled bubbles (BubbleParticleSystem + BubbleParticleRenderer) ---
    // Small billboards rising off the player's mouth while the eye is submerged, popping
    // at the water surface. One batched billboard draw covers all of them.
    public const int BubbleParticleMax = 128;
    public const float BubblePerSecond = 1f;
    public const float BubbleLifetime = 4f;
    public const float BubbleRise = 0.1f;
    public const float BubbleDrag = 3f;
    public const float BubbleSize = 0.09f;
    public const float BubbleGrow = 0.02f;
    // Seconds a bubble takes to fade in at spawn and out at the end of its life
    public const float BubbleFadeSeconds = 0.4f;
    // Each swim stroke with the eye underwater lets out one burst of this many bubbles
    // (BubbleParticleSystem.TrackSwimStrokes), centred this far in front of the head and scattered
    // up to this far around that point.
    public const int SwimStrokeBubbleCount = 4;
    public const float SwimStrokeBubbleForward = 0.4f;
    public const float SwimStrokeBubbleSpread = 0.15f;

    // --- Pebbles (PebbleGenerator) ---
    // Ankle-high stones in rare low-frequency patches; a density roll thins inside them
    public const bool PebblesEnabled = true;
    public const float PebblePatchFrequency = 1f / 96f;
    public const float PebblePatchThreshold = 0.25f;
    public const float PebbleDensity = 0.08f;
    // Edge length of one stone, in whole texels (footprint and height alike)
    public const int PebbleMinSizeTexels = 4;
    public const int PebbleMaxSizeTexels = 8;
    // Fraction of natural pebble stones that are obsidian instead of flint (~1% per rock)
    public const float PebbleObsidianChance = 0.01f;

    // --- Flowers (FlowerGenerator over PlantClumps) ---
    // Meadow cross-blocks in clumps on dry grass, only where the forest is open enough
    public const bool FlowersEnabled = true;
    public const int MeadowCellShift = 5;
    public const int MeadowRadius = 4;
    public const int MeadowJitter = 3;
    public const int MeadowMinMembers = 4;
    public const float MeadowClumpChance = 0.45f;
    public const float MeadowEdgeDensity = 0.25f;
    // ForestOpenness a clump's centre needs: the open stretches between the woodland
    public const float MeadowMinOpenness = 0.55f;

    // --- Foliage (FoliageGenerator) ---
    // Ground cover on the biome's own surface: forest grass tufts are common, desert dead shrubs
    // sparse. Each is gated by a low-frequency patch and then a position-hash density roll.
    public const bool FoliageEnabled = true;
    public const float GrassPatchFrequency = 1f / 48f;
    public const float GrassPatchThreshold = 0.05f;
    // The tuft density runs from the dry end to the wet end of the climate's wetness
    public const float GrassDensityDry = 0.15f;
    public const float GrassDensityWet = 0.5f;
    public const float ShrubPatchFrequency = 1f / 64f;
    public const float ShrubPatchThreshold = 0.2f;
    public const float ShrubDensity = 0.04f;

    // --- Cotton (CottonGenerator over PlantClumps) ---
    // Clumped, not scattered: at most one clump per 2^CottonCellShift-block cell, accepted with
    // CottonClumpChance and only on the warm, dry forest fringe before the desert line: a forest
    // centre whose desert weight (0.5 on the desert line itself) lies in
    // [CottonMinDesertWeight, CottonMaxDesertWeight), the top end being where the grass/sand border
    // dither starts, so a clump never lands on dithered sand. The centre sits up to CottonJitter
    // blocks off the cell's middle, so centres are at least 2^shift - 2 * jitter apart, and the clump
    // fills a CottonRadius disc: the CottonMinMembers cells nearest the centre always, the rest with
    // a density falling from 1 at the centre to CottonEdgeDensity at the rim. Clumps never overlap
    // while that spacing exceeds 2 * radius.
    public const bool CottonEnabled = true;
    public const int CottonCellShift = 6;
    public const int CottonJitter = 16;
    public const int CottonRadius = 3;
    public const int CottonMinMembers = 5;
    public const float CottonClumpChance = 0.6f;
    public const float CottonEdgeDensity = 0.1f;
    public const float CottonMinDesertWeight = 0.2f;
    public const float CottonMaxDesertWeight = 0.5f - BiomeBorderDither;

    // --- Tree felling (TopplingTree) ---
    // Seconds for a felled tree to swing from upright to flat (cubic ease-in: slow tip,
    // accelerating slam), then rest before its wood pops out as item drops
    public const float TreeToppleSeconds = 1.2f;
    public const float TreeFellRestSeconds = 2f;
    // Collection bounds: the wood cap covers a fell's nodes plus a wide trunk's shells and is
    // also the TreeSupport search cap (felling and the orphan crumble share it), then the
    // overall cap including the canopy. Sized to bound a mature wide trunk's nodes, shells and
    // fins and its canopy with room to spare. Anything past the caps stays standing (and
    // still drops normally when mined).
    public const int TreeFellWoodCap = 2048;
    public const int TreeFellTotalCap = 4096;

    // --- Reactive tree growth (TreeGrowthWriter) ---
    // Node cap of a grown tree (graph nodes only, never shells); it also bounds the
    // TreeGraph.FromWorld rebuild walk. Kept well under the fell's wood cap so a grown tree's
    // wood, shells included, still fells whole
    public const int TreeGrowNodeCap = 512;
    // Growth pulses one grow call may run at most
    public const int TreeGrowMaxPulsesPerCall = 256;

    // --- Leaf decay (LeafDecayManager) ---
    // Leaf decay simulation: one tick every LeafDecayTickSeconds, like the fluid tick, at most
    // MaxLeafDecayTicksPerFrame of them in a frame; leftover time is dropped so a hitch never
    // turns into a spiral of catch-up ticks
    public const float LeafDecayTickSeconds = 0.25f;
    public const int MaxLeafDecayTicksPerFrame = 2;
    // Leaf and tree-wood cells one decay tick processes per frame at most (the tick start drops
    // the air of a scheduled box with one read each). A tick's cells are walked in per-frame
    // slices until the next tick; a leaf scans its cluster's tip offsets best first, which stops
    // at the first live tip on an intact leaf but reads every offset on a doomed one, so this
    // stays well under the fluid budget.
    public const int MaxLeafDecayCellsPerFrame = 512;
    // Leaf decays plus orphaned-wood crumbles one tick may perform. The rest re-queue for the
    // next tick, so a ~300-leaf oak crown dissolves over ~8 ticks (about 2 s) instead of
    // vanishing in one frame.
    public const int LeafDecayMaxRemovalsPerTick = 40;
    // Ceiling on cells queued for the next decay tick, only a memory bound: ScheduleBox stops
    // adding once it is reached, and a stale leaf left behind waits for the next nearby edit.
    // It must hold a whole felled tree's box (the largest worldgen tree's reach on every side,
    // grown by the schedule reach, ~49k cells today) with room to spare, or a fell would
    // silently skip the far side of its crown.
    public const int MaxLeafDecayBacklogCells = 1 << 17;

    // --- Mob caps (MobCaps) ---
    // Two caps shared by every mob kind, judged against the living mobs of every manager together
    // (dying ones excluded), so a new kind needs no cap of its own. The ring spawners stop at the
    // natural cap; a forced spawn (a slime splitting, a scenario, a save restore, a client's
    // mirror) may pass it, but nothing ever spawns past the hard cap, which also sizes the
    // healthbar buffers (MobsMaxAlive).
    public const int MobNaturalCap = 12;
    public const int MobHardCapFactor = 15;
    public const int MobHardCap = MobNaturalCap * MobHardCapFactor;

    // --- Mob fall damage (MobFallDamage, Mob.NoteLanding) ---
    // A mob landing from more than its safe height takes this share of its own full health (at least
    // 1) per whole block past it, so the rule suits a small and a large pool alike. The safe height is
    // MobSafeFallBlocks; a slime adds its own hop's apex, so its hops never hurt it. Landing in water
    // is free, and only the authoritative simulation bills it.
    public const float MobSafeFallBlocks = 3f;
    public const float MobFallDamagePerBlockShare = 0.1f;

    // --- Mob lava (MobLava, Mob.UpdateHazards) ---
    // A mob whose body box touches lava burns: every tick spent in it costs this share of its own full
    // health (at least 1), with the hit flash. Leaving the lava clears the partial tick, as for the
    // player, so a brush never eats an instant hit.
    public const float MobLavaTickSeconds = 0.5f;
    public const float MobLavaDamageShare = 0.15f;

    // --- Mob loot toss (MobLoot) ---
    // A killed mob's every dropped item leaves its body with this toss on top of a drop's own small
    // pop: a random horizontal heading at MinSpeed..MaxSpeed blocks per second plus Lift upward, so
    // the loot arcs out instead of appearing at its feet.
    public const float MobLootTossMinSpeed = 1.5f;
    public const float MobLootTossMaxSpeed = 2.5f;
    public const float MobLootTossLift = 2.5f;

    // --- Mob notice range (MobTargets.NoticeRadius) ---
    // A mob only chases a target within this horizontal distance, scaled by MobSneakNoticeFactor
    // while the target sneaks, so a crouching player can creep past. Inside the spawn ring's outer
    // edge on purpose: a mob spawned at the far edge waits until the player comes closer.
    public const float MobNoticeRadiusBlocks = 32f;
    public const float MobSneakNoticeFactor = 0.5f;

    // --- Mob interest (MobInterest, Mob.PickTarget / WanderGoal) ---
    // A mob whose line of sight to its target (checked every MobSightCheckSeconds) stays blocked for
    // MobLoseInterestSeconds stops chasing until a check finds the line clear again. A mob chasing
    // nothing wanders: it walks or hops toward a goal MobWanderMin..MaxRadiusBlocks away, picked anew
    // every MobWanderRepickSeconds or on arrival, at MobWanderSpeedFactor of its walk speed (the
    // goblin; a slime keeps its hop). One idle for MobIdleDespawnSeconds and farther than
    // MobIdleDespawnMinDistanceBlocks from every target despawns (fading out, no loot).
    public const float MobLoseInterestSeconds = 8f;
    public const float MobSightCheckSeconds = 0.5f;
    public const float MobWanderMinRadiusBlocks = 3f;
    public const float MobWanderMaxRadiusBlocks = 8f;
    public const float MobWanderRepickSeconds = 6f;
    public const float MobWanderArriveBlocks = 1f;
    public const float MobWanderSpeedFactor = 0.5f;
    public const float MobIdleDespawnSeconds = 30f;
    public const float MobIdleDespawnMinDistanceBlocks = 24f;

    // --- Torch-safe ground (MobSpawner.IsLitByBlocks) ---
    // A ring spawn is refused where the cell a mob would stand in holds this much block light
    // (0..15) or more, so torches and lamps keep a base clear of new mobs.
    public const int MobSpawnBlockLightLimit = 8;

    // --- Ledge caution (LedgeProbe, Mob.IsLedgeAhead) ---
    // A mob will not walk (a goblin's next step, MobLedgeProbeBlocks past its body) or hop (a slime's
    // landing spot) onto ground more than MobLedgeMaxDropBlocks below its feet unless its target
    // stands lower than that; with MobSafeFallBlocks it pairs so a cautious mob never takes fall damage.
    public const float MobLedgeMaxDropBlocks = 3f;
    public const float MobLedgeProbeBlocks = 0.5f;

    // --- Slimes (SlimeManager) ---
    // Forest mobs that hop at the player. Natural spawns appear on dry forest surface in a ring
    // around the player and are culled when they stray too far. Damage only lands while a slime
    // is airborne; a grounded touch shoves it instead. Combat values live here so a future
    // player-attack pass reads one place.
    public const float SlimeSpawnIntervalSeconds = 8f;
    public const float SlimeSpawnMinRadiusBlocks = 20f;
    public const float SlimeSpawnMaxRadiusBlocks = 40f;
    public const float SlimeDespawnRadiusBlocks = 64f;
    public const int SlimeSpawnTries = 8;
    public const int SlimeContactDamage = 12;
    public const float SlimeShoveSpeed = 3.5f;
    // Horizontal shove a landing player punch gives a slime; ground friction then scrubs it off
    // so the hit reads as a knockback rather than a launch.
    public const float SlimePunchShoveSpeed = 5f;
    // Knockback a slime hit gives the player: a horizontal shove (decayed by Camera) plus a small
    // upward pop so the hit reads even on flat ground.
    public const float SlimeKnockbackSpeed = 6f;
    public const float SlimeKnockbackLift = 4.5f;
    // Slime sizes (SlimeSize), 1..SlimeMaxSize: size 1 is the plain slime, and every size above it
    // adds one step to each stat's multiplier, so a size s slime has (1 + (s - 1) * step) times the
    // size-1 value: the body scale (drawn model, collision box, healthbar height), the health pool
    // (over the entity JSON's hp), the contact damage and the loot table rolls. The largest size is
    // the rare giant, which splits into the size below it like any other.
    public const int SlimeMaxSize = 4;
    public const float SlimeSizeScaleStep = 0.5f;
    public const float SlimeSizeHealthStep = 1f;
    public const float SlimeSizeDamageStep = 0.5f;
    public const float SlimeSizeLootStep = 1f;
    // Size-scaled hops (SlimeSize.HopLift/HopReach/WindupScale): a larger slime's hop launch speeds
    // (the vertical lift and the horizontal reach) and the crouch before it are the size-1 ones times
    // the body scale to these powers, so a big slime hops higher and farther after a longer crouch.
    // The apex height grows with the square of the lift factor (v^2 / 2g).
    public const float SlimeHopLiftExponent = 0.4f;
    public const float SlimeHopReachExponent = 0.4f;
    public const float SlimeHopWindupExponent = 0.6f;
    // The size a ring spawn rolls (SlimeSize.Roll): each size past 1 is reached with the large-size
    // chance again (so size s+1 has chance^s), and that chance eases from the day value to the night
    // value with DayCycle.NightFactor. A spawn whose ground lies at least SlimeUndergroundDepthBlocks
    // below its column's surface counts as full night, whatever the hour.
    public const float SlimeLargeChanceDay = 0.1f;
    public const float SlimeLargeChanceNight = 0.4f;
    // The last step, into the largest size (the giant), is taken with this share of the usual chance,
    // so a giant is rarer than the geometric falloff alone would make it.
    public const float SlimeMaxSizeChanceFactor = 0.25f;
    public const int SlimeUndergroundDepthBlocks = 4;
    // A slime above the smallest size splits when a hit takes it from above half its health to at
    // or below half while it survives (SlimeSize.SplitsOnHit): this many children of the next
    // smaller size pop out of its body, shoved outward with a small hop. They are forced spawns, so a
    // split near the hard mob cap makes only as many as still fit, and none at the cap.
    public const int SlimeSplitChildren = 2;
    public const float SlimeSplitShoveSpeed = 4f;
    public const float SlimeSplitLift = 5f;
    // Cave slimes (MobSpawner.TryFindCaveSpawn): slimes also spawn on dark cave floors in the same
    // ring, searched SlimeCaveSpawnTries times from a random height within SlimeCaveSearchHalfHeight of
    // the player's feet, down at most SlimeCaveScanDepth cells, on a floor at least
    // SlimeUndergroundDepthBlocks under its column's surface whose standing cell has at most
    // SlimeCaveMaxSkyLight sky light. A player underground is searched for cave spawns first, one on
    // the surface for surface spawns first, each falling back to the other.
    public const int SlimeCaveSpawnTries = 16;
    public const int SlimeCaveSearchHalfHeight = 12;
    public const int SlimeCaveScanDepth = 24;
    public const int SlimeCaveMaxSkyLight = 4;
    // Slime merge (SlimeMerge): two living slimes of the same size below the largest whose bodies
    // touch merge into one of the next size (the split's inverse), its health the pair's share of
    // their pools scaled to the larger one, once both have lived SlimeMergeCooldownSeconds, so a
    // split's children do not merge straight back.
    public const float SlimeMergeCooldownSeconds = 10f;

    // --- Goblins (GoblinManager) ---
    // Short forest raiders that throw the flint they carry at the player from a standoff band,
    // then close in for a club swing once their stones are gone. They spawn like slimes (dry
    // forest surface, a ring around the player) and are culled when they stray too far. The
    // throw band must stay inside the stone's level-ground range, v^2/g at GoblinStoneSpeed
    // under ThrownProjectile.Gravity, with margin for a chest-height target and the
    // semi-implicit Euler drop, or the far edge of the band has no solvable arc.
    public const float GoblinSpawnIntervalSeconds = 14f;
    public const float GoblinSpawnMinRadiusBlocks = 20f;
    public const float GoblinSpawnMaxRadiusBlocks = 40f;
    public const int GoblinSpawnTries = 8;
    public const float GoblinDespawnRadiusBlocks = 64f;
    public const float GoblinWalkSpeed = 3.2f;
    public const float GoblinJumpSpeed = 8.5f;
    // Stones (flint) a goblin carries, rolled inclusive at spawn; the rest drop on death.
    public const int GoblinStoneMin = 3;
    public const int GoblinStoneMax = 6;
    // Horizontal distance band a goblin throws from: it closes in beyond the far edge and
    // backpedals inside the near one.
    public const float GoblinThrowMinRange = 4f;
    public const float GoblinThrowMaxRange = 12f;
    // A fresh goblin notices the player before its first throw; unjittered so scripted shots
    // repeat.
    public const float GoblinFirstThrowDelaySeconds = 1f;
    public const float GoblinThrowCooldownSeconds = 2.2f;
    public const float GoblinThrowCooldownJitter = 0.6f;
    // How long a goblin whose throw line was blocked waits before testing it again.
    public const float GoblinLosRetrySeconds = 0.3f;
    // 20^2 / 28 = 14.3 blocks of level range, so the 12-block band edge solves with ~2 blocks
    // to spare.
    public const float GoblinStoneSpeed = 20f;
    public const int GoblinStoneDamage = 8;
    public const float GoblinStoneKnockbackSpeed = 3f;
    public const float GoblinMeleeReach = 1.9f;
    // How close an empty-handed goblin walks before it stops and swings.
    public const float GoblinStandoffBlocks = 1.1f;
    public const int GoblinMeleeDamage = 10;
    public const float GoblinMeleeCooldownSeconds = 1.1f;
    // Knockback a club swing gives the player: a horizontal shove plus a small upward pop.
    public const float GoblinKnockbackSpeed = 5f;
    public const float GoblinKnockbackLift = 3f;
    // Horizontal shove a grounded goblin gets when the player walks into it.
    public const float GoblinShoveSpeed = 3.5f;

    // --- Entity healthbars (EntityHealthbarRenderer) ---
    // Five hearts float above every live mob, billboarded to face the camera, using the same
    // heart sprites as the player HUD. The pool is drawn proportionally across the row (each
    // heart is MaxHealth / Hearts HP), so any entity kind reads correctly whatever its pool is.
    // The buffers are sized for the most mobs that can ever be alive together: the hard cap.
    public const int MobsMaxAlive = MobHardCap;
    public const int EntityHealthbarHearts = 5;
    // Width (and height) of one heart in blocks; neighbours sit edge to edge like the HUD row.
    public const float EntityHeartSizeBlocks = 0.2f;
    // How far above the mob's head (Position + Mob.HeadAboveCentre) the row's centre floats.
    public const float EntityHealthbarLiftBlocks = 0.22f;

    // --- Floating damage numbers (DamageNumberPool, DamageNumberRenderer) ---
    // A hit shows its damage as a small number above the mob's healthbar that rises, drifts a
    // little sideways and fades out: fully opaque for the hold, faded over the rest of its life. It
    // pops in at PopScale and springs back. The pool is a fixed array; a full one replaces its
    // oldest number.
    public const int DamageNumberCapacity = 32;
    public const float DamageNumberLifetimeSeconds = 0.9f;
    public const float DamageNumberHoldSeconds = 0.45f;
    public const float DamageNumberRiseBlocks = 0.8f;
    public const float DamageNumberDriftBlocks = 0.25f;
    public const float DamageNumberPopSeconds = 0.18f;
    public const float DamageNumberPopScale = 1.5f;
    // Start height above the healthbar row's centre, and the digit cap height, in blocks.
    public const float DamageNumberLiftBlocks = 0.18f;
    public const float DamageNumberCapHeightBlocks = 0.2f;
    // A client sees a killing blow only as the mob leaving the server's snapshot. Within this range
    // of the camera (well inside every despawn radius, so it is never a despawn) that is a death,
    // shown as the health the mob had left.
    public const float DamageNumberDeathRangeBlocks = 32f;

    // --- Charged throws (ThrownProjectile) ---
    // Right-click in the air charges a throwable, release throws it. Damage scales from half
    // at a tap to full at a full charge, and the launch speed scales the same way.
    public const float ThrowChargeSeconds = 0.9f;
    public const float ThrowMinCharge = 0.5f;
    public const float ThrowSpeed = 40f;
    public const float ThrowLifetimeSeconds = 5f;

    // --- HUD feedback (HotbarNameFade, HotbarPickupPop) ---
    // Selecting a hotbar slot (or the selected slot changing item) shows the item's name above
    // the hotbar: fully opaque for the hold, then faded out, about 1.5 s in all.
    public const float HotbarNameHoldSeconds = 1.0f;
    public const float HotbarNameFadeSeconds = 0.5f;
    // A slot whose count rises (a pickup, a craft result, a server snapshot) bumps its icon to
    // this scale and springs back over the duration. The hotbar's icon fill leaves room for it.
    public const float HotbarPopSeconds = 0.3f;
    public const float HotbarPopScale = 1.3f;

    // --- Tool break shatter (ToolShatter): a tool whose durability runs out bursts into chips of
    // its sprite's mean colour at the hand, and its hotbar slot flashes. Cosmetic: never a server. ---
    public const int ToolShatterCount = 10;
    // Blocks per second the chips fly out at
    public const float ToolShatterSpeed = 3f;
    public const float ToolShatterSize = 0.08f;
    // sRGB colour of the chips when the item's sprite cannot be read
    public static readonly Vector3 ToolShatterFallbackColor = new(0.75f, 0.7f, 0.6f);
    // How long a broken tool's hotbar slot flashes, and the strength of its white overlay at the start
    public const float HotbarFlashSeconds = 0.35f;
    public const float HotbarFlashAlpha = 0.6f;

    // --- Slime block (the block "bounce" flag): a body that lands on one is thrown back up at
    // this share of its impact and bills no fall damage; below the minimum it just rests ---
    public const float SlimeBlockBounceRestitution = 0.8f;
    public const float SlimeBlockBounceMinSpeed = 5f;

    // --- Bandage (ItemUseAction.Heal): HP one restores, spent from the stack ---
    public const int BandageHealAmount = 20;

    // --- Ash fertilizer (ItemUseAction.GrowTree): growth pulses one use of ash adds to the
    // aimed tree, spent from the stack ---
    public const int AshGrowthPulses = 3;

    // --- Saplings (SaplingManager): a planted sapling grows one pulse at a time until its
    // species' maturity step, one pulse per SaplingPulseSeconds jittered by SaplingPulseJitter ---
    public const float SaplingPulseSeconds = 3f;
    public const float SaplingPulseJitter = 0.5f;

    // --- Goblin flee (Goblin.IsFleeing): below this share of its pool a goblin gives up and runs
    // from the player at this much of its walk speed ---
    public const float GoblinFleeHealthShare = 0.25f;
    public const float GoblinFleeSpeedFactor = 1.4f;

    // --- Goblin dodge (GoblinManager.IsAimedAt): a charged throw aimed at a goblin (at least this
    // charge, within the cone and range) makes it sidestep out of the line while the aim holds ---
    public const float GoblinDodgeChargeThreshold = 0.5f;
    public const float GoblinDodgeConeDegrees = 12f;
    public const float GoblinDodgeRangeBlocks = 20f;

    // --- Goblin pack alert (GoblinManager.AlertPackAround): a goblin that notices a player pulls
    // every other goblin within this radius forward, skipping their first-throw delay ---
    public const float GoblinPackAlertRadiusBlocks = 12f;

    // --- Goblin pack spawn (GoblinManager.UpdateSpawning): a ring spawn rolls up to this many
    // goblins, the rest of the pack landing within this radius of the found cell ---
    public const int GoblinPackMaxSize = 3;
    public const float GoblinPackSpawnRadiusBlocks = 3f;
    public const int GoblinPackSpawnTries = 8;

    // --- Goblin torches (GoblinTorches): a spawn may carry torches, held in the off hand while it
    // is dark (night or underground), lit like a small campfire and dropped on death ---
    public const float GoblinTorchChanceDay = 0.03f;
    public const float GoblinTorchChanceNight = 0.4f;
    public const float GoblinTorchChanceUnderground = 0.7f;
    public const int GoblinTorchMaxCount = 2;
    // Night factor from which a carried torch is out
    public const float GoblinTorchNightFactor = 0.5f;

    // --- Fallen logs (FallenLogGenerator): a rare detached oak log on the forest floor ---
    public const bool FallenLogsEnabled = true;
    // One candidate log per 2^FallenLogCellShift-block cell, accepted with FallenLogChance
    public const int FallenLogCellShift = 5;
    public const float FallenLogChance = 0.3f;
    public const int FallenLogMinLength = 3;
    public const int FallenLogMaxLength = 5;

    // --- Boulders (BoulderGenerator): a rare blob of stone or cobblestone on the forest and
    // taiga floor ---
    public const bool BouldersEnabled = true;
    // One candidate boulder per 2^BoulderCellShift-block cell, accepted with BoulderChance
    public const int BoulderCellShift = 5;
    public const float BoulderChance = 0.2f;
    public const int BoulderMinBlocks = 2;
    public const int BoulderMaxBlocks = 4;

    // --- Reeds (ReedGenerator): clumps of reeds on the columns at a water's edge ---
    public const bool ReedsEnabled = true;
    public const int ReedCellShift = 5;
    public const float ReedClumpChance = 0.6f;
    public const int ReedRadius = 2;
    public const int ReedJitter = 2;
    public const int ReedMinMembers = 2;
    public const float ReedEdgeDensity = 0.35f;
    // The edge band: ground this far under the water level (or level with it) takes reeds
    public const int ReedShallowDepth = 1;

    // --- Lily pads (LilyPadGenerator): a pad on a still lake's surface ---
    public const bool LilyPadsEnabled = true;
    public const float LilyPadChance = 0.35f;

    // --- Glow mushrooms (GlowMushroomGenerator): a dim cyan light on deep cave floors ---
    public const bool GlowMushroomsEnabled = true;
    public const float GlowMushroomChance = 0.04f;
    // Blocks under the column's height a mushroom needs (deep caves, never the surface)
    public const int GlowMushroomMinDepth = 12;

    // --- Cave formations (CaveFormationGenerator): stalactites on ceilings, stalagmites on
    // floors, and a stalagmite under every stalactite's drip point ---
    public const bool CaveFormationsEnabled = true;
    public const float StalactiteChance = 0.03f;
    public const float StalagmiteChance = 0.03f;
    public const int CaveFormationMinDepth = 8;

    // --- Gravel and sand pockets (GravelPocketGenerator): loose blobs buried inside stone that
    // cave in when mined ---
    public const bool GravelPocketsEnabled = true;
    public const float GravelPocketFrequency = 1f / 24f;
    public const float GravelPocketThreshold = 0.3f;
    // Blocks under the column's surface a pocket needs (soil and rock caps stay solid)
    public const int GravelPocketMinBurial = 3;
    // Share of a pocket's cells that are sand rather than gravel
    public const float GravelPocketSandShare = 0.35f;

    // --- Cairns (CairnGenerator): a cobblestone stack landmark on a mountain summit ---
    public const bool CairnsEnabled = true;
    public const int CairnCellShift = 6;
    public const float CairnChance = 0.25f;

    // --- Underground pools (UndergroundPoolGenerator): water on cave floors above the lava band
    // and below the sea ---
    public const bool UndergroundPoolsEnabled = true;
    public const float PoolPatchFrequency = 1f / 96f;
    public const float PoolPatchThreshold = 0.35f;
    // Blocks under the column's surface a pool needs (entrances and ravine floors stay dry)
    public const int PoolBurial = 8;
    public const float PoolChance = 0.5f;

    // --- Goblin notice pip (NoticePipRenderer): a "!" pops over a goblin's head the moment it
    // first notices a player (the same moment its first-throw delay starts) ---
    public const float GoblinNoticePipSeconds = 0.9f;
    // Blocks the pip floats above the head, and its cap height
    public const float GoblinNoticePipRiseBlocks = 0.55f;
    public const float GoblinNoticePipCapHeightBlocks = 0.45f;
    // Pips drawn per frame; past it the rest wait for the next frame
    public const int GoblinNoticePipMax = 16;

    // --- Pickup sparkle (FireParticleKind.Spark): a few bright motes pop where a drop is collected,
    // at the collector's chest. Cosmetic: singleplayer only (a client's pickups are server-side). ---
    public const int PickupSparkleCount = 5;
    public const float PickupSparkleSpread = 0.5f;
    // Blocks below the eye the motes start at (the chest, where a collected drop arrives)
    public const float PickupSparkleDrop = 0.35f;
    public const float SparkLifetime = 0.45f;
    // Blocks per second a spark rises at, and its size
    public const float SparkRise = 1.6f;
    public const float SparkSize = 0.12f;

    // --- Low-health vignette (LowHealthVignette, vignette-fragment.glsl) ---
    // Below the threshold (a fraction of the pool) the screen edge pulses red like a slow
    // heartbeat, its strength rising from the min to the max as health falls to nothing. Between
    // beats it eases down to the floor share of that strength rather than vanishing. Off while
    // dead and in creative.
    public const float LowHealthThreshold = 0.3f;
    public const float LowHealthPulseSeconds = 1.25f;
    public const float LowHealthMinStrength = 0.35f;
    public const float LowHealthMaxStrength = 0.85f;
    public const float LowHealthPulseFloor = 0.35f;
    // Damage direction arc (DamageDirection, vignette-fragment.glsl): a red arc on the screen edge
    // toward where the last mob blow came from, full for the hold and then faded out smoothly.
    public const float DamageArcHoldSeconds = 0.4f;
    public const float DamageArcFadeSeconds = 1.1f;
    // Surfacing droplets (SurfacingDroplets, vignette-fragment.glsl): when the eye comes up out of
    // water, drops streak down the screen, full for the hold and gone this long after surfacing.
    public const float SurfacingDropletHoldSeconds = 0.25f;
    public const float SurfacingDropletSeconds = 1.5f;

    // --- Multiplayer networking (NetProtocol, GameServer, NetworkClient) ---
    // The server broadcasts the player list on this cadence; clients send their own state at the
    // state cadence. The day clock syncs once a second, which is far below a visible sun step.
    public const double NetPlayerListSeconds = 0.1;
    public const double NetPlayerStateSeconds = 0.05;
    public const double NetTimeSyncSeconds = 1.0;
    // How often the server broadcasts the live mobs and drops.
    public const double NetEntitySyncSeconds = 0.1;
    // The largest speed the server accepts from a client (blocks/s) before clamping a state toward
    // it. Sprint flight moves PlayerPhysics.BaseSpeed * FlyBoostMultiplier = 8 * 10 = 80 b/s
    // horizontally and the same 80 b/s vertically (Space/Ctrl reuse the move speed), so the
    // flight diagonal is 80 * sqrt(2) ~ 113 b/s; 130 leaves slack for frame jitter.
    public const float NetMaxMoveBlocksPerSecond = 130f;
    // How far below a connected player's feet the server probes for ground (PlayerBodyProbe): a
    // body whose box lowered by this much collides is standing. It must exceed any clearance the
    // client's physics leaves under a landed body: PlayerPhysics moves in CollisionStepSize (0.05)
    // steps and stops before the first colliding one, so a landing rests less than 0.05 above the
    // block top (StepClearance adds only 0.001 to a step-up), and gravity then settles it closer.
    public const float NetGroundProbeBlocks = 0.15f;
    // The movement rubberband (MovementRules), on top of the speed clamp above. The fastest a
    // state may climb without the flight flag, in blocks/s: a jump leaves the ground at
    // PlayerPhysics.JumpSpeed (11), and swimming up, the swim exit boost and a mob's knockback
    // lift are all slower, so only flight or a cheat climbs faster.
    public const float NetMaxRiseBlocksPerSecond = 16f;
    // Climb allowed in one state on top of that rate: a sprint auto-step of a whole block
    // (PlayerPhysics' SprintStepHeight) can land inside a single 50 ms state, plus jitter.
    public const float NetRiseSlackBlocks = 1.5f;
    // How long a player without the flight flag may stay airborne without sinking before the
    // server rubberbands it: longer than a full jump's climb and apex (about a third of a second
    // rising), so only a hover trips it.
    public const double NetHoverGraceSeconds = 1.0;
    // A state arriving more than this long after the previous one (a lag spike) is judged only by
    // the speed clamp and the collision test: the rise and hover rules need a short, honest gap.
    public const double NetMaxJudgedStateGapSeconds = 0.5;
    // How far from the player's eye a client may edit a block: the client's own raycast reach,
    // one block of cell-centre slack, and one state interval of movement, since the eye the server
    // judges from can lag the eye that aimed the edit by up to one PlayerState.
    public const float NetMaxEditReachBlocks =
        World.InteractionReach + 1f + (float)(NetMaxMoveBlocksPerSecond * NetPlayerStateSeconds);
    // How far the eye a world intent claims may sit from the player's last accepted position: the
    // eye that aimed the action can run ahead of the server's copy by one state interval of
    // movement, plus the same slack a clamped move gets (NetValidation.AllowedDistance).
    public const float NetMaxIntentEyeDriftBlocks =
        (float)(NetMaxMoveBlocksPerSecond * NetPlayerStateSeconds) + 2f;
    // Edits (break completions, placements, right-click uses, throws) one player may make per
    // second, and how many may come in a burst above that before the server refuses them.
    public const float NetEditIntentsPerSecond = 20f;
    public const float NetEditIntentBurst = 20f;
    // Seconds by which a break completion may beat the server's own count of its hits: the start
    // can reach the server later than the completion, relative to when the client sent them.
    public const double NetBreakSlackSeconds = 0.1;
    // The same forgiveness for the pace between two attacks.
    public const double NetAttackSlackSeconds = 0.1;
    // A dead client sends a Respawn intent as soon as the server's status says it died, then again
    // this often while the status still reads dead (an earlier one refused, or lost to a race).
    public const double NetRespawnRetrySeconds = 1.0;
    // A creative client mines one block per this many seconds while the button is held, rather than
    // one per frame, so a held click stays inside the server's edit rate (singleplayer is unpaced).
    public const float ClientCreativeBreakSeconds = 0.1f;
    // How long a blocking send to one client may stall the server's main thread before the
    // connection is treated as dead (NetworkStream.WriteTimeout), in milliseconds.
    public const int NetSendTimeoutMs = 5000;
    // A connection (said hello or not) that sent nothing for this long is dropped. Clients send a
    // KeepAlive every NetKeepAliveSeconds and their PlayerState at 20 Hz, so this only fires for a
    // dead or stalled peer.
    public const double NetIdleTimeoutSeconds = 30.0;
    // Far-LOD regions the server builds per Pump. A joining client asks for hundreds; building
    // them all inside one message handler stalled the tick for seconds.
    public const int NetLodBuildsPerPump = 4;
    // Far-LOD region requests a client keeps outstanding at once, and how long before an
    // unanswered one is asked again (TCP is reliable: the resend only covers a server that
    // dropped the request, e.g. because its asker disconnected meanwhile).
    public const int NetLodInFlightMax = 64;
    public const double NetLodResendSeconds = 15.0;
    // How long a shutdown signal (SIGTERM, SIGHUP, process exit) waits for the server's main loop
    // to save the world before the handler gives up and lets the process end.
    public const double ServerShutdownWaitSeconds = 30.0;
    // Whether a dedicated server lets players be creative by default (GameServer.AllowCreative;
    // the server's --allow-creative argument turns it on). Off: every player stays survival and
    // creative grants are refused whatever the client's C toggle says.
    public const bool ServerAllowCreative = false;
    // How long a connect attempt may hang before the client gives up and returns to the menu.
    public const double NetConnectTimeoutSeconds = 10.0;
    // Sent roughly this often so an idle connection is not mistaken for a dead one.
    public const double NetKeepAliveSeconds = 5.0;
    // Largest squash/distance a remote player model is drawn from before it is culled.
    public const float NetRemotePlayerReachBlocks = 4000f;
}
