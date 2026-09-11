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

    // How many mesh generation tasks can run concurrently on the thread pool
    public const int MaxConcurrentMeshGenerations = 4;

    // How many dedicated threads for block data generation
    public const int MaxConcurrentBlockDataGenerations = 4;

    // Delay when no chunks need generating (ms)
    public const int GenerationDelayIdleMs = 200;

    // --- Lighting ---
    // Full day/night cycle length in seconds (sun orbit + sky + light modulation)
    public const float DayLengthSeconds = 600f;

    // --- Ambience ---
    // Looping day/night ambience, crossfaded by the lighting pass's dayness (0 at night,
    // 1 in full day). Dayness itself ramps over ~30 s at dawn/dusk, so the crossfade is
    // inherently slow; FadeRate only smooths the per-frame volume steps.
    public const string DayAmbiencePath = "assets/sfx/forest-ambience.mp3";
    public const string NightAmbiencePath = "assets/sfx/night-ambience.mp3";
    // Peak volume of each loop when fully faded in (0..1)
    public const float AmbienceVolume = 0.35f;
    // Exponential approach rate per second toward the crossfade targets
    public const float AmbienceFadeRate = 0.8f;
    // Both loops rise from silence over this many seconds at startup
    public const float AmbienceStartupFadeSeconds = 8f;
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

    // Dynamic point lights in the world shader (primed TNT strobes); lamp glow is baked
    public const int MaxPointLights = 8;

    // Distance fog that blends far terrain into the sky (also hides the LOD edge at night)
    public const float FogNearBlocks = 200f;
    public const float FogFarBlocks = 1800f;

    // Sunlight and lamp light spread through air losing one level per block from a max
    // of 15, so one edited block can change light up to this far away. Block edits must
    // remesh every chunk in this radius, not just face-adjacent ones.
    public const int LightPropagationRadius = 15;

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

    // How many completed LOD region meshes to upload to GPU per frame
    public const int MaxLodUploadsPerFrame = 8;

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

    // Beaches ring the sea: dry columns up to BeachAboveSeaLevel over the waterline and
    // submerged columns down to BeachBelowSeaLevel under it build their top and soil band
    // from sand instead of grass/dirt. Ravine bare-stone walls still win over the beach.
    public const int BeachAboveSeaLevel = 2;
    public const int BeachBelowSeaLevel = 3;

    // Gravel patches cap some beach columns: where the gravel noise passes its threshold
    public const float GravelFrequency = 1f / 48f;
    public const float GravelThreshold = 0.45f;

    // Fluid simulation: one tick every FluidTickSeconds, at most MaxFluidTicksPerFrame of them
    // in a frame so a hitch never turns into a spiral of catch-up ticks
    public const float FluidTickSeconds = 0.25f;
    public const int MaxFluidTicksPerFrame = 4;

    // Cells processed per fluid tick at most; the remainder rides the next tick instead.
    // Bounds a chunk-load wake-up storm to a flat per-tick cost rather than one 100ms+
    // spike, at the price of a longer settling tail after spawn.
    public const int MaxFluidCellsPerTick = 2048;

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

    // --- Terrain shape (natural rolling base with large flat patches) ---
    // The base is the original rolling terrain (BaseFrequency, 6-octave product noise times
    // BaseAmplitude). A rare low-frequency mask flattens large patches toward gentle meadow
    // rolling on a broad swell, so most of the world keeps its natural look and only some
    // 200-400 block regions go flat. Mask values between the smoothstep edges are partial,
    // which gives soft natural transitions rather than a hard second biome.
    public const float BaseFrequency = 0.001f;
    public const float BaseAmplitude = 256f;
    public const float FlatMaskFrequency = 1f / 600f;   // wavelength ~600 blocks: flat-patch scale
    public const float FlatStart = 0.2f;                // fully natural below this...
    public const float FlatEnd = 0.6f;                  // ...fully flat above this (soft blend between)
    public const float MeadowFrequency = 1f / 160f;     // gentle rolling inside the flats
    public const float MeadowAmplitude = 9f;            // blocks, +- around the local base level
    public const float SwellFrequency = 1f / 900f;      // continent-scale undulation the flats ride on
    public const float SwellAmplitude = 22f;            // blocks; neighboring meadows sit at different levels

    // --- Ravines (2D, cut into the heightmap so the far LOD shows them too) ---
    public const bool RavinesEnabled = true;
    public const float RavineRidgeFrequency = 1f / 300f;   // spacing of candidate canyon lines
    public const float RavineWarpFrequency = 1f / 150f;    // how tightly the lines wind
    public const float RavineWarpAmplitude = 20f;          // blocks of sideways displacement
    public const float RavineMaskFrequency = 1f / 600f;    // rarity: ravines exist where mask noise is high
    public const float RavineMaskStart = 0.4f;
    public const float RavineMaskEnd = 0.55f;
    public const float RavineWidth = 0.06f;                // ridge noise band; ~12-15 blocks half-width
    public const float RavineDepth = 50f;                  // blocks, at full mask
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
}
