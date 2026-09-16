using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Configuration for world generation and chunk management. Distances and frequencies are in
/// blocks (one block is 0.5 m; a frequency of 1/64 means features ~64 blocks across).
/// </summary>
public static class WorldGenerationConfig
{
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
    // columns at levels 0 / 1 / 2 through Chunk.GetColumnHeights, and a column that misses
    // Chunk.ColumnCache is generated on the spot (measured medians 17.7 / 17.6 / 65.3 ms per
    // build, tracking the column count, not the mesh). The four chunk-gen threads are doing that
    // same work on that same cache at that same moment, so LodGen workers do not add throughput to
    // an idle resource -- they contend for the one the burst already saturates. The lever the
    // measurement points at is cutting the work (item D2, ColumnCache's wholesale Clear() and its
    // 16384-entry cap against a level-2 ring of roughly 51k distinct columns), not more threads.
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

    // Distance fog that blends far terrain into the sky (also hides the LOD edge at night)
    public const float FogNearBlocks = 200f;
    public const float FogFarBlocks = 1800f;

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
    // Every noise channel derives its seed from this one (TerrainGenerator, NoiseCaves, WormCarver)
    public const int Seed = 1337;

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

    // Wave normal steepness multiplier for the water shading (geometry moves less than it looks)
    public const float WaterNormalScale = 2.0f;
    // Screen-space refraction offset at the camera, in uv units; shrinks with distance in the shader
    public const float WaterRefractStrength = 0.06f;
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

    // Beaches ring the sea: dry columns up to BeachAboveSeaLevel over the waterline and
    // submerged columns down to BeachBelowSeaLevel under it build their top and soil band
    // from sand instead of grass/dirt. Ravine bare-stone walls still win over the beach.
    public const int BeachAboveSeaLevel = 2;
    public const int BeachBelowSeaLevel = 3;

    // Gravel patches cap some beach columns: where the gravel noise passes its threshold
    public const float GravelFrequency = 1f / 48f;
    public const float GravelThreshold = 0.45f;

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
    // forest ground just before the hard forest/desert line (0.5). Only the last stretch before the
    // desert is affected, so lakes elsewhere in the forest keep their full depth. Without it a pond
    // that straddled the border was carved only on the forest side and ended in a vertical wall
    // against the uncarved desert (see TerrainGenerator.SurfaceWaterGate).
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

    // Temperature: cold where the noise runs low. Cold land is taiga (with humidity) or the barren
    // frozen biome (without it). Temperature and humidity are read only while choosing a biome and
    // the worldgen features that follow from it, so nothing else in the game sees them.
    public const float TemperatureFrequency = 1f / 2300f;
    public const float TemperatureColdStart = -0.35f;  // at or below: fully cold
    public const float TemperatureColdEnd = -0.05f;    // at or above: fully warm

    // Humidity: dry where the noise runs low. Dry land is desert (warm) or barren frozen (cold).
    public const float HumidityFrequency = 1f / 1700f;
    public const float HumidityDryStart = -0.35f;      // at or below: fully dry
    public const float HumidityDryEnd = -0.05f;        // at or above: fully wet

    // Temperate buffer between hot and cold biomes: desert needs firmly warm air and the
    // taiga/frozen need firmly cold air, so the ColdWeight band in between always classifies
    // as forest. Without it the warm/cold isoline puts dunes directly against snow whenever
    // the humidity stays dry across the temperature line.
    public const float DesertMaxCold = 0.30f;   // above: too cold for desert, forest instead
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

    // --- Mud ---
    // Dirt with water directly above it and at least this many water cells in its
    // 26-neighbourhood (faces, edges and corners alike) rots to mud the moment a
    // neighbouring fluid ticks and notices it.
    public const int MudWaterNeighbours = 2;

    // Blocks the far LOD water sheet sits below the chunk water surface, so the two never
    // z-fight where they meet at the full-detail ring. Depth resolution there is ~0.02 blocks;
    // the step is also visible edge-on as a thin line at the ring, so keep it as small as safe
    public const float LodWaterSink = 0.05f;

    // Underwater view: the fog closes in to a few blocks and everything turns blue-green.    // The fade is deliberately short, so breaking the surface reads as a hard boundary
    // rather than a slow filter, but is still eased instead of snapped.
    public const float UnderwaterFadeSeconds = 0.15f;
    public const float UnderwaterFogNearBlocks = 1f;
    public const float UnderwaterFogFarBlocks = 48f;

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

    // --- Trees (TreeGenerator) ---
    public const bool TreesEnabled = true;
    // A ground column grows a tree candidate with this probability; spacing (below) thins it
    public const float TreeDensity = 0.02f;
    // A candidate is kept only when its hash is the lowest within this Chebyshev radius,
    // which spaces trunks apart without any cross-chunk state
    public const int TreeSpacingRadius = 2;
    // Trunk height in blocks, from the ground up; the bottom TreeLogHeight blocks are full
    // cubes (1 size), everything above tapers through branch and twig boxes
    public const int TreeMinHeight = 6;
    public const int TreeMaxHeight = 10;
    public const int TreeLogHeight = 3;                     // full-cube log blocks at the trunk base
    public const int TreeBranchCount = 4;                   // limbs per tree (plus the crown spike)
    public const int TreeBranchMinLength = 2;
    public const int TreeBranchMaxLength = 4;
    public const float TreeBranchRiseChance = 0.45f;        // per limb step: rise instead of extend
    public const int TreeRootDepth = 4;                     // trunk extension into post-carve air below the base
    public const int TreeCrownRadius = 2;                   // leaf blob radius at branch tips + top
    // Ground rules: needs soil (not ravine rock), gentle slope, and modest altitude
    public const int TreeMinSoil = 2;
    public const int TreeMaxSlope = 2;
    public const int TreeMinGroundHeight = 4;
    public const int TreeMaxGroundHeight = 90;
    // Chunk border margin: how far a trunk outside this chunk can still reach into it
    public const int TreeReachBlocks = 8;
    public const int TreeMaxHeightBlocks = 16;              // tallest a tree (plus crown) can stand
    // Canopy columns poisoned around a wood/leaf edit (limb reach + blob radius + margin),
    // so the far LOD never resurrects an edited crown
    public const int TreeCanopyPoisonRadius = 8;
    // No trunk roots this close (Chebyshev) to the origin column, so the surface spawn the
    // camera picks there is always in the open
    public const int TreeSpawnClearRadius = 3;

    // --- Spruce (TreeGenerator's taiga conifer) ---
    // The taiga grows a cone: a full-cube spruce trunk with needle blobs stacked every level from
    // SpruceCrownStart up to the tip, their radius tapering from SpruceMaxRadius to one. Placement
    // (density, spacing, ground rules) is shared with the broadleaf tree above.
    public const int SpruceMinHeight = 7;
    public const int SpruceMaxHeight = 12;
    public const int SpruceCrownStart = 2;   // blocks of bare trunk before the canopy begins
    public const int SpruceMaxRadius = 3;    // widest blob radius, at the crown base
    // Taiga keeps more trees than the openness mask alone would leave, so a cold forest still
    // reads as wooded rather than as bare snow.
    public const float SpruceDensity = 0.03f;
    public const float SpruceOpennessRelief = 0.5f;

    // --- Cacti (CactusGenerator) ---
    // Desert saguaros: a tapered stem throws a few arms that bend upward into prongs like a
    // pitchfork. Four taper sizes (wood.family "cactus", levels 0..3) and no leaves.
    public const bool CactusEnabled = true;
    // A desert column grows a candidate with this probability; spacing (below) thins it
    public const float CactusDensity = 0.010f;
    // A candidate is kept only when its hash is the lowest within this Chebyshev radius
    public const int CactusSpacingRadius = 2;
    public const int CactusMinHeight = 3;
    public const int CactusMaxHeight = 6;
    public const int CactusMaxArms = 3;
    // Cells a prong climbs after its arm bends up; its top cell wears the tip size
    public const int CactusProngMinRise = 2;
    public const int CactusProngMaxRise = 4;
    public const int CactusRootDepth = 2;
    public const int CactusMinGroundHeight = 1;
    public const int CactusMaxGroundHeight = 90;
    public const int CactusMaxSlope = 2;
    // Chunk border margin: how far a stem outside this chunk can still reach into it
    public const int CactusReachBlocks = 6;
    public const int CactusMaxHeightBlocks = 12;

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

    // --- Flowers (FlowerGenerator) ---
    // Meadow cross-blocks in low-frequency patches on dry grass; a density roll thins inside them
    public const bool FlowersEnabled = true;
    public const float FlowerPatchFrequency = 1f / 64f;
    public const float FlowerPatchThreshold = 0.2f;
    public const float FlowerDensity = 0.06f;

    // --- Tree felling (TopplingTree) ---
    // Seconds for a felled tree to swing from upright to flat (cubic ease-in: slow tip,
    // accelerating slam), then rest before its wood pops out as item drops
    public const float TreeToppleSeconds = 1.2f;
    public const float TreeFellRestSeconds = 2f;
    // Collection bounds: wood flood cap, then overall cap including the canopy. Anything
    // past the caps stays standing (and still drops normally when mined).
    public const int TreeFellWoodCap = 256;
    public const int TreeFellTotalCap = 512;

    // --- Slimes (SlimeManager) ---
    // Forest mobs that hop at the player. Natural spawns appear on dry forest surface in a ring
    // around the player and are culled when they stray too far. Damage only lands while a slime
    // is airborne; a grounded touch shoves it instead. Combat values live here so a future
    // player-attack pass reads one place.
    public const int SlimeMaxAlive = 8;
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

    // --- Entity healthbars (EntityHealthbarRenderer) ---
    // Five hearts float above every live slime, billboarded to face the camera, using the same
    // heart sprites as the player HUD. The pool is drawn proportionally across the row (each
    // heart is MaxHealth / Hearts HP), so any entity kind reads correctly whatever its pool is.
    public const int EntityHealthbarHearts = 5;
    // Width (and height) of one heart in blocks; neighbours sit edge to edge like the HUD row.
    public const float EntityHeartSizeBlocks = 0.2f;
    // How far above the slime's head (Position + HalfExtent * VerticalScale) the row's centre floats.
    public const float EntityHealthbarLiftBlocks = 0.22f;

    // --- Charged throws (ThrownProjectile) ---
    // Right-click in the air charges a throwable, release throws it. Damage scales from half
    // at a tap to full at a full charge, and the launch speed scales the same way.
    public const float ThrowChargeSeconds = 0.9f;
    public const float ThrowMinCharge = 0.5f;
    public const float ThrowSpeed = 40f;
    public const float ThrowLifetimeSeconds = 5f;
}
