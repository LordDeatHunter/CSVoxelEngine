# Biome generation: current state (handoff map)

Audience: another model that will modify biome generation. This documents what exists **today**, where
each rule lives, every tuning constant, what depends on it, and the traps. Paths are relative to the
repo root; code is under `OpenGLTest/src/`. Read `AGENTS.md` ("Biomes", "Code organization") for the
repo's standing rules (American English, no dead code, no pointless tests, JSON-for-content, build
Release with the .NET 10 SDK).

State: **phase 1** of the biome plan is in (the semi-dry ramp, Steppe and Savanna). Phases 2-4 (a
domain-warped climate, a permafrost tundra, a pack-ice edge on the frozen ocean) are not.

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
so heights blend across borders (slopes, never cliffs).

---------------------------------------------------------------------------------------------------

## 2. Files

| File | Role |
|---|---|
| `WorldGen/BiomeClassifier.cs` | **The core.** `SampleClimate` (the only reader of the temperature and humidity noises), `BiomeWeights` (two-out and three-out), `Classify`, `ColdOf`, `WarmOf`, `DryOf`, `SemiDryOf`, `WetnessOf`, `ColdWeight`, `WarmWeight`, `DitherBiome`. |
| `WorldGen/BiomeTraits.cs` | Shared yes/no biome rules: `ShowsBareRock`, `RimsWaterWithSand` (near fill + both LOD paths). |
| `WorldGen/TerrainNoise.cs` | Every noise instance; `Reseed()` builds them from `WorldSeed`. `MakeNoise(seedOffset, freq, octaves)`. |
| `Config/WorldGenerationConfig.cs` | All constants (continent/temperature/humidity ramps, ocean, desert, dither, water, snow, mountains, openness, dry hills). |
| `WorldGen/TerrainGenerator.cs` | `Compute(chunkX, chunkZ)` builds a `ColumnData`; `LandHeight` blends per-biome height profiles; `Climate(x,z,out warmth01,out wetness01)`; public forwarders (`BiomeAt`, `BiomeWeights`, `ColdWeight`, `WarmWeight`, `IsDesert`). |
| `WorldGen/ColumnData.cs` | Per-chunk-column arrays incl. `Biomes[]`, `CornerWarmth`, `CornerWetness`. |
| `Chunks/ColumnCache.cs` | Column cache + `GenerateColumn` (surface/soil block per biome). |
| `WorldGen/WaterBodies.cs` | Lakes/rivers/sea level; which biomes may hold surface water. |
| `WorldGen/Features/SnowGenerator.cs` | Snow, ice, `ColdWeightAt` (cold + altitude). |
| `WorldGen/Features/*`, `WorldGen/Features/Underground/*` | Per-biome vegetation/scenery (see §8). |
| `Simulation/MobSpawner.cs` | `SpawnsMobs(biome)`: which biomes ring-spawn mobs. |
| `assets/data/trees/*.json` | `worldgen.biomes` per tree species. |
| `Effects/Climate/BiomeTint.cs`, `EyeClimate.cs` | Grass/foliage color from climate (not from `Biome`). |
| `Effects/Post/BiomeGrade.cs` | Post-process color grade from climate. |
| `Lod/LodMeshBuilder.cs`, `Lod/LodFaceEmitter.cs` | Far-terrain LOD wears biome-specific tiles. |
| `OpenGLTest.Tests/WorldGen/BiomeTests.cs` | The rule tests (see §10). |

---------------------------------------------------------------------------------------------------

## 3. The noises (`TerrainNoise.Reseed`)

All built by `MakeNoise(seedOffset, frequency, octaves)`: OpenSimplex2, seed = `WorldSeed.Value + offset`,
FBm when `octaves > 1`. Output is roughly [-1, 1] (single octave spans the full range; FBm is scaled
down, which matters for ramp thresholds).

| Noise | Seed offset | Frequency (wavelength) | Octaves | Used for |
|---|---|---|---|---|
| `ContinentNoise` | 70 | 1/2800 | 2 | ocean vs land |
| `TemperatureNoise` | 71 | 1/4500 | **1** | cold/warm ramps |
| `HumidityNoise` | 88 | 1/1700 | 2 | dry and semi-dry ramps |
| `BiomeDitherNoise` | 89 | 1/28 | 1 | ragged desert material line |
| `OpennessNoise` | 86 | 1/900 | 2 | forest woodland vs open (trees + hill flattening) |

**Trap:** `SnowGenerator.DriftNoise` also uses seed offset **89** (`TerrainGenerator.MakeNoise(89, ...)`),
the same as `BiomeDitherNoise`. Different frequencies, so not identical fields, but the same seed base.
Do not "fix" it without knowing it re-lays snow drift (or dither); it is not a bug today.

**Trap:** OpenSimplex is exactly 0 at the lattice origin whatever the seed, so the origin's raw
temperature and humidity are both 0. The semi-dry ramp's midpoint sits just below 0 so the origin
stays Forest (the origin rule in `BiomeTests`). A future warp (phase 2) that moves the origin's sample
position breaks that unless its offset is zero at the origin, or the test is relaxed to its real rule
("the origin is a land biome that spawns mobs").

Changing any frequency, octave count or seed offset **re-lays the whole world** for existing seeds.
Temperature was deliberately made single-octave (the extra octave steepened the gradient and squeezed
the temperate band between snow and sand), which also set the share of the world that is cold.

---------------------------------------------------------------------------------------------------

## 4. Weight pipeline (`BiomeClassifier.BiomeWeights`)

Notation: `smooth(a, b, v)` = smoothstep 0 at `v<=a`, 1 at `v>=b`.

```
continent   = ContinentNoise(x,z)
ocean       = 1 - smooth(ContinentOceanStart=-0.30, ContinentOceanEnd=-0.04, continent)   // 1 = deep ocean

SampleClimate(x, z, out t, out h)       // the only reader of TemperatureNoise / HumidityNoise
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

A second, continuous read of the same climate sample (`SampleClimate`, so colors and biomes agree),
used **only** for tint/grade/aurora, never for placement:

```
warmth01  = 0.5 * (1 - ColdOf(t)) + 0.5 * WarmOf(t)          // 0 fully cold, exactly 0.5 across temperate band, 1 fully warm
wetness01 = WetnessOf(h) = 0.5 * (1 - DryOf(h)) + 0.5 * (1 - SemiDryOf(h))
                                                              // 0 fully dry, exactly 0.5 between the ramps, 1 wet
```

* Evaluated **once per chunk column at its minimum corner** and stored in `ColumnData.CornerWarmth /
  CornerWetness` (so meshers do no noise; multiplayer clients get it streamed). The call must use
  `chunkX << Chunk.SizeShift` as float so it is bit-identical to `BiomeTint.TintsAt` on the direct path.
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
1. Forest profile (all land): `ForestBaseHeight 12 + swell (±6, 1/1400, 2 oct) + hills (HillAmplitude 32
   * hillMask * hillScale) + valley rough (±2.5) + MountainHeight`, where
   `hillScale = (1 - OpennessHillReduction 0.5 * openness) * (1 - DryHillReduction 0.6 * aridWeight)`:
   the steppe, the savanna and the tundra are plains (the three-out `BiomeWeights` runs before the hill
   sum for this).
2. Desert: lerp toward `ForestBaseHeight + swell + DuneNoise * DuneAmplitude(8)` by `desertWeight`.
3. Ocean: lerp toward `OceanShoreHeight 3 → OceanFloorHeight -44` by `ocean^1.35`, minus trenches
   (`TrenchDepth 34`), lerped in by `ocean`.
4. Shore clamp: land held ≥ `SeaLevel+1` (SeaLevel = 0), clamp eased out across `[OceanShoreWeight 0.35, 0.5]`
   of ocean weight so the coast slopes.
5. Lakes/rivers/ravines/oases/hot springs/sea stacks cut after this (`Compute`).
Mountains: ridged warped noise gated by `MountainMask`, `MountainAmplitude 60`, faded out near the
origin (`MountainSpawnClear 48..120`). A column of a snowless grass biome (`BiomeTraits.ShowsBareRock`:
Forest, Steppe, Savanna) at height ≥ `MountainRockHeight 56` is bare stone (soil 0);
`MountainSnowFullHeight 96` is full snow whatever the climate (this is how a warm biome gets snowy
peaks, via `SnowGenerator.ColdWeightAt = max(cold, altitude01)`).

### Surface/soil blocks (`ColumnCache.GenerateColumn`)
| Biome | Top | Soil | Notes |
|---|---|---|---|
| Forest, Taiga, Steppe, Savanna (default case) | grass (dirt if wet column) | dirt `DirtDepth` | sand/gravel on beach height **only** where `SurfaceWaterWeight > 0` (`BiomeTraits.RimsWaterWithSand`) |
| Desert | sand | sandstone, `SandstoneDepth 4` | soil 0 if ravine-stripped |
| Ocean | sand ≥ -4, gravel ≥ -16, else stone | same | soil depth 2 |
| Frozen | stone | stone, depth 2 | snow pass covers it; barren |

Taiga uses the **default** (grass) branch: its "snowiness" comes entirely from `SnowGenerator`, not the
biome. Steppe and Savanna differ from Forest only in tint, trees and flora.

### Water (`WaterBodies`)
* Ocean: level = `SeaLevel`. Lakes/rivers: every land biome but the desert (`CanHoldSurfaceWater`:
  Forest, Taiga, Frozen, Steppe, Savanna). Desert dry.
* `SurfaceWaterGate(desertWeight)` fades lakes/rivers out over `desertWeight 0.3..0.5`, so ponds close on
  steppe or savanna ground before the desert line (an exact line on both humid and temperate borders).
* Lakes: hashed 192-block cells, `LakeChance 0.35`, radius 11..46, depth 3..13. Rivers: warped ridge,
  `RiverBedDepth 3`, dry out as forest-profile height rises (`RiverLowStart 14 .. RiverLowEnd 30`).

### Snow/ice (`SnowGenerator`, reads `ColdWeightAt`, **not** the biome)
* `SnowColdThreshold 0.55`: snow layer placed above this cold weight (depth grows with cold, noise drift,
  max 8 layers). `FreezeColdThreshold 0.72`: surface water becomes ice. Hot springs never freeze.
* Because Taiga/Frozen need `cold >= 0.70`, snow starts (0.55) slightly **before** the biome line and
  freezing (0.72) just **after** it: a taiga column at cold 0.70..0.72 has snow but unfrozen lakes, and
  the cold edge of a steppe wears snow before the tundra starts.
* Cold trees get a snow dusting (`SnowCanopyTests`).

### Vegetation and mobs
* Trees (JSON `worldgen.biomes`): oak = forest; spruce = taiga; cactus = desert; dead_tree = desert,
  frozen, steppe (cactus wins the desert: `TreeGenerator.SpeciesByBiome` takes the first non-oasis
  species per biome in ordinal name order); stone_pine = savanna (sparse; its umbrella crown stands in
  for an acacia); palm = desert oases only; baobab grown only.
* Flowers: open Forest or Steppe (`FlowerGenerator.IsOpenMeadow`, `ForestOpenness >= MeadowMinOpenness`).
* Grass tufts: Forest, Steppe, Savanna (`FoliageGenerator.TryGetGrassTuft`), density by wetness.
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
TemperatureFrequency 1/4500   TemperatureColdStart -0.35    TemperatureColdEnd -0.05
TemperatureWarmStart 0.05     TemperatureWarmEnd 0.35       ColdBiomeMinCold 0.70
HumidityFrequency 1/1700      HumidityDryStart -0.35        HumidityDryEnd -0.05
HumiditySemiDryStart -0.05    HumiditySemiDryEnd 0.03       DryHillReduction 0.6
DuneFrequency 1/170           DuneAmplitude 8               SandstoneDepth 4
BiomeBorderDither 0.16        BiomeDitherFrequency 1/28
WaterDesertFadeStart 0.3      WaterDesertFadeEnd 0.5        WaterBodyThreshold 0.05
SnowColdThreshold 0.55        FreezeColdThreshold 0.72      SnowMaxDepth 8   SnowFrequency 1/140
OpennessFrequency 1/900       OpennessStart 0.0             OpennessEnd 0.45  OpennessHillReduction 0.5
MountainRockHeight 56         MountainSnowFullHeight 96
ForestBaseHeight 12           HillAmplitude 32              MountainAmplitude 60
WorldFeatureConfig: CottonMaxDesertWeight = 0.5 - BiomeBorderDither
PostProcessConfig: BiomeGradeStrength 0.04, ColdStart 0.45, ColdFull 0.85, EaseRate 0.5
SurfaceShadingConfig: Grass/Foliage Color corners (ColdDry/WarmDry, ColdMid/WarmMid, ColdWet/WarmWet),
  BiomeTintTextureGray 98/255
SimulationConfig: MaxLeafDecayBacklogCells 1<<18 (holds the stone pine's and the palm's felled box)
```

`HumiditySemiDry*` was tuned against the origin (raw humidity exactly 0, see §3): with the midpoint at
-0.01 the origin is Forest. A probe over ±2048 at stride 16 (default seed) counted Forest 8751,
Steppe 6794, Savanna 1756, Desert 5139, Ocean 31477, Taiga 7727, Frozen 4405 samples.

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
  `Flower` (Forest/Steppe), `Foliage` tufts (Forest/Steppe/Savanna; dead shrubs via `IsDesert`),
  `DesertWell`, `FallenLog`, `IceSpike` (frozen), `Oasis` (desert, deep), `SeaStack` (ocean), `HotSpring`
  (cold, hashed), `Reed`/`LilyPad`/`Pebble`/`Cairn`/`RuinedPillar`, underground generators.
* **Mobs:** `Simulation/MobSpawner.cs` `SpawnsMobs` (Forest, Steppe, Savanna); a supplied column reads
  `Biomes[i]`, a generated one the undithered `BiomeAt`. **A new biome gets no mobs unless it is added
  there.**
* **LOD:** `Lod/LodMeshBuilder.cs` `ClassifyGenerated` (snow top for Taiga/Frozen, ice top for Frozen,
  beach via `RimsWaterWithSand` (or Ocean), bare rock via `ShowsBareRock` above `MountainRockHeight`,
  water-sheet kind) and `ClassifySupplied` (bare rock via `ShowsBareRock` with `DirtDepth == 0`;
  beaches Ocean-only), and `Lod/LodFaceEmitter.cs` (Desert sand/sandstone, Frozen/sea/rock stone;
  Steppe and Savanna fall to the tinted grass top and the soil side band like Forest). For a world
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
6. Caches (`ColumnCache`, six `GenerationalCache` sites) only hold pure functions of the key; a biome
   change is picked up via `WorldSeed.Apply` resets, but existing **saved** chunks keep their old terrain
   (only modified chunks are saved; unmodified regenerate, so a biome change causes visible seams at the
   edge of edited areas in old saves).
7. Content rule: block behavior stays data-driven; prefer a JSON flag over a new `switch` on the biome
   where the question is "what does this block do". Biome-keyed placement is already code + tree JSON;
   a biome rule asked in more than one place belongs in `BiomeTraits`.

---------------------------------------------------------------------------------------------------

## 10. Tests and verification

* `OpenGLTest.Tests/WorldGen/BiomeTests.cs` holds the rules, all relational:
  `TemperatureRampsAreExclusiveWithATemperateBandBetween` (between the ramps a column classifies
  Forest when `SemiDryOf < 0.5`, Steppe otherwise), `HumidityRampsAreExclusive`, `TheDryAxisNeverClassifiesForest`,
  `DesertAndSnowAreNeverNearAndTheDryCorridorIsSteppeOrTundra`, `SteppeAndSavannaBothExistNearOrigin`,
  `AridWeightIsSmooth`, `BiomeBorderDitherFlipsCoherentPatchesNotSingleColumns` (with the binding-ramp
  label rule), `EveryTreeGrowsInItsSpeciesBiome` (every land biome a non-oasis species names must grow
  a tree in the window), `SurfaceWaterFadeHasClosedOnEveryDesertColumn`, and the origin rule (the
  origin column classifies Forest).
  Related: `BiomeTintTests` (`ClimateAgreesWithTheBiomeClassification`, `TheGrassDriesMonotonically`),
  `BiomeGradeTests`, `LakeTests`, `SnowCanopyTests`, `SceneryTests`, `CottonTests` (savanna only),
  `FoliageTests`, `DeadTreeTests` (`ItNeverSharesABiomeWithALeafyWorldgenSpecies`), `WorldSourceLodTests`
  (supplied steppe/savanna columns wear grass over dirt like forest; a stripped one shows rock),
  `ClientWorldgenIsolationTests`.
* Run: `dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release` (needs .NET 10 SDK; on a
  runtime-only-10 box prefix `DOTNET_ROLL_FORWARD=LatestMajor`). Follow the repo test policy: assert
  rules/relations, never constants or content literals.
* Visual: `OpenGLTest/visual-examples/biomes.json` (forest, steppe, the steppe/desert and savanna/desert
  edges, savanna from above and under the stone pines, desert, the steppe/tundra transition; a flying
  camera, its vantages from a headless probe), plus `grass-tint.json`, `biome-grade.json`, `aurora.json`,
  `snow-sparkle.json`, `water.json`, all with `VOXEL_TIME=147` (`VOXEL_SCRIPT=<json>`); use
  `VOXEL_SPAWN=x,y,z` to jump to a biome. Nothing in-game prints the current biome; a quick headless
  probe is a test calling `TerrainGenerator.BiomeAt/BiomeWeights/Climate` over a grid (do not commit it
  as a throwaway; add a rule test).
* Docs: update the "Biomes" section of `AGENTS.md` and its Layout entries when you change behavior.

---------------------------------------------------------------------------------------------------

## 11. Known quirks / limits (things the next model may want to change)

* Only **three** independent inputs; no altitude/"continental interior", no latitude-style gradient, no
  hills or mountains biome, swamp, jungle or badlands. The steppe, the savanna and the tundra are
  plains through the arid weight; there is no separate grassland height profile.
* **Undithered borders:** only the desert line dithers. Forest/Steppe/Savanna borders share one grass
  fill and change only in tint, trees and flora, and the Taiga/Frozen line lies under snow, so neither is
  dithered: the tree line and the tint step follow a smooth contour.
* The semi-dry ramp is narrow (0.08 of humidity noise, about a quarter of the dry ramp's width), so the
  forest-to-steppe tint change is quick across a border. Widening it moves the origin (§3).
* The binding-ramp dither label is exact only inland; on a coast a mislabeled column is grass either way.
* Cold biomes have no height identity (same profile as forest). Desert and Ocean are the only ones with
  their own.
* Temperature noise is 1 octave at 1/4500: climates are enormous and smooth (borders are far apart).
  Humidity is 1/1700, so humidity varies ~2.6x faster than temperature; most border crossings are humid
  flips. Borders are smooth blobs until the phase-2 domain warp.
* The origin is Forest only because its raw climate is exactly 0 (§3); spawn is additionally forced to a
  forest valley (mountain clear + lake spawn-clear radius 72).
* Desert, Taiga, Frozen and Ocean are mob-free by design (`MobSpawner.SpawnsMobs`).
* Snow/freeze thresholds (0.55 / 0.72) straddle the Taiga line (0.70); see §6. The frozen ocean's ice
  edge is still one `FreezeColdThreshold` line (the phase-4 pack-ice edge is not in).
* A supplied (imported) column's far LOD decides ice from its stored Frozen biome and its beaches stay
  Ocean-only; only the generated path has a cold weight and grass-biome beaches.
* Existing saves regenerate unmodified chunks under the new rules, so seams appear beside edited areas.
* Tree biome lists are lowercase enum names; an unknown name fails at content load.

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
