namespace VoxelEngine.Config;

/// <summary>
/// World generation tuning: the seed, terrain shape, dirt depth, sea and water levels, lakes and
/// rivers, beaches and gravel, snow and ice, biomes, mud, lava pockets, ores, forest hills,
/// mountains and openness, ravines, caves and worms, tree placement and the deterministic worldgen
/// cache sizes. Distances and frequencies are in blocks (one block is 0.5 m; a frequency of 1/64
/// means features ~64 blocks across).
/// </summary>
public static class WorldGenerationConfig
{
    // --- Terrain ---
    // Every noise channel and worldgen hash derives its seed from this one (TerrainNoise,
    // NoiseCaves, WormCarver, the feature generators). It is runtime state, not a constant: a world
    // sets it through WorldSeed.Apply between worlds (never while one is live), and it defaults to
    // WorldSeed.Default, so tests, the item debugger and scripted runs see the old fixed seed.
    public static int Seed => WorldSeed.Value;

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
    // SeaLevel, after the carvers and before trees (see ChunkTerrainFill.FillWater). Measured over a
    // 1024x1024 area around the origin, terrain heights run -65..112 with a mean near +15, so
    // 0 floods about a quarter of the map (basins up to ~65 deep) while the origin column
    // (height 0) stays dry as a shoreline. Coverage: -8 -> 8%, 0 -> 26%, 4 -> 45%, 8 -> 53%.
    public const bool WaterEnabled = true;
    public const int SeaLevel = 0;

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
    // the flood test read the same LakeWeight/RiverWeight functions (WaterBodies). Every land
    // biome but the desert (WaterBodies.CanHoldSurfaceWater): a desert stays dry, and sand/gravel
    // show only where these weights are present.
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
    // land temperature (cold / temperate / warm) and humidity (dry / semi-dry / wet) form a 3x3
    // climate grid (BiomeClassifier.Classify): cold is taiga (wet, semi-dry) or barren frozen tundra
    // (dry), temperate is forest (wet) or steppe (semi-dry, dry), warm is forest (wet), savanna
    // (semi-dry) or desert (dry). So the dry axis reads desert -> steppe -> tundra and no forest
    // ever stands between sand and snow. Every field is a smooth 0..1 weight, so the height
    // profile blends across a border instead of stepping, and a coherent dither field breaks the
    // desert's material line into a ragged patchwork.
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
    // gap between them is the temperate band (forest or steppe, by humidity). Cold land is taiga
    // (with humidity) or the barren frozen biome (without it). Temperature and humidity are read
    // only through BiomeClassifier.SampleClimate, which domain-warps the sample position first
    // (ClimateWarp* below), while choosing a biome and the worldgen features
    // that follow from it, and by the grass/foliage tint through TerrainGenerator.Climate, so
    // nothing else in the game sees them.
    // Low frequency (one octave, see TerrainNoise.Reseed) keeps the temperature gradient shallow,
    // so the raw-noise gap between the two ramps spans a wide band of world: at this frequency it
    // keeps every hot column at least a full-detail load radius from snow
    // (BiomeTests.DesertAndSnowAreNeverNearAndTheDryCorridorIsSteppeOrTundra), with room left for
    // the climate warp below, which narrows the band wherever it squeezes the plane. Changing it
    // re-lays every temperature value except the origin's, which is exactly 0 at any frequency.
    public const float TemperatureFrequency = 1f / 5500f;
    public const float TemperatureColdStart = -0.35f;  // at or below: fully cold
    public const float TemperatureColdEnd = -0.05f;    // at or above: no cold at all
    // The warm ramp must start at or after the cold ramp ends, so no column is ever both cold and
    // warm (a test rule: TemperatureRampsAreExclusiveWithATemperateBandBetween). Desert begins at
    // the ramp's midpoint (WarmWeight 0.5), where the dune profile is half blended in.
    public const float TemperatureWarmStart = 0.05f;   // at or below: no warmth
    public const float TemperatureWarmEnd = 0.35f;     // at or above: fully warm

    // Humidity: a three-band axis like temperature. dry = 1 - smooth(HumidityDryStart, HumidityDryEnd,
    // humidity): dry land is desert (warm), steppe (temperate) or barren frozen (cold).
    public const float HumidityFrequency = 1f / 1700f;
    public const float HumidityDryStart = -0.35f;      // at or below: fully dry
    public const float HumidityDryEnd = -0.05f;        // at or above: no dryness at all
    // semiDry = 1 - smooth(HumiditySemiDryStart, HumiditySemiDryEnd, humidity): semi-dry land is
    // steppe when temperate and savanna when warm (a cold one stays taiga). The ramp starts at or
    // after the dry ramp ends, so the two are exclusive (a test rule: HumidityRampsAreExclusive) and
    // semiDry is 1 wherever any dryness is, which is what makes the savanna sit between forest and
    // desert on the warm row and the steppe between the forest and both dry biomes. The origin's
    // raw humidity is exactly 0 for every seed (OpenSimplex is zero at the lattice origin), so the
    // ramp's midpoint sits just below 0 to keep the spawn a forest (BiomeTests.OriginColumnIsForest).
    public const float HumiditySemiDryStart = -0.05f;  // at or below: fully semi-dry
    public const float HumiditySemiDryEnd = 0.03f;     // at or above: wet
    // Share of the forest-profile hills semi-dry and dry land loses (scaled by the smooth arid weight,
    // BiomeClassifier.BiomeWeights), which flattens steppe, savanna and tundra into plains while the
    // border stays a slope.
    public const float DryHillReduction = 0.6f;

    // One shared domain-warp displacement of the temperature and humidity sample position
    // (TerrainNoise.ClimateWarp, read only by BiomeClassifier.SampleClimate), so biome borders, and
    // the tint, grade and aurora that read the same sample, are ragged rather than smooth blobs.
    // The amplitude is the FastNoiseLite SetDomainWarpAmp value, not blocks: the peak sideways
    // displacement in blocks scales linearly with it, but FastNoiseLite's default fractal bounding
    // and the gradient's own range keep it well under the amplitude. One OpenSimplex2 warp with no
    // fractal: one evaluation per climate sample. Changing either value re-lays every climate value
    // of every seed, except at the origin, where the warp is exactly zero. Tuning rules: amplitude
    // times frequency stays small enough that the warp stretches the plane by at most 2
    // (BiomeClimateTests.TheWarpStretchesTheClimateBoundedly), and the band the warp squeezes must
    // still keep the desert/snow load-radius separation
    // (BiomeTests.DesertAndSnowAreNeverNearAndTheDryCorridorIsSteppeOrTundra), which is the tighter
    // of the two. If a rule fails, lower these values (or TemperatureFrequency, which widens the
    // band and re-lays the climate), never the rule.
    public const float ClimateWarpFrequency = 1f / 1400f;
    public const float ClimateWarpAmplitude = 360f;

    // Temperate band between hot and cold biomes: taiga/frozen live on the cold ramp (at
    // ColdBiomeMinCold and colder), desert and savanna on the warm ramp (from its midpoint up), and
    // the raw-noise gap between those two lines classifies as forest (wet) or steppe (semi-dry and
    // dry). Without it the warm/cold isoline would put dunes directly against snow whenever the
    // humidity stays dry across the temperature line; with it the dry corridor runs desert ->
    // steppe -> tundra. Widen the band by raising both TemperatureWarm constants together (which
    // keeps the dune fade width), never by moving the cold ramp.
    public const float ColdBiomeMinCold = 0.70f; // below: too warm for taiga/frozen, forest or steppe instead

    // Ocean depth ramp: the shelf sits just over the waterline and the basin falls to the floor.
    public const float OceanShoreHeight = 3f;       // blocks, the shallowest shelf
    public const float OceanFloorHeight = -44f;     // blocks, the deepest basin center
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

    // --- Mud ---
    // Dirt with water directly above it and at least this many water cells in its
    // 26-neighborhood (faces, edges and corners alike) rots to mud the moment a
    // neighboring fluid ticks and notices it.
    public const int MudWaterNeighbors = 2;

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

    // TreeBatches.ShapeCache and TreeCanopy.CanopyCache, both on the same GenerationalCache as
    // the column and worm-path caches above (item D6).
    //
    // The SHAPE cache holds one grown, rasterized tree per accepted trunk column, keyed by (x, z):
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
    // requires 2 * ReachHorizontal <= TreeCellBlocks, so a batch's neighbor obstacles always come
    // from the 8 adjacent cells.
    public const int TreeCellShift = 6;
    public const int TreeCellBlocks = 1 << TreeCellShift;

    // TreeBatches.BatchCache, on the same GenerationalCache as the sites above. One entry holds a
    // whole 64x64 cell of grown trees, measured over the 3x3 cells around tree-perf.json's vantages:
    // forest ~21 trees x ~250 cells x 16 B per entry (~82 KB mean, max ~70 trees / ~250 KB), taiga
    // ~79 trees x ~230 cells x 16 B per entry (~288 KB mean, max ~350 KB). It is shared by Stamp on
    // the generation workers and the first LOD ring's canopy sweep on the LOD builder, whose working
    // set is the generation disc (~70 cells) plus the first LOD ring (~17x17 regions, each reading up
    // to 4 cells, ~320 cells). Lowered from 384 so even an all-taiga cache stays under ~100 MB
    // (336 x ~288 KB ~ 95 MB); residency floor 252 at G = 4.
    public const int TreeBatchCacheCapacity = 336;
    public const int TreeBatchCacheGenerations = 4;

    // TreeCanopy.ProxyCanopyCache: the proxy CanopyColumns of a chunk column that the far LOD
    // rings read instead of the grown canopy. Cheap to rebuild (no growth, only placement and the
    // species' lod crown), and sized like the real canopy cache because the same per-fine-column
    // sweep in LodRegion.BuildMeshData drives it. G = 4 like every site; re-price only against a
    // capture (PERF_TODO lesson 4).
    public const int TreeProxyCanopyCacheCapacity = 4096;
    public const int TreeProxyCanopyCacheGenerations = 4;

    // Felled-canopy markers kept before TreeCanopy.CanopyDisturbed is wiped wholesale (it only
    // bounds memory). Sized so a fell's square, (2 * FellPoisonRadius + 1)^2 ~ 1000 columns, wipes
    // the set no more often than the old 289-column square did against 16384.
    public const int CanopyDisturbedLimit = 65536;
}
