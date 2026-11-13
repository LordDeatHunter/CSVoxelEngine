using System.Numerics;
using VoxelEngine.Simulation.Fluids;

namespace VoxelEngine.Config;

/// <summary>
/// Water rendering tuning: the planar reflection and its reuse, waves, refraction, absorption and
/// scatter, caustics, the underwater rays and fog, flow, shore foam, sun glitter, the screen-space
/// reflection march and the far-LOD water sheet.
/// </summary>
public static class WaterRenderConfig
{
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
    // Vertical FOV change, in degrees. Camera.GetProjectionMatrix builds from CurrentFov, which
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

    // Wave normal steepness multiplier for the water shading (geometry moves less than it looks)
    public const float WaterNormalScale = 2.0f;
    // Screen-space refraction offset at the camera, in uv units; shrinks with distance in the shader
    public const float WaterRefractStrength = 0.06f;
    // Seen from below, the surface is a Snell window: the whole sky above is squeezed into a
    // ~97 degree cone, so the same wave tilt swings the view through it further than it does
    // looking down. Multiplies WaterRefractStrength (and the WaterRefraction slider) for
    // fragments whose face is viewed from behind.
    public const float WaterWindowRefractScale = 1.75f;
    // Beer-Lambert absorption per block of water, per channel (red dies first, so depth reads teal)
    public static readonly Vector3 WaterAbsorb = new(0.20f, 0.07f, 0.04f);
    // In-scattered water color that replaces the seabed as the water thickens
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
    // silhouette several blocks deep rejects its neighbors outright.
    public const float WaterRayUpsampleToleranceBlocks = 2f;

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

    // Cap on how many cells up the eye's column the underwater rays look for the water top
    // (ChunkManager.GetFluidColumnTopGlobal); a deeper column reads its surface at the cap
    public const int FluidSurfaceSearchCells = 64;

    // Blocks the far LOD water sheet sits below the chunk water surface, so the two never
    // z-fight where they meet at the full-detail ring. Depth resolution there is ~0.02 blocks;
    // the step is also visible edge-on as a thin line at the ring, so keep it as small as safe
    public const float LodWaterSink = 0.05f;

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

    // Open water (OpenWater): a submerged eye probes 8 directions at each of these radii (blocks)
    // for water at its own surface level. Up to ShareNear of them wet is a small body (a pond, a
    // river, a cave pool) and keeps the plain fog above; from ShareFar it is a sea and gets the
    // ocean fog below. The weight eases at OpenWaterEaseRate per second while swimming between them.
    public static readonly int[] OpenWaterProbeRadii = [12, 24, 36];
    public const float OpenWaterShareNear = 0.4f;
    public const float OpenWaterShareFar = 0.8f;
    public const float OpenWaterEaseRate = 2f;
    // The ocean fog: reaches further, is a fuller blue (sRGB, dimmed by dayness like the plain
    // color) and is shaded by the view direction, darker looking down into the deep and brighter
    // looking up at the lit surface (OpenWater.FogShade; the span is the |dir.y| where the shade
    // reaches its end values).
    public const float OceanFogFarBlocks = 96f;
    public static readonly Vector3 OceanFogColor = new(0.05f, 0.30f, 0.62f);
    public const float OceanFogShadeDown = 0.65f;
    public const float OceanFogShadeUp = 1.5f;
    public const float OceanFogShadeSpan = 0.6f;
    // How much of the physical inside reflection the underside of the surface keeps. At 1 it would
    // be the true Snell window (a mirror of the water below everywhere past the critical angle);
    // lower lets the world above show through the whole underside, with the seabed mirrored over it.
    public const float WaterUnderMirror = 0.5f;
    // What the water does to the world seen up through the surface: a blue cast it is multiplied
    // by (linear) and the share of the fog color hazed over it.
    public static readonly Vector3 WaterUnderTint = new(0.55f, 0.8f, 1f);
    public const float WaterUnderHaze = 0.2f;
    // The share of the window's refraction offset kept past the critical angle, where the far
    // shore sits in a thin band that the full offset tears into ribbons.
    public const float WaterUnderGrazeRefract = 0.3f;
}
