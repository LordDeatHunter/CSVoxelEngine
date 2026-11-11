namespace VoxelEngine.Config;

/// <summary>
/// World generation tuning: the seed, terrain shape, dirt depth, sea and water levels, lakes and
/// rivers, beaches and gravel, snow and ice, biomes, mud, lava pockets, ores, forest hills,
/// mountains and openness, ravines, caves and worms, tree placement, the deterministic worldgen
/// cache sizes and the realm layer bands (barrier, hell, cloud slab, sky islands). Distances and frequencies are in blocks (one block is 0.5 m; a frequency of 1/64
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

    // TerrainPeaks' massif verdict cache: one TerrainSite per massif cell (an accepted site or
    // none), a pure function of (cell, seed), so a column scan tests each candidate once per cell
    // instead of once per column. An entry is a few floats; GENERATIONS matches the other sites.
    public const int MassifSiteCacheCapacity = 1024;
    public const int MassifSiteCacheGenerations = 4;

    // TerrainPeaks' lone-peak verdict cache: one TerrainSite per lone-peak cell, a pure function of
    // (cell, seed). Its factory samples the land nine times, so caching it keeps that off each column.
    public const int PeakSiteCacheCapacity = 4096;
    public const int PeakSiteCacheGenerations = 4;

    // GreatLakes' verdict cache: one GreatLakeSite per great-lake cell (an accepted lake or none),
    // a pure function of (cell, seed). Its factory walks thousands of LandHeight and river samples
    // over the rim, the interior and the shore annulus, which is what the cache keeps off every
    // column. An entry is a few lobes of floats; GENERATIONS matches the other sites.
    public const int GreatLakeSiteCacheCapacity = 512;
    public const int GreatLakeSiteCacheGenerations = 4;

    // WaterBodies' pond verdict cache: one (accepted, surface, floor) per lake cell, a pure function
    // of (cell, seed). Its factory samples the rim (and the river rings for an elevated pond), which
    // the cache keeps off every column inside a candidate's radius. GENERATIONS matches the other sites.
    public const int PondSiteCacheCapacity = 4096;
    public const int PondSiteCacheGenerations = 4;

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

    // --- Great lakes (BIOMEGEN §6, phase D) ---
    // A second, much rarer lake tier (GreatLakes): one hashed candidate per GreatLakeCellBlocks
    // cell, a union of one to three lobe discs whose outlines are wobbled inward by
    // GreatLakeShapeNoise, optionally carrying an island. Like a pond it chooses its own surface
    // from its rim (GreatLakeBankDepth under the lowest rim point) and is refused on a rim that
    // climbs more than GreatLakeSiteFlatness, on ocean, desert or a biome that holds no water, on
    // interior ground rising over the rim, and over a massif or lone peak. A lake standing above
    // SeaLevel may get a river ramp: past its lobe edges the river level steps down one block every
    // GreatLakeRiverRunBlocks until it reaches SeaLevel, so a river reaching its shore meets the lake
    // at the lake's own surface, and an outflow channel may leave it from the lowest point of its rim,
    // tapering shut inside the ramp. A cutting river reaching the shore makes the lake river-fed; when
    // its ramp cannot be kept (too long, crowding a neighbor, banks too low) the lake falls back to
    // resting at SeaLevel, the river's own waterline. Ponds yield to it, the river yields inside its
    // bowl, and a ravine tapers out across its guard band (and across a ramp).
    public const bool GreatLakesEnabled = true;      // false: no great lake anywhere (ponds and rivers unchanged)
    public const int GreatLakeCellBlocks = 1280;     // candidate grid, one possible great lake per cell
    public const float GreatLakeChance = 0.3f;       // fraction of cells that roll a candidate
    public const float GreatLakeRadiusSkew = 1.4f;   // exponent on the size roll: most lakes nearer the minimum
    public const float GreatLakeMinRadius = 120f;    // main lobe radius of the smallest great lake
    public const float GreatLakeMaxRadius = 400f;    // main lobe radius of the largest great lake
    public const float GreatLakeMinDepth = 15f;      // floor depth under the surface, rolled per lake...
    public const float GreatLakeMaxDepth = 30f;      // ...up to this
    public const float GreatLakeBankDepth = 3f;      // surface under the lowest rim; above the pond's 2, since a
                                                     // 30-deep bowl's sub-threshold carve takes ~0.05*33 off a bank
    public const float GreatLakeSiteFlatness = 20f;  // reject a site whose rim climbs more than this
    public const float GreatLakeInteriorRiseMax = 12f; // how far interior ground may rise over the highest rim
                                                       // point before the site would erase relief
    public const float GreatLakeShoreShareMin = 0.2f; // shore ramp share of the radius on a flat rim...
    public const float GreatLakeShoreShareMax = 0.4f; // ...widening to this as the rim spread reaches the flatness
    public const float GreatLakeRimBandShare = 0.2f; // share of the shore ramp, from the outline inward, the rim walk samples
    public const float GreatLakeRimSpacing = 16f;    // blocks between rim walk samples
    public const float GreatLakeInteriorSpacing = 32f; // blocks between the coarse interior pass's samples
    public const float GreatLakeShapeFrequency = 1f / 120f; // outline wobble: bays a few lobes across a lake
    public const float GreatLakeShapeAmplitude = 0.18f;    // fraction of a lobe radius the wobble pulls inward
    public const float GreatLakeExtraLobeChance = 0.6f; // chance of each of the two extra lobes
    public const float GreatLakeLobeOffsetMin = 0.5f; // extra lobe center distance from the main center, in main radii...
    public const float GreatLakeLobeOffsetMax = 0.9f; // ...up to this
    public const float GreatLakeLobeRadiusMin = 0.4f; // extra lobe radius, in main radii...
    public const float GreatLakeLobeRadiusMax = 0.7f; // ...up to this
    public const float GreatLakeRavineGuardBlocks = 24f; // band past the outline across which a ravine tapers in
    public const float GreatLakePondClearance = 16f; // a pond keeps this far beyond a great lake's guard band
    public const float GreatLakeRiverFedMaxDrop = 12f; // the most a river-fed lake whose rivers cannot step down
                                                       // from its own surface may be lowered to rest at SeaLevel
    public const float GreatLakeRiverRunBlocks = 6f;  // blocks past a lobe edge per block the river level steps down; >= 1
    public const float GreatLakeRiverBankRise = 2f;   // ground a ramp's river and outflow need over their level
    public const float GreatLakeRampMaxLength = 192f; // a longer ramp is not kept; this bounds the river footprint
    public const float GreatLakeOutflowHalfWidth = 6f; // outflow channel half width
    public const float GreatLakeOutflowTaperBlocks = 16f; // the outflow tapers shut over its last this many blocks
    public const float GreatLakeOutflowMinLength = 24f; // a shorter outflow is not kept; >= GreatLakeOutflowTaperBlocks
    public const float GreatLakeIslandChance = 0.5f; // chance a lake carries an island in its main lobe
    public const float GreatLakeIslandMinRadius = 18f; // island radius range
    public const float GreatLakeIslandMaxRadius = 60f;
    public const float GreatLakeIslandShoreShare = 0.5f; // share of the island radius its shore ramp takes
    public const float GreatLakeSpawnClearRadius = ReliefSpawnClearStart; // no great lake reaches this close to the origin

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
    // Both read TerrainGenerator.ColdWeight (a biome-generation input), so the snow line fades in
    // as a column cools instead of snapping at a biome border. Freezing is a band rather than a
    // line: between PackIceColdStart and FreezeColdThreshold a floe noise decides ice or open water
    // per column (SnowGenerator.IsFrozenWater), the ice share rising with the cold, so the edge is
    // ragged pack ice that thickens into a solid sheet.
    public const bool SnowAndIceEnabled = true;
    public const float SnowColdThreshold = 0.48f;   // ColdWeight above which the first snow patches settle
    public const float SnowFullColdThreshold = 0.6f; // ColdWeight from which every column is snowed (the ragged snow band ends; must stay at or below PackIceColdStart)
    // ColdWeight above which the first floes form. Must stay at or above SnowColdThreshold: Stamp
    // skips sub-snow columns before it ever freezes, and the far LOD covers a frozen sheet in snow
    // only above the snow line, so ice below the snow line would disagree between near and far.
    public const float PackIceColdStart = 0.6f;
    public const float FreezeColdThreshold = 0.72f; // ColdWeight above which the sheet is solid (the band's end)
    public const float PackIceFrequency = 1f / 36f; // floe noise: a wavelength of tens of blocks
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
    // Deep cave floors receive spaced source groups and contained, prefilled bowls.
    // Sources sit at or below LavaMaxY and buried under the
    // terrain, so the surface, ravines and entrances stay dry and sea caves stay water.
    public const bool LavaEnabled = true;
    public const int LavaMaxY = -24;
    public const int LavaBurial = 2;
    // Retained patch field is the barrier vulcanite proximity mask, not a fluid scatter.
    public const float LavaPatchFrequency = 1f / 128f;
    public const float LavaPatchThreshold = 0.2f;
    // One small source group per 64-cube; candidates stay in its middle half.
    public const int CaveFluidCellShift = 6;
    public const int CaveFluidSiteMargin = 16;

    // Contained lava bowls: one candidate chunk per 64-cube, only where it meets a dry cave.
    public const int CaveLavaLakeCellShift = 6;
    public const int CaveLavaLakeMinRadius = 3;
    public const int CaveLavaLakeMaxRadius = 6;
    public const int CaveLavaLakeMinDepth = 2;
    public const int CaveLavaLakeMaxDepth = 3;
    public const int CaveLavaLakeBurial = 16;
    public const int CaveLavaLakeLocalLevel = 6;
    public const int CaveLavaLakeAirHeight = 3;
    public const float CaveLavaLakeChanceShallow = 0.18f;
    public const float CaveLavaLakeChanceDeep = 0.8f;

    // --- Malachite ore (MalachiteGenerator) ---
    // Common coal-like blobs replacing stone at any depth: cells where the vein field
    // (sampled on the OreNoiseStride lattice and trilinearly interpolated, OreNoiseLattice)
    // exceeds MalachiteThreshold become ore. Deposits average ~50 ores each and most
    // chunk columns hold several, so casual mining runs into ore without a dedicated
    // hunt. Frequency sets the blob diameter; the threshold sets deposit size and
    // spacing (measured in MalachiteTests, not guessed). The interpolation smooths the
    // field's peaks, so the threshold sits lower than the old per-cell field's 0.88.
    public const bool MalachiteEnabled = true;
    public const float MalachiteFrequency = 1f / 18f;
    public const float MalachiteThreshold = 0.83f;
    // Cave biome richness (plans/CAVE_BIOMES.md CB-2): added to the threshold in that cave biome's cells, so
    // negative means richer and positive poorer; every biome not listed keeps the plain field. First guesses.
    public const float MalachiteCrystalShift = -0.03f;

    // --- Coal ore (CoalGenerator) ---
    // Same threshold-on-noise deposits as malachite (on the same OreNoiseLattice field), but
    // hostable in sandstone as well as stone,
    // so desert columns carry shallow coal where malachite only ever shows in rock. A slightly
    // longer wavelength packs each blob over more cells (the threshold alone fixes the ore
    // fraction), which is what makes coal veins a touch larger than malachite's without making
    // the world any richer in ore. Measured relationally in CoalTests, not guessed.
    public const bool CoalEnabled = true;
    public const float CoalFrequency = 1f / 20f;
    public const float CoalThreshold = 0.84f;
    // Cave biome richness (plans/CAVE_BIOMES.md CB-2): added to the threshold in that cave biome's cells, so
    // negative means richer and positive poorer; every biome not listed keeps the plain field. First guesses.
    public const float CoalFungalShift = -0.03f;
    public const float CoalDesertEchoShift = -0.02f;
    public const float CoalMagmaDeepShift = 0.04f;   // burned out by the heat

    // --- Hematite ore (HematiteGenerator) ---
    // Iron: the same OreNoiseLattice deposits on a noise of its own, but deep stone only, in the
    // lava pockets' band at or below HematiteMaxY. A higher threshold than malachite's keeps the
    // veins rarer (measured relationally in HematiteTests, not guessed). The ceiling is an
    // absolute world Y, so HasOre stays a pure (x, y, z) point query equal to what Stamp writes.
    public const bool HematiteEnabled = true;
    public const float HematiteFrequency = 1f / 18f;
    public const float HematiteThreshold = 0.86f;
    // Cave biome richness (plans/CAVE_BIOMES.md CB-2): added to the threshold in that cave biome's cells, so
    // negative means richer and positive poorer; every biome not listed keeps the plain field. First guesses.
    public const float HematiteMagmaDeepShift = -0.03f;
    public const int HematiteMaxY = LavaMaxY;

    // --- Tin ore (TinGenerator) ---
    // The weaker, commoner metal: a lower threshold than malachite's (more ore, measured relationally
    // in TinTests) in a shallower and wider absolute-Y band, so HasOre stays a pure point query.
    public const bool TinEnabled = true;
    public const float TinFrequency = 1f / 18f;
    public const float TinThreshold = 0.8f;
    // Cave biome richness (plans/CAVE_BIOMES.md CB-2): added to the threshold in that cave biome's cells, so
    // negative means richer and positive poorer; every biome not listed keeps the plain field. First guesses.
    public const float TinFrozenEchoShift = -0.02f;
    public const float TinDesertEchoShift = -0.02f;
    public const int TinMinY = -96;
    public const int TinMaxY = 64;

    // --- Vulcanite ore (VulcaniteGenerator) ---
    // The tier above iron: the same OreNoiseLattice deposits on a noise of its own (seed offset 186), hosted
    // by basalt alone (the barrier band and the Magma Caverns' skin), so nothing is written above BarrierTopY.
    // The threshold is the far-from-lava one, which almost no blend passes; near lava it drops by up to
    // VulcaniteNearLavaShift, scaled by VulcaniteGenerator.LavaProximity (1 at the lava, 0 out of reach), so
    // the veins gather at the pools (measured relationally in VulcaniteTests). First guesses.
    public const bool VulcaniteEnabled = true;
    public const float VulcaniteFrequency = 1f / 14f;
    public const float VulcaniteThreshold = 0.93f;
    public const float VulcaniteNearLavaShift = -0.11f;
    // Hell: rows above the layer's lava level over which the proximity falls linearly from 1 to 0
    public const int VulcaniteLavaReachBlocks = 12;
    // The barrier band: how far (in LavaGenerator patch-noise units) under LavaPatchThreshold the proximity
    // starts to rise, reaching 1 at a lava patch's edge and staying 1 inside it, where the deep pockets pond
    public const float VulcanitePatchMargin = 0.15f;

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

    // --- Relief: highlands (BIOMEGEN §6) ---
    // A continent-scale highland weight (TerrainRelief.HighlandWeight), orthogonal to the climate:
    // it lifts the base height of the forest and dune profiles and multiplies the hill amplitude,
    // and mountain ranges rise only where it is positive. Every term it drives is exactly neutral
    // where the weight is 0 (no lift, a hill gain of 1, no range), so unmasked land is unchanged.
    // The spawn fades the weight out (ReliefSpawnClear*), so the origin stays a lowland.
    public const bool HighlandsEnabled = true;
    public const float ReliefFrequency = 1f / 5000f;       // highland/lowland wavelength (2 octaves)
    public const float HighlandMaskStart = 0.1f;           // lowland below this relief value...
    public const float HighlandMaskEnd = 0.35f;            // ...full highland above it
    public const float HighlandBaseLift = 16f;             // blocks a full highland raises the base
    public const float HighlandHillGain = 1.0f;            // extra share of the hill amplitude at full highland
    // No highland this close to the origin (faded in by the second radius), and through the
    // highland weight no mountain range either, so the spawn view stays a forest valley and the
    // land around it (where most visual-examples scenarios stand) is the land without the relief
    // layer. The highlands begin on the horizon the far LOD shows from the spawn.
    public const float ReliefSpawnClearStart = 1536f;
    public const float ReliefSpawnClearEnd = 2560f;

    // --- Forest mountains (occasional, not common) ---
    // Tall ranges that run only along highlands: a warped ridged field (peaks where |noise| is
    // near zero) riding a lower-frequency spine, sampled stretched along a hashed direction per
    // MountainRangeCellBlocks cell so a range reads as a chain, and gated by a rarity mask times
    // the highland weight. The ridge is raised to MountainSharpness so only the crest climbs,
    // leaving the hills around it intact.
    public const bool MountainsEnabled = true;
    public const float MountainFrequency = 1f / 240f;     // ridge line spacing
    public const float MountainWarpFrequency = 1f / 420f;  // how tightly a range winds
    public const float MountainWarpAmplitude = 30f;        // blocks of sideways displacement
    public const float MountainMaskFrequency = 1f / 1600f; // where ranges sit versus plain hills
    // The rarity mask is multiplied by the highland weight and mostly saturates inside one, so it
    // only breaks a range here and there; the narrow spine is what keeps ranges apart
    public const float MountainMaskStart = -0.3f;          // no mountain below this...
    public const float MountainMaskEnd = 0.1f;             // ...a full range above it
    public const float MountainAmplitude = 60f;            // blocks a fully-masked crest adds
    public const float MountainSharpness = 3f;             // >1 sharpens the crest into a peak
    public const int MountainRangeCellBlocks = 1536;       // one range direction per cell this wide
    public const float MountainRangeBlend = 0.15f;         // half-width of the cell-border crossfade, in cell fractions (< 0.5)
    // Jitters only the cell lookup, so the cell borders wander instead of running straight
    // north-south and east-west (the amplitude is a FastNoiseLite domain-warp value, not blocks)
    public const float MountainRangeCellWarpFrequency = 1f / 3000f;
    public const float MountainRangeCellWarpAmplitude = 1600f;
    public const float MountainRangeStretch = 6f;          // along-range wavelength multiplier
    public const float MountainSpineFrequency = 1f / 1400f; // the lower-frequency spine a range's ridges ride
    public const float MountainSpineSharpness = 2.5f;      // >1 narrows the spine
    public const float MountainSpineFloor = 0.3f;          // share of the ridge left where the spine is high but the ridge is low
    // Above this altitude a forest column turns to bare rock, so a high crest reads as a rocky
    // mountain instead of another tree-covered hill (trees also stop at each species'
    // worldgen.maxGround).
    public const float MountainRockHeight = 56f;
    // Height at which a peak wears full snow whatever its climate (SnowGenerator.ColdWeightAt)
    public const int MountainSnowFullHeight = 96;

    // --- Relief: massifs and lone peaks (BIOMEGEN §6, phase B) ---
    // Hashed sites (TerrainSites, TerrainPeaks) summed into the forest profile after the ranges.
    // A massif is a rare, large mountain: each accepted site rolls an absolute summit height and
    // rises from the relief land at its center to it, a pointed body with spurred flanks inside a
    // wobbled outline. A site is accepted only clear of the spawn, its neighbors, the ocean and the
    // desert, and only where it stands well clear of its rim. Every term is exactly 0 outside a
    // site's disc and within the spawn clearing (ReliefSpawnClear*).
    public const bool MassifsEnabled = true;
    public const int MassifCellBlocks = 1536;              // one massif candidate per cell this wide
    public const float MassifChance = 0.35f;               // chance a cell rolls a candidate
    public const float MassifSizeSkew = 1.5f;              // >1 skews the radius toward small
    public const float MassifMinRadius = 160f;             // blocks, the smallest nominal radius
    public const float MassifMaxRadius = 360f;             // blocks, the largest nominal radius
    public const float MassifMinSummit = 96f;              // lowest absolute summit a site rolls
    // Keep at or below ReliefCeilingHeight - ReliefCeilingKnee, so the ceiling never shapes a crown
    public const float MassifMaxSummit = 128f;
    public const float MassifMinRise = 48f;                // blocks the summit must stand above its center and its rim
    public const float MassifSharpness = 1.4f;             // >1 points the crown
    public const float MassifMinSpacing = 720f;            // blocks between two candidates (lowest hash wins)
    public const float MassifShapeFrequency = 1f / 220f;   // how fast the outline wobbles
    public const float MassifShapeAmplitude = 0.25f;       // share of the radius the outline pulls inward
    public const float MassifSpurFrequency = 1f / 110f;    // spur ridge spacing on the flanks
    // At or below 0.25 keeps the center the highest point of the term
    public const float MassifSpurShare = 0.25f;

    // A lone peak is a small, steep peak standing on flat land: accepted only clear of the spawn,
    // its neighbors, every massif, the ocean and the desert, and only where the land under its rim
    // (without lone peaks) is flat and above the sea.
    public const bool LonePeaksEnabled = true;
    public const int PeakCellBlocks = 448;                 // one lone-peak candidate per cell this wide
    public const float PeakChance = 0.45f;                 // chance a cell rolls a candidate
    public const float PeakSizeSkew = 1.3f;                // >1 skews the radius toward small
    public const float PeakMinRadius = 32f;                // blocks, the smallest nominal radius
    public const float PeakMaxRadius = 72f;                // blocks, the largest nominal radius
    public const float PeakMinHeight = 28f;                // blocks the lowest peak adds at its center
    public const float PeakMaxHeight = 56f;                // blocks the tallest peak adds at its center
    public const float PeakSharpness = 1.6f;               // >1 points the peak
    // Largest rim height spread (center included) a site may stand on: fewer, flatter-footed peaks below
    public const float PeakSiteFlatness = 6f;
    public const float PeakMinSpacing = 144f;              // blocks between two candidates (lowest hash wins)
    public const float PeakShapeFrequency = 1f / 40f;      // how fast the outline wobbles
    public const float PeakShapeAmplitude = 0.25f;         // share of the radius the outline pulls inward

    // A soft ceiling on the forest profile wherever a hashed site raises it: the identity up to
    // ReliefCeilingHeight - ReliefCeilingKnee, then easing toward ReliefCeilingHeight without ever
    // reaching it. Keep ReliefCeilingHeight below SkyConfig.CloudHeight, so no peak pierces the
    // cloud layer.
    public const float ReliefCeilingHeight = 148f;
    public const float ReliefCeilingKnee = 20f;

    // --- Islands and archipelagos (BIOMEGEN §6, phase C) ---
    // An ocean-gated island term: oceanWeight' = oceanWeight * (1 - islandLand), folded into the one
    // place the ocean weight is produced (BiomeClassifier.BiomeWeights), so every consumer sees the
    // same island. It is exactly 0 wherever the pre-island ocean weight is at or below
    // OceanShoreWeight (land and the coast band keep their bits) and inside ReliefSpawnClear*.
    // Two channels rise out of the deep water: a large one (islands that carry hills) and a small
    // one (knolls and islets); a hashed archipelago footprint lowers the small channel's threshold
    // and raises its gain along an elongated ellipse, so islets string out into chains.
    public const bool IslandsEnabled = true;
    public const float IslandLargeFrequency = 1f / 1200f;  // large-island noise spacing
    public const float IslandLargeStart = 0.45f;           // large-island noise where land begins...
    public const float IslandLargeEnd = 0.90f;             // ...and where it is full
    public const float IslandLargeGain = 1.25f;            // >1 so a deep core saturates to whole land
    public const float IslandSmallFrequency = 1f / 420f;   // small-island noise spacing
    public const float IslandSmallStart = 0.55f;           // small-island noise where land begins...
    public const float IslandSmallEnd = 0.85f;             // ...and where it is full
    public const float IslandSmallGain = 0.8f;             // small islands stay low outside an archipelago
    public const bool ArchipelagosEnabled = true;
    public const int ArchipelagoCellBlocks = 1792;         // one archipelago candidate per cell this wide
    public const float ArchipelagoChance = 0.3f;           // chance a cell rolls a candidate
    public const float ArchipelagoSizeSkew = 1f;           // >1 skews the length toward short
    public const float ArchipelagoMinHalfLength = 480f;    // blocks, the shortest chain's half length
    public const float ArchipelagoMaxHalfLength = 860f;    // blocks, the longest chain's half length
    public const float ArchipelagoMinAspect = 0.2f;        // half-width over half-length, the narrowest...
    public const float ArchipelagoMaxAspect = 0.45f;       // ...and the widest
    public const float ArchipelagoCoreShare = 0.5f;        // share of the ellipse at full archipelago weight
    public const float ArchipelagoThresholdDrop = 0.45f;   // how far the small channel's ramp drops inside
    public const float ArchipelagoSmallGain = 1.25f;       // the small channel's gain inside an archipelago

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
    public const float CaveSurfaceFadeEnd = 6f;
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

    /// <summary>
    /// Lattice spacing, in blocks, of the ore vein and gravel/sand pocket noise
    /// (<see cref="VoxelEngine.WorldGen.Features.Underground.OreNoiseLattice"/>). Must be a power of
    /// two dividing <see cref="VoxelEngine.Chunks.Chunk.Size"/>, so the lattice is world-aligned and
    /// neighboring chunks share their boundary samples.
    /// </summary>
    public const int OreNoiseStride = 2;

    // Cheese: blobby rooms where a low-frequency field exceeds a threshold that loosens with depth
    public const float CheeseFrequency = 1f / 80f;
    public const float CheeseYScale = 1.5f;
    public const float CheeseThresholdShallow = 0.66f;
    public const float CheeseThresholdDeep = 0.46f;
    public const float CheeseDepthStart = 8f;
    public const float CheeseDepthEnd = 280f;

    // Intermediate rooms on a shorter wavelength than cheese, increasingly frequent with depth.
    public const float CavePocketFrequency = 1f / 36f;
    public const float CavePocketYScale = 1.2f;
    public const float CavePocketThresholdShallow = 0.64f;
    public const float CavePocketThresholdDeep = 0.38f;
    public const float CavePocketDepthStart = 6f;
    public const float CavePocketDepthEnd = 320f;

    // Spaghetti: tunnels along the curves where two independent fields are both near zero.
    // Radii are in noise units; the tunnel radius in blocks is roughly radius * wavelength / 30.
    public const float SpaghettiFrequency = 1f / 64f;
    public const float SpaghettiYScale = 1.2f;
    public const float SpaghettiRadius = 0.13f;            // wider walkable core
    public const float SpaghettiRadiusVariation = 0.02f;
    public const float SpaghettiThicknessFrequency = 1f / 200f;

    // Narrow spaghetti: tighter, more frequent tunnels, still walkable, only in patches
    public const float NarrowSpaghettiFrequency = 1f / 32f;
    public const float NarrowSpaghettiRadius = 0.18f;       // standing clearance in developed patches
    public const float NarrowSpaghettiMaskFrequency = 1f / 160f;
    public const float NarrowSpaghettiMaskStart = -0.1f;    // patches exist where mask noise exceeds this...
    public const float NarrowSpaghettiMaskEnd = 0.3f;       // ...and are fully developed above this

    // Desert caves: long, narrow noise tunnels between bounded large ellipsoid rooms.
    public const float DesertTunnelXScale = 0.28f;
    public const float DesertTunnelRadius = 0.075f;
    public const int DesertPocketCellBlocks = 96;
    public const int DesertPocketCellHeight = 64;
    public const int DesertPocketMinRadius = 25;
    public const int DesertPocketMaxRadius = 36;
    public const int DesertPocketMinHeight = 12;
    public const int DesertPocketMaxHeight = 20;
    public const int DesertPocketWallBlocks = 3;
    public const float DesertPocketChanceShallow = 0.35f;
    public const float DesertPocketChanceDeep = 0.8f;
    public const float DesertPocketDepthStart = 20f;
    public const float DesertPocketDepthEnd = 320f;

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

    // --- Layers (plans/LAYERS.md): the hell below and the sky above, as vertical bands of the one
    // world. Every Y range of the realms lives here (the visual cloud sea in SkyConfig), nowhere
    // else; LayerBandTests holds their ordering. World y, inclusive at both ends of each band.

    /// <summary>Top of the barrier band, where its ramp starts: the barrier block's share of the
    /// stone rises from 0 here to 100 % at <see cref="BarrierFullTopY"/>.</summary>
    public const int BarrierTopY = -500;

    /// <summary>Top of the barrier band's solid core: 100 % barrier from here down to
    /// <see cref="BarrierFullBottomY"/>.</summary>
    public const int BarrierFullTopY = BarrierTopY - 30;

    /// <summary>Bottom of the barrier band's solid core; below it the share ramps back to 0 at
    /// <see cref="BarrierBottomY"/>.</summary>
    public const int BarrierFullBottomY = BarrierTopY - 70;

    /// <summary>Bottom of the barrier band, where its lower ramp ends at 0 %.</summary>
    public const int BarrierBottomY = BarrierTopY - 100;

    /// <summary>Whether worldgen stamps the barrier band (BarrierGenerator).</summary>
    public const bool BarrierEnabled = true;

    /// <summary>Frequency of the barrier's 3D patch noise (seed offset 162): a cell in either ramp
    /// is barrier where the noise, mapped to 0..1, is under the ramp's share at its y, so the
    /// band's edges break into coherent patches about this wavelength across.</summary>
    public const float BarrierPatchFrequency = 1f / 20f;

    /// <summary>Where hell starts: the first cell under the barrier band. Hell has no bottom.</summary>
    public const int HellTopY = BarrierBottomY - 1;

    /// <summary>Height of one hell layer: an open cavern pocket plus the hellrock slab under it,
    /// repeating downward forever from <see cref="HellTopY"/> (the layer index counts these).</summary>
    public const int HellLayerPeriodBlocks = 96;

    /// <summary>Thickness of the hellrock slab at the bottom of every hell layer, separating one
    /// pocket from the next.</summary>
    public const int HellSlabThicknessBlocks = 16;

    /// <summary>Whether worldgen fills hell (HellGenerator): below <see cref="HellTopY"/> every cell
    /// is the hell generator's, cavern pockets in hellrock separated by slabs.</summary>
    public const bool HellEnabled = true;

    /// <summary>Lattice spacing of the hell cavern noise in blocks: a power of two dividing the
    /// chunk size, so neighboring chunks sample the same lattice points.</summary>
    public const int HellNoiseStride = 4;

    /// <summary>Frequency of the hell cavern noise (seed offset 163, two octaves): about the width
    /// of one cavern chamber.</summary>
    public const float HellCavernFrequency = 1f / 64f;

    /// <summary>The cavern noise's y is scaled by this, so chambers are a little flatter than wide.</summary>
    public const float HellCavernYScale = 1.5f;

    /// <summary>How far in from a pocket's ceiling and floor the edge bias reaches: within this many
    /// cells of either the cells close up, more the nearer the edge, so ceilings and floors follow
    /// the noise instead of a flat plane.</summary>
    public const int HellEdgeBlocks = 32;

    /// <summary>The edge bias right at a pocket's ceiling and floor, in noise units (falling
    /// quadratically to 0 at <see cref="HellEdgeBlocks"/>): above the noise's range, so the
    /// outermost cells are always hellrock.</summary>
    public const float HellEdgeStrength = 1.6f;

    /// <summary>The cavern noise must exceed this (plus the edge bias) for a pocket cell of the top
    /// layer to open; each layer down lowers it by <see cref="HellOpenThresholdPerLayer"/>, so
    /// deeper pockets are more open, down to <see cref="HellOpenThresholdMin"/>.</summary>
    public const float HellOpenThresholdTop = -0.1f;

    /// <summary>How much more open each hell layer is than the one above it.</summary>
    public const float HellOpenThresholdPerLayer = 0.02f;

    /// <summary>The most open a hell layer gets.</summary>
    public const float HellOpenThresholdMin = -0.3f;

    /// <summary>The lava level of the top hell layer: every open cell fewer than this many cells
    /// above its pocket's floor holds a lava source, so the dips of a cavern floor pond lava.
    /// Rises by <see cref="HellLavaDepthPerLayer"/> each layer down, up to <see cref="HellLavaMaxDepth"/>.</summary>
    public const int HellLavaBaseDepth = 14;

    /// <summary>How many cells the lava level rises each hell layer down.</summary>
    public const int HellLavaDepthPerLayer = 1;

    /// <summary>The highest lava level of any hell layer, in cells above its pocket's floor.</summary>
    public const int HellLavaMaxDepth = 22;

    /// <summary>Frequency of the hell biome noise (seed offset 164, two octaves, 3D): sampled at
    /// each layer's mid row, so a hell biome region is a few hundred blocks across and differs from
    /// layer to layer.</summary>
    public const float HellBiomeFrequency = 1f / 320f;

    /// <summary>Largest hashed lean each hell layer adds to the biome noise, so one layer leans
    /// toward one biome and the next toward another while each still mixes them.</summary>
    public const float HellBiomeLayerBias = 0.25f;

    /// <summary>Spacing of the 2D lattice per layer the hell biome noise is read on, blended
    /// bilinearly: the chunk size, so a chunk reads four points per layer.</summary>
    public const int HellBiomeLatticeStride = 16;

    /// <summary>Spacing of the hell pool noise's lattice: a power of two dividing the chunk size.</summary>
    public const int HellPoolLatticeStride = 8;

    /// <summary>A hell column whose biome value is under this is Ashlands.</summary>
    public const float HellAshlandsMaxValue = -0.1f;

    /// <summary>A hell column whose biome value is over this is a Fungal Grove; between the two
    /// thresholds it is a Magma Cavern.</summary>
    public const float HellFungalMinValue = 0.1f;

    /// <summary>Frequency of the hell pool noise (seed offset 165, 3D): which columns of a biome
    /// with a partial lava share pond lava and which fill their pool rows with soil.</summary>
    public const float HellPoolFrequency = 1f / 24f;

    /// <summary>How many cells under an open cavern cell take the biome's floor skin: the top one
    /// its surface, the rest its soil.</summary>
    public const int HellFloorSkinBlocks = 3;

    /// <summary>How many cells over an open cavern cell take the biome's ceiling skin.</summary>
    public const int HellCeilingSkinBlocks = 2;

    /// <summary>The share of the pool columns that hold lava in the Ashlands (the rest are ash
    /// plains at the lava level).</summary>
    public const float HellAshlandsLavaShare = 0.35f;

    /// <summary>The share of the pool columns that hold lava in a Fungal Grove.</summary>
    public const float HellFungalLavaShare = 0.08f;

    /// <summary>One ceiling cell right over open cavern in this many is an ember crystal in the
    /// Ashlands and the Magma Caverns.</summary>
    public const int HellEmberCrystalRate = 40;

    /// <summary>One Magma Cavern floor surface cell in this many is a glowing magma block.</summary>
    public const int HellMagmaFloorRate = 7;

    /// <summary>One Fungal Grove ceiling cell right over open cavern in this many is a fungal glow.</summary>
    public const int HellFungalGlowRate = 18;

    /// <summary>The longest vine (HellFlora) a hell ceiling cell hangs: the flora rule reads this many
    /// cells over an air cell.</summary>
    public const int HellVineMaxLength = 6;

    /// <summary>One Fungal Grove ceiling cell over open cavern in this many hangs a vine.</summary>
    public const int HellFungalVineRate = 4;

    /// <summary>One Ashlands ceiling cell over open cavern in this many hangs a vine.</summary>
    public const int HellAshlandsVineRate = 45;

    /// <summary>One Fungal Grove air cell over fungal moss in this many grows a glow fern.</summary>
    public const int HellGlowFernRate = 5;

    /// <summary>One Ashlands air cell over ash in this many grows an ember bloom.</summary>
    public const int HellEmberBloomRate = 16;

    /// <summary>The side of a hell tree cell (HellTrees): each cell of each layer holds at most one
    /// tree, grown inside the cell, so a tree never meets another. Divides the chunk size.</summary>
    public const int HellTreeCellBlocks = 8;

    /// <summary>How far inside its cell's sides a hell tree's root stands.</summary>
    public const int HellTreeCellMargin = 2;

    /// <summary>The chance a Fungal Grove tree cell roots a fungal tree.</summary>
    public const float HellFungalTreeChance = 0.6f;

    /// <summary>The chance an Ashlands tree cell roots a charred tree.</summary>
    public const float HellCharredTreeChance = 0.25f;

    /// <summary>Capacity of the hell tree cache (one entry per tree cell and layer, a tree or none),
    /// read by the generation workers; see <see cref="TreeShapeCacheCapacity"/>.</summary>
    public const int HellTreeCacheCapacity = 8192;

    /// <summary>Generations of the hell tree cache.</summary>
    public const int HellTreeCacheGenerations = 4;

    // Cave biomes (plans/CAVE_BIOMES.md CB-1): the overworld's carved caves stop being biome-blind. A frozen
    // or desert column's caves echo its surface down to a wavy line near CaveEchoBottomY; under it the cave
    // sub-biomes (crystal, fungal, the magma-touched deep) fill the rock down to the barrier. CaveBiomes
    // classifies, its palette pass rewrites only stone. LayerBandTests holds the band order. First guesses.

    /// <summary>Whether worldgen dresses the overworld caves in their cave biome's palette (CaveBiomes).</summary>
    public const bool CaveBiomesEnabled = true;

    /// <summary>Lattice spacing of the three cave-biome noises in blocks: a power of two dividing the chunk
    /// size, so neighboring chunks sample the same lattice points and the point query equals the stamp.</summary>
    public const int CaveBiomeLatticeStride = 8;

    /// <summary>The echo band's mean bottom: an echo cell lies strictly above
    /// <c>CaveEchoBottomY + CaveEchoDriftBlocks * drift</c>, drift being the echo drift noise in -1..1.</summary>
    public const int CaveEchoBottomY = -200;

    // Bounded 3D echo bodies spread under neighboring columns, beyond the original echo band.
    public const int CaveEchoVolumeBottomY = BarrierTopY + 100;
    public const int CaveEchoVolumeCellBlocks = 128;
    public const int CaveEchoVolumeRadius = 96;
    public const int CaveEchoVolumeVerticalRadius = 112;
    public const int CaveEchoVolumeCacheCapacity = 4096;
    public const int CaveEchoVolumeCacheGenerations = 4;

    /// <summary>How far the echo band's bottom wanders up and down with the echo drift noise (seed offset 179),
    /// so the hand-over to the sub-biomes is never a flat line.</summary>
    public const int CaveEchoDriftBlocks = 24;

    /// <summary>Frequency of the echo drift noise (seed offset 179).</summary>
    public const float CaveEchoDriftFrequency = 1f / 160f;

    /// <summary>The rock an echo leaves untouched under its column's surface: an echo cell needs a depth of at
    /// least <c>CaveEchoCoverBlocks + CaveEchoCoverJitterBlocks * (drift + 1) / 2</c> under the column top, so
    /// the surface, its soil and shallow cliffs keep their own blocks.</summary>
    public const int CaveEchoCoverBlocks = 8;

    /// <summary>How much deeper the echo cover reaches where the echo drift noise is high, so the echo's top
    /// edge is ragged rather than a copy of the surface.</summary>
    public const int CaveEchoCoverJitterBlocks = 6;

    /// <summary>The magma-touched deep's mean top: a cell under the echo band is MagmaDeep at or below
    /// <c>CaveDeepTopY + CaveDeepDriftBlocks * deepDrift</c>, deepDrift being the deep drift noise in -1..1.
    /// Nothing is written at or below <see cref="BarrierTopY"/>.</summary>
    public const int CaveDeepTopY = -264;

    /// <summary>How far the deep's top wanders with the deep drift noise (seed offset 180).</summary>
    public const int CaveDeepDriftBlocks = 16;

    /// <summary>Frequency of the deep drift noise (seed offset 180).</summary>
    public const float CaveDeepDriftFrequency = 1f / 120f;

    /// <summary>Frequency of the sub-biome selector noise (seed offset 181, two octaves): about the size of
    /// one crystal or fungal region between the echo band and the deep.</summary>
    public const float CaveSubBiomeFrequency = 1f / 96f;

    /// <summary>A sub-band cell above the deep is Crystal where the selector noise is above this.</summary>
    public const float CaveCrystalMinValue = 0.12f;

    /// <summary>A sub-band cell above the deep is Fungal where the selector noise is below this; between
    /// this and <see cref="CaveCrystalMinValue"/> it stays plain stone.</summary>
    public const float CaveFungalMaxValue = -0.12f;

    /// <summary>One crystal cave face cell in this many is a crystal block.</summary>
    public const int CaveCrystalAccentRate = 10;

    /// <summary>One fungal cave face cell in this many is a fungal glow.</summary>
    public const int CaveFungalAccentRate = 60;

    /// <summary>One magma-touched cave face cell in this many is a magma block.</summary>
    public const int CaveMagmaAccentRate = 24;

    // Cave biome shape (plans/CAVE_BIOMES.md CB-2, CaveShape): only the sub-band biomes change the carve. Crystal
    // caves open bigger cheese rooms, fungal caves wider tunnels and the magma-touched deep both, while plain and
    // echo caves are carved exactly as before (a boost of 0 and a scale of 1 are exact). Each boost is scaled per
    // region by 1 + CaveShapeScaleVariation * s, s being NoiseCaves' ninth lattice channel (seed offset 182), so
    // cave size varies across a biome. A worm node's radius is multiplied at its center. First guesses.

    /// <summary>Frequency of the cave shape scale noise (seed offset 182), NoiseCaves' ninth lattice channel.</summary>
    public const float CaveShapeScaleFrequency = 1f / 192f;

    /// <summary>How far the shape scale noise moves a sub-biome's boosts: each is multiplied by
    /// <c>1 + CaveShapeScaleVariation * s</c>, s in -1..1.</summary>
    public const float CaveShapeScaleVariation = 0.5f;

    /// <summary>How much a crystal cell lowers the cheese threshold: bigger rooms.</summary>
    public const float CaveCrystalCheeseBoost = 0.10f;

    /// <summary>How much a magma-touched cell lowers the cheese threshold.</summary>
    public const float CaveMagmaCheeseBoost = 0.05f;

    /// <summary>How much a magma-touched cell lowers the cavern threshold: more open caverns.</summary>
    public const float CaveMagmaCavernBoost = 0.10f;

    /// <summary>The share a fungal cell widens both spaghetti tunnel radii by.</summary>
    public const float CaveFungalTunnelBoost = 0.45f;

    /// <summary>What a worm node's radius is multiplied by when its center is a fungal cell.</summary>
    public const float CaveFungalWormRadiusFactor = 1.35f;

    /// <summary>What a worm node's radius is multiplied by when its center is a magma-touched cell.</summary>
    public const float CaveMagmaWormRadiusFactor = 1.15f;

    // Cave biome exclusive ores (plans/CAVE_BIOMES.md CB-2, CaveOres): one vein ore per sub-band biome, amethyst in
    // crystal caves (seed offset 183), jade in fungal caves (184) and cinnabar in the magma-touched deep (185), each
    // on the OreNoiseLattice like the Y-band ores. Each hosts only plain stone whose cell is its own biome, at or below
    // its MaxY, and runs right after the Y-band ores, before the palette pass: that pass rewrites only exact stone, so
    // the ore survives even inside the magma-touched deep's scorched hellrock. The echoes get no exclusive ore. First
    // guesses.

    /// <summary>Whether worldgen stamps the cave biome exclusive ores (CaveOres).</summary>
    public const bool CaveOresEnabled = true;

    /// <summary>Frequency of the amethyst vein noise (seed offset 183).</summary>
    public const float CaveAmethystFrequency = 1f / 16f;

    /// <summary>Amethyst vein threshold in -1..1: higher is rarer.</summary>
    public const float CaveAmethystThreshold = 0.80f;

    /// <summary>The highest world y amethyst grows at; below the crystal caves' highest reach.</summary>
    public const int CaveAmethystMaxY = -192;

    /// <summary>Frequency of the jade vein noise (seed offset 184).</summary>
    public const float CaveJadeFrequency = 1f / 18f;

    /// <summary>Jade vein threshold in -1..1: higher is rarer.</summary>
    public const float CaveJadeThreshold = 0.80f;

    /// <summary>The highest world y jade grows at; below the fungal caves' highest reach.</summary>
    public const int CaveJadeMaxY = -192;

    /// <summary>Frequency of the cinnabar vein noise (seed offset 185).</summary>
    public const float CaveCinnabarFrequency = 1f / 14f;

    /// <summary>Cinnabar vein threshold in -1..1: higher is rarer.</summary>
    public const float CaveCinnabarThreshold = 0.82f;

    /// <summary>The highest world y cinnabar grows at; below the magma-touched deep's highest reach.</summary>
    public const int CaveCinnabarMaxY = -256;

    /// <summary>Capacity of the sky tree cache (one entry per sky tree cell, a tree or none), read by the
    /// generation workers; see <see cref="TreeShapeCacheCapacity"/>.</summary>
    public const int SkyTreeCacheCapacity = 4096;

    /// <summary>Generations of the sky tree cache.</summary>
    public const int SkyTreeCacheGenerations = 4;

    /// <summary>Capacity of the battle tower site cache (one entry per tower cell, a site or none), read by the
    /// generation workers; see <see cref="TreeShapeCacheCapacity"/>.</summary>
    public const int TowerSiteCacheCapacity = 256;

    /// <summary>Generations of the battle tower site cache.</summary>
    public const int TowerSiteCacheGenerations = 4;

    // Battle tower (plans/BATTLE_TOWER.md BT-2): a procedural shell of unbreakable tower brick, one hashed site per
    // tower cell, dressed by hand-authored towerFloor / towerAltar templates. First guesses, tuned against the
    // default seed's rarity

    /// <summary>Master switch for the battle towers (<c>TowerPlacement</c>).</summary>
    public const bool TowersEnabled = true;

    /// <summary>The side of a tower cell: each cell rolls at most one tower, wholly inside it.</summary>
    public const int TowerCellBlocks = 1536;

    /// <summary>The chance a tower cell rolls a tower, before the site rules (level, dry land) refuse it.</summary>
    public const float TowerChance = 0.6f;

    /// <summary>No tower footprint reaches this close to the origin.</summary>
    public const float TowerSpawnClearRadius = ReliefSpawnClearStart;

    /// <summary>The smallest radius a tower rolls, to the outside of its outer wall.</summary>
    public const int TowerMinRadius = 20;

    /// <summary>The largest radius a tower rolls.</summary>
    public const int TowerMaxRadius = 24;

    /// <summary>The fewest floors a tower rolls.</summary>
    public const int TowerMinFloors = 4;

    /// <summary>The most floors a tower rolls.</summary>
    public const int TowerMaxFloors = 8;

    /// <summary>Blocks from one floor slab to the next.</summary>
    public const int TowerFloorHeight = 7;

    /// <summary>Thickness of the outer wall, in cells.</summary>
    public const int TowerWallThickness = 2;

    /// <summary>Width of the stair corridor between the outer and the inner wall, in cells.</summary>
    public const int TowerStairWidth = 3;

    /// <summary>Thickness of the inner wall around the floor rooms, in cells.</summary>
    public const int TowerInnerWallThickness = 1;

    /// <summary>Half-steps of the spiral stair per full turn around the tower.</summary>
    public const int TowerStairStepsPerTurn = 60;

    /// <summary>Air cells kept clear above every stair step (and the entrance and doorways).</summary>
    public const int TowerStairHeadroom = 3;

    /// <summary>Stair half-steps the ground entrance spans before the climb starts.</summary>
    public const int TowerEntranceSteps = 3;

    /// <summary>How many half-steps either side of a landing its inner-wall doorway spans.</summary>
    public const int TowerDoorwayHalfSteps = 2;

    /// <summary>Half-steps past a landing at which a floor's barrier door (BT-4) closes the stair; even, and above
    /// <see cref="TowerDoorwayHalfSteps"/> + 2 so the door stands clear of the doorway.</summary>
    public const int TowerDoorStepOffset = 6;

    /// <summary>How far the entrance apron reaches outside the outer wall.</summary>
    public const int TowerApronDepth = 3;

    /// <summary>Height of the crenellated parapet above the roof slab: solid up to the layer under its top, merlons on
    /// the top layer, where the forcefield dome stands.</summary>
    public const int TowerParapetHeight = 4;

    /// <summary>How far the roof's forcefield dome rises over its base on the parapet's top layer, at the center
    /// (<c>TowerDome</c>). It must clear the altar dressing (<see cref="TowerAltarHeight"/>) over the whole template.</summary>
    public const int TowerDomeHeight = 12;

    /// <summary>The tallest the altar dressing on the roof may be (the towerAltar template's layer cap).</summary>
    public const int TowerAltarHeight = 8;

    /// <summary>How deep the brick foundation reaches under the ground slab.</summary>
    public const int TowerFoundationDepth = 6;

    /// <summary>The lowest a conquered tower's wall stump stands above its ground slab, in blocks (BT-8, <c>TowerRuin</c>).</summary>
    public const int TowerRuinStumpMinBlocks = 2;

    /// <summary>The highest a conquered tower's wall stump stands above its ground slab, in blocks (BT-8); kept under one
    /// floor's height so no stump reaches the first floor's slab.</summary>
    public const int TowerRuinStumpMaxBlocks = 9;

    /// <summary>The share of a ruined tower's ground-floor air columns that get a rubble pile on the ground slab (BT-8).</summary>
    public const float TowerRuinRubbleChance = 0.15f;

    /// <summary>The share of the rubble piles that are gravel rather than cobblestone (BT-8).</summary>
    public const float TowerRuinGravelShare = 0.4f;

    /// <summary>The share of a ruined tower's brick cells that crumble to cobblestone rather than stone bricks (BT-8).</summary>
    public const float TowerRuinCobbleShare = 0.35f;

    /// <summary>The largest ground height step from the center a tower site accepts across its disc.</summary>
    public const int TowerMaxStep = 4;

    /// <summary>Spacing of the ground samples a tower site checks across its disc.</summary>
    public const int TowerSiteSampleStep = 4;

    /// <summary>How far past the apron the site check keeps sampling for water and level ground.</summary>
    public const int TowerSiteWaterRing = 3;

    /// <summary>How many Chebyshev rings of tower cells around the player's own cell a <c>/locate tower</c> scans, so it
    /// covers at least <see cref="TowerLocateRings"/> * <see cref="TowerCellBlocks"/> blocks. Rolling a cell is only a
    /// hash; a candidate pays its cached site verdict only when it is nearer than the answer.</summary>
    public const int TowerLocateRings = 8;

    /// <summary>How far west of a tower's footprint box the <c>/locate</c> teleport lands. Keep it at most
    /// <see cref="TowerSiteWaterRing"/>, so the landing lies inside the ring the site rules judged dry and level.</summary>
    public const int TowerLocateLandingGapBlocks = 2;

    /// <summary>Bottom of the solid cloud slab: the patchy real cloud blocks around y 200, above the
    /// relief ceiling (<see cref="ReliefCeilingHeight"/>) and the drawn cloud layer
    /// (<see cref="SkyConfig.CloudHeight"/>).</summary>
    public const int CloudSlabBottomY = 198;

    /// <summary>Top of the solid cloud slab.</summary>
    public const int CloudSlabTopY = 203;

    /// <summary>Master switch for the solid cloud patches (<c>CloudPatchGenerator</c>).</summary>
    public const bool CloudPatchesEnabled = true;

    /// <summary>Frequency of the 2D noise whose high values outline the cloud patches: patches tens of
    /// blocks across with open sky between them.</summary>
    public const float CloudPatchFrequency = 1f / 90f;

    /// <summary>The patch noise value above which a column holds cloud; higher is sparser.</summary>
    public const float CloudPatchThreshold = 0.3f;

    /// <summary>The patch noise value at which a column reaches the patch's full thickness (the core);
    /// between the threshold and this the cloud thins toward one cell at the edge.</summary>
    public const float CloudPatchCoreValue = 0.6f;

    /// <summary>Frequency of the 2D noise that roughens each patch's thickness and lifts its top, so
    /// the top domes unevenly instead of stepping in rings.</summary>
    public const float CloudPatchDetailFrequency = 1f / 14f;

    /// <summary>Lowest cell a sky island may occupy.</summary>
    public const int SkyIslandBottomY = 320;

    /// <summary>Highest cell a sky island may occupy.</summary>
    public const int SkyIslandTopY = 420;
}
