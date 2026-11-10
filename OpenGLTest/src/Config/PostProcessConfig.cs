namespace VoxelEngine.Config;

/// <summary>
/// Post and screen-space tuning: the biome grade, damage fringe, underwater wobble, FXAA, contact
/// AO, godrays and the per-pass GPU timer gate.
/// </summary>
public static class PostProcessConfig
{
    // --- Biome color grade (BiomeGrade, post-fragment.glsl's uGrade) ---
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
    /// neighborhoods shorten the search span a little, which keeps high-contrast texture detail
    /// (ore specks, leaf cutouts) from being blurred along a false edge.</summary>
    public const float FxaaReduceMul = 1f / 8f;

    /// <summary>FXAA's longest blur span in pixels along the detected edge (uFxaaSpanMax); the
    /// classic 8 smooths long near-horizontal block silhouettes without reaching across
    /// unrelated geometry.</summary>
    public const float FxaaSpanMax = 8f;

    /// <summary>FXAA's relative contrast gate (uFxaaEdgeThreshold, FXAA 3.11's "quality"
    /// value): a pixel whose 3x3 luma range is below this fraction of its brightest neighbor is
    /// left as it is. Skipping those flat pixels is most of the pass's cost saving.</summary>
    public const float FxaaEdgeThreshold = 0.125f;

    /// <summary>FXAA's absolute contrast floor (uFxaaEdgeThresholdMin, FXAA 3.11's default):
    /// dark neighborhoods need at least this luma range before they are smoothed, so noise in
    /// caves and at night is not smeared.</summary>
    public const float FxaaEdgeThresholdMin = 0.0312f;

    // --- Contact AO (ContactAo, contact-ao-fragment.glsl, SH-28) ---

    /// <summary>Master switch for the screen-space contact AO pass run mid entity pass
    /// (ScreenPasses.RenderContactAo). Off, the frame is exactly what it was without it.</summary>
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

    // --- Opaque chunk occlusion cull (ChunkOcclusionCuller, HizOcclusion, PERF_TODO F9) ---

    /// <summary>Master switch for the Hi-Z occlusion cull between the segments of the opaque chunk
    /// pass. Off, the pass is the single front-to-back multi-draw it was; on, the frame is
    /// pixel-identical (see <see cref="Rendering.HizOcclusion"/>).</summary>
    public const bool ChunkOcclusionCullEnabled = true;

    /// <summary>Where the front-to-back opaque run is split, as shares of its chunk count, ascending:
    /// after each split the pyramid is rebuilt from the depth drawn so far and every later chunk is
    /// tested against it (<see cref="Rendering.HizOcclusion.SegmentStarts"/>). Each split costs one
    /// pyramid build and one cull dispatch. Measured on perf-fly (PERF_TODO F9): one to five splits all
    /// landed within run-to-run noise of each other; two keep a near split for caves at little cost.</summary>
    public static readonly float[] ChunkOcclusionSplits = [1f / 8f, 1f / 2f];

    // --- GPU timer queries (GpuPassTimer.BeginGpuFrame / BeginGpuPass, item R6) ---
    // One frame in this many still issues its TIME_ELAPSED queries while the X overlay is down,
    // so the once-a-second snapshot standing when X is pressed already carries GPU figures
    // instead of "--". Chosen, not measured: ~12 samples per publish window at 100 fps for an
    // eighth of the always-on GL query traffic. Moving it owes a counted capture, not a guess.
    public const int GpuTimingIdleInterval = 8;

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

    // Sun dazzle (SunDazzle, SunDazzleRenderer, dazzle-fragment.glsl): the blinding bloom of looking
    // straight at the sun, added over the whole frame (the foreground included) after the godrays.
    // Radii and reaches are shares of the screen height.
    /// <summary>Master switch for the dazzle pass; off, it costs nothing and the frame is exactly what it was.</summary>
    public const bool SunDazzleEnabled = true;
    /// <summary>Linear HDR light the dazzle adds right at the sun at its peak, times the sun color.</summary>
    public const float SunDazzleStrength = 1.5f;
    /// <summary>Angle between the view and the sun within which the dazzle is at full strength.</summary>
    public const float SunDazzleFullDegrees = 4f;
    /// <summary>Angle between the view and the sun beyond which there is no dazzle.</summary>
    public const float SunDazzleFacingDegrees = 34f;
    /// <summary>Radius of the bright core around the sun (a Gaussian).</summary>
    public const float SunDazzleCoreRadius = 0.09f;
    /// <summary>Share of the peak that is the soft veil reaching out across the view, rather than the core.</summary>
    public const float SunDazzleVeilShare = 0.3f;
    /// <summary>Distance from the sun over which the veil falls to 1/e.</summary>
    public const float SunDazzleVeilReach = 0.3f;
    /// <summary>Brightness of the fan of rays round the core, as a share of the peak.</summary>
    public const float SunDazzleRayShare = 0.3f;
    /// <summary>Distance from the sun over which the rays fall to 1/e.</summary>
    public const float SunDazzleRayReach = 0.3f;
    /// <summary>Depth taps across the sun's disc that measure how much of it is in open sky.</summary>
    public const int SunDazzleTaps = 32;
    /// <summary>Rate (per second) at which the dazzle follows the share of the disc in open sky, so
    /// a leaf or a block edge crossing the sun fades it instead of stepping it.</summary>
    public const float SunDazzleEaseRate = 12f;
}
