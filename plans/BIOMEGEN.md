# Biome generation: handoff map and remaining work

> Guide: plans/README.md

## Status

Audience: another model that will modify biome generation. The Reference section documents what
exists **today**: where each rule lives, every tuning constant, what depends on it, and the traps.
Paths are relative to the repo root; code is under `OpenGLTest/src/`. Read `AGENTS.md` ("Biomes",
"Code organization") for the standing rules, and the `biome-worldgen` skill
(`.claude/skills/biome-worldgen/SKILL.md`) for the principles, the new-biome checklist and the
seed-offset and tuning traps before any change here.

Done: the semi-dry ramp, Steppe and Savanna; a domain-warped climate sample; a permafrost tundra with
dead shrubs; a ragged pack-ice edge; drifting icebergs (§11); the terrain-shape phases A to D
(relief highlands and ranges, massifs and lone peaks, islands and archipelagos, great lakes) and
phase E's great-lake river ramps and outflows. Each is documented where it lives: §4 (the island
fold), §6 (each mechanism), §7 (constants), §9 (invariants 11-17), §10 (tests), §11 (limits). Their
perf numbers are in `PERF.md`, their shots in `visual-examples/terrain-shape.json`. Also done: the
cave biomes, a 3D cave biome field under the surface biomes (§12; tracked in `CAVE_BIOMES.md`).
Last updated 2026-11-09.

## In progress

Nothing.

## Open

Phase E polish, none started, in rough priority order. Each keeps the skill's principles and goes
through plan-and-implement as its own phase; knob names are proposals.

- [ ] **Desert mesa or butte.** A flat-topped lone peak on the dune profile (clamp the profile at a hashed cap height). The site helper exists, but a peak must not be summed into sand: massifs and lone peaks reject any ocean or desert weight today (§11).
- [ ] **Plateaus and terraces.** Quantize a low-frequency height term into steps with `smoothstep` risers under a rarity mask, so an area reads as tablelands (badlands in the desert). Lowest priority.
- [ ] **Scree or gravel band** between the rock line and the snow line on a massif.
- [ ] **Archipelago shelf.** A shallower ocean ramp under a chain of islands so the water between them is light and wadeable (§11 notes it is absent).
- [ ] **Coast warp.** A small-amplitude domain warp (about 1/200, a few tens of blocks) of the continent sample alone, for bays, headlands and fjord-like inlets. It must stay out of `SampleClimate` (invariant 8) and re-lays every coast, so it is gated on actually wanting it. Reefs, shipwrecks and deep-sea points of interest stay `P-56` content.

## Reference

### 1. The model in one paragraph

Biome is a **pure function of world (x, z) and the seed**. Three low-frequency OpenSimplex2 noises
(continentalness, temperature, humidity) are each pushed through smoothstep "ramps" into 0..1
weights. Ocean = low continentalness. On land, temperature (three bands: cold / temperate / warm) and
humidity (three bands: dry / semi-dry / wet) form a 3x3 grid:

|               | wet    | semi-dry | dry    |
|---------------|--------|----------|--------|
| **cold**      | Taiga  | Taiga    | Frozen |
| **temperate** | Forest | Steppe   | Steppe |
| **warm**      | Forest | Savanna  | Desert |

Seven `Biome` values: `Forest, Desert, Ocean, Taiga, Frozen, Steppe, Savanna` (`WorldGen/Biome.cs`,
ordinals 0..6, appended, never reordered). Forest needs wet air, so the dry axis reads
desert -> steppe -> tundra (Frozen) and the warm row forest -> savanna -> desert: **no strip of forest
ever separates sand from snow**. Everything is a smooth weight first and only classified at the end,
so heights blend across borders (slopes, never cliffs). Temperature and humidity are sampled at a
**domain-warped** position (one shared low-frequency warp, exactly zero at the origin), so the
climate borders bend and wander instead of tracing the smooth contours of the raw noises.

---------------------------------------------------------------------------------------------------

### 2. Files

| File | Role |
|---|---|
| `WorldGen/BiomeClassifier.cs` | **The core.** `SampleClimate` (the only reader of the temperature and humidity noises and of the climate warp), `BiomeWeights` (two-out and three-out), `Classify`, `ColdOf`, `WarmOf`, `DryOf`, `SemiDryOf`, `WetnessOf`, `ColdWeight`, `WarmWeight`, `DitherBiome`. |
| `WorldGen/BiomeTraits.cs` | Shared yes/no biome rules: `ShowsBareRock`, `RimsWaterWithSand` (near fill + both LOD paths). |
| `WorldGen/TerrainNoise.cs` | Every noise instance; `Reseed()` builds them from `WorldSeed`. `MakeNoise(seedOffset, freq, octaves)`. |
| `Config/WorldGenerationConfig.cs` | All constants (continent/temperature/humidity ramps, ocean, desert, dither, water, snow, relief highlands, mountain ranges, massifs and lone peaks, the relief ceiling, islands and archipelagos, great lakes, openness, dry hills). |
| `WorldGen/TerrainGenerator.cs` | `Compute(chunkX, chunkZ)` builds a `ColumnData`; `LandHeight` blends per-biome height profiles and adds the relief layer's terms and the hashed peaks (through the private `LandHeightCore`); `ReliefLandHeight` (no hashed peaks) and `PreFeatureLandHeight` (massifs, no lone peaks), the profiles the site tests read, and their static `Func` fields `LandHeightAt`/`ReliefHeightAt`/`PreFeatureHeightAt`; `Climate(x,z,out warmth01,out wetness01)`; public forwarders (`BiomeAt`, `BiomeWeights`, `ColdWeight`, `WarmWeight`); `PermafrostBlock`, the Frozen top and soil. |
| `WorldGen/TerrainRelief.cs` | The relief layer (§6): `SpawnClear`, `HighlandWeight` (the only reader of `ReliefNoise`), `HighlandLift`, `HillGain`, `MountainHeight(x, z, highland)` and the range field (`RangeLattice`, `RangeShape`/`RangeShapeAt`, `RangeBlend`, `RangeDirection`, `CellShape`). |
| `WorldGen/TerrainSites.cs`, `TerrainSite.cs` | The shared hashed-site helper (§6): `CellRange`, `TryRoll`, `Wobble`/`InwardWobble`, `RimPoint`/`RimRange`, `YieldsToNeighbor`; `TerrainSite` is one accepted site (`default` = none). Lakes, massifs and lone peaks use it. |
| `WorldGen/TerrainPeaks.cs` | The hashed peaks (§6): `MassifHeight`/`WithMassif`, `LonePeakHeight`/`WithLonePeak`, `SoftCeiling`, the cached verdicts `MassifSiteAt`/`PeakSiteAt` (built by `BuildMassifSite`/`BuildPeakSite`, accepted all-or-nothing on the pre-island ocean weight; an island site, `TerrainSite.OnIsland`, is scaled by the island land weight), `ResetCaches` (from `WorldSeed.Apply`). |
| `WorldGen/TerrainIslands.cs` | The island term (§4, §6): `LandWeight` (the only reader of `IslandNoise`/`IslandSmallNoise`), the stateless hashed archipelago footprints `TryGetArchipelago`/`FootprintWeight`/`ArchipelagoWeight`. No cache. |
| `WorldGen/ColumnData.cs` | Per-chunk-column arrays incl. `Biomes[]`, `CornerWarmth`, `CornerWetness`. |
| `Chunks/ColumnCache.cs` | Column cache + `GenerateColumn` (surface/soil block per biome). |
| `WorldGen/WaterBodies.cs` | Lakes/rivers/sea level; which biomes may hold surface water. Lake candidates roll, wobble and sample their rim through `TerrainSites`. `SampleSurfaceWater` is the one place ponds, rivers and great lakes compose (`WaterLevelAt`, `SurfaceWaterWeight`, `Compute`). |
| `WorldGen/GreatLakes.cs`, `GreatLakeSite.cs`, `GreatLakeLobe.cs` | The great-lake tier (§6): the cached verdict `SiteAt` (built by `BuildSite`), the per-column `Sample` (bowl weight, surface, floor, ravine guard, river level, river bank, outflow weight), `RiverLevelAt` (the river ramp), `RampRiverBeside` (the bank-raise support test), `RiverShare`, `NearAnyFootprint` (the pond yield), `ResetCaches` (from `WorldSeed.Apply`); `GreatLakeSite` is one cell's verdict (`default` = none) with its ramp length and outflow channel, `GreatLakeLobe` one disc of its outline or its island. |
| `WorldGen/Features/SnowGenerator.cs` | Snow, ice, `ColdWeightAt` (cold + altitude), `IsFrozenWater` (the pack-ice rule the far LOD shares). |
| `WorldGen/Features/*`, `WorldGen/Features/Underground/*` | Per-biome vegetation/scenery (see §8). |
| `Simulation/MobSpawner.cs` | `SpawnsMobs(biome)`: which biomes ring-spawn mobs. |
| `assets/data/trees/*.json` | `worldgen.biomes` per tree species. |
| `Effects/Climate/BiomeTint.cs`, `EyeClimate.cs` | Grass/foliage color from climate (not from `Biome`). |
| `Effects/Post/BiomeGrade.cs` | Post-process color grade from climate. |
| `Lod/LodMeshBuilder.cs`, `Lod/LodFaceEmitter.cs` | Far-terrain LOD wears biome-specific tiles. |
| `OpenGLTest.Tests/WorldGen/BiomeTests.cs` | The rule tests (see §10). |
| `OpenGLTest.Tests/WorldGen/BiomeClimateTests.cs` | The climate-sample rules: one warped sample behind every reader, the warp's stretch bound (§10). |
| `WorldGen/Caves/*` | The cave biomes (§12): the field and its point query (`CaveBiomes`, `CaveBiome`, `CaveCellRole`), the per-chunk map (`CaveBiomeMap`), the palettes (`CaveBiomePalette`), the cave shape (`CaveShape`), the ore richness (`CaveRichness`), the exclusive ores (`CaveOres`) and the eye's biome probe (`EyeCaveBiomeProbe`). |

---------------------------------------------------------------------------------------------------

### 3. The noises (`TerrainNoise.Reseed`)

All built by `MakeNoise(seedOffset, frequency, octaves)`: OpenSimplex2, seed = `WorldSeed.Value + offset`,
FBm when `octaves > 1`. Output is roughly [-1, 1] (single octave spans the full range; FBm is scaled
down, which matters for ramp thresholds).

| Noise | Seed offset | Frequency (wavelength) | Octaves | Used for |
|---|---|---|---|---|
| `ContinentNoise` | 70 | 1/2800 | 2 | ocean vs land |
| `TemperatureNoise` | 71 | 1/5500 | **1** | cold/warm ramps |
| `HumidityNoise` | 88 | 1/1700 | 2 | dry and semi-dry ramps |
| `ClimateWarp` | 151 | 1/1400 | 1 (warp) | displaces the temperature and humidity sample position |
| `BiomeDitherNoise` | 89 | 1/28 | 1 | ragged desert material line |
| `OpennessNoise` | 86 | 1/900 | 2 | forest woodland vs open (trees + hill flattening) |
| `PackIceNoise` | 152 | 1/36 | 2 | floes vs open leads in the pack-ice band (`SnowGenerator.IsFrozenWater` only) |
| `ReliefNoise` | 153 | 1/5000 | 2 | the highland weight (`TerrainRelief.HighlandWeight` only) |
| `MountainSpineNoise` | 154 | 1/1400 | 1 | the spine a mountain range's ridges ride (`TerrainRelief.CellShape`) |
| `MountainRangeCellWarp` | 155 | 1/3000 | 1 (warp) | jitters only the range-cell lookup (`TerrainRelief.RangeLattice`) |
| `IslandNoise` | 159 | 1/1200 | 2 | large islands (`TerrainIslands.LandWeight` only) |
| `IslandSmallNoise` | 160 | 1/420 | 2 | small islands, eased in by the archipelago footprints (`TerrainIslands.LandWeight` only) |
| `GreatLakeShapeNoise` | 161 | 1/120 | 2 | pulls a great-lake lobe's outline inward into bays (`GreatLakes.Sample` only) |

The range ridge itself is the older `MountainNoise` (3 octaves at `MountainFrequency`) under
`MountainWarp`, gated by `MountainMask` (offset 84); those keep their offsets and frequencies.

**Trap:** `SnowGenerator.DriftNoise` also uses seed offset **89** (`TerrainGenerator.MakeNoise(89, ...)`),
the same as `BiomeDitherNoise`. Different frequencies, so not identical fields, but the same seed base.
Do not "fix" it without knowing it re-lays snow drift (or dither); it is not a bug today.

`ClimateWarp` is not a value noise but an OpenSimplex2 **domain warp** (`MakeWarp`, like `HillWarp`,
`RiverWarp` and the rest): `ClimateWarp.DomainWarp(ref x, ref z)` moves a position. One warp is shared
by both climate channels, so a border bends the same way in temperature and humidity, and it is read
only inside `SampleClimate`. Its amplitude constant is FastNoiseLite's `SetDomainWarpAmp` value, not
blocks: the peak displacement in blocks grows linearly with it but stays well under it. Offset 151 is
clear of every other channel (this class uses at most 90 besides it and `PackIceNoise`'s 152, the
feature generators and caves at most 142). `PackIceNoise` (152) is not a climate channel: it never
moves a biome border, it only decides ice versus open water inside the pack-ice band (§6).

**Trap:** OpenSimplex is exactly 0 at the lattice origin whatever the seed, so the origin's raw
temperature and humidity are both 0. The semi-dry ramp's midpoint sits just below 0 so the origin
stays Forest (`BiomeTests.OriginColumnIsForest`). The climate warp keeps that: its displacement at the
origin is exactly zero too (`TheWarpStretchesTheClimateBoundedly` asserts it), so the origin samples
the raw noises at (0, 0). A change that moves the origin's sample position, or a semi-dry ramp whose
midpoint reaches 0, breaks the test, which should then be relaxed to its real rule ("the origin is a
land biome that spawns mobs"), never tuned toward one seed.

Changing any frequency, octave count, warp amplitude or seed offset **re-lays the whole world** for
existing seeds. Temperature was deliberately made single-octave (the extra octave steepened the
gradient and squeezed the temperate band between snow and sand), which also set the share of the world
that is cold, and its frequency was lowered from 1/4500 to 1/5500 with phase 2 to leave the warp room
inside the desert/snow separation (§9).

---------------------------------------------------------------------------------------------------

### 4. Weight pipeline (`BiomeClassifier.BiomeWeights`)

Notation: `smooth(a, b, v)` = smoothstep 0 at `v<=a`, 1 at `v>=b`.

```
continent   = ContinentNoise(x,z)
pre         = 1 - smooth(ContinentOceanStart=-0.30, ContinentOceanEnd=-0.04, continent)   // PreIslandOceanWeight, 1 = deep ocean
island      = TerrainIslands.LandWeight(x, z, pre)        // exactly 0 when pre <= OceanShoreWeight or SpawnClear == 0
ocean       = island > 0 ? pre * (1 - island) : pre       // the folded weight every consumer reads
                                                           // (the rest is WeightsForOcean(x, z, ocean, ...))

SampleClimate(x, z, out t, out h)       // the only reader of TemperatureNoise / HumidityNoise / ClimateWarp:
                                        //   (wx, wz) = ClimateWarp.DomainWarp(x, z); t = Temperature(wx, wz); h = Humidity(wx, wz)
cold        = 1 - smooth(TemperatureColdStart=-0.35, TemperatureColdEnd=-0.05, t)          // ColdOf
warm        =     smooth(TemperatureWarmStart=+0.05, TemperatureWarmEnd=+0.35,  t)          // WarmOf
dry         = 1 - smooth(HumidityDryStart=-0.35,     HumidityDryEnd=-0.05,      h)          // DryOf
semiDry     = 1 - smooth(HumiditySemiDryStart=-0.05, HumiditySemiDryEnd=+0.03,  h)          // SemiDryOf

desertWeight = min(warm, dry) * (1 - ocean)          // drives dune height blend, water fade, dither
aridWeight   = semiDry * (1 - ocean)                 // flattens dry and semi-dry land into plains

Classify(ocean, cold, warm, dry, semiDry):
  ocean >= OceanShoreWeight (0.35)            -> Ocean
  else cold >= ColdBiomeMinCold (0.70)        -> dry >= 0.5 ? Frozen : Taiga
  else warm >= 0.5                            -> dry >= 0.5 ? Desert : semiDry >= 0.5 ? Savanna : Forest
  else                                        -> (semiDry >= 0.5 || dry >= 0.5) ? Steppe : Forest
```

Ordering matters: ocean beats everything, then the cold biomes, then the warm row, else the temperate
row. Desert is still exactly `min(warm, dry) >= 0.5`.

**The island fold (phase C).** `BiomeWeights` is three steps: `PreIslandOceanWeight` (the old
ocean expression, verbatim), `TerrainIslands.LandWeight` of it, and `WeightsForOcean` (the climate,
the desert and arid weights and `Classify`, verbatim) over the folded weight. `LandWeight` returns
0 before any noise read or hash when `pre <= OceanShoreWeight`, then 0 when `TerrainRelief.SpawnClear`
is 0; otherwise it is `min(1, max(large, small) * smooth(OceanShoreWeight, 1, pre) * clear)`, where
`large = smooth(IslandLargeStart, IslandLargeEnd, IslandNoise) * IslandLargeGain` and
`small = smooth(IslandSmallStart - drop, IslandSmallEnd - drop, IslandSmallNoise) *
lerp(IslandSmallGain, ArchipelagoSmallGain, a)` with `drop = ArchipelagoThresholdDrop * a` and `a` the
archipelago weight (`ArchipelagoWeight`: the max over the reaching cells of one hashed elongated
ellipse per `ArchipelagoCellBlocks` cell, 1 over its `ArchipelagoCoreShare` core, easing to 0 at its
rim). The multiply happens only when the island weight is above 0, so land, the coast band and the
spawn keep their bits. Every consumer of the ocean weight (the classification, the desert and arid
weights, the ocean lerp and shore clamp, the ravine and entrance fades, water bodies, sea stacks,
trees, mob spawns, the far LOD) sees the one folded weight with no edit of its own, so island land is
ordinary land of its climate biome. `ContinentNoise` is unchanged and `SampleClimate` is not warped
by it.

**Great lakes (phase D) only read the pipeline.** A great lake never changes any weight: its
verdict reads the folded ocean weight, the desert weight (through `SurfaceWaterGate`) and the biome
of every rim and interior sample (`LandHeight`'s outs), and refuses a site that touches any ocean or
desert weight or a biome that holds no surface water (§6). A great lake therefore never meets the
sea or the desert line and never moves a biome border.

The warp sits inside `SampleClimate`, before every reader: `BiomeWeights`, `ColdWeight` (so the snow
and ice lines through `SnowGenerator.ColdWeightAt`), `WarmWeight`, `DitherBiome`'s binding-ramp label,
`HotSpringGenerator` and `Climate` (§5) all see the same warped sample. Continentalness is not warped,
so coasts keep their shape; only the climate borders move.

Key structural facts:

* **Three temperature bands on raw noise `t`:** cold ramp `[-0.35, -0.05]`, temperate gap
  `(-0.05, +0.05)` and beyond (warm ramp starts at +0.05), warm ramp `[+0.05, +0.35]`. The ramps must
  not overlap (test `TemperatureRampsAreExclusiveWithATemperateBandBetween`).
* **Three humidity bands on raw noise `h`:** the dry ramp `[-0.35, -0.05]` and the semi-dry ramp
  `[-0.05, +0.03]`, wholly on its wet side. They are exclusive (test `HumidityRampsAreExclusive`), so
  `semiDry` is 1 wherever any dryness is. Bands: dry = `dry >= 0.5`; semi-dry = `dry < 0.5` and
  `semiDry >= 0.5`; wet = the rest. The semi-dry ramp is narrow (0.08 of noise space, max smoothstep
  slope 1.5/0.08 = 18.75 per unit humidity); `AridWeightIsSmooth` bounds the arid weight's per-block
  step by that slope (a product-rule bound with the measured ocean change).
* Taiga/Frozen need `cold >= 0.70`, i.e. firmly cold, not just on the ramp. Desert and Savanna need
  `warm >= 0.5` (ramp midpoint, `t ~ 0.20`); Desert also `dry >= 0.5` (`h ~ -0.20`).
* Everything between the cold line and the warm midpoint is **Forest (wet) or Steppe (semi-dry,
  dry)** by construction. That is what keeps dunes from touching snow, and what makes the dry corridor
  between them steppe instead of forest. The test `DesertAndSnowAreNeverNearAndTheDryCorridorIsSteppeOrTundra`
  pins a minimum distance (the full-detail load radius, `ChunkRadius * 16`) between "would be desert
  if fully dry" and any snow, and asserts every dry land column classifies Desert, Steppe or Frozen.
  `TheDryAxisNeverClassifiesForest` locks the grid over a lattice of (t, h).
  **To widen the temperate band: raise both `TemperatureWarm*` constants together (keeps the dune fade
  width). Never move the cold ramp.**
* `BiomesEnabled` (const) zeroes the weights and forces Forest (written as a multiplier on purpose, so
  the compiler doesn't flag dead branches; keep that style for new const toggles).
* **Taiga vs Frozen split is the dry ramp only**, at `dry >= 0.5`; a semi-dry cold column is taiga.
* There is **no** per-biome noise besides these. Taiga/Frozen/Forest/Steppe/Savanna all ride the
  **forest height profile**; only Desert (dunes) and Ocean have their own profiles, and the arid weight
  flattens the forest profile's hills on dry and semi-dry land (§6).

#### Dither (`DitherBiome`)

Only around the desert line, and only inside `|desertWeight - 0.5| < BiomeBorderDither (0.16)`. A
coherent noise (`BiomeDitherNoise`, 28-block wavelength, flips where it is > 0) flips whole patches,
giving a ragged grass/sand line. Rules:

* A **Savanna** column flips to Desert. Savanna is always on the warm ramp, so sand never enters the
  temperate band.
* A **Steppe** column never flips (it is below the warm line: the old "no sand below the warm line").
* A **Desert** column flips to the grass biome across the line it actually borders (the
  **binding-ramp label**): of the two ramps that bind `desertWeight = min(warm, dry)`,
  `WarmOf(t) <= DryOf(h)` (warmth binds) gives Steppe, otherwise (dryness binds) Savanna. Exact away
  from coasts; near a coast the ocean factor also scales `desertWeight`, and a mislabeled column is
  grass either way.
* Forest never borders Desert now, so it and every other biome are returned unchanged.

Only surface *material* flips; height already came from the smooth weight. Result stored in
`ColumnData.Biomes[]`. Note `BiomeAt()` returns the **undithered** classification, while
`ColumnData.Biomes` is **dithered** (and forced to Forest on oases); code that wants "what the world
actually placed" should read `Biomes[]` (the fill does). Classification tests read `BiomeAt`; only
`BiomeBorderDitherFlipsCoherentPatchesNotSingleColumns` reads the dithered biomes (coherence, the
label rule, never a Forest flip).

---------------------------------------------------------------------------------------------------

### 5. Climate axes for color (`TerrainGenerator.Climate`)

A second, continuous read of the same climate sample (`SampleClimate`, warped exactly as the biome's,
so colors and biomes agree), used **only** for tint/grade/aurora, never for placement:

```
warmth01  = 0.5 * (1 - ColdOf(t)) + 0.5 * WarmOf(t)          // 0 fully cold, exactly 0.5 across temperate band, 1 fully warm
wetness01 = WetnessOf(h) = 0.5 * (1 - DryOf(h)) + 0.5 * (1 - SemiDryOf(h))
                                                              // 0 fully dry, exactly 0.5 between the ramps, 1 wet
```

* Evaluated **once per chunk column at its minimum corner** and stored in `ColumnData.CornerWarmth /
  CornerWetness` (so meshers do no noise; multiplayer clients get it streamed). The call must use
  `chunkX << Chunk.SizeShift` as float so it is bit-identical to `BiomeTint.TintsAt` on the direct path;
  the warp is a deterministic function of the same float arguments, so the stored and the direct
  climate stay the same bits (`TheStoredCornerClimateReproducesTheDirectLatticeEvaluation`).
* `EyeClimate.Coldness(w) = clamp01(1 - 2w)`, `Warmness(w) = clamp01(2w - 1)` read it back.
* Wetness by biome: Desert and Frozen sit at or below the dry-ramp midpoint's wetness (about 0.25);
  Steppe and Savanna in the semi-dry band (about 0.25..0.75, 0.5 between the ramps); Forest at or
  above the semi-dry midpoint's. `ClimateAgreesWithTheBiomeClassification` checks it relationally.
* **Palette:** three rows of a cold (warmth 0) and a warm (warmth 1) corner per palette in
  `Config/SurfaceShadingConfig.cs`: `GrassColor{ColdDry,WarmDry}` at wetness 0, `GrassColor{ColdMid,WarmMid}`
  at 0.5, `GrassColor{ColdWet,WarmWet}` at 1, and the same `FoliageColor*` a little darker.
  `BiomeTint.PaletteSrgb` interpolates piecewise (Dry->Mid up to 0.5, Mid->Wet above, both exactly the
  Mid row at 0.5, so continuous), then bilinear on the 16-block lattice. Each Mid corner sits between
  its Dry and Wet corners on the red/green ratio but is yellower than their plain average, so the grass
  dries monotonically and steppe and savanna read as dry grassland (`TheGrassDriesMonotonically`).
* **Grade:** `BiomeGrade.Tint` keeps `min(Warmness, 1 - wetness)` for its warm share. With the two-ramp
  wetness the savanna (Warmness 0.5..1, 1 - wetness 0.25..0.75) **takes up to half to three quarters of
  the warm grade**, and the desert (1 - wetness >= 0.75) a stronger share than before; the temperate
  band stays exactly neutral (its warmth01 is 0.5). Intended: the warm grassland looks warm.
* Grass tuft density (`FoliageGenerator.GrassDensityAt`) also reads wetness, so tufts thin out on the
  steppe and savanna.
* The tint is **not** keyed to the `Biome` enum: two columns of equal climate tint identically. That
  contract matters, because the stored corner climate rides the network and the import files.

---------------------------------------------------------------------------------------------------

### 6. What each biome changes

#### Terrain height (`TerrainGenerator.LandHeight`)
1. Forest profile (all land): `ForestBaseHeight 12 + swell (±6, 1/1400, 2 oct) + lift + hills
   (HillAmplitude 32 * hillMask * hillScale) + valley rough (±2.5) + MountainHeight(x, z, highland)`
   (`reliefForest`), then the hashed peaks: `forestHeight = WithLonePeak(WithMassif(reliefForest))`, where
   `highland = TerrainRelief.HighlandWeight(x, z)`, `lift = HighlandBaseLift * highland` and
   `hillScale = (1 - OpennessHillReduction 0.5 * openness) * (1 - DryHillReduction 0.6 * aridWeight)
   * (1 + HighlandHillGain * highland)`: the steppe, the savanna and the tundra are plains (the
   three-out `BiomeWeights` runs before the hill sum for this), and an arid highland is a lifted
   plateau. The lift sits right after the swell and the gain is the last factor of `hillScale`, so at
   a highland weight of 0 they add an exact `0f` and multiply by an exact `1f` and the float sums keep
   their association.
2. Desert: lerp toward `ForestBaseHeight + swell + lift + DuneNoise * DuneAmplitude(8)` by
   `desertWeight` (the lift too, so a highland desert does not sit in a basin; the hill gain and the
   ranges are forest-profile only and lerp out with the hills).
3. Ocean: lerp toward `OceanShoreHeight 3 → OceanFloorHeight -44` by `ocean^1.35`, minus trenches
   (`TrenchDepth 34`), lerped in by `ocean`.
4. Shore clamp: land held ≥ `SeaLevel+1` (SeaLevel = 0), clamp eased out across `[OceanShoreWeight 0.35, 0.5]`
   of ocean weight so the coast slopes.
5. Lakes/rivers/ravines/oases/hot springs/sea stacks cut after this (`Compute`).
**Relief (`WorldGen/TerrainRelief.cs`, phase A).** `HighlandWeight` is
`smooth(HighlandMaskStart 0.1, HighlandMaskEnd 0.35, ReliefNoise) * SpawnClear`, orthogonal to the
climate (it never moves a biome border; altitude reaches the biome only through rock and snow,
below). `SpawnClear` is 0 within `ReliefSpawnClearStart 1536` blocks of the origin and 1 past
`ReliefSpawnClearEnd 2560`: the spawn region (and almost every visual-examples ground) is the land
without the relief layer, and highlands begin on the horizon the far LOD shows from the spawn. On
the default seed about 29 % of land has a weight of at least 0.5; mean land height is about 13 in
the lowlands and 36 in full highland.
Mountains: `MountainHeight(x, z, highland)` is 0 wherever the highland weight is 0 (it returns
before any mountain noise is read), else `RangeShape(warped x, z) * mask * MountainAmplitude 60`,
where `mask = smooth(MountainMaskStart -0.3, MountainMaskEnd 0.1, MountainMask) * highland`, so a
range rises **only inside a highland** (the rarity mask mostly saturates there and only breaks a
range here and there; the narrow spine is what keeps ranges apart). `RangeShape` makes ranges
**linear**: the plane is cut into `MountainRangeCellBlocks 1536` cells, each with a hashed direction
(`RangeDirection`, `SplitMix64` of the cell and the seed); a cell's shape (`CellShape`) rotates the
point into that direction, divides the along-range coordinate by `MountainRangeStretch 6` and reads
`spine^MountainSpineSharpness * (MountainSpineFloor + (1 - floor) * ridge^MountainSharpness)`, the
ridge the 3-octave `MountainNoise` and the spine the lower-frequency `MountainSpineNoise`, so a range
is a chain hundreds of blocks long rather than a blob. Neighboring cells crossfade only inside
`MountainRangeBlend 0.15` of a cell border (`RangeBlend`), so the field is continuous and most
points evaluate one cell; the cell lookup alone (`RangeLattice`) is jittered by
`MountainRangeCellWarp` (up to roughly 350 blocks) so no straight north-south or east-west seam
shows, while `CellShape` reads the unjittered point. About 13 % of highland carries a range taller
than 15 blocks; the tallest land on the default seed is about 127, under the cloud slab (160).
Rivers dry into washes in a highland, because the lift raises the forest-profile height past
`RiverLowEnd`. A column of a snowless grass biome (`BiomeTraits.ShowsBareRock`:
Forest, Steppe, Savanna) at height ≥ `MountainRockHeight 56` is bare stone (soil 0);
`MountainSnowFullHeight 96` is full snow whatever the climate (this is how a warm biome gets snowy
peaks, via `SnowGenerator.ColdWeightAt = max(cold, altitude01)`).

**Massifs and lone peaks (`WorldGen/TerrainPeaks.cs`, phase B).** Both are hashed sites on the
shared helper `WorldGen/TerrainSites.cs`, which generalizes the lake candidate code:
`TryRoll` (one candidate per cell: `SplitMix64.Hash(cell, Seed ^ salt)`, the chance against unit 0,
the center at unit 1 and 2 inside the cell, the size `unit3^skew`; later rolls of a site read units 4
and up), `CellRange` (the cells whose candidates can reach a point), `Wobble` (the lake's
`nominal * (1 + amp * n)`) and `InwardWobble` (`nominal * (1 - amp * (0.5 + 0.5 n))`, never past the
nominal, so a term is already 0 at its nominal cull radius), `RimPoint`/`RimRange` (the center and 8
rim samples through a static `Func`) and `YieldsToNeighbor` (lowest-hash spacing among the raw
candidates that passed their chance). `WaterBodies.LakeSample`/`TryLakeSite` now run on it and are
bit-identical to before (794482 samples compared, 8752 inside a lake, none differed).
A **massif** (`MassifHeight`, salt "MASS", cell `MassifCellBlocks 1536`) rolls an **absolute summit**
in `MassifMinSummit 96..MassifMaxSummit 128`, and its height is the rise from the relief land at its
center (`ReliefLandHeight`, the land without hashed peaks) to that summit. A candidate is accepted
(`BuildMassifSite`) only when its center is outside the spawn clearing, it wins the
`MassifMinSpacing 720` spacing, its center and all 8 nominal-rim points carry zero ocean and zero
desert weight (whose lerps would dilute it), the rise is at least `MassifMinRise 48`, and the highest
relief land on its rim stays at least that far below the summit. Its body is
`smoothstep(1 - d/r)^MassifSharpness` inside the inward-wobbled radius (pointed, not a cone with a
flat cap), plus spurs on the flanks: `MassifSpurShare * 4 body (1 - body) * (1 - |MassifSpurNoise|)^MountainSharpness`.
A **lone peak** (`LonePeakHeight`, salt "PEAK", cell `PeakCellBlocks 448`) adds a rolled
`PeakMinHeight 28..PeakMaxHeight 56` as `smoothstep(1 - d/r)^PeakSharpness`; it is accepted
(`BuildPeakSite`) only off the spawn clearing, past `PeakMinSpacing 144`, off every massif (the massif
term at its center is 0), on forest-profile climate at its center and rim, where the land **without
lone peaks** (`PreFeatureLandHeight`: relief plus massifs) over its center and 8 rim points spreads at
most `PeakSiteFlatness 6` and stays above the sea, and where that center land plus its height stays at
or under the ceiling knee. Neither acceptance ever reads `LandHeight`, so nothing recurses; lakes read
`LandHeight` and so see the peaks (a pond is rejected beside one), never the reverse. Each site is
added under `SoftCeiling` (the identity up to `ReliefCeilingHeight 148 - ReliefCeilingKnee 20 = 128`,
then an exponential approach to 148 that never reaches it, C1 at the knee), which is only a
backstop: the summit range ends at the knee, so it rounds a spur or odd off-center land, never a
crown. `WithMassif`/`WithLonePeak` return `max(forest, SoftCeiling(forest + add))` and the input
float unchanged (sign of zero included) when the term is 0, and both terms are exactly 0 within the
spawn clearing (checked before any read) and outside every accepted disc. Each cell's verdict is a
`GenerationalCache` entry (`MassifSiteAt`, `PeakSiteAt`), so the 9-sample site tests run once per
cell, not per column; the per-column cost is two `CellRange` scans with one roll per reaching cell,
each culled by distance before the cache is probed. On the default seed, within ±16000: 21 massifs
(rises 55.7..109.9, radii 161..359, the tallest crown land 132.2) and 418 lone peaks (heights
28.0..56.0). Like the ranges, both dry rivers into washes (they raise the forest-profile height past
`RiverLowEnd`) and reach the rock and snow caps through the altitude path.

**Islands and archipelagos (`WorldGen/TerrainIslands.cs`, phase C).** An island is nothing but a
lower ocean weight (the fold of §4), so its height comes from the same lerps: where the folded weight
falls the ocean ramp lerps out toward the forest/dune profile (the island core takes the forest
profile, highland lift and ranges included), and the shore clamp holds land below folded weight 0.35
at `SeaLevel + 1` or above, eased out across 0.35..0.5. That 0.35..0.5 rim classifies Ocean and wears
the shelf's sand (`ColumnCache.GenerateColumn`'s Ocean branch, `IsBeachHeight` heights), so a small
island is a sand-rimmed knoll and a large one carries hills, its climate biome's surface, trees, a
lake or a river (the existing rules, no island gating). Massifs and lone peaks may stand on an island,
but only wholly: a site is accepted all-or-nothing on the **pre-island** weight (a continental site,
center `PreIslandOceanWeight == 0`, needs it 0 at every rim point, so its verdict is exactly the
pre-phase-C one; an off-continent site needs saturated island land, folded weight 0 and no desert,
at its center and rim), and an island site (`TerrainSite.OnIsland`) is multiplied by
`TerrainIslands.LandWeight` at the column, which is exactly 0 wherever `pre <= OceanShoreWeight`, so
an island peak never raises the coast or the continent and fades out where its island does. On the
default seed within ±16000: about 5.5 % of deep ocean (pre >= 0.8) is island land outside
archipelagos and 19 % inside their footprints, 0.5 % of the coast band (0.35 < pre < 0.7), 96
archipelago cells, the highest island land 92.9 and 55 % of island columns at or above
`ForestBaseHeight`; there are 3 island lone peaks within ±20000 and no island massif.

**Great lakes (`WorldGen/GreatLakes.cs`, phase D).** A second lake tier beside the ponds, on
the same hashed-site helper (salt "GLAK", cell `GreatLakeCellBlocks 1280`, `GreatLakeChance 0.3`,
size skew `GreatLakeRadiusSkew 1.4`). A candidate is a union of one to three **lobe** discs
(`GreatLakeLobe`): the main one of radius `GreatLakeMinRadius 120..MaxRadius 400` at the rolled
center, plus each of two extras with `GreatLakeExtraLobeChance 0.6`, set `GreatLakeLobeOffset*`
main radii out with `GreatLakeLobeRadius*` of its radius; each outline is pulled inward by
`TerrainSites.InwardWobbleOf` over `GreatLakeShapeNoise` (161) so the shore has bays. The verdict
(`BuildSite`, cached per cell by `SiteAt` in a `GenerationalCache` of 4 generations, reset by
`WorldSeed.Apply`) checks, cheap rejections first: the roll and the lowest-hash spacing (twice the
largest reach plus the guard, so two footprints never meet); the spawn clearing (no lobe grown by the
guard within `GreatLakeSpawnClearRadius` = `ReliefSpawnClearStart`); the relief landmarks (no accepted
massif or lone-peak disc meets a lobe grown by the guard; read from their caches one way, so nothing
recurses); a dense **rim walk** of `LandHeight` at `GreatLakeRimSpacing 16` over each lobe's shore
annulus (the band, `GreatLakeRimBandShare` of the widest shore ramp in from the wobbled outline out to
the nominal edge, where the bowl weight stays under `WaterBodyThreshold`, i.e. the banks), refused on
any ocean weight, a desert gate under 1, a biome that holds no water or a spread over
`GreatLakeSiteFlatness 20`; and a coarse **interior pass** at `GreatLakeInteriorSpacing 32` with the
same biome checks, refused where interior ground rises more than `GreatLakeInteriorRiseMax 12` over the
highest rim point (the carve would plane it). The surface is the lowest rim point minus
`GreatLakeBankDepth 3`, the floor a rolled `GreatLakeMinDepth 15..MaxDepth 30` below it, and the shore
ramp share widens from `GreatLakeShoreShareMin 0.2` to `Max 0.4` with the rim spread. A **cutting
river** (river weight above 0 on lowland that has not dried into a wash) anywhere in a lobe's shore
annulus makes the lake **river-fed**; a river-fed lake whose rim-chosen surface lies below `SeaLevel`
is refused. What happens next is phase E's **river ramp** (below): a lake over the sea that can carry
one keeps its own surface, and a river-fed lake that cannot falls back to the phase D rule, resting at
`SeaLevel`, the river's own waterline, refused when its rim-chosen surface lies more than
`GreatLakeRiverFedMaxDrop 12` above it. An **island** (`GreatLakeIslandChance 0.5`, radius
`GreatLakeIslandMinRadius 18..MaxRadius 60`) rolled into the main lobe's dry-of-shore core is kept
only where its whole disc is lake-holding land at least a bank above the surface and no river cutting
toward the lake's river level (its surface when ramped, else `SeaLevel`) crosses it; a dropped island
leaves the lake accepted.

**River ramps and outflows (phase E).** A lake standing above `SeaLevel` is offered a ramp of
`RampLength = (Surface - SeaLevel) * GreatLakeRiverRunBlocks 6` blocks. Past its lobes the **river
level** is one function, `GreatLakes.RiverLevelAt(site, beyond)`: the surface where `beyond`
(`GreatLakes.Beyond`, the distance past the nearest lobe's nominal edge, 1-Lipschitz) is at most 0,
else `max(SeaLevel, Surface - floor(beyond / Run))`, so it steps down one block every `Run` blocks,
never rises away from the lake, differs by at most a block between 4-neighbors and is exactly
`SeaLevel` past the ramp. `GreatLakes.Sample` hands it out as `riverLevel` (`SeaLevel` everywhere
else) and `WaterBodies.SampleSurfaceWater` passes it on, so the three readers agree by construction:
`Compute`'s river carve targets `riverLevel - RiverBedDepth`, with the lowland fade
(`WaterBodies.RiverLowland(forestHeight, riverLevel)`) measured above the river's own level, so a
river dries into a wash relative to the step it runs on; `WaterLevelFor` stores that level as the
column's `WaterLevel`; and `WaterLevelAt` (with it the far-LOD sheet) reports the same. The ramp is
kept only when it **fits** (`RampFits`: at most `GreatLakeRampMaxLength 192`, the site's reach plus
`GreatLakes.RiverReach` (= max ramp + 1 + the guard, 217) within `MaxReach + Guard` so two footprints
still never meet under the lowest-hash spacing, and out of the spawn clearing) and every
noise-river sample on it stands in lake-holding, ocean-free, full-gate ground at least
`GreatLakeRiverBankRise 2` above its own level, at the point and 2 blocks to either side
(`RiversHoldOnRamp`). A ramped lake may also get an **outflow** (`TryOutflow`): over every lobe's
Radius + 1 ring the candidates outside every other lobe whose direction moves away from every other
lobe center (`OutflowRingCount`, `OutflowRingPoint`, `IsOutflowCandidate`), it takes the one on the
lowest `LandHeight` (the first on a tie) and runs a straight channel radially out from it,
`GreatLakeOutflowHalfWidth 6` wide, starting back in the lake (where `RiverShare` is 0) and running
down the same ramp. Its length is capped at `RampLength - HalfWidth - 1` so it lies wholly in the bank
zone, and is cut short at the first corridor sample whose cross section fails the bank-rise checks or
whose axis does not carve (`RiverLowland > 0` at its level); under `GreatLakeOutflowMinLength 24`
there is none. `GreatLakes.OutflowWeight` is a parabola across the channel tapering shut over its last
`GreatLakeOutflowTaperBlocks 16`, folded into the river weight before `RiverShare`. A river-fed lake
keeps its ramp (and its surface) whether or not it got an outflow; a rim-fed lake keeps a ramp only to
carry an outflow. The verdict reads `LandHeight`, `RiverWeight` (pure noise) and the relief caches one
way, never `Sample`, a pond or `ColumnCache`, so nothing recurses.

A step contour of the ramp would otherwise leave a dry column one block under its wet neighbor (a
shallow channel edge at the lower level beside water at the upper one), i.e. an exposed one-block
water wall. `Sample` also returns `riverBank`, `RiverLevelAt(site, beyond - 1)` inside the bank zone
(`beyond < RampLength + 1`) and `NoWater` elsewhere: the highest level any neighbor can hold. `Compute`
raises a column left dry after the carve (int height at or above its stored level) and lower than
`riverBank` to it, but only when `GreatLakes.RampRiverBeside` finds ramp river or outflow weight in a
4-neighbor (a lazy re-scan of the candidates, paid only on that rare path). The rule only raises dry
ground, so it opens no leak, and it also covers banks the build-time sampling missed. The ramp's
ravine guard stays at 1 out to `RampLength + 1` and eases out across the guard band past it, so a
raised bank is never cut again.
The per-column `GreatLakes.Sample` rolls only the cells that can reach the column and culls them by
distance before probing the cache; inside a footprint it reads the shape noise once and returns the
bowl weight (the max over lobes of `smooth(0, shoreShare, 1 - d/R)`, dipped to 0 over the island by
`GreatLakeIslandShoreShare`), the site's surface and floor, and a **ravine guard** (1 inside a lobe
outline, easing to 0 `GreatLakeRavineGuardBlocks 24` past it); for a ramped site, culled by its reach
plus `RiverReach`, it also returns the river level, the river bank and the outflow weight above, and
the ramp's guard. `WaterBodies.SampleSurfaceWater` is the one place the tiers compose: it folds the
outflow into the river weight, and where the great-lake weight is above 0 it replaces the pond fields and
the river weight is multiplied by `GreatLakes.RiverShare` (`1 - smooth(0, WaterBodyThreshold, w)`,
exactly 0 wherever lake water stands, continuous, so a channel ends without a step); `WaterLevelAt`,
`SurfaceWaterWeight` and `Compute` all read it, so the stored `WaterLevel`, the flood fill, the
far-LOD sheet, the shore sand and the carve agree by construction (`ColumnCache` and `LodMeshBuilder`
are untouched). A pond yields at site level: `TryLakeSite` first refuses a pond
`GreatLakes.NearAnyFootprint` finds within its radius plus `GreatLakePondClearance 16` of a lobe grown
by the guard, or by `RiverReach` for a ramped lake (its whole footprint, ramp included), so no column
belongs to both tiers and no surface splits. `Compute` feeds `guard * WaterBodyThreshold` into
`RavineCutFade`, so a ravine has no cut at the outline and tapers in across the guard band instead of
cutting a bank or an island below the surface. On the default seed within the cells covering ±17000:
7 accepted of 234 rolled, all standing above `SeaLevel` (surfaces 2..8) and all ramped (ramps 12..48
blocks), 5 of them river-fed and 3 with an outflow (29..41 blocks; cells (-8, -8) and (13, -7) are
rim-fed lakes kept ramped for theirs, (4, 3) is river-fed with one), so no lake in that box falls
back to `SeaLevel`; four of the seven are cold (Taiga or Frozen) and freeze into the pack ice like any
water, ramp rivers included. Over ±23000 there are 14 accepted sites, 13 of them ramped.

#### Surface/soil blocks (`ColumnCache.GenerateColumn`)
| Biome | Top | Soil | Notes |
|---|---|---|---|
| Forest, Taiga, Steppe, Savanna (default case) | grass (dirt if wet column) | dirt `DirtDepth` | sand/gravel on beach height **only** where `SurfaceWaterWeight > 0` (`BiomeTraits.RimsWaterWithSand`) |
| Desert | sand | sandstone, `SandstoneDepth 4` | soil 0 if ravine-stripped |
| Ocean | sand ≥ -4, gravel ≥ -16, else stone | same | soil depth 2 |
| Frozen | permafrost | permafrost, depth 2, then stone | soil 0 (bare stone) if ravine-stripped; the snow pass covers it; frozen lake beds wear permafrost too |

Taiga uses the **default** (grass) branch: its "snowiness" comes entirely from `SnowGenerator`, not the
biome. Steppe and Savanna differ from Forest only in tint, trees and flora. `permafrost` is a JSON
block (`assets/data/blocks/permafrost.json`, `hit_rock`, hand-diggable, drops itself) whose tile is committed art.

#### Water (`WaterBodies`)
* Ocean: level = `SeaLevel`. Lakes/rivers: every land biome but the desert (`CanHoldSurfaceWater`:
  Forest, Taiga, Frozen, Steppe, Savanna). Desert dry.
* `SurfaceWaterGate(desertWeight)` fades lakes/rivers out over `desertWeight 0.3..0.5`, so ponds close on
  steppe or savanna ground before the desert line (an exact line on both humid and temperate borders).
* Lakes: hashed 192-block cells, `LakeChance 0.35`, radius 11..46, depth 3..13. Rivers: warped ridge,
  `RiverBedDepth 3` under their own level, dry out as forest-profile height rises above that level
  (`RiverLowStart 14 .. RiverLowEnd 30`).
* Island land (phase C) is land of its climate biome, so it holds lakes and rivers under the same
  rules; nothing gates them on islands (§11).
* Great lakes (phase D): hashed 1280-block cells, `GreatLakeChance 0.3`, one to three lobes with a
  main radius 120..400, depth 15..30, the rim-chosen surface; composed with ponds and rivers in
  `WaterBodies.SampleSurfaceWater` (above).
* Rivers stand at `SeaLevel`, except on an elevated great lake's ramp (phase E), where their level
  steps down from the lake's surface (`GreatLakes.RiverLevelAt`); an outflow channel runs down the
  same ramp. A river-fed lake whose ramp cannot be kept rests at `SeaLevel`.

#### Snow/ice (`SnowGenerator`, reads `ColdWeightAt`, **not** the biome)
* `SnowColdThreshold 0.55`: snow layer placed above this cold weight (depth grows with cold, noise drift,
  max 8 layers).
* **Pack ice.** Surface water (the sea, lakes and rivers alike) freezes by one pure function of
  (x, z, seed), `SnowGenerator.IsFrozenWater(x, z, cold)`, fed the column's `ColdWeightAt`:
  open water at or below `PackIceColdStart 0.6`; a solid sheet past `FreezeColdThreshold 0.72`; in
  between, a column freezes where the coherent floe field `0.5 + 0.5 * PackIceNoise` lies under
  `smoothstep(PackIceColdStart, FreezeColdThreshold, cold)`, so the edge is floes and open leads whose
  ice share rises with the cold until they close into the sheet (a dither of the freeze line, as
  `DitherBiome` raggeds the desert line). For a fixed column the result is monotone in the cold. Hot
  springs never freeze: `HotSpringGenerator.IsSite` is tested **last**, only for a column that would
  otherwise freeze, so the LodGen threads never pay it per cell. Snow follows the ice: `Stamp` skips
  an open column's snow cell, and every ice cell lies above the snow line because
  `PackIceColdStart >= SnowColdThreshold`.
* The near stamp and the generated far LOD's `_iceTop` call the same function, so their edges agree
  cell for cell (`PackIceTests.TheFarLodSheetAgreesWithTheNearIce`). The far LOD's `_snowTop` stays the
  plain snow line (it also decides snow on land); the ordering above is what makes every frozen sheet
  cell snow-covered there too.
* Because Taiga/Frozen need `cold >= 0.70`, snow starts (0.55) slightly **before** the biome line,
  floes appear in the band from 0.6, also before it, and the sheet is solid only past 0.72, just
  **after** it: a taiga column at cold 0.70..0.72 has snow but may still hold open leads, and the cold
  edge of a steppe wears snow, and its water the first floes, before the tundra starts.
* Cold trees get a snow dusting (`SnowCanopyTests`).

#### Vegetation and mobs
* Trees (JSON `worldgen.biomes`): oak = forest; spruce = taiga; cactus = desert; dead_tree = desert,
  frozen, steppe (cactus wins the desert: `TreeGenerator.SpeciesByBiome` takes the first non-oasis
  species per biome in ordinal name order); stone_pine = savanna (sparse; its umbrella crown stands in
  for an acacia); palm = desert oases only; baobab grown only.
* Flowers: open Forest or Steppe (`FlowerGenerator.IsOpenMeadow`, `ForestOpenness >= MeadowMinOpenness`).
* Grass tufts: Forest, Steppe, Savanna (`FoliageGenerator.TryGetGrassTuft`), density by wetness.
* Dead shrubs: sparse patches on desert sand and on Frozen permafrost (`FoliageGenerator.TryGetDeadShrub`;
  `Stamp` pairs each ground with its biome, sand only in a Desert column and permafrost only in a Frozen
  one). A tundra shrub replaces the snow cell the snow pass laid over permafrost (see §11).
* Cotton: Savanna centers with `desertWeight < CottonMaxDesertWeight` (= 0.5 - `BiomeBorderDither`, clear
  of the savanna/sand dither).
* Boulders: Forest, Taiga, Steppe, Savanna floor.
* Mobs: Forest, Steppe, Savanna (`MobSpawner.SpawnsMobs`).

---------------------------------------------------------------------------------------------------

### 7. Tuning constants (current values)

`Config/WorldGenerationConfig.cs` unless noted.

```
SeaLevel 0                    BeachAboveSeaLevel 2          BeachBelowSeaLevel 3
ContinentFrequency 1/2800     ContinentOceanStart -0.30     ContinentOceanEnd -0.04
OceanShoreWeight 0.35         OceanShoreHeight 3            OceanFloorHeight -44      OceanDepthExponent 1.35
OceanSandDepth 4              OceanGravelDepth 16
TemperatureFrequency 1/5500   TemperatureColdStart -0.35    TemperatureColdEnd -0.05
TemperatureWarmStart 0.05     TemperatureWarmEnd 0.35       ColdBiomeMinCold 0.70
HumidityFrequency 1/1700      HumidityDryStart -0.35        HumidityDryEnd -0.05
HumiditySemiDryStart -0.05    HumiditySemiDryEnd 0.03       DryHillReduction 0.6
ClimateWarpFrequency 1/1400   ClimateWarpAmplitude 360 (a SetDomainWarpAmp value, not blocks)
DuneFrequency 1/170           DuneAmplitude 8               SandstoneDepth 4
BiomeBorderDither 0.16        BiomeDitherFrequency 1/28
WaterDesertFadeStart 0.3      WaterDesertFadeEnd 0.5        WaterBodyThreshold 0.05
SnowColdThreshold 0.55        FreezeColdThreshold 0.72      SnowMaxDepth 8   SnowFrequency 1/140
PackIceColdStart 0.6          PackIceFrequency 1/36         (rule: SnowColdThreshold <= PackIceColdStart < FreezeColdThreshold)
OpennessFrequency 1/900       OpennessStart 0.0             OpennessEnd 0.45  OpennessHillReduction 0.5
MountainRockHeight 56         MountainSnowFullHeight 96
ForestBaseHeight 12           HillAmplitude 32              MountainAmplitude 60      MountainSharpness 3
ReliefFrequency 1/5000        HighlandMaskStart 0.1         HighlandMaskEnd 0.35      HighlandsEnabled true
HighlandBaseLift 16           HighlandHillGain 1.0          ReliefSpawnClearStart 1536  ReliefSpawnClearEnd 2560
MountainMaskStart -0.3        MountainMaskEnd 0.1           (rarity mask, times the highland weight)
MountainRangeCellBlocks 1536  MountainRangeBlend 0.15       MountainRangeStretch 6
MountainSpineFrequency 1/1400 MountainSpineSharpness 2.5    MountainSpineFloor 0.3
MountainRangeCellWarpFrequency 1/3000   MountainRangeCellWarpAmplitude 1600 (a SetDomainWarpAmp value; about 350 blocks)
MassifsEnabled true           MassifCellBlocks 1536         MassifChance 0.35         MassifSizeSkew 1.5
MassifMinRadius 160           MassifMaxRadius 360           MassifMinSummit 96        MassifMaxSummit 128
MassifMinRise 48              MassifSharpness 1.4           MassifMinSpacing 720
MassifShapeFrequency 1/220    MassifShapeAmplitude 0.25     MassifSpurFrequency 1/110  MassifSpurShare 0.25
LonePeaksEnabled true         PeakCellBlocks 448            PeakChance 0.45           PeakSizeSkew 1.3
PeakMinRadius 32              PeakMaxRadius 72              PeakMinHeight 28          PeakMaxHeight 56
PeakSharpness 1.6             PeakSiteFlatness 6            PeakMinSpacing 144
PeakShapeFrequency 1/40       PeakShapeAmplitude 0.25
ReliefCeilingHeight 148       ReliefCeilingKnee 20          (SkyConfig.CloudHeight is 160)
MassifSiteCacheCapacity 1024  MassifSiteCacheGenerations 4  PeakSiteCacheCapacity 4096  PeakSiteCacheGenerations 4
IslandsEnabled true           IslandLargeFrequency 1/1200   IslandLargeStart 0.45     IslandLargeEnd 0.90
IslandLargeGain 1.25          IslandSmallFrequency 1/420    IslandSmallStart 0.55     IslandSmallEnd 0.85
IslandSmallGain 0.8           ArchipelagosEnabled true      ArchipelagoCellBlocks 1792  ArchipelagoChance 0.3
ArchipelagoSizeSkew 1         ArchipelagoMinHalfLength 480  ArchipelagoMaxHalfLength 860
ArchipelagoMinAspect 0.2      ArchipelagoMaxAspect 0.45     (half width over half length)
ArchipelagoCoreShare 0.5      ArchipelagoThresholdDrop 0.45 ArchipelagoSmallGain 1.25
GreatLakesEnabled true        GreatLakeCellBlocks 1280      GreatLakeChance 0.3       GreatLakeRadiusSkew 1.4
GreatLakeMinRadius 120        GreatLakeMaxRadius 400        GreatLakeMinDepth 15      GreatLakeMaxDepth 30
GreatLakeBankDepth 3          GreatLakeSiteFlatness 20      GreatLakeInteriorRiseMax 12
GreatLakeShoreShareMin 0.2    GreatLakeShoreShareMax 0.4    GreatLakeRimBandShare 0.2
GreatLakeRimSpacing 16        GreatLakeInteriorSpacing 32   GreatLakeShapeFrequency 1/120  GreatLakeShapeAmplitude 0.18
GreatLakeExtraLobeChance 0.6  GreatLakeLobeOffsetMin 0.5    GreatLakeLobeOffsetMax 0.9
GreatLakeLobeRadiusMin 0.4    GreatLakeLobeRadiusMax 0.7    (extra lobes, in main radii)
GreatLakeRavineGuardBlocks 24 GreatLakePondClearance 16     GreatLakeRiverFedMaxDrop 12 (the sea-level fallback)
GreatLakeRiverRunBlocks 6     GreatLakeRiverBankRise 2      GreatLakeRampMaxLength 192 (RiverReach = 192 + 1 + 24)
GreatLakeOutflowHalfWidth 6   GreatLakeOutflowTaperBlocks 16  GreatLakeOutflowMinLength 24
GreatLakeIslandChance 0.5     GreatLakeIslandMinRadius 18   GreatLakeIslandMaxRadius 60  GreatLakeIslandShoreShare 0.5
GreatLakeSpawnClearRadius = ReliefSpawnClearStart           GreatLakeSiteCacheCapacity 512  GreatLakeSiteCacheGenerations 4
Noise seed offsets: ReliefNoise 153, MountainSpineNoise 154, MountainRangeCellWarp 155,
  MassifShapeNoise 156, MassifSpurNoise 157 (2 octaves), PeakShapeNoise 158,
  IslandNoise 159, IslandSmallNoise 160 (2 octaves each), GreatLakeShapeNoise 161 (2 octaves),
  BarrierGenerator.PatchNoise 162 (3D, the barrier band's ramps; plans/LAYERS.md),
  HellGenerator.CavernNoise 163 (3D, 2 octaves, the hell caverns; plans/LAYERS.md)
  HellBiomes.BiomeNoise 164 (3D, 2 octaves, the hell biomes), HellBiomes.PoolNoise 165 (3D, the hell pools),
  CloudPatchGenerator.PatchNoise 166 (2 octaves) and DetailNoise 167 (the solid cloud patches; plans/LAYERS.md),
  SkyIslands.ZoneNoise 168, OutlineNoise 169, TopNoise 170, StalactiteNoise 171 (the sky islands),
  CloudSeaGenerator cover 172 (2 octaves) and detail 173 (the cloud sea; plans/LAYERS.md),
  SkyBiomes.BiomeNoise 174 (2 octaves, the sky biomes; plans/LAYERS.md L-13)
  SkyLandmasses warp 175 and 176, hills 177 (3 octaves), keel 178 (the sky landmasses),
  CaveBiomes echo drift 179, deep drift 180, selector 181 (2 octaves; 3D, the cave biomes; plans/CAVE_BIOMES.md),
  NoiseCaves.ShapeScale 182 (3D, the cave sub-biomes' shape scale, NoiseCaves' ninth channel),
  CaveOres amethyst 183, jade 184, cinnabar 185 (3D, the cave biome exclusive ores; plans/CAVE_BIOMES.md CB-2),
  VulcaniteGenerator.VeinNoise 186 (3D, the vulcanite veins in basalt);
  the next free noise offset is 187
Hash salts (no noise, SplitMix64.Hash(cell, Seed ^ salt), so a new one re-lays nothing else):
  ruins 0x5E7A-0x5E7D (RuinPlacement, one per kind), waystone satellites 0x5E7E (WaystonePlacement),
  battle towers 0x5E7F (TowerPlacement, cell 1536; plans/BATTLE_TOWER.md BT-2), tower ruin 0x5E80 (TowerRuin, BT-8),
  cave biome accents 0x43415645 (CaveBiomes.BlockFor, through HellBiomes.Hash)
WorldFeatureConfig: CottonMaxDesertWeight = 0.5 - BiomeBorderDither
PostProcessConfig: BiomeGradeStrength 0.04, ColdStart 0.45, ColdFull 0.85, EaseRate 0.5
SurfaceShadingConfig: Grass/Foliage Color corners (ColdDry/WarmDry, ColdMid/WarmMid, ColdWet/WarmWet),
  BiomeTintTextureGray 98/255
SimulationConfig: MaxLeafDecayBacklogCells 1<<18 (holds the stone pine's and the palm's felled box)
```

`HumiditySemiDry*` was tuned against the origin (raw humidity exactly 0, see §3): with the midpoint at
-0.01 the origin is Forest. After the phase-2 warp, a probe over ±3072 at stride 16 (default seed)
found about Ocean 38 %, Forest 15 %, Taiga 14 %, Steppe 14 %, Desert 9 %, Frozen 7 % and Savanna 3 %
of the samples; savanna stays the rarest land biome, since its semi-dry band is narrow.

`ClimateWarp*` tuning rules: the warp must stretch the plane by at most 2
(`BiomeClimateTests.TheWarpStretchesTheClimateBoundedly`), and the temperate band it squeezes must
still keep the desert/snow load-radius separation (§9, invariant 4), which is the tighter of the two.
At these values the warp moves a climate sample by up to roughly 80 blocks and stretches it by about a
third at most. Raising the amplitude bends the borders further but narrows the band: if the separation
test fails, lower the warp (or lower `TemperatureFrequency`, which widens the band and re-lays the
climate), never the rule. `TemperatureFrequency` went from 1/4500 to 1/5500 for exactly that room.

Relief tuning rules: keep the tallest land below `SkyConfig.CloudHeight`
(`TerrainReliefTests.TheTerrainStaysBelowTheCloudLayer`) and the far LOD under the AddQuad vertex
ceiling over the steepest highland (`LodReliefTests`; highland peaks measured 17580 / 11812 / 9548
vertices at levels 0 / 1 / 2, under the 24576 rent). `MountainRangeStretch` must stay well above 1,
or ranges stop being linear (`RangesRunAlongTheirCellsDirection` fails, correctly); a
`MountainRangeBlend` past about 0.18 needs the core offsets of that test shrunk. Raising
`HighlandMaskEnd` so no column in ±16000 saturates breaks the positive controls of
`TerrainReliefTests` and the region search of `LodReliefTests`. Shrinking `ReliefSpawnClear*` re-lays
the ground under many scenarios (with 48..120, 180 of about 421 vantages moved).

Hashed-peak tuning rules: keep `MassifSpurShare` at or below 0.25, so the center stays the highest
point of the massif term (the flank bump `4 b (1 - b)` peaks at 1 where the body is 0.5); keep
`MassifMaxSummit` at or below `ReliefCeilingHeight - ReliefCeilingKnee`, so the soft ceiling never
shapes a crown (a summit pushed into it reads as a flat cap, which
`TerrainPeaksTests.AMassifsCrownIsAPeakNotAPlateau` catches); keep `ReliefCeilingHeight` below
`SkyConfig.CloudHeight`, so no site pierces the cloud slab. `PeakSiteFlatness` trades count for
flatness: raising it places more lone peaks on rolling ground, lowering it keeps them to true plains
(at 6, 418 within ±16000). If a massif rule test fails, tune `MassifMinRise`, `MassifSharpness`,
`MassifSpurShare`, the summit range or `MassifChance`, never the assertion. The far LOD over a
massif and a lone peak stays well under the vertex ceiling (massif regions 11032 / 9856 / 7756
vertices at levels 0 / 1 / 2, relief 47 / 89 / 111; lone-peak regions 10744 / 5988 / 5600, relief
51 / 62 / 62); if it ever fails, tune the spur and peak shapes, never `LodRegion.Levels`.

Island tuning rules: the island values were tuned from the plan's starting knobs (large 0.30..0.60,
small 0.50..0.75, drop 0.35), which put 17.5 % of deep ocean under islands and 3.1 % of the coast band;
widening a channel's `Start..End` gives gentler flanks and fewer islands. Keep the coast band clear
(island land well under the deep-ocean share there) and keep the far LOD's island shores within
`LodReliefTests.IslandShoresLeaveNoMoreDryPitsInTheFarLodThanAContinentalCoast` (§11): a narrower
ramp steepens the shore and widens the dry ring. `IslandLargeGain` above 1 is what lets a deep core
saturate to whole land, which the island sites need. If the pit fact fails, retune the ramps (or add a
monotone shore-band remap inside `TerrainIslands`), never the LOD builder or the assertion.

Great-lake tuning rules: keep `SmoothStep(GreatLakeRimBandShare) >= WaterBodyThreshold`, the relation
the rim walk and the river scan rely on (the annulus they sample must cover the whole sub-threshold
shore band; `GreatLakeSiteTests.TheRimWalkCoversTheDryShoreBand`), and keep `GreatLakeBankDepth` above
the bank lowering the sub-threshold carve causes (about `WaterBodyThreshold * (bank + MaxDepth)`). The
knobs shipped untuned: `GreatLakeChance`, `GreatLakeSiteFlatness` and `GreatLakeInteriorRiseMax` trade
count for flatness, and the tests need enough accepted sites in ±16000 (at least one river-fed, one
ramped, one with an outflow and one with an island); if a bank-leak fact fails, tighten `GreatLakeRimSpacing` or raise the bank depth,
never loosen the test. A rejected verdict costs about 0.005 ms and an accepted one about 2.1 ms with
its ramp checks (phase D's 1.3 plus about 0.8), paid once per cell (`PERF.md`); raise
`GreatLakeInteriorSpacing` first if it ever grows.

River-ramp tuning rules (phase E): `GreatLakeRiverRunBlocks` must stay at or above 1, since the
one-block-between-neighbors step rule rests on `Beyond` being 1-Lipschitz and the run being at least
a block; `GreatLakeOutflowMinLength` must stay at or above `GreatLakeOutflowTaperBlocks`, or a
minimal outflow would be all taper; and a ramp is kept only while the site's reach plus
`RiverReach` stays within `MaxReach + Guard`, which is what keeps footprints apart under the
unchanged lowest-hash spacing and the `CellRange` unchanged, so `GreatLakeRampMaxLength` is also the
lever on the river cull's cost (it sizes `RiverReach`). With `SeaLevel` 0 the max ramp at a run of 6
serves surfaces 1..32, and a lake reaching more than about 447 blocks keeps the sea-level fallback.
If the default seed's ±16000 scan ever lacks a river-fed lake with a ramp or a lake with an outflow
(the tests' non-vacuity guards), tune in this order: lower the run (not below 3), then the bank rise
(not below 1), then the outflow's minimum length (not below the taper); widen the tests' scan box
rather than weaken a rule, and never loosen a test. The shipped values needed no tuning.

---------------------------------------------------------------------------------------------------

### 8. Everything that branches on biome (the blast radius of adding/changing one)

Grep `Biome\.` to confirm; at time of writing:

* **Classifier:** `WorldGen/BiomeClassifier.cs` (`Classify`, `DitherBiome`), `WorldGen/BiomeTraits.cs`
  (`ShowsBareRock` = Forest/Steppe/Savanna; `RimsWaterWithSand` = Forest/Taiga/Steppe/Savanna).
* **Fill:** `Chunks/ColumnCache.cs` `GenerateColumn` switch (Desert/Ocean/Frozen/default = every grass
  biome); `TerrainGenerator.Compute` rock cap (`ShowsBareRock`); `WorldGen/ChunkTerrainFill.cs:65`
  (oasis columns are forced to Forest fill).
* **Water:** `WorldGen/WaterBodies.cs` (`CanHoldSurfaceWater`: not Ocean, not Desert; Ocean level);
  `WorldGen/GreatLakes.cs` refuses a site whose rim or interior holds a biome failing the same test.
* **Trees:** each `assets/data/trees/<species>.json` `worldgen.biomes` (data-driven; adding a biome to a
  species is JSON). Current: cactus=desert; dead_tree=desert+frozen+steppe; oak=forest; palm=desert
  (oasis only); spruce=taiga; stone_pine=savanna; baobab has no worldgen. The `Biome` enum names are
  parsed lowercase. Per-species `openness` scales `ForestOpenness` thinning (stone_pine 0). A new
  worldgen species must not raise the tree reach without checking `TreeGenerator.ReachChunkRadius`,
  `ProxyReach` and `SimulationConfig.MaxLeafDecayBacklogCells`.
* **Features (per-biome):** `Boulder` (Forest/Taiga/Steppe/Savanna), `Cotton` (Savanna),
  `Flower` (Forest/Steppe), `Foliage` tufts (Forest/Steppe/Savanna; dead shrubs on desert sand and
  frozen permafrost, `TryGetDeadShrub` gated on Desert or Frozen),
  `DesertWell`, `FallenLog` (the hollow log of the biome's species from its `worldgen.fallenLog`:
  Forest oak, Taiga spruce, Savanna stone pine; none where the species declares none),
  `IceSpike` (frozen), `Oasis` (desert, deep), `SeaStack` (ocean), `HotSpring`
  (cold, hashed), `Reed`/`LilyPad`/`Pebble`/`Cairn`/`RuinedPillar`, underground generators.
* **Mobs:** `Simulation/MobSpawner.cs` `SpawnsMobs` (Forest, Steppe, Savanna); a supplied column reads
  `Biomes[i]`, a generated one the undithered `BiomeAt`. **A new biome gets no mobs unless it is added
  there.**
* **LOD:** `Lod/LodMeshBuilder.cs` `ClassifyGenerated` (snow top above `SnowColdThreshold` of
  `ColdWeightAt`, ice top on a water cell exactly where `SnowGenerator.IsFrozenWater` says so,
  beach via `RimsWaterWithSand` (or Ocean), bare rock via `ShowsBareRock` above `MountainRockHeight`,
  water-sheet kind) and `ClassifySupplied` (snow and ice from the stored Taiga/Frozen biome, the ice
  an approximation of the pack-ice band since a supplied column has no cold weight; bare rock via
  `ShowsBareRock` with `DirtDepth == 0`; beaches Ocean-only). Both paths fill the `_soilStripped` grid from the cell-center column's
  `DirtDepth == 0` (the generated path reads the `ColumnData` `Prefetch` already holds), and
  `FloorMaterial` gives a Frozen lake bed code 4, permafrost (2, stone, when stripped).
  `Lod/LodFaceEmitter.cs` maps code 4 to permafrost and orders the soil side band Desert sandstone,
  sea or rock stone, Frozen permafrost (stone when stripped), else the soil; the top chain is
  untouched, since snow always wins on a Frozen cell. Steppe and Savanna fall to the tinted grass top
  and the soil side band like Forest. For a world
  source that *supplies* columns the LOD reads the stored `Biomes`; for generated it re-derives from
  worldgen. **Any biome change must be mirrored in the LOD builder or distant terrain disagrees with
  near chunks.**
* **Net/save:** `ColumnData` biomes ride `ChunkNetCodec.EncodeColumn` and `ColumnFileCodec` (imported
  maps store a biome byte per column). Adding an enum value changes a persisted byte's meaning space;
  append, don't reorder (`Biome` ordinals: Forest 0, Desert 1, Ocean 2, Taiga 3, Frozen 4, Steppe 5,
  Savanna 6; `TreeGenerator.SpeciesByBiome` sizes itself from the enum).
* **Climate-only (not biome):** tint, grade, aurora, snow, ice, grass tuft density. Cold-related visuals
  follow `ColdWeight`.
* **Cave biomes:** `WorldGen/Caves/CaveBiomes.Classify` reads the column's stored, dithered surface biome:
  a Frozen column's caves echo as FrozenEcho, a Desert one's as DesertEcho (§12). Changing how Frozen or
  Desert classify, or the dither, re-lays the echo caves.

---------------------------------------------------------------------------------------------------

### 9. Invariants that must survive any change

1. **Pure function of (x, z, seed)**; thread-safe, no shared mutable state. Noises are built once in
   `Reseed` (from `WorldSeed.Apply`, never while a world is live).
2. **Smooth weights, discrete classification last.** Heights lerp by weights; a border is a slope.
3. **Desert line is exactly weight 0.5** on both the humid and the temperate border; the dune blend,
   `SurfaceWaterGate`, and `DitherBiome` are all keyed to it. Move the line and all three move.
4. **Warm and cold ramps exclusive**, with a temperate gap; **dry and semi-dry ramps exclusive**;
   desert never adjacent to snow within the load radius, and the dry corridor is steppe or tundra
   (`DesertAndSnowAreNeverNearAndTheDryCorridorIsSteppeOrTundra`).
5. `Climate()` stays evaluated at the column's min corner with exact int-to-float args (bit-identity with
   `BiomeTint` direct path; `BiomeTintTests.TheStoredCornerClimateReproducesTheDirectLatticeEvaluation`),
   and it reads the same `SampleClimate` the biome does.
6. Caches (`ColumnCache`, ten `GenerationalCache` sites, the massif, lone-peak, great-lake
   and pond verdicts included) only hold pure functions of the key; a biome
   change is picked up via `WorldSeed.Apply` resets, but existing **saved** chunks keep their old terrain
   (only modified chunks are saved; unmodified regenerate, so a biome change causes visible seams at the
   edge of edited areas in old saves).
7. Content rule: block behavior stays data-driven; prefer a JSON flag over a new `switch` on the biome
   where the question is "what does this block do". Biome-keyed placement is already code + tree JSON;
   a biome rule asked in more than one place belongs in `BiomeTraits`.
8. **Every climate read goes through `SampleClimate`**, the one reader of `TemperatureNoise`,
   `HumidityNoise` and `ClimateWarp`, so the biome, the cold and warm weights, snow, the tint, the
   grade and the aurora all see one warped sample (`BiomeClimateTests.EveryClimateReaderSeesTheOneSample`).
   Never warp anywhere else and never add a second sampler.
9. **The climate warp is bounded:** a unit step in the world moves the warped sample by at most 2
   (a Lipschitz bound on `ClimateWarp.DomainWarp`, `BiomeClimateTests.TheWarpStretchesTheClimateBoundedly`),
   so it can at most halve a band, and its displacement at the origin is exactly zero. Invariant 4 is
   the tighter limit in practice: the squeezed temperate band must still hold the load radius.
10. **Ice is `SnowGenerator.IsFrozenWater`** on the near stamp and the generated far path alike, never a
   second freeze test; `PackIceColdStart >= SnowColdThreshold` (so every ice cell is snow-covered near
   and far) and `PackIceColdStart < FreezeColdThreshold` (so the band exists), both asserted by
   `PackIceTests.IceThickensMonotonicallyWithCold`. The hot-spring test stays last inside it.
11. **Every relief term is neutral at highland weight 0:** `HighlandLift(0)` is exactly `0f`,
   `HillGain(0)` exactly `1f` and `MountainHeight(x, z, 0)` exactly 0, so land with zero weight keeps
   its old bits (`TerrainReliefTests.EveryReliefTermIsNeutralWhereTheHighlandWeightIsZero`). Insert
   any new relief term so that it, too, adds an exact `0f` or multiplies by an exact `1f` there,
   without re-parenthesizing the existing sums. The hashed peaks go one step further:
   `TerrainPeaks.WithMassif` and `WithLonePeak` return their input float itself wherever their term
   is 0, so land outside every site is bit-identical to the land without them
   (`TerrainPeaksTests.LandIsByteIdenticalOutsideEveryMassif`, `LandIsByteIdenticalOutsideEveryLonePeak`).
12. **Ranges rise only inside highlands** (`TerrainReliefTests.MountainRangesRiseOnlyInsideHighlands`),
   run along their cell's direction (`RangesRunAlongTheirCellsDirection`) and never switch cells
   abruptly (`TheRangeFieldNeverSwitchesCellsAbruptly`); the relief is a pure function of position and
   seed (`TerrainReliefSeedTests.TheReliefIsAPureFunctionOfPositionAndSeed`).
13. **The relief fades out within `ReliefSpawnClear*`:** the highland weight is exactly 0 wherever
   `SpawnClear` is (`TerrainReliefTests.TheHighlandWeightIsAUnitWeightThatVanishesAtTheSpawn`), and
   through it every range, massif and lone peak (each term is exactly 0 wherever `SpawnClear` is,
   checked before any other read, and no site's center lies inside it); a future large feature uses
   the same clear.
14. **A site's acceptance never reads `LandHeight`.** A massif reads only `ReliefLandHeight` and the
   climate, a lone peak only `PreFeatureLandHeight`, the massif term and the climate, so neither
   cache factory can recurse into its own kind (passing lone peaks on in the profile a peak factory
   reads would). Lake acceptance reads `LandHeight` and may therefore depend on the peaks, never the
   reverse. The verdict caches are cleared by `WorldSeed.Apply` (`TerrainPeaks.ResetCaches`,
   `TerrainReliefSeedTests.TheSiteCachesFollowTheSeed`).
15. **Islands never touch the continent or the coast band.** Wherever the pre-island ocean weight
   (`BiomeClassifier.PreIslandOceanWeight`) is at or below `OceanShoreWeight`, `BiomeWeights` (every
   out), the massif and lone-peak terms and therefore `LandHeight` are bit-identical to the terrain
   without islands, **by construction**: `TerrainIslands.LandWeight` is exactly 0 there (checked before
   any read), the fold multiplies only when it is above 0, a site is accepted all-or-nothing on the
   pre-island weight (so a continental verdict is the old verdict) and an island site is scaled by that
   same exact-0 weight (`TerrainIslandsTests.NoColumnAtOrInsideTheShoreBandChanges`,
   `TerrainPeaksTests.ASiteNeverStraddlesTheIslandFold`,
   `TerrainPeaksTests.AnIslandSiteNeverRaisesTheShoreBandOrTheContinent`; against the tree before phase
   C, 0 of the 1170724 points of a ±20000 grid at stride 37 changed where the baseline ocean weight was
   at or below `OceanShoreWeight`, and no continental site verdict changed). The term is 0 within
   `ReliefSpawnClear*` (`TheIslandTermVanishesInTheSpawnClearing`), reads no height (so no site
   factory recurses through it), and never touches `ContinentNoise` or `SampleClimate`. The archipelago
   verdict is a stateless hash, so it added no cache site. **Not** claimed
   identical: chunk blocks and lake water near islands, since a tree batch grows every tree of its
   64x64 cell together and a continental lake's rim may reach island land (§11).
16. **Great lakes touch only their footprint.** Every great-lake output (`GreatLakes.Sample`'s
   weight, guard and outflow, `NearAnyFootprint`) is exactly 0, its river level `SeaLevel` and its
   river bank `NoWater`, outside the accepted lobe discs grown by `GreatLakeRavineGuardBlocks` (by
   `GreatLakes.RiverReach` for a ramped lake) and within `GreatLakeSpawnClearRadius`, and
   `SampleSurfaceWater` returns the plain pond and river values there (the float expressions of the
   river carve reduce to the old ones at `SeaLevel`), so land elsewhere is bit-identical to the
   terrain without the tier, except for ponds that yield within pond reach of a footprint
   (`GreatLakeSiteTests.TheSampleIsZeroOutsideEveryFootprintAndNearTheSpawn`; against the tree before
   phase D, 0 of the 1168181 points of a ±20000 grid at stride 37 beyond that footprint changed, and
   against the tree before phase E, 0 of the 1164135 points beyond the ramped footprints plus the
   pond-yield ring, and 0 of 992 chunk columns wholly beyond them, changed).
   **Every bank stands at or above its surface** (`GreatLakeTests.GreatLakeBanksStandAtOrAboveTheWater`,
   `GreatLakeSiteTests.ASitesWaterSitsUnderItsRimAndOverItsFloor`), no river carves where lake water
   stands (`RiverShare`, `GreatLakeTests.NoRiverCarvesInsideAGreatLake`), and **no lake overlaps a
   massif or lone peak** (`GreatLakeSiteTests.NoMassifOrLonePeakStandsInAGreatLake`). The verdict reads
   the relief caches one way and is cleared by `WorldSeed.Apply` (`GreatLakes.ResetCaches`).
17. **A river ramp has one level and never leaks.** The river level is one function,
   `GreatLakes.RiverLevelAt`, read by the carve, `WaterLevelFor` and `WaterLevelAt` alike (so the
   stored `WaterLevel`, the flood and the far-LOD sheet agree); it equals the lake's surface at the
   shore, never rises away from the lake, reaches `SeaLevel` at the ramp's end and differs by at most a
   block between 4-neighbors (`GreatLakeRiverTests.TheRiverLevelStepsDownFromTheSurfaceToTheSea`,
   `NeighboringColumnsInARampDifferByAtMostOneBlock`, `AnOutflowNeverStepsUpAwayFromItsLake`,
   `GreatLakeTests.NoRampWaterStandsMoreThanABlockOverItsWetNeighbor`). A river reaching an elevated
   lake meets it at the lake's own surface (`GreatLakeRiverTests.AnElevatedLakeARiverReachesKeepsItsOwnSurface`,
   `GreatLakeTests.ARiverMeetsItsLakeAtTheLakesOwnLevel`). Dry ground beside ramp water is raised to the
   highest level a neighbor can hold (the bank-raise rule), so every ramp and outflow bank stands at or
   above the water (`GreatLakeTests.GreatLakeBanksStandAtOrAboveTheWater`, which covers ramp rivers);
   every sampled river and outflow point stands `GreatLakeRiverBankRise` over its level
   (`AnOutflowsGroundStandsOverItsLevel`), the outflow leaves through the low side of its lake
   (`AnOutflowLeavesThroughTheLowSideOfItsLake`, `AnOutflowCarriesWaterAwayFromItsLake`), ramps are
   ravine-guarded out to `RampLength + 1`, and the ramp and outflow checks read `LandHeight`,
   `RiverWeight` and the relief caches one way, so nothing recurses.

---------------------------------------------------------------------------------------------------

### 10. Tests and verification

* `OpenGLTest.Tests/WorldGen/BiomeTests.cs` holds the rules, all relational:
  `TemperatureRampsAreExclusiveWithATemperateBandBetween` (between the ramps a column classifies
  Forest when `SemiDryOf < 0.5`, Steppe otherwise), `HumidityRampsAreExclusive`, `TheDryAxisNeverClassifiesForest`,
  `DesertAndSnowAreNeverNearAndTheDryCorridorIsSteppeOrTundra`, `SteppeAndSavannaBothExistNearOrigin`,
  `AridWeightIsSmooth`, `BiomeBorderDitherFlipsCoherentPatchesNotSingleColumns` (with the binding-ramp
  label rule), `EveryTreeGrowsInItsSpeciesBiome` (every land biome a non-oasis species names must grow
  a tree in the window), `SurfaceWaterFadeHasClosedOnEveryDesertColumn`, and the origin rule
  (`OriginColumnIsForest`).
* `OpenGLTest.Tests/WorldGen/BiomeClimateTests.cs` holds the climate-sample rules:
  `EveryClimateReaderSeesTheOneSample` (`ColdWeight`, `WarmWeight`, `Climate`'s warmth and wetness,
  `BiomeWeights` and `BiomeAt` all agree with one `SampleClimate` call, relationally) and
  `TheWarpStretchesTheClimateBoundedly` (the finite-difference Jacobian of `ClimateWarp.DomainWarp` and
  unit steps along both axes and both diagonals stay within a stretch of 2, the warp really stretches
  and shrinks somewhere, and it is exactly zero at the origin).
  Related: `BiomeTintTests` (`ClimateAgreesWithTheBiomeClassification`, `TheGrassDriesMonotonically`),
  `BiomeGradeTests`, `LakeTests`, `SnowCanopyTests`, `SceneryTests`, `CottonTests` (savanna only),
  `FoliageTests`, `DeadTreeTests` (`ItNeverSharesABiomeWithALeafyWorldgenSpecies`), `WorldSourceLodTests`
  (supplied steppe/savanna columns wear grass over dirt like forest; a stripped one shows rock),
  `ClientWorldgenIsolationTests`.
* The tundra (phase 3): `SnowIceTests.FrozenGroundIsPermafrostOverStone` (relational: a stripped
  column's surface is never permafrost, nothing deeper than the 2-cell soil band is permafrost, an
  undisturbed soiled column never tops out in dirt or grass, with non-vacuity counts) and
  `SnowSettlesOnColdDryGround` (skips shrub columns); `FoliageTests.TundraShrubsStandOnPermafrost` and
  the per-cell ground rules of `FoliageStampsOnlyOnItsOwnGround` (snow over permafrost may become a
  shrub, snow over any other ground never does); `WorldSourceLodTests.ASuppliedFrozenColumnWearsPermafrostSoil`,
  `AStrippedSuppliedFrozenColumnWearsStoneSoil` and `AGeneratedFrozenRegionsLodWearsPermafrost` (the far
  LOD agrees on both column paths).
* The pack ice (phase 4): `OpenGLTest.Tests/WorldGen/Features/PackIceTests.cs`:
  `IceThickensMonotonicallyWithCold` (no ice at or below the band, solid past it, a column never thaws
  as the cold rises, the knob ordering of invariant 10), `HotSpringsNeverFreeze`,
  `TheNearSeaCellAgreesWithTheRule` (generated sea cells are ice exactly where the rule says),
  `TheFarLodSheetAgreesWithTheNearIce` (a level-0 region's sheet is open or snow-covered ice cell for
  cell by the same rule) and `TheIceEdgeIsRaggedPatchesNotAContour` (one cold holds both ice and open
  water, neighbors agree well above independent coins, the ice share rises across the band); plus
  `SnowIceTests.ColdOceanFreezesAndWarmOceanStaysLiquid` in band form. `WaterRavineTests` and
  `LakeTests` check their fills only on columns at or below `PackIceColdStart`, where water is
  always open.
* The relief layer (phase A): `OpenGLTest.Tests/WorldGen/TerrainReliefTests.cs`
  (`TheHighlandWeightIsAUnitWeightThatVanishesAtTheSpawn`, `EveryReliefTermIsNeutralWhereTheHighlandWeightIsZero`,
  `MountainRangesRiseOnlyInsideHighlands`, `HighlandsStandAboveTheLowlands`,
  `TheTerrainStaysBelowTheCloudLayer`, `RangesRunAlongTheirCellsDirection`,
  `TheRangeFieldNeverSwitchesCellsAbruptly`), `TerrainReliefSeedTests.TheReliefIsAPureFunctionOfPositionAndSeed`
  (in the `Worldgen seed` collection) and `OpenGLTest.Tests/Lod/LodReliefTests.cs`
  (`TheLodLevelsKeepTheirNestingInvariants`, `TheFarLodSitsInsideTheChunkTerrainOverAHighlandRange`:
  every dry up face of a steep highland-range region sits at or below the lowest of its four
  columns at every level, and the region stays under the AddQuad vertex ceiling).
  `WaterRavineTests.ARavineUnderAWaterBodyStaysWetAndFills` scans ±4096 now, since the highlands dry
  the rivers of its old window.
* The hashed peaks (phase B): `OpenGLTest.Tests/WorldGen/TerrainSitesTests.cs`
  (`CellRangeCoversEveryCellThatCanReachAPoint`, `ARolledCandidateLiesInsideItsCell`,
  `InwardWobbleNeverExceedsItsNominal`, `RimRangeBracketsEverySample`);
  `OpenGLTest.Tests/WorldGen/TerrainPeaksTests.cs` (`LandIsByteIdenticalOutsideEveryMassif`,
  `LandIsByteIdenticalOutsideEveryLonePeak`, `ASiteNeverLowersLand`,
  `TheSoftCeilingIsIdentityBelowItsKneeMonotoneAndBelowTheCeiling`, `AMassifsSummitStandsAboveItsRim`,
  `AMassifsCrownIsAPeakNotAPlateau`, `MassifTermsComeOnlyFromAcceptedSites`,
  `MassifSitesNeverCrowdEachOther`, `MassifSummitsStayUnderTheCloudLayer` (lone-peak cores too),
  `ALonePeakNeverStandsOnGroundThatFailsTheFlatnessTest` (with a positive control: a rolled
  candidate on rough ground really is rejected), `NoLonePeakStandsOnAMassif`,
  `LonePeakSitesNeverCrowdEachOther`); `TerrainReliefSeedTests.TheSiteCachesFollowTheSeed` (after
  every `WorldSeed.Apply` the cached verdicts equal the uncached builders, and the sites move with the
  seed; it is the only guard on the cache reset, since the relief fact passes without it); and
  `LodReliefTests.TheFarLodSitsInsideTheChunkTerrainOverAMassif` (with a summit-exposure check: the
  highest up face of the region holding a massif's crown carries the region's brightest
  `Vertex.Sun`) and `TheFarLodSitsInsideTheChunkTerrainOverALonePeak`.
  `TerrainReliefTests.TheTerrainStaysBelowTheCloudLayer` now accepts the highest land in a highland,
  a massif or a lone peak.
* The islands (phase C): `OpenGLTest.Tests/WorldGen/TerrainIslandsTests.cs`
  (`TheFoldOnlyEverLowersTheOceanWeightThroughTheIslandTerm`, `NoColumnAtOrInsideTheShoreBandChanges`,
  `IslandsRiseOnlyWhereThePreIslandOceanWasPastTheShore`, `TheIslandTermVanishesInTheSpawnClearing`,
  `DeeperOceanNeverHoldsLessIsland`, `AnArchipelagoNeverLowersIslandLand`,
  `AnArchipelagoHoldsMoreSmallIslandColumnsThanTheSameWindowWithoutIt`,
  `AnArchipelagoRunsAlongItsDirection`, `IslandLandIsDryLandOfItsClimate`);
  `TerrainPeaksTests.ASiteNeverStraddlesTheIslandFold` and
  `AnIslandSiteNeverRaisesTheShoreBandOrTheContinent`;
  `TerrainReliefSeedTests.TheIslandTermIsAPureFunctionOfPositionAndSeed`; and
  `LodReliefTests.TheFarLodSitsInsideTheChunkTerrainOverAnIsland` (with the summit-exposure check,
  canopy excluded), `TheFarLodSitsInsideTheChunkTerrainOverAnArchipelago` and
  `IslandShoresLeaveNoMoreDryPitsInTheFarLodThanAContinentalCoast` (the shore-pit metric of §11,
  aggregated over several island and continental-coast regions per level).
* The great lakes (phase D): `OpenGLTest.Tests/WorldGen/GreatLakeSiteTests.cs`, over the accepted
  sites of a ±16000 cell scan (`ASitesWaterSitsUnderItsRimAndOverItsFloor`,
  `NoSiteReachesTheSpawnClearing`, `AcceptedSitesNeverOverlap`, `ALakeStandsWhollyOnLakeGround`,
  `NoMassifOrLonePeakStandsInAGreatLake`, `AnIslandStandsAboveItsLake`,
  `TheRimWalkCoversTheDryShoreBand`, `TheSampleIsZeroOutsideEveryFootprintAndNearTheSpawn`);
  `OpenGLTest.Tests/WorldGen/GreatLakeTests.cs`, over the chunk columns of up to three real lakes (a
  river-fed ramped one and one with an outflow first, windows grown by each ramp) through
  `ColumnCache` (`TheStoredLevelIsTheLevelWaterLevelAtReports`, which pins the near water to the
  far-LOD sheet, `GreatLakeBanksStandAtOrAboveTheWater` with `LakeTests`' leak rule over the lake
  and its ramp rivers, `NoRampWaterStandsMoreThanABlockOverItsWetNeighbor`,
  `NoRiverCarvesInsideAGreatLake`, `TheBowlIsDugDownToItsFloor`, `NoPondShareAGreatLakesGround` (the
  ramp and outflow count as lake ground), `ARiverMeetsItsLakeAtTheLakesOwnLevel` (renamed from
  `ARiverMeetsARiverFedLakeAtItsOwnLevel`), `AnOutflowCarriesWaterAwayFromItsLake`; oasis and
  hot-spring columns are skipped, as the checked column and as a neighbor, since they override the
  stored level by design); `OpenGLTest.Tests/WorldGen/GreatLakeRiverTests.cs`, over the accepted sites
  (`AnElevatedLakeARiverReachesKeepsItsOwnSurface`, `TheRiverLevelStepsDownFromTheSurfaceToTheSea`,
  `NeighboringColumnsInARampDifferByAtMostOneBlock`, `AnOutflowLeavesThroughTheLowSideOfItsLake`,
  `AnOutflowNeverStepsUpAwayFromItsLake`, `AnOutflowsGroundStandsOverItsLevel`);
  `LodReliefTests.TheFarLodSitsInsideTheChunkTerrainOverAGreatLake`;
  and the great-lake cells in `TerrainReliefSeedTests` (`TheReliefIsAPureFunctionOfPositionAndSeed`,
  and `TheSiteCachesFollowTheSeed`, which now also checks `GreatLakes.SiteAt` against `BuildSite`,
  ramp length and outflow length included).
  `TerrainGenerator.Compute` identity is not a test: it was a manual diff against a copy of the tree
  before phase D, and again before phase E (`PERF.md`).
* Visual: `visual-examples/terrain-shape.json` (`VOXEL_TIME=1090`, shortly after a sunrise, since the
  run lasts about eight minutes and outlasts the afternoon from 147; the origin valley, a
  forest highland, a range along and across its direction, an arid plateau, a highland border, a
  river wash, a range across a cell border from above, a far-LOD vista; then phase B's
  `massif_far`, `massif_flank`, `massif_summit`, `lone_peak` and `massif_lod_vista`, around the
  massif of cell (0, -4) centered near (1131, -4900) and the lone peak near (-2721, 3061); then phase
  C's `island_far`, `island_beach`, `island_knoll`, `archipelago_chain` and `island_lod_vista`, around
  the island near (4617, 4), the knoll near (5572, 20) and the archipelago of cell (2, 1) centered near
  (3776, 2601); then phase D's `great_lake_river` (since phase E the NE river stepping up from 0 to
  the frozen, river-fed lake of cell (4, 3) at its surface 7), phase E's `great_lake_elevated` (that
  lake from height, with stepped ramp channels on both sides), `great_lake_outflow` (down its outflow
  from the rim crossing near (5932, 4453), the levels descending 7 to 3) and `great_lake_inflow` (up
  the stepped west arm to the lake), `great_lake_lod_vista` (across the river-fed lake of cell
  (-6, -5), now at its own surface 2 with a 12-block ramp), and `great_lake_shore`,
  `great_lake_island`, `great_lake_bay` and `great_lake_height` around the rim-fed two-lobe lake of
  cell (-8, -8) near (-9341, -9791)). Phase E's shots all film (4, 3), the only nearby lake that is
  river-fed, ramped and has an outflow; it is frozen, so its steps read as snow-capped terraces (the
  unfrozen outflow of (-8, -8) runs under a closed oak canopy and did not read). It starts at
  `VOXEL_TIME=1090` (was 1160); to fit the phase D and E shots into daylight the waits were shortened
  rather than the start moved, so the last shot (about 1609) still lands before sunset. The island
  and great-lake vantages and the audit of the other scenarios (every teleport outside
  terrain-shape.json lies at least 4118 blocks outside a great-lake footprint, ramps included) are in
  its header:
  `icebergs.json` and `biomes.json`'s `pack_ice`/`pack_ice_low` now have an island near (-1100, -2510)
  in or beside the frame; both were re-run, still read, and carry a header note.
* Run: `dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release` (needs .NET 10 SDK; on a
  runtime-only-10 box prefix `DOTNET_ROLL_FORWARD=LatestMajor`). Follow the repo test policy: assert
  rules/relations, never constants or content literals.
* Visual: `OpenGLTest/visual-examples/biomes.json` (forest, steppe, the steppe/desert and savanna/desert
  edges, savanna from above and under the stone pines, desert, the steppe/tundra transition, a
  high shot along the warped desert/savanna line, the tundra's shrubs and snags from low height and
  a cut where the permafrost band shows between the snow and the stone, and the pack-ice edge of a
  cold sea from height (`pack_ice`) and grazing over an open lead (`pack_ice_low`); a flying camera, its vantages from a headless probe
  that must be redone whenever the climate re-lays), plus `grass-tint.json`, `biome-grade.json`, `aurora.json`,
  `snow-sparkle.json`, `water.json`, all with `VOXEL_TIME=147` (`VOXEL_SCRIPT=<json>`); use
  `VOXEL_SPAWN=x,y,z` to jump to a biome. Nothing in-game prints the current biome; a quick headless
  probe is a test calling `TerrainGenerator.BiomeAt/BiomeWeights/Climate` over a grid (do not commit it
  as a throwaway; add a rule test).
* Docs: update the "Biomes" section of `AGENTS.md` and its Layout entries when you change behavior.

---------------------------------------------------------------------------------------------------

### 11. Known quirks / limits (things the next model may want to change)

* Only **three** climate inputs (continentalness, temperature, humidity) decide the biome; relief
  (`ReliefNoise`, §6) is a fourth, orthogonal input that shapes the height only and never the biome.
  No latitude-style gradient, no hills or mountains biome, swamp, jungle or badlands. The steppe,
  the savanna and the tundra are plains through the arid weight, lifted plateaus inside a highland;
  there is no separate grassland height profile.
* **Steep highland coasts.** The highland lift goes into the forest and dune profiles but not the
  ocean profile, so where a highland meets the sea the shore band lerps from lifted land down to the
  unlifted ocean ramp and the coast is steeper than a lowland one.
* **The relief re-laid out-of-highland mountains.** Ranges rise only inside highlands now, so every
  old mountain outside one is gone; land with zero highland weight is otherwise bit-identical (a
  ±20000 grid at stride 37: 544070 of 544070 eligible points). Existing saves regenerate their
  unmodified chunks there and inside the highlands, so seams appear at the edge of edited chunks.
  Scenarios that stood on a removed mountain were moved (the shared build patch to (157, 283),
  `grass-tint.json`'s mountain shots onto a range near (1050, 3635), `biomes.json`'s
  `tundra_permafrost` onto a ravine near (-1110, 806)).
* The range cells are an axis-aligned lattice under a jittered lookup: ranges in neighboring cells
  do not connect, they crossfade inside the blend band.
* **Hashed peaks (phase B):**
  * A massif or lone peak rejects every candidate whose center or nominal rim touches any ocean or
    desert weight, so none stand at a coast or at the desert border (and there are no sand
    mountains). The desert mesa variant is not implemented; it is listed under Open.
  * Massif summits are absolute (96..128), lower than the design's "120-200 above the local base":
    the trade for staying under the cloud slab without flat caps. Taller massifs need the cloud layer
    raised first.
  * `SoftCeiling` can still round a spur, or a massif's flank where it rides a high range, near 128..148;
    crowns stay below its knee by construction.
  * Accepted sites are at least their spacing apart, but the spacing is lowest-hash among raw
    candidates, so a rejected low-hash neighbor can suppress a site.
  * Rivers dry into washes on a massif or a lone peak (the forest-profile height crosses
    `RiverLowEnd`), and lakes reject rims that a peak raised, so lake counts drop near peaks.
  * Existing saves regenerate their unmodified chunks inside massif and lone-peak discs, so seams
    appear at the edge of edited chunks there; land outside every disc is bit-identical.
  * The far LOD's valley darkening beside a massif (`LodSkyExposure`) is judged visually (the
    `massif_lod_vista` shot shows no over-dark valley); only the summit's full exposure is asserted,
    and on a region whose crown sits in the next region the border face below it is darkened, as
    intended, which is why that check picks the region holding the summit.
  * No scenario vantage moved: every `visual-examples` teleport farther than 1536 blocks from the
    origin lies more than 64 blocks from any massif or lone-peak term.
  * Cost: two `CellRange` scans per `LandHeight` call, about +7 % of `TerrainGenerator.Compute` on
    ordinary ground (`PERF.md`).
* **Islands and archipelagos (phase C):**
  * A small island narrower than a coarse LOD cell vanishes under the far water sheet (the sheet is
    dropped only where a cell's own footprint reaches the level), so distant islets thin out with the
    LOD level.
  * **Far-LOD shore pits.** A level cell draws its top at the eroded minimum over its footprint widened
    by `SampleMargin`, so a dry cell whose window reaches deep water sinks below the sea with no sheet
    over it; continental coasts already do this, and island shores, which are steeper, do it a little
    more. Measured as the share of shore-window cells (window holds wet and dry columns) left uncovered
    with a top below `SeaLevel - CellSize`, and as the ring width that implies, island vs continental
    coast: level 0 0.231 / 0.95 blocks vs 0.167 / 0.68, level 1 0.523 / 6.25 vs 0.319 / 3.62, level 2
    0.622 / 17.46 vs 0.431 / 12.06 (7 island and 6 coast regions; a single coast region swings from 0
    to over 0.9, which is why the fact aggregates). The `island_lod_vista` shot shows no visible dry
    ring.
  * Ravines, rivers and lakes cut island land under the ordinary rules, so a river can take a small
    island down to `SeaLevel - RiverBedDepth`, and an island in a highland region takes the lift.
  * Tree batches grow every tree of a 64x64 cell together, so an island tree can change the
    continental trees of the same or a neighboring cell; and a continental lake whose rim reaches island
    land can be accepted or refused differently. Neither moves `LandHeight` on the continent: 0 of 529
    all-continental columns of a 1000-column sample changed in `TerrainGenerator.Compute` (`PERF.md`).
  * Many island-land components join a continental coast as peninsulas rather than standing alone:
    about 275 of the 556 components within ±8000 hold no continental dry land within 100 blocks.
  * There is no archipelago shelf (listed under Open).
  * Existing saves regenerate their unmodified ocean chunks wherever an island now rises, so seams
    appear at the edge of edited ocean chunks.
  * The far LOD's summit exposure over an island is asserted only on summit corners clear of canopy:
    `LodSkyExposure`'s horizon reads the neighbors' top cell, canopy bumps included, so a wooded summit
    is rightly shaded below full sun (an existing rule, not island-specific).
* **Great lakes (phase D):**
  * Existing saves regenerate their unmodified chunks inside every great-lake footprint (and where a
    pond near one now yields), so seams appear at the edge of edited chunks there; land elsewhere is
    bit-identical (invariant 16).
  * An elevated great lake (surface above `SeaLevel`) reflects against the single sea-level mirror
    plane (`RenderManager.WaterPlaneY`), the same approximation an elevated pond has.
  * Its shore gets beach sand only near `SeaLevel` (`IsBeachHeight`), like an elevated pond's.
  * A river-fed lake whose ramp cannot be kept (too long, crowding the spacing, or banks too low)
    still rests at `SeaLevel` (refused past `GreatLakeRiverFedMaxDrop`); none in the default seed's
    ±16000 box does.
  * A hot spring placed on a great-lake shore or inside a ramp overrides the stored level there
    (`Compute` applies oases and hot springs after the lake carve and the bank raise), and the column
    tests skip it; oases are desert-only and never meet a lake.
  * Discrete sampling (16-block rim, 32-block interior) cannot prove every bank dry; the bank-leak
    fact found none over the three tested lakes.
  * Cold great lakes freeze into the pack ice like any water, so a Taiga or Frozen one reads in game as
    a flat snow plain (four of the seven default-seed sites in ±16000).
  * Elevated lakes raise `MaxWaterLevel` in their chunks, so more chunks pay the fluid-wake border scan.
  * Cost: every column pays a `CellRange` scan and a roll per reaching great-lake cell (and every pond
    candidate the same again in `NearAnyFootprint`), about +5 % of `Compute` on ordinary land and
    +22..24 % inside a footprint (`PERF.md`).
* **Great-lake river ramps and outflows (phase E):**
  * Generated water stays all-source and static, so a ramp step between two wet columns is a
    one-block water ledge the fluid wake does not wake (the upper water's side cell is above the
    lower column's terrain top); it spills only once a player edit wakes it.
  * The noise river has no direction, so every arm of a river near a ramped lake steps down away from
    it; an upstream arm usually dries into a wash, since its lowland fade is measured above its own
    level. A river passing near a lake inside its ramp gets a stepped hump.
  * An outflow descends only while the ramp lasts and ends in a closed taper inside it (its length is
    capped at `RampLength - HalfWidth - 1`): it does not reach lower ground or the sea.
  * The bank-raise rule can lift a dry bank column a few blocks where the verdict's sampling missed
    low ground, leaving a small raised lip; it never leaks.
  * Lily pads and pack ice follow the stepped river levels (near and far agree, since both read the
    stored level), so a cold ramp reads as snow-capped terraces.
  * Ramp rivers above `SeaLevel` get no beach sand, like an elevated lake's shore (`IsBeachHeight`).
  * Formerly river-fed lakes re-laid with their own surface, floor and island, and ponds yield inside
    the wider footprints, so existing saves show seams at the edge of edited chunks inside ramp
    footprints; land beyond them is bit-identical (invariant 16).
  * Cost (`PERF.md`): `Compute` on ordinary land +0.9 % (within noise), +2.6 % inside a lake,
    +1.4 % on a ramp and +7.5 % in an outflow channel (about 0.02 ms per chunk column); an accepted
    ramped verdict about 2.1 ms warm, about 0.8 ms over phase D, once per cell.
* **Undithered borders:** only the desert line dithers. Forest/Steppe/Savanna borders share one grass
  fill and change only in tint, trees and flora, and the Taiga/Frozen line lies under snow, so neither is
  dithered: the tree line and the tint step follow a smooth contour.
* The semi-dry ramp is narrow (0.08 of humidity noise, about a quarter of the dry ramp's width), so the
  forest-to-steppe tint change is quick across a border. Widening it moves the origin (§3).
* The binding-ramp dither label is exact only inland; on a coast a mislabeled column is grass either way.
* Cold biomes have no height identity (same profile as forest). Desert and Ocean are the only ones with
  their own; height regimes come from the relief layer instead (highlands of any climate, §6), and
  from the hashed massifs and lone peaks (§6), which stand only on forest-profile climate.
* Temperature noise is 1 octave at 1/5500: climates are enormous (borders are far apart). Humidity is
  1/1700, so humidity varies ~3x faster than temperature; most border crossings are humid flips. The
  climate warp bends every climate border over a wavelength of about 1400 blocks; it is low frequency
  on purpose (the stretch bound and the desert/snow separation limit how much it may squeeze), so a
  border wanders rather than frays. Only the desert line is also dithered at the block scale.
* **The warp re-laid every seed.** Phase 2 (the warp, and `TemperatureFrequency` going from 1/4500 to
  1/5500) moved every climate border except at the origin; existing saves regenerate their unmodified
  chunks under the new climate, so old worlds show seams of biome, height and tint at the edge of their
  edited chunks.
* The origin is Forest only because its raw climate is exactly 0 (§3); spawn is additionally forced to a
  forest valley (relief clear `ReliefSpawnClear*`, which also keeps massifs, lone peaks and islands away, +
  lake spawn-clear radius 72).
* Desert, Taiga, Frozen and Ocean are mob-free by design (`MobSpawner.SpawnsMobs`).
* The snow line (0.55), the pack-ice band (0.6..0.72) and the Taiga line (0.70) overlap; see §6.
* A supplied (imported) column's far LOD decides ice from its stored Frozen biome, so it shows no
  ragged pack-ice band, and its beaches stay Ocean-only; only the generated path has a cold weight and
  grass-biome beaches. A stored per-column ice flag would fix it but is a format change.
* The pack-ice band re-laid cold water: existing saves regenerate their unmodified chunks under the
  band rule, so cold seas and lakes can show seams of ice against open water at the edge of edited
  chunks. The near stamp reads `ColdWeightAt` at the column's bed height and the far LOD at the
  cell's highest ground, so a lake above `MountainRockHeight` (where altitude raises the cold) can
  disagree between near and far by a cell.
* **Drifting icebergs** (entities, never blocks; `AGENTS.md` "Icebergs"). They spawn on open sea at the
  pack-ice edge: the stored column is `Biome.Ocean` at `SeaLevel` (no river or lake), the cold weight
  (`SnowGenerator.ColdWeightAt`) lies from `IcebergConfig.SpawnColdMin`, a little below
  `PackIceColdStart`, up to `FreezeColdThreshold` where the sheet closes, and the top water is not
  frozen by `SnowGenerator.IsFrozenWater` (`IcebergSpawnRule.InBand`), so they appear in the open
  leads of the pack-ice band and a fringe of open sea just warmer than it. They drift along the wind,
  stop at the pack ice, shallows and each other, can be pushed (a big berg barely) and stood on.
  Limits: a column a world source supplies has no worldgen climate, so an imported sea grows no
  natural bergs; the spawn reads the same `ColdWeightAt` as the near ice stamp, so a berg never
  spawns inside the sheet but may drift into the band's floes only until it touches one. Tests:
  `IcebergSpawnRuleTests`, `IcebergSpawnerTests`, `IcebergManagerTests`; visual check
  `visual-examples/icebergs.json` (`VOXEL_TIME=147`), at the band near (-1045, -2470).
* Existing saves regenerate unmodified chunks under the new rules, so seams appear beside edited areas.
* Tree biome lists are lowercase enum names; an unknown name fails at content load.
* **The shrub-over-snow exception.** Every surface stamp writes into air only, except one: a tundra
  dead shrub replaces the snow cell `SnowGenerator` (which stamps before the foliage) laid over
  permafrost. It is confined to permafrost ground; snow over stone, sand or grass is never replaced.
  The snow cell's state byte is 0, so nothing else needs clearing.
* The far LOD's soil side band is `NominalDirtDepth` deep for every biome, Frozen included, against the
  near fill's 2 permafrost cells, so a distant tundra cut shows a deeper permafrost band than near.
* Side effects of permafrost replacing the Frozen stone top: the rare pebble that could land on a
  Frozen stone top where the snow cell was skipped at a chunk border (a surface at local y 15) no longer
  appears, since permafrost has no `pebbleWeight`; and ore veins (malachite, coal), which replace only
  stone hosts, no longer reach the top 2 soil cells of a Frozen column.
* Existing saves: unmodified Frozen chunks regenerate with permafrost, edited ones keep their old rock
  tops, so seams appear at the edge of edited tundra.

---------------------------------------------------------------------------------------------------

### 12. Cave biomes

The overworld's carved caves are dressed by a 3D cave biome field that sits under the surface
biomes rather than beside them: the upper caves echo the frozen or desert land above them, and the
deep caves drift into three sub-biomes of their own. The as-built detail, the knobs and the measured
costs of each phase are in `CAVE_BIOMES.md` (Reference); the mobs and chests are in `AGENTS.md`
("Cave biomes", "Cave mobs").

* **Model.** `CaveBiome` is its own enum, never `Biome` (whose ordinals ride the column codecs). One
  rule, `CaveBiomes.Classify`, reads three 3D noises blended on a stride-8 lattice (seed offsets
  179-181, §7), the cell's y, its depth under the column top and the column's **stored, dithered
  surface biome**. That last read is the one link to the surface biomes: changing how Frozen or Desert
  classify, or the desert dither, re-lays the echo caves.
* **Bands.** Echo: a Frozen column's caves are FrozenEcho and a Desert column's DesertEcho, from a
  jittered cover under the column top down to the wavy `CaveEchoBottomY` line (-200 plus a drift);
  every other column's are plain. Under the echo, the selector picks Crystal, plain or Fungal, and
  MagmaDeep takes over under the wavy `CaveDeepTopY` line (-264 plus a drift). Nothing at or below
  `BarrierTopY`, so the barrier and hell are untouched; `LayerBandTests` holds the order.
* **One map.** `CaveBiomes.Fill` writes `CaveBiomeMap` once per chunk, right after the column fill;
  every consumer (cave shape, ore richness, exclusive ores, palette pass, underground ruin chests)
  reads it, and the point query `CaveBiomes.At` equals it bit for bit across every seam.
* **Palettes.** The palette pass rewrites only cells that are exactly `stone`, so ores, pockets, pools
  and plants survive. Every palette block is reused (no new content id) and is solid, opaque and
  occluding; ice, sand, gravel and basalt are excluded on purpose.
* **Shape.** `CaveShape` gives crystal bigger cheese rooms, fungal wider spaghetti tunnels and the
  magma deep both, each boost scaled per region by NoiseCaves' ninth channel (seed offset 182), plus a
  worm radius factor at the node center. Plain and echo cells carve bit for bit as before.
* **Ores.** `CaveRichness` shifts the Y-band ores' thresholds per cave biome (negative richer), keeping
  the stamp equal to the point query; `CaveOres` adds amethyst (crystal), jade (fungal) and cinnabar
  (magma deep), seed offsets 183-185, in plain stone of their own biome only.
* **Atmosphere.** `CaveClimate` and `CaveAtmosphere` ease in each biome's fog, ambient tint and drifting
  motes from the eye's cave biome, which an authoritative world computes (`EyeCaveBiomeProbe`) and the
  server sends in `PlayerStatus` (protocol 28), since a client has no seed.
* **Mobs and loot.** One mob per sub-biome (shardling, sporeling, magmaborn; protocol 29), a cave spawn
  search filtered by cave biome, and underground ruin chests that roll a table of their own cave biome;
  see `AGENTS.md`.
* **Cost.** Cumulative against the pre-cave-biome build, on the deep band (the chunks between the echo
  line and the barrier, the most affected class): +27% warm with optimized code after CB-5's row fast
  path in `CaveBiomes.Fill` (+41% before it), +54% on the default probe, whose few calls run mostly
  tier-0 code; echo caves +5%, other underground chunks within noise. The tables are in
  `CAVE_BIOMES.md` (Reference, "Cumulative cost (CB-5)").
* **Tests.** `CaveBiomeTests`, `CaveShapeTests`, `CaveOreRichnessTests`, `CaveOreTests`,
  `CaveBiomeSpotTests` (prints a spot per biome for `visual-examples/cave-biomes.json`), `LayerBandTests`
  and `StampGateTests`.
* **Known gaps.** Hard vertical edges in rock where the stored column biome flips between Frozen or
  Desert and anything else; frozen and desert cliffs deeper than the cover wear the echo stone while the
  far LOD still draws stone; cave size and ore richness step hard at biome borders, like the palette;
  the exclusive ores have no use yet; existing saves regenerate their unmodified cave chunks, so seams
  show at the edge of edited chunks.

---------------------------------------------------------------------------------------------------
