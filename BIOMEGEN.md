# Biome generation: current state (handoff map)

Audience: another model that will modify biome generation. This documents what exists **today**, where
each rule lives, every tuning constant, what depends on it, and the traps. Paths are relative to the
repo root; code is under `OpenGLTest/src/`. Read `AGENTS.md` ("Biomes", "Code organization") for the
repo's standing rules (American English, no dead code, no pointless tests, JSON-for-content, build
Release with the .NET 10 SDK).

State: **phases 1 to 4** of the biome plan are in (the semi-dry ramp, Steppe and Savanna; a
domain-warped climate sample; a permafrost tundra with dead shrubs; a ragged pack-ice edge on cold
water), plus drifting iceberg entities on the open water at the ice edge (§11), plus **phase A of
§13**: a continent-scale relief layer (highlands and linear mountain ranges, `WorldGen/TerrainRelief.cs`,
§6), and **phase B**: hashed massifs and lone peaks on a shared hashed-site helper
(`WorldGen/TerrainPeaks.cs`, `WorldGen/TerrainSites.cs`, §6), and **phase C**: ocean-gated islands
and hashed archipelagos folded into the ocean weight (`WorldGen/TerrainIslands.cs`, §4, §6). The rest
of §13 (great lakes, optional polish) is planned and not implemented.

---------------------------------------------------------------------------------------------------

## 1. The model in one paragraph

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

## 2. Files

| File | Role |
|---|---|
| `WorldGen/BiomeClassifier.cs` | **The core.** `SampleClimate` (the only reader of the temperature and humidity noises and of the climate warp), `BiomeWeights` (two-out and three-out), `Classify`, `ColdOf`, `WarmOf`, `DryOf`, `SemiDryOf`, `WetnessOf`, `ColdWeight`, `WarmWeight`, `DitherBiome`. |
| `WorldGen/BiomeTraits.cs` | Shared yes/no biome rules: `ShowsBareRock`, `RimsWaterWithSand` (near fill + both LOD paths). |
| `WorldGen/TerrainNoise.cs` | Every noise instance; `Reseed()` builds them from `WorldSeed`. `MakeNoise(seedOffset, freq, octaves)`. |
| `Config/WorldGenerationConfig.cs` | All constants (continent/temperature/humidity ramps, ocean, desert, dither, water, snow, relief highlands, mountain ranges, massifs and lone peaks, the relief ceiling, openness, dry hills). |
| `WorldGen/TerrainGenerator.cs` | `Compute(chunkX, chunkZ)` builds a `ColumnData`; `LandHeight` blends per-biome height profiles and adds the relief layer's terms and the hashed peaks (through the private `LandHeightCore`); `ReliefLandHeight` (no hashed peaks) and `PreFeatureLandHeight` (massifs, no lone peaks), the profiles the site tests read, and their static `Func` fields `LandHeightAt`/`ReliefHeightAt`/`PreFeatureHeightAt`; `Climate(x,z,out warmth01,out wetness01)`; public forwarders (`BiomeAt`, `BiomeWeights`, `ColdWeight`, `WarmWeight`); `PermafrostBlock`, the Frozen top and soil. |
| `WorldGen/TerrainRelief.cs` | The relief layer (§6): `SpawnClear`, `HighlandWeight` (the only reader of `ReliefNoise`), `HighlandLift`, `HillGain`, `MountainHeight(x, z, highland)` and the range field (`RangeLattice`, `RangeShape`/`RangeShapeAt`, `RangeBlend`, `RangeDirection`, `CellShape`). |
| `WorldGen/TerrainSites.cs`, `TerrainSite.cs` | The shared hashed-site helper (§6): `CellRange`, `TryRoll`, `Wobble`/`InwardWobble`, `RimPoint`/`RimRange`, `YieldsToNeighbor`; `TerrainSite` is one accepted site (`default` = none). Lakes, massifs and lone peaks use it. |
| `WorldGen/TerrainPeaks.cs` | The hashed peaks (§6): `MassifHeight`/`WithMassif`, `LonePeakHeight`/`WithLonePeak`, `SoftCeiling`, the cached verdicts `MassifSiteAt`/`PeakSiteAt` (built by `BuildMassifSite`/`BuildPeakSite`, accepted all-or-nothing on the pre-island ocean weight; an island site, `TerrainSite.OnIsland`, is scaled by the island land weight), `ResetCaches` (from `WorldSeed.Apply`). |
| `WorldGen/TerrainIslands.cs` | The island term (§4, §6): `LandWeight` (the only reader of `IslandNoise`/`IslandSmallNoise`), the stateless hashed archipelago footprints `TryGetArchipelago`/`FootprintWeight`/`ArchipelagoWeight`. No cache. |
| `WorldGen/ColumnData.cs` | Per-chunk-column arrays incl. `Biomes[]`, `CornerWarmth`, `CornerWetness`. |
| `Chunks/ColumnCache.cs` | Column cache + `GenerateColumn` (surface/soil block per biome). |
| `WorldGen/WaterBodies.cs` | Lakes/rivers/sea level; which biomes may hold surface water. Lake candidates roll, wobble and sample their rim through `TerrainSites`. |
| `WorldGen/Features/SnowGenerator.cs` | Snow, ice, `ColdWeightAt` (cold + altitude), `IsFrozenWater` (the pack-ice rule the far LOD shares). |
| `WorldGen/Features/*`, `WorldGen/Features/Underground/*` | Per-biome vegetation/scenery (see §8). |
| `Simulation/MobSpawner.cs` | `SpawnsMobs(biome)`: which biomes ring-spawn mobs. |
| `assets/data/trees/*.json` | `worldgen.biomes` per tree species. |
| `Effects/Climate/BiomeTint.cs`, `EyeClimate.cs` | Grass/foliage color from climate (not from `Biome`). |
| `Effects/Post/BiomeGrade.cs` | Post-process color grade from climate. |
| `Lod/LodMeshBuilder.cs`, `Lod/LodFaceEmitter.cs` | Far-terrain LOD wears biome-specific tiles. |
| `OpenGLTest.Tests/WorldGen/BiomeTests.cs` | The rule tests (see §10). |
| `OpenGLTest.Tests/WorldGen/BiomeClimateTests.cs` | The climate-sample rules: one warped sample behind every reader, the warp's stretch bound (§10). |

---------------------------------------------------------------------------------------------------

## 3. The noises (`TerrainNoise.Reseed`)

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

## 4. Weight pipeline (`BiomeClassifier.BiomeWeights`)

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

**The island fold (§13 phase C).** `BiomeWeights` is three steps: `PreIslandOceanWeight` (the old
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

### Dither (`DitherBiome`)

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

## 5. Climate axes for color (`TerrainGenerator.Climate`)

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

## 6. What each biome changes

### Terrain height (`TerrainGenerator.LandHeight`)
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
**Relief (`WorldGen/TerrainRelief.cs`, §13 phase A).** `HighlandWeight` is
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

**Massifs and lone peaks (`WorldGen/TerrainPeaks.cs`, §13 phase B).** Both are hashed sites on the
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

**Islands and archipelagos (`WorldGen/TerrainIslands.cs`, §13 phase C).** An island is nothing but a
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

### Surface/soil blocks (`ColumnCache.GenerateColumn`)
| Biome | Top | Soil | Notes |
|---|---|---|---|
| Forest, Taiga, Steppe, Savanna (default case) | grass (dirt if wet column) | dirt `DirtDepth` | sand/gravel on beach height **only** where `SurfaceWaterWeight > 0` (`BiomeTraits.RimsWaterWithSand`) |
| Desert | sand | sandstone, `SandstoneDepth 4` | soil 0 if ravine-stripped |
| Ocean | sand ≥ -4, gravel ≥ -16, else stone | same | soil depth 2 |
| Frozen | permafrost | permafrost, depth 2, then stone | soil 0 (bare stone) if ravine-stripped; the snow pass covers it; frozen lake beds wear permafrost too |

Taiga uses the **default** (grass) branch: its "snowiness" comes entirely from `SnowGenerator`, not the
biome. Steppe and Savanna differ from Forest only in tint, trees and flora. `permafrost` is a JSON
block (`assets/data/blocks/permafrost.json`, `hit_rock`, hand-diggable, drops itself) whose tile comes
from `tools/generate-permafrost-texture.py`.

### Water (`WaterBodies`)
* Ocean: level = `SeaLevel`. Lakes/rivers: every land biome but the desert (`CanHoldSurfaceWater`:
  Forest, Taiga, Frozen, Steppe, Savanna). Desert dry.
* `SurfaceWaterGate(desertWeight)` fades lakes/rivers out over `desertWeight 0.3..0.5`, so ponds close on
  steppe or savanna ground before the desert line (an exact line on both humid and temperate borders).
* Lakes: hashed 192-block cells, `LakeChance 0.35`, radius 11..46, depth 3..13. Rivers: warped ridge,
  `RiverBedDepth 3`, dry out as forest-profile height rises (`RiverLowStart 14 .. RiverLowEnd 30`).
* Island land (§13 phase C) is land of its climate biome, so it holds lakes and rivers under the same
  rules; nothing gates them on islands (§11).

### Snow/ice (`SnowGenerator`, reads `ColdWeightAt`, **not** the biome)
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

### Vegetation and mobs
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

## 7. Tuning constants (current values)

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
Noise seed offsets: ReliefNoise 153, MountainSpineNoise 154, MountainRangeCellWarp 155,
  MassifShapeNoise 156, MassifSpurNoise 157 (2 octaves), PeakShapeNoise 158,
  IslandNoise 159, IslandSmallNoise 160 (2 octaves each)
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

---------------------------------------------------------------------------------------------------

## 8. Everything that branches on biome (the blast radius of adding/changing one)

Grep `Biome\.` to confirm; at time of writing:

* **Classifier:** `WorldGen/BiomeClassifier.cs` (`Classify`, `DitherBiome`), `WorldGen/BiomeTraits.cs`
  (`ShowsBareRock` = Forest/Steppe/Savanna; `RimsWaterWithSand` = Forest/Taiga/Steppe/Savanna).
* **Fill:** `Chunks/ColumnCache.cs` `GenerateColumn` switch (Desert/Ocean/Frozen/default = every grass
  biome); `TerrainGenerator.Compute` rock cap (`ShowsBareRock`); `WorldGen/ChunkTerrainFill.cs:65`
  (oasis columns are forced to Forest fill).
* **Water:** `WorldGen/WaterBodies.cs` (`CanHoldSurfaceWater`: not Ocean, not Desert; Ocean level).
* **Trees:** each `assets/data/trees/<species>.json` `worldgen.biomes` (data-driven; adding a biome to a
  species is JSON). Current: cactus=desert; dead_tree=desert+frozen+steppe; oak=forest; palm=desert
  (oasis only); spruce=taiga; stone_pine=savanna; baobab has no worldgen. The `Biome` enum names are
  parsed lowercase. Per-species `openness` scales `ForestOpenness` thinning (stone_pine 0). A new
  worldgen species must not raise the tree reach without checking `TreeGenerator.ReachChunkRadius`,
  `ProxyReach` and `SimulationConfig.MaxLeafDecayBacklogCells`.
* **Features (per-biome):** `Boulder` (Forest/Taiga/Steppe/Savanna), `Cotton` (Savanna),
  `Flower` (Forest/Steppe), `Foliage` tufts (Forest/Steppe/Savanna; dead shrubs on desert sand and
  frozen permafrost, `TryGetDeadShrub` gated on Desert or Frozen),
  `DesertWell`, `FallenLog`, `IceSpike` (frozen), `Oasis` (desert, deep), `SeaStack` (ocean), `HotSpring`
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

---------------------------------------------------------------------------------------------------

## 9. Invariants that must survive any change

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
6. Caches (`ColumnCache`, eight `GenerationalCache` sites, the massif and lone-peak verdicts
   included) only hold pure functions of the key; a biome
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
   verdict is a stateless hash, so invariant 6's eight cache sites are unchanged. **Not** claimed
   identical: chunk blocks and lake water near islands, since a tree batch grows every tree of its
   64x64 cell together and a continental lake's rim may reach island land (§11).

---------------------------------------------------------------------------------------------------

## 10. Tests and verification

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
* The relief layer (§13 phase A): `OpenGLTest.Tests/WorldGen/TerrainReliefTests.cs`
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
* The hashed peaks (§13 phase B): `OpenGLTest.Tests/WorldGen/TerrainSitesTests.cs`
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
* The islands (§13 phase C): `OpenGLTest.Tests/WorldGen/TerrainIslandsTests.cs`
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
* Visual: `visual-examples/terrain-shape.json` (`VOXEL_TIME=1090`, shortly after a sunrise, since the
  run lasts about eight minutes and outlasts the afternoon from 147; the origin valley, a
  forest highland, a range along and across its direction, an arid plateau, a highland border, a
  river wash, a range across a cell border from above, a far-LOD vista; then phase B's
  `massif_far`, `massif_flank`, `massif_summit`, `lone_peak` and `massif_lod_vista`, around the
  massif of cell (0, -4) centered near (1131, -4900) and the lone peak near (-2721, 3061); then phase
  C's `island_far`, `island_beach`, `island_knoll`, `archipelago_chain` and `island_lod_vista`, around
  the island near (4617, 4), the knoll near (5572, 20) and the archipelago of cell (2, 1) centered near
  (3776, 2601)). It now starts at `VOXEL_TIME=1090` (was 1160), so the last shot (about 1580) still
  lands before sunset. The island vantages and the audit of the other scenarios are in its header:
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

## 11. Known quirks / limits (things the next model may want to change)

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
    mountains). The desert mesa variant of §13.1 is not implemented; it is listed under phase E.
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
    ordinary ground (§13.3).
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
    all-continental columns of a 1000-column sample changed in `TerrainGenerator.Compute` (§13.3).
  * Many island-land components join a continental coast as peninsulas rather than standing alone:
    about 275 of the 556 components within ±8000 hold no continental dry land within 100 blocks.
  * There is no archipelago shelf (the optional shallower ramp under a chain of §13.2).
  * Existing saves regenerate their unmodified ocean chunks wherever an island now rises, so seams
    appear at the edge of edited ocean chunks.
  * The far LOD's summit exposure over an island is asserted only on summit corners clear of canopy:
    `LodSkyExposure`'s horizon reads the neighbors' top cell, canopy bumps included, so a wooded summit
    is rightly shaded below full sun (an existing rule, not island-specific).
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

## 12. Checklist for adding a new biome

1. Add the enum value (append to `Biome`; never reorder).
2. `BiomeClassifier.Classify` (+ any new weight/ramp, read through `SampleClimate` if it is climate; a
   new `TerrainNoise` instance needs a **new unused seed offset**; config constants in
   `WorldGenerationConfig`). Keep ramps smooth, exclusive where they share an axis, and borders sloped;
   keep the desert line at 0.5. Decide whether `DitherBiome` should touch it.
3. `LandHeight` if it has its own profile (blend by a weight, don't switch).
4. `ColumnCache.GenerateColumn` surface/soil, and `BiomeTraits` (`ShowsBareRock`, `RimsWaterWithSand`)
   if it shares the grass fill.
5. `WaterBodies.CanHoldSurfaceWater` / `WaterLevelFor` if water behaves differently.
6. Features: tree species JSON `worldgen.biomes` (check the tree reach), generators that gate on biome
   (flowers, tufts, cotton, boulders), `MobSpawner.SpawnsMobs` if mobs.
7. **LOD** (`LodMeshBuilder` + `LodFaceEmitter`) so distance matches near terrain; world-source-supplied
   column path too.
8. Tint palette / grade if it should look different (they key on climate, not biome; a new palette row
   must keep the grass monotone).
9. Tests in `BiomeTests` for the new rules (relational, computed from the seams); update `AGENTS.md`
   and this file; frame it in `visual-examples/biomes.json`.

---------------------------------------------------------------------------------------------------

## 13. Terrain shape and water shape (phases A to C done; D and E planned)

Status: **A, B and C implemented**: A (relief regimes), B (hashed massifs and lone peaks) and C
(islands and archipelagos) are in (§4, §6, §7, §9 invariants 11-15); D (great lakes) and E (optional
polish) are planned and not implemented.

Before phase A one forest profile (swell, masked hills and one rare ridged `MountainHeight`) covered every
land biome; it now carries the relief layer's highlands and linear ranges, and the hashed massifs and
lone peaks on top of them. The ocean is whatever
`ContinentNoise` carves, lakes are small hashed ponds (radius at
most 46) and rivers end at sea level. This section plans, for later phases, how the world gets
mountainous regions, ranges, massifs, lone peaks, larger lakes, islands of every size and
archipelagos. It is a design: the knob names are proposals, every "about" range is a starting point
to tune and not a decision, and a phase's own plan may rename or drop any of them.

**13.0 Principles.** Everything here keeps the §9 invariants:

* A pure function of (x, z, seed); smooth weights summed or lerped, never switched.
* Every term is exactly zero where its mask is zero, so land the mask does not touch stays
  byte-identical: a seed re-lays only inside the masked regions.
* Relief is **not a biome**: the `Biome` enum does not grow. Altitude feeds rock and snow only
  through the existing `MountainRockHeight` and `ColdWeightAt` path, so the desert/snow separation
  rule (invariant 4) is never touched.
* Every large feature respects a spawn clearing like `ReliefSpawnClear*` (`TerrainRelief.SpawnClear`).
* The far LOD follows the heightmap and `WaterLevel` by construction, but each phase re-verifies the
  LOD level nesting and the `LodSkyExposure` constants against the new height range.
* Each new noise gets a new unused seed offset (153 and up; 153-160 are taken by phases A to C).
* Each phase is priced with the perf-capture skill, since `LandHeight` and `BiomeWeights` run per column cell, per
  far-LOD cell and per tree candidate.

**13.1 Terrain relief: a regime layer over the one profile.** Add a continent-scale **relief
field** (`ReliefNoise`, wavelength about 4-6k blocks, one or two octaves) read in `LandHeight` as a
smooth 0..1 `highlandWeight`, orthogonal to climate. Shapes ride it from region scale down to a
point:

* **Mountainous regions (highlands). Implemented (phase A)** as `TerrainRelief.HighlandWeight`
  with `ReliefFrequency`, `HighlandMaskStart/End`, `HighlandBaseLift`, `HighlandHillGain`,
  `HighlandsEnabled` and `ReliefSpawnClearStart/End` (§6, §7). The design as planned:
  `highlandWeight` lifts the base height and multiplies the
  hill amplitude together (`HighlandBaseLift`, `HighlandHillGain`, `HighlandMaskStart/End`), so an
  area reads as high, broken country with real valleys, not isolated spikes on a plain. The arid
  flattening (`DryHillReduction`) still applies, so a highland steppe is a high plateau with gentle
  relief. Lakes keep working through the rim rule; rivers dry out as today via `RiverLowEnd`, which
  gives highlands their dry washes.
* **Mountain ranges. Implemented (phase A)** as `TerrainRelief.MountainHeight(x, z, highland)` and
  the range field (`MountainRangeCellBlocks`, `MountainRangeBlend`, `MountainRangeStretch`,
  `MountainSpine*`, `MountainRangeCellWarp*`, the relaxed `MountainMaskStart/End`; §6, §7). The design
  as planned: keep `MountainHeight`'s warped ridge but make its mask the product of the
  rarity mask and `highlandWeight`, so ranges run along highlands instead of appearing anywhere.
  Make them **linear**: stretch the ridge sample along a per-region hashed direction (an anisotropic
  frequency on the warped coordinates) so a range is a chain tens of blocks wide and hundreds long
  rather than a blob; a second, lower-frequency ridge term adds a spine the smaller ridges hang off.
  Keep `MountainSharpness`.
* **Very large mountains (massifs). Implemented (phase B)** as `TerrainPeaks.MassifHeight` /
  `WithMassif` with the cached verdict `MassifSiteAt` (`BuildMassifSite`) and the `Massif*` knobs
  (§6, §7). Departure from the design below: a site rolls an **absolute summit** (96..128) and rises
  from the relief land at its center to it, instead of a fixed 120-200 above the local base, which
  drove almost every summit into the soft ceiling as a flat cap; the shoulder is a pointed
  `smoothstep^MassifSharpness` with spurs from its own noise rather than the range field, and only
  forest-profile climate (no ocean or desert weight at its center or rim) takes one. The design as
  planned: a rare hashed site, one candidate per large cell (about
  1024-2048 blocks, `MassifCellBlocks`, `MassifChance`), modeled on `LakeSample`: a radial profile
  (radius about 150-400, peak about 120-200 above the local base, `MassifMinRadius/MaxRadius`,
  `MassifMinHeight/MaxHeight`) with a flattened-cone or `smoothstep` shoulder, summed with the ridge
  field at the site so the massif has foothills and spurs, its outline wobbled by a low-frequency
  shape noise like `LakeShapeNoise` so it is not a cone. The altitude path gives it a treeline
  (species `maxGround`), the bare-rock band and the snow cap for free; a scree or gravel band
  between the rock line and the snow line is a possible later polish. It needs the spawn clearing
  and a check that `MassifMaxHeight` stays inside what the far-LOD nesting and `LodSkyExposure`
  tolerate.
* **Small mountains and lone peaks. Implemented (phase B)** as `TerrainPeaks.LonePeakHeight` /
  `WithLonePeak` with the cached verdict `PeakSiteAt` (`BuildPeakSite`), its flatness test on
  `TerrainGenerator.PreFeatureLandHeight` and the `Peak*` knobs (§6, §7); no peak stands on a massif,
  and the desert mesa is not implemented (moved to phase E). The design as planned: smaller hashed
  sites on a finer cell (about 384-512 blocks,
  a higher chance), radius about 30-80 and height about 30-60, placed only where the land is flat
  enough (a rim test like `TryLakeSite`, sampled from a pre-feature profile, never from `LandHeight`
  itself, which would recurse) so a peak stands alone in a plain or a steppe. A desert variant with
  a flat top (mesa or butte: clamp the profile at a hashed cap height) is optional and cheap once
  the site helper exists.
* **Plateaus and terraces (optional, last).** Quantize a low-frequency height term into steps with
  `smoothstep` risers under a rarity mask, so an area reads as tablelands, biome-flavored (badlands
  in the desert). Lowest priority.

Shared helper (**implemented in phase B** as `WorldGen/TerrainSites.cs`, which lakes now use
bit-identically, and the tests below are in `TerrainPeaksTests` and `LodReliefTests`, §10): one
`TerrainSites` (or similar) hashed-site routine used by massifs and peaks (cell,
chance, offset in cell, size roll, spawn clearing, lowest-hash spacing), generalizing the lake
candidate code rather than copying it. Order inside `LandHeight`: highlands and ranges come before
the dune and ocean lerps (they are part of the forest profile) and site peaks are summed into the
same profile, so every later carve (lakes, rivers, ravines, oases) sees them. Interaction notes:
`RiverLowEnd` and `LakeSiteFlatness` already keep water off steep ground; ravines and cave entrances
need nothing; caves fade from the surface by depth, so a massif's interior is ordinary rock with
caves; the openness mask must not flatten a massif (it applies to the hill term only, as today).
Tests to add (relational): a massif site's peak column is higher than every column on its rim; a
lone peak never stands on ground that fails the flatness test; with the relief mask at zero the
height is byte-identical to today's; every masked term is zero outside its mask; the far LOD's
heights still sit strictly inside the chunk terrain over a massif.

**13.2 Water shape: great lakes, islands, archipelagos.** Three additions to `WaterBodies` and the
ocean term, all through the existing `WaterLevelFor`, so the stored `WaterLevel`, the flood fill and
the far-LOD sheet agree by construction:

* **Larger lakes and inland seas.** A second lake tier beside the ponds: a much larger candidate
  cell (about 1024-1536 blocks, `GreatLakeCellBlocks`, a low `GreatLakeChance`), radius about
  120-400, depth about 15-30 (`GreatLake*` knobs), the same rim-chosen surface (lowest rim minus the
  bank depth) so every bank is above the water, but a **lobed outline**: two or three offset discs
  per site plus the shape noise at a lower frequency, so a great lake has bays and is never a
  circle. The flatness rule relaxes for the tier (a larger rim variance is accepted and the lowest
  rim still wins), and where the rim is uneven the shore blend widens so a great lake sits in
  rolling land. Rivers should **end in** a great lake instead of being refused: today
  `RiverCrossesSite` drops a pond a river crosses; for the great tier invert it, so the lake wins
  and the river carves toward the lake's surface (a per-column river target level in place of the
  constant `SeaLevel` for the channel and in `WaterLevelFor`) and drains into the lake. An elevated
  great lake is also a chance for an outflow river on its downhill side later. Islands inside a
  great lake come free from the lobed outline where the discs leave a gap. Pack ice, hot springs,
  reeds and lily pads all key on `WaterLevel` and the cold and need nothing.
* **Islands in the ocean (large and small). Implemented (phase C)** as `TerrainIslands.LandWeight`,
  folded in `BiomeClassifier.BiomeWeights` between `PreIslandOceanWeight` and `WeightsForOcean`, with
  `IslandNoise` (159) and `IslandSmallNoise` (160) and the `Island*` knobs (§4, §6, §7, invariant 15).
  Departures from the design below: the two channels combine by their **max** (not a sum), the large
  channel's gain is above 1 (`IslandLargeGain 1.25`) so a deep core saturates to whole land, which is
  what lets a massif or lone peak stand on an island; the small channel runs at 1/420; sites are
  accepted all-or-nothing on the pre-island weight and an island site is scaled by the island land
  weight (so the land with `pre <= OceanShoreWeight` is bit-identical, a stronger promise than
  `oceanWeight == 0`); no shore-band remap was needed, the ramps alone met the far-LOD shore-pit
  target. The design as planned: The continent noise alone gives few islands. Add an
  ocean-gated island term that never moves the inland: compute `oceanWeight` as today, then an
  island land weight from two channels (`IslandNoise` about 1/1200 for large islands, a second
  about 1/300-1/600 for small ones, two octaves each, thresholded and smoothstepped), scaled by how
  deep in the ocean the column is (`islandGain * smooth(OceanShoreWeight, 1, oceanWeight)`) so
  islands rise mid-ocean and never clutter the coast, and fold it back as
  `oceanWeight' = oceanWeight * (1 - islandLand)`. Columns with `oceanWeight == 0` are
  byte-identical to today. Island heights come from the same forest profile the blend already lerps
  toward, so a large island can carry hills, a lake and even a peak site; a small island is a
  sand-rimmed knoll through `IsBeachHeight`. Climate rides the one `SampleClimate` sample, so a warm
  dry island wears sand and a cold one freezes into the pack ice; trees and features follow their
  biomes. `ContinentNoise` itself is **not** changed and still not warped (changing it re-lays every
  coast).
* **Archipelagos. Implemented (phase C)** as `TerrainIslands.TryGetArchipelago` /
  `FootprintWeight` / `ArchipelagoWeight` with the `Archipelago*` knobs (§4, §7): one candidate per
  1792-block cell through `TerrainSites.TryRoll` (salt "ARCH"; the direction and aspect from hash units
  4 and 5), an ellipse of half length 480..860 and aspect 0.2..0.45, full over its core half and
  easing to 0 at its rim, inside which the small channel's ramp drops by `ArchipelagoThresholdDrop` and
  its gain rises to `ArchipelagoSmallGain`. Departures: **no cache** (the verdict is a pure hash, so
  each read rerolls the few reaching cells), no spacing rule, overlapping footprints combine by max,
  and no shelf. The design as planned: A hashed "archipelago cell" (about 1536-2048 blocks, a low chance) that locally
  raises the small-island gain and lowers its threshold inside an elongated footprint (a hashed
  direction and aspect), so chains and clusters of small islands appear in lines across open water
  with empty ocean between the cells; sea stacks (`SeaStackGenerator`) already decorate shallow
  water and will cluster there too. Optional polish: a shelf (a shallower ocean ramp) under a chain,
  so the water between the islands is light and wadeable.
* **Coast shape (optional).** A small-amplitude domain warp of the continent *sample* alone (about
  1/200, a few tens of blocks) for bays, headlands and fjord-like inlets. It must stay out of
  `SampleClimate` (invariant 8) and is the one item here that re-lays every coast, so it is listed
  last and gated on wanting it. Reefs, shipwrecks and deep-sea points of interest stay `P-56`
  content.

Tests to add (relational): a great lake's surface is below its lowest rim and above its floor; a
river that reaches a great lake carves to the lake's level, never below it; no column with
`oceanWeight == 0` changes height when the island term is on; islands only exist where the
pre-island ocean weight was above `OceanShoreWeight`; an archipelago cell holds more small-island
columns than an equal open-ocean window; the far LOD sheet over a great lake sits at the stored
level. (The island and archipelago ones are in `TerrainIslandsTests` and `TerrainPeaksTests`, §10.)

**13.3 Suggested phases.** One phase per prompt, each ending with a green Release build and test
run, a perf-capture before and after, new shots in `biomes.json` or a new
`visual-examples/terrain-shape.json`, and the §6, §7 and §11 docs updated; existing saves re-lay
only where a new mask is non-zero.

* **A. Relief regimes: done.** `ReliefNoise` highlands that lift the base and steepen the hills,
  ranges coupled to them and made linear (hashed per-cell direction, spine, jittered cell lookup),
  all in `TerrainRelief`, faded out within 1536..2560 blocks of the spawn. Identity against the tree
  before it: 544070 of 544070 eligible points of a ±20000 grid (stride 37; highland weight 0 and no
  old mountain) bit-identical, 143439 zero-weight points differing only where an old out-of-highland
  mountain was removed. `TerrainGenerator.Compute` median ms per column, before -> after (5
  interleaved Release runs over 2048 columns at least 1000 blocks out): all 0.2339 -> 0.2518 (+7.6 %),
  lowland 0.2404 -> 0.2383 (-0.8 %), highland 0.2147 -> 0.2362 (+10.0 %), range 0.2347 -> 0.3273
  (+39.5 %). The LOD nesting invariants hold and `LodSkyExposure` needed no change (far valleys over
  highlands are not over-dark in the `lod_vista` shot).
* **B. Hashed peaks: done.** The shared site helper `TerrainSites` (the lake candidates moved onto
  it, bit-identical over 794482 samples), massifs and lone peaks in `TerrainPeaks`, summed into the
  forest profile after the ranges under a soft ceiling, with two cached verdict sites. Identity: both
  terms are exactly 0 within the spawn clearing and outside every accepted disc, and `WithMassif` /
  `WithLonePeak` then return their input float, so that land is bit-identical to the tree before
  (`LandIsByteIdenticalOutsideEveryMassif`, `LandIsByteIdenticalOutsideEveryLonePeak`).
  `TerrainGenerator.Compute` median ms per chunk column, before -> after (5 interleaved Release runs,
  median of the medians; the baseline is a copy of the working tree, since phase A was uncommitted):
  all 0.2221 -> 0.2372 (+6.8 %), lowland 0.1869 -> 0.1996 (+6.8 %, the fixed cost of the two
  `CellRange` scans every `LandHeight` call pays), massif 0.2323 -> 0.2700 (+16.2 %), peak
  0.2023 -> 0.2252 (+11.3 %); recorded in `PERF_TODO.md`. The LOD nesting invariants hold,
  `LodSkyExposure` needed no change (a summit is fully exposed at every level, and the
  `massif_lod_vista` shot shows no over-dark valley). The desert mesa moved to E.
* **C. Islands and archipelagos: done.** The ocean-gated island term (two noise channels scaled by
  ocean depth, folded as `pre * (1 - island)` only when the island weight is above 0) and the
  stateless hashed archipelago footprints, in `TerrainIslands`; massif and lone-peak sites accepted
  all-or-nothing on the pre-island weight, island sites scaled by the island land weight. Identity
  against a copy of the working tree before it: of a ±20000 grid at stride 37 (1170724 points),
  105692 changed overall and 0 where the baseline ocean weight was at or below `OceanShoreWeight`
  (none of the 624405 zero-weight points); 0 site verdicts changed at continental centers (707 sites
  before, 710 after: 3 new island lone peaks, no island massif); `TerrainGenerator.Compute` changed 120
  of 1000 sampled chunk columns and none of the 529 whose every cell had baseline ocean weight 0.
  `TerrainGenerator.Compute` median ms per chunk column, before -> after (5 interleaved Release runs,
  median of the medians, columns 1000..12000 blocks out): all 0.2425 -> 0.2823 (+16.4 %), land
  0.2311 -> 0.2272 (-1.7 %, within the run-to-run spread: land pays one compare), ocean past the shore
  0.2042 -> 0.2500 (+22.4 %: the spawn clear, the archipelago hashes and two 2-octave noises per
  `BiomeWeights` call), island 0.2019 -> 0.2822 (+39.8 %, which also pays the land's lake, river and
  ravine work), archipelago 0.1789 -> 0.2463 (+37.7 %); recorded in `PERF_TODO.md`. The LOD nesting
  invariants hold and `LodSkyExposure` needed no change: the far LOD sits inside the chunk terrain
  over an island and an archipelago at every level, an island summit's canopy-free corners are fully
  exposed, and the island shore-pit share stays within twice a continental coast's plus slack (§11).
  Shots: `island_far`, `island_beach`, `island_knoll`, `archipelago_chain`, `island_lod_vista` in
  `terrain-shape.json`.
* **D. Great lakes:** the second lake tier, lobed outlines, rivers ending in lakes.
* **E. Optional:** coast warp, plateaus and terraces, scree bands, great-lake outflow rivers, the
  desert mesa or butte (a flat-topped lone peak on the dune profile; a site helper call away, but it
  must not sum a peak into sand, see §11).

Each phase goes through the plan-and-implement workflow with its own scouting; the knob names and
ranges above are proposals, and the phase's plan may rename or drop them.
