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

    // Directional shadow map resolution (depth texture is Size x Size)
    public const int ShadowMapSize = 2048;

    // Half-extent in blocks of the shadow camera's ortho box around the view target
    public const float ShadowRadiusBlocks = 90f;

    // Farthest chunk-center distance (blocks) that still casts into the shadow map
    public const float ShadowCasterRadiusBlocks = 110f;

    // Dynamic point lights in the world shader (primed TNT strobes); lamp glow is baked
    public const int MaxPointLights = 8;

    // Distance fog that blends far terrain into the sky (also hides the LOD edge at night)
    public const float FogNearBlocks = 200f;
    public const float FogFarBlocks = 1800f;

    // Sunlight and lamp light spread through air losing one level per block from a max
    // of 15, so one edited block can change light up to this far away. Block edits must
    // remesh every chunk in this radius, not just face-adjacent ones.
    public const int LightPropagationRadius = 15;

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

    // --- Leaf item drops ---
    // Mining a leaves block rolls each chance independently, so one block can drop
    // nothing, a vine, a branch, or (rarely) both.
    public const float LeafVineDropChance = 0.15f;
    public const float LeafBranchDropChance = 0.15f;

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
