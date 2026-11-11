# Biomes

The climate grid, terrain shape, relief, water bodies, cold weather, vegetation, scenery and the cave biomes. Checklist: the `biome-worldgen` skill.

## Biomes

Seven biomes (`WorldGen/Biome.cs`; new values are appended, never inserted, since the ordinals ride
`ColumnFileCodec` and the network column and index `TreeGenerator.SpeciesByBiome`). **Ocean** is a
flooded basin ramping from a sandy shore down to bare stone. The six land biomes sit on a **3x3
climate grid** of temperature (cold / temperate / warm) and humidity (dry / semi-dry / wet),
`BiomeClassifier.Classify`:

|               | wet    | semi-dry | dry    |
|---------------|--------|----------|--------|
| **cold**      | taiga  | taiga    | frozen |
| **temperate** | forest | steppe   | steppe |
| **warm**      | forest | savanna  | desert |

**forest** (smooth rolling grass hills with flat valleys), **desert** (dunes of sand over
sandstone), **taiga** (a snowy spruce wood), **frozen** (a tundra: permafrost under the snow, over
stone, with sparse dead shrubs, leafless snags and occasional ice), **steppe** (an open, dry-tinted grass plain with scattered dead trees) and
**savanna** (a dry grassland of umbrella-crowned stone pines and cotton). Forest needs wet air, so
the dry axis reads desert -> steppe -> tundra and the warm row forest -> savanna -> desert: no strip
of forest ever stands between sand and snow. Selection is a pure function of (x, z) from
low-frequency noise — continentalness (low = ocean) plus temperature and humidity, which are read
**only** through `BiomeClassifier.SampleClimate` (the one reader of the two noises) while choosing a
biome, the worldgen features that follow from it and the grass/foliage tint
(`TerrainGenerator.Climate`, see "Biome-tinted grass and leaves") — in `BiomeClassifier.BiomeWeights`
(forwarded by `TerrainGenerator.BiomeWeights`); `TerrainGenerator.BiomeAt` classifies the result.
`SampleClimate` **domain-warps** the sample position before reading either noise, with one shared
OpenSimplex2 warp (`TerrainNoise.ClimateWarp`, seed offset 151, `WorldGenerationConfig.ClimateWarp*`;
the amplitude is a FastNoiseLite `SetDomainWarpAmp` value, not blocks), so every climate border bends
and wanders instead of tracing the raw noises' smooth blobs, and the biome, the cold and warm weights,
snow and ice, the tint, the grade and the aurora all read the same warped sample
(`BiomeClimateTests.EveryClimateReaderSeesTheOneSample`); never warp anywhere else. The warp is exactly
zero at the origin, and its stretch budget is a Lipschitz bound of 2: a unit step in the world moves
the warped sample by at most 2 (`BiomeClimateTests.TheWarpStretchesTheClimateBoundedly`), while the
desert/snow separation below is the tighter limit on how strong it may be. The warp (with
`TemperatureFrequency` lowered to make room for it) re-laid every seed except the origin: existing
saves regenerate their unmodified chunks under the new climate, so old worlds show seams at the edge
of their edited chunks.
Temperature is a **three-band axis**: the cold ramp (`TemperatureColdStart/End`, read by
`ColdWeight`: snow, ice, and taiga/frozen at `ColdBiomeMinCold`), a temperate band, and the warm
ramp (`TemperatureWarmStart/End`, read by `WarmWeight`; desert and savanna begin at its midpoint).
Humidity is a three-band axis too: the dry ramp (`HumidityDryStart/End`, `BiomeClassifier.DryOf`)
and the **semi-dry ramp** (`HumiditySemiDryStart/End`, `SemiDryOf`), which starts at or after the
dry ramp ends, so the two are exclusive and `semiDry` is 1 wherever any dryness is (a test rule,
`HumidityRampsAreExclusive`). `desertWeight = min(warm, dry) * (1 - ocean)` is exactly 0.5 on the
desert line on both borders, the humid one and the temperate one, which is the one line the dune
height blend, the surface-water fade (`WaterDesertFade*`) and the border dither are keyed to. A
third smooth weight, `aridWeight = semiDry * (1 - ocean)`, flattens `DryHillReduction` of the
forest-profile hills on dry and semi-dry land, so the steppe, the savanna and the tundra are plains
while every border stays a slope. The raw-noise gap between `ColdBiomeMinCold` on the cold ramp
and the warm midpoint classifies forest (wet) or steppe (semi-dry, dry) and keeps hundreds of
blocks of it between sand and snow: widen it by raising the warm ramp, never by moving the cold
one, and the warm ramp must start at or after the cold ramp ends (a test rule,
`TemperatureRampsAreExclusiveWithATemperateBandBetween`). The rules sit in the pure seams
`BiomeClassifier.Classify` / `ColdOf` / `WarmOf` / `DryOf` / `SemiDryOf`: `TheDryAxisNeverClassifiesForest`
locks the grid, and `DesertAndSnowAreNeverNearAndTheDryCorridorIsSteppeOrTundra` pins the band to
at least the full-detail load radius (`ChunkRadius * Chunk.Size`, Chebyshev distance) from any
column that would be desert if fully dry to any snow, and every dry land column to desert, steppe
or tundra. The origin's raw humidity is exactly 0 for every seed, so the semi-dry ramp's midpoint
sits just below 0 to keep the spawn a forest (`BiomeTests.OriginColumnIsForest`). Reaching that bar took a lower
`TemperatureFrequency` (1/5500, was 1/2300, then 1/4500 before the climate warp) and a
**single-octave** temperature noise (was two octaves), both of which re-laid every temperature value; single-octave output spans the raw
[-1, 1] where two-octave FBm was scaled by 1/1.5, so it also rescaled the cold ramp's share
(taiga, frozen, snow). The weights are smooth 0..1 and `TerrainGenerator.Compute` lerps the height
between the forest, desert-dune and ocean-ramp profiles by them, so a border is a slope, never a
cliff; the cold biomes ride the forest profile (only their surface differs), and a coherent
`TerrainNoise.BiomeDitherNoise` field (`BiomeClassifier.DitherBiome`) breaks the grass/sand material line into ragged patches
rather than a per-column checkerboard: inside the band around the desert line a savanna column may
take sand (it is always on the warm ramp, so sand never enters the temperate band), a steppe column
never does, and a desert column takes the grass biome across the line it actually borders — of the
two ramps that bind `min(warm, dry)`, a binding warmth gives steppe and a binding dryness savanna
(the label check in `BiomeBorderDitherFlipsCoherentPatchesNotSingleColumns`). The forest, steppe
and savanna borders share one grass fill and the taiga/frozen line lies under snow, so neither is
dithered. Shared yes/no biome rules live in `WorldGen/BiomeTraits.cs` (`ShowsBareRock`: the
snowless grass biomes whose stripped columns show stone; `RimsWaterWithSand`: the grass-fill
biomes whose lake and river rims wear sand), so the near fill and both far-LOD column paths ask one
rule. The ocean biome begins at `OceanShoreWeight`, and `Compute`
fades the land's above-the-waterline clamp out across `[OceanShoreWeight, 0.5]` — clamping only
while the biome was land put a hard step where the coast met the water, so the blended band is
ocean biome and slopes below the waterline into the sea. The ocean ramp falls to
`OceanFloorHeight` with rare warped **trenches** cut in, and its floor wears sand, gravel or stone by
depth (`OceanSandDepth`/`OceanGravelDepth`). The forest floor is a broad swell plus a positive-only
hill field gated by a hill-region mask (`Hill*`/`ForestBaseHeight`), so hills rise in patches with
flats between them. A **relief layer** (`WorldGen/TerrainRelief.cs`, BIOMEGEN §6) rides
on top, orthogonal to the climate: a continent-scale **highland weight**
(`TerrainRelief.HighlandWeight`, a smoothstep of `ReliefNoise`, seed offset 153, about 5000 blocks,
two octaves, over `HighlandMaskStart/End`) lifts the base of the forest and dune profiles
(`HighlandBaseLift`, added right after the swell) and multiplies the hill amplitude
(`HighlandHillGain`); `DryHillReduction` still applies after it, so an arid highland is a lifted
plateau rather than broken country. **Mountain ranges** rise only inside highlands: the rarity mask
(`MountainMask*`) is multiplied by the highland weight, and the warped ridge (`MountainSharpness`)
rides a lower-frequency **spine** (`MountainSpineNoise`, offset 154) and is sampled stretched
(`MountainRangeStretch`) along a hashed direction per `MountainRangeCellBlocks` cell, so a range is a
chain hundreds of blocks long; neighboring cells crossfade only in a narrow band at their borders
(`MountainRangeBlend`), and the cell lookup alone is jittered by `MountainRangeCellWarp` (offset 155)
so no straight north-south or east-west seam shows. The whole layer fades out within
`ReliefSpawnClearStart/End` (1536..2560 blocks) of the origin, so the spawn region and nearly every
scenario's ground stay as they were; highlands begin on the horizon the far LOD shows from the
spawn. A highland's lift raises the forest-profile height past `RiverLowEnd`, so its rivers dry
into washes, and its high ground reaches the rock and snow caps through the same altitude path as
before: a crest above `MountainRockHeight` in a snowless grass biome (`BiomeTraits.ShowsBareRock`)
is bare stone (`ColumnCache.GenerateColumn` reads the stripped soil, the far LOD a matching rock
cap), so it reads as rock rather than another wooded hill, and `SnowGenerator.ColdWeightAt` caps a
peak with snow. Every relief term is exactly neutral where the highland weight is 0 (no lift, a hill
gain of exactly 1, no range), so land with zero weight is bit-identical to the terrain before the
layer, except where an old out-of-highland mountain stood (those mountains are gone, so unmodified
chunks of an old save re-lay there). `TerrainReliefTests` (neutral at zero, ranges only inside
highlands, below the cloud layer, ranges run along their cell's direction, a continuous crossfade),
`TerrainReliefSeedTests` (a pure function of position and seed) and `LodReliefTests` (the LOD
nesting invariants, and the far LOD inside the chunk terrain over a steep highland range at every
level) lock it down. **Massifs and lone peaks** (`WorldGen/TerrainPeaks.cs`, phase B) are hashed
sites rolled through the shared `TerrainSites` helper (the one lake candidates use too) and summed
into the forest profile after the ranges (`WithMassif`, then `WithLonePeak`): a rare massif rolls an
absolute summit (`MassifMinSummit..MaxSummit`) and rises from `ReliefLandHeight` to it with a pointed,
spurred body, and a lone peak stands only where the pre-feature profile (`PreFeatureLandHeight`) is
flat over its rim (`PeakSiteFlatness`), never on a massif; a site's acceptance never reads `LandHeight`,
so nothing recurses, and its cached verdict (`MassifSiteAt`/`PeakSiteAt`) is reset by `WorldSeed.Apply`.
Both terms are exactly zero outside their discs and inside the spawn clear, so land there is
bit-identical, and the soft `SoftCeiling` (`ReliefCeilingHeight`, knee `ReliefCeilingKnee`) keeps any
spur under the clouds (`TerrainPeaksTests`, `TerrainSitesTests`, the site facts in
`TerrainReliefSeedTests` and `LodReliefTests`). **Islands and archipelagos**
(`WorldGen/TerrainIslands.cs`, phase C) rise out of deep water by lowering the ocean weight itself:
`BiomeClassifier.BiomeWeights` takes the pre-island weight (`PreIslandOceanWeight`, the old ocean
expression), asks `TerrainIslands.LandWeight` for an island land weight (the max of a large-island
channel, `IslandNoise` offset 159, and a small-island one, `IslandSmallNoise` offset 160, scaled by how
far past the shore band the water is), folds it as `pre * (1 - island)` only when that weight is above
0 and hands the result to `WeightsForOcean`, so every consumer of the ocean weight (the
classification, the height lerps and shore clamp, water bodies, trees, mobs, the far LOD) sees island
land as ordinary land of its climate biome, sand-rimmed through the Ocean-classified 0.35..0.5 rim.
A stateless hashed **archipelago** footprint (one elongated ellipse per `ArchipelagoCellBlocks` cell,
no cache) lowers the small channel's threshold and raises its gain, so islets string out in chains.
`LandWeight` is exactly 0 wherever the pre-island weight is at or below `OceanShoreWeight` (checked
before any read) and inside `ReliefSpawnClear*`; `ContinentNoise` and `SampleClimate` are untouched. A
massif or lone-peak site is accepted all-or-nothing on the pre-island weight (wholly continental, or
wholly on saturated island land), and an island site (`TerrainSite.OnIsland`) is scaled by the island
land weight at the column, so `BiomeWeights`, the peak terms and `LandHeight` are bit-identical on the
continent and the coast band by construction (`TerrainIslandsTests`, the island facts of
`TerrainPeaksTests` (`ASiteNeverStraddlesTheIslandFold`, `AnIslandSiteNeverRaisesTheShoreBandOrTheContinent`),
`TerrainReliefSeedTests.TheIslandTermIsAPureFunctionOfPositionAndSeed` and the island, archipelago and
shore-pit facts of `LodReliefTests`). `visual-examples/terrain-shape.json` (`VOXEL_TIME=1090`,
so the long run stays in daylight) films the origin valley, a highland interior, a range along and
across, an arid plateau, a highland border, a river wash, a range crossing a cell border, a far-LOD
vista, a massif from afar, its flank and its crown, a lone peak and a massif in the far LOD, and an
island from offshore, its beach, a sand knoll, an archipelago chain and islands in the far LOD
(`island_far`, `island_beach`, `island_knoll`, `archipelago_chain`, `island_lod_vista`). A low-frequency
**openness** mask (`Openness*`) flattens the hills a little and thins the trees toward none, so the
forest alternates between rolling woodland and open grass instead of one uniform canopy
(`TreeGenerator.TryGetTree` reads `TerrainGenerator.ForestOpenness`, scaled per species by its
`worldgen.openness`, so a taiga keeps more of its spruce, while the desert's cactus takes all of it and spacing 4, so a desert alternates
between cactus groves and empty dunes). On land a hashed lake bowl and a warped river ridge carve below
their own water level and flood only those (every land biome but the desert; both fade out toward a
desert so a pond closes on its own rim) — see the Water invariant below. Far from the spawn (past
`GreatLakeSpawnClearRadius`, 1536 blocks) rare **great lakes** (`WorldGen/GreatLakes.cs`, phase D)
spread over lobed bowls a few hundred blocks across: their surface is chosen from their rim like a
pond's, they stand only on lake-holding ground with no ocean, desert, massif or lone peak inside and no
interior rise over the rim, ponds give way to them and a ravine guard keeps ravines out of their banks.
A river reaching one never refuses it (phase E): past an elevated lake's shore the river level steps
down a block at a time over a ramp from the lake's own surface to `SeaLevel`, so the river meets the
lake at its surface, and an outflow channel may leave the lake's low side down the same ramp; a lake
whose rivers cannot step down from it rests at `SeaLevel` instead. A cold great lake freezes into the
pack ice like any water. `visual-examples/terrain-shape.json` shoots them (`great_lake_shore`,
`great_lake_height`, `great_lake_bay`, `great_lake_island`, `great_lake_river`, `great_lake_elevated`,
`great_lake_outflow`, `great_lake_inflow`, `great_lake_lod_vista`).
A desert column is sand over `SandstoneDepth` sandstone blocks, then stone; a frozen column is a
`permafrost` top and two cells of permafrost soil over stone, under the snow layer the snow pass
lays (a ravine strips it down to bare stone, like a stripped desert column); forest, taiga, steppe and savanna share the grass-over-dirt fill
(sand or gravel only on the rims of real water); a ravined (bare-stone) column keeps those rules.
Existing saves regenerate their unmodified chunks under the current rules, so a biome change shows
as seams at the edge of the edited (saved) chunks of an old world.

**Cold weather** (`WorldGen/Features/SnowGenerator.cs`) runs last of the surface features and reads
`SnowGenerator.ColdWeightAt` — the climate's `TerrainGenerator.ColdWeight` (a biome-generation input)
so the snow line and the pack-ice band fade in as a column cools rather than snapping at a biome border,
**raised by altitude** above `MountainRockHeight` and full at `MountainSnowFullHeight`, so a warm
biome's peak wears a snow cap and the near stamp, the drawn depth and the far LOD all read the one
function. It freezes a cold column's surface water into a
walkable ice sheet (the top water cell becomes `ice`), lays one **layered snow** cell above the
solid ground (or the ice) and dusts the canopy tops of a cold column's trees, so a taiga or frozen
forest reads white from above (`SnowCanopyTests`). Freezing is not one line but a **pack-ice band**:
at or below `PackIceColdStart` water stays open, past `FreezeColdThreshold` it is a solid sheet, and
in between a coherent floe noise (`TerrainNoise.PackIceNoise`, seed offset 152, `PackIceFrequency`,
a wavelength of tens of blocks) decides ice or open water per column against a smoothstep of the
cold, so the edge breaks into floes and open leads whose ice share rises with the cold until they
close into the sheet. `SnowGenerator.IsFrozenWater(x, z, cold)` is that one rule, a pure function of
(x, z, seed) that the near stamp and the generated far LOD's `_iceTop` both call, for the sea, lakes
and rivers alike; hot springs never freeze (tested last, so the LodGen threads pay
`HotSpringGenerator.IsSite` only for water that would otherwise freeze), and open water gets no
snow. The far LOD's `_snowTop` stays the plain snow line (`SnowColdThreshold`), since it also
decides snow on land; because `PackIceColdStart >= SnowColdThreshold`, every frozen sheet cell is
snow-covered there exactly as the near stamp covers every ice cell it writes (`PackIceTests`). A snow cell's depth is drawn from `SnowGenerator.DepthAt` when its state
byte is 0 and from the state byte when a player stacks it (`SnowShape`); each layer is 1/16 of a
block, so a full stack is solid and partial layers are walked through (`BlockType.IsSolidCell`).
`WorldGen/Features/IceSpikeGenerator.cs` grows occasional single-column ice spikes on the frozen plains.

`ColumnData.Biomes` carries the per-column classification for the chunk mesher and the far LOD
(`ColumnCache.GetBiomes`); the LOD samples it per cell and wears the matching tile (snow for a cold
cell, snow-covered ice for a frozen sea sheet — snow tile when snow-covered, else ice, and never
flagged water, since the flag alone selects the fragment-shader water model whatever the tile; on
the generated path a sheet cell is frozen exactly when `SnowGenerator.IsFrozenWater` says so at its
center, so the far pack-ice edge carries the same floes and leads as the near one, while a supplied
column's sea ice still follows its stored Frozen biome), so a
distant desert and sea floor agree with the near chunks. Its beach and bare-rock tops ask the same
`BiomeTraits` rules as the near fill (`RimsWaterWithSand`, `ShowsBareRock`; a supplied column's rock
rule is `ShowsBareRock` with a zero `DirtDepth`, its beaches stay ocean-only), and steppe and
savanna fall through to the tinted grass top and the soil side band, exactly like the forest. A
frozen cell's soil side band and lake bed wear permafrost on both column paths (stone where the
cell-center column's `DirtDepth` is zero: `LodMeshBuilder`'s stripped-soil grid, read from the
prefetched `ColumnData` on the generated path too), while its top stays snow.
Vegetation is biome-gated. Trees come from the species files (`assets/data/trees`), each planted
only in its `worldgen.biomes`: `oak` in the forest, `spruce` in the taiga, the leafless `cactus`
(a thick stem whose few arms bend upward, wearing its own `cactus_side`/`cactus_top` tiles)
in the desert (where it wins over the dead tree, since
`TreeGenerator.SpeciesByBiome` takes the first species in name order), the leafless `dead_tree` in
the desert, the frozen tundra and the steppe, and `stone_pine` in the savanna, whose umbrella crown
stands in for an acacia (its reach equals the palm's, so `ReachChunkRadius` did not grow), while
`baobab` never generates (`EveryTreeGrowsInItsSpeciesBiome` checks every land biome a species
names); the forest and the steppe grow flowers in open meadows (`FlowerGenerator`), grass tufts
grow in the forest, steppe and savanna, and dead shrubs grow sparsely on desert sand and on the
frozen plains' permafrost (`FoliageGenerator.TryGetDeadShrub`). A tundra shrub replaces the snow cell
the snow pass already laid over permafrost: the one exception to the into-air stamp rule, confined
to permafrost ground (snow over any other ground is never replaced).
Wild cotton grows in **clumps** in the savanna (`CottonGenerator`: a savanna center whose desert
weight stays under `CottonMaxDesertWeight`, where the savanna/sand dither starts, so a clump never
lands on dithered sand), stamped after the pebbles and before the flowers and foliage. Placement is the reusable
`PlantClumps` rule over a `PlantClumpSpec`: one candidate clump per coarse cell, its center jittered
inside the cell (so centers are at least `MinCenterSpacing` apart), a disc whose `MinMembers` nearest
cells always belong and whose rest thin toward the rim, and a column asks every cell that can reach
it, so a clump is never cut at a cell or chunk border. A pure hash of (cell, seed), no noise, so no
reseed; a later clumped plant needs only its own spec and gate. Like flowers, the far LOD draws none.
How a tree is grown, stamped, felled and decays is in `### Trees`. Mob spawning (`MobSpawner`,
shared by slimes and goblins) takes a column whose biome spawns mobs (`MobSpawner.SpawnsMobs`:
every land biome, so only the ocean spawns nothing; a supplied column's stored biome, a generated column's undithered
`TerrainGenerator.BiomeAt`). Steppe and savanna hold lakes and rivers like the forest
(`WaterBodies.CanHoldSurfaceWater`: every land biome but the desert).

**Scenery and landmarks** are pure per-cell functions like the rest of worldgen — each decided
from a hash of (cell, seed) or a `PlantClumps` clump, stamped per chunk and clipped at its border,
into air or stone only, and never drawn by the far LOD (`SceneryTests`, `PillarWellTests`,
`GeodeMeteorTests`, `OasisTests`, `PoolCairnTests`, `PondPlantTests`, `CaveLifeTests`,
`SpringStackTests`, `GravelPocketTests`, `SnowCanopyTests`). Each stamp also declares a conservative
`WriteBand` (a `StampBand`: the world-y range its `Stamp` can write, derived from its own code, never
from tuning), and `ChunkTerrainFill.GenerateBlockData` skips a stamp whose band misses the chunk, so the
output is bit-identical; a new feature stamp must add its band and its row in `StampGateTests`' feature
table (`stampGates: false` is the test-only ungated path, PERF_TODO G6). A stamp whose site can sit in
a neighbor chunk column (fallen logs, boulders, wells) gets only an upper bound from
`ChunkTerrainFill.NeighborhoodMaxHeight`, and the snow canopy pass (`SnowGenerator.StampCanopy`, beside
the gated `StampGround`) is never gated, since its leaves may come from neighbor trees:
- **Fallen logs** (`FallenLogGenerator`): a rare hollow log of 3-5 cells lying along X or Z on the
  floor, one per 32-block cell (a log never leaves its cell, so two never cross). Its block is the
  `hollow_<species>_log` of the **start column's** biome tree species (`worldgen.fallenLog`,
  `FallenLogGenerator.HollowLogFor`; `TryGetPlacedLog` is the one placement rule the stamp and the
  tests share): oak in the forest, spruce in the taiga, stone pine in the savanna. A biome whose
  species declares none gets no fallen logs (the desert's cactus, the dead tree of the tundra and
  steppe, the oasis palm), and neither does a start column whose stored, dithered surface biome
  differs from `TerrainGenerator.BiomeAt` (so no stone pine log lies on dithered desert sand) or a
  water column. The hollow log is an ordinary `facing: "horizontal"` model block, not tree wood, so
  it never welds, fells or decays: the shared `block/hollow_log` model is a square tube authored
  along Z (3 px walls round a 10 px hole, open at both ends, each wall split at the cell's middle so
  no quad covers a whole side), its outside the species log's own side tile and its inside and end
  rings the placeholder `hollow_<species>_log_inner`/`_end` tiles. The state byte holds a facing (`FacingAlongZ` = north
  or `FacingAlongX` = west, picking the pre-turned model and boxes), never a `WoodState`. Its four
  wall `hitbox`es with `collidesByHitbox` leave the hole open to the raycast and the player body
  (only the player: mobs, drops and projectiles see a full cell). Its `hardness` is half the
  species log's full-cube one with the same axe gate, so it mines in half the time; it is
  worldgen-only (`hasItem: false`), drops the plain log (its own loot table) and is picked as it
  (`pickAs`). The far LOD draws none. Known gap: snow does not settle on a taiga log, since the log
  takes the cell the snow pass would cover. `HollowLogTests` (half hardness and the same gate, the
  bark outside, the plain-log drop, the pick, the open shell that can be stood on, the stamped
  facings) and `SceneryTests` (a whole stamped log in every biome that has one, turned along its
  axis); `visual-examples/hollow-logs.json` (`VOXEL_TIME=147`) frames one in each biome.
- **Boulders** (`BoulderGenerator`): a rare 2-4 cell blob of stone or cobblestone on the forest,
  taiga, steppe and savanna floor.
- **Cairns** (`CairnGenerator`): a three-cell cobblestone stack with a one-stone pebble hat on a
  local summit.
- **Ruined pillars** (`RuinedPillarGenerator`) and the **desert well** (`DesertWellGenerator`):
  broken stone columns on the surface, and the desert's ring-and-roof well dug into sand over
  sandstone.
- **Sea stacks** (`SeaStackGenerator`) and **hot springs** (`HotSpringGenerator`): stone stacks
  rising out of the sea near a shore, and steaming water bodies in caves.
- **Oases** (`OasisGenerator`): a desert water disc with a grass ring and palms — the one place the
  `palm` species (a worldgen species with `worldgen.oasis`) is planted, since the ordinary desert
  placement never picks it.
- **Reeds** (`ReedGenerator`) and **lily pads** (`LilyPadGenerator`): shore reeds on the shallow
  water edge and pads floating on ponds, both over `PlantClumps`.
- **Glow mushrooms** (`GlowMushroomGenerator`) on dry, dark cave floors, and **cave formations**
  (`CaveFormationGenerator`): stalactites hanging from ceilings and stalagmites standing on floors,
  both `needsSupport` and `flowResistant` (rock: a flow leaves them standing).
- **Geodes** (`GeodeGenerator`) and **meteor craters** (`MeteorGenerator`): a hollow, crystal-lined
  sphere deep in solid stone (its `crystal_block` is the one non-ore block on the reflective
  material path), and a blast crater with scattered meteor rock.
- **Gravel and sand pockets** (`GravelPocketGenerator`): blobs of loose rock buried in stone, both
  `falls` blocks, so mining into one can bring a small collapse down.
- **Underground pools** (`UndergroundPoolGenerator`): still water on carved cave floors.

The tuft density of the `tall_grass`/`dead_shrub` plants runs with the climate's wetness
(`FoliageGenerator.GrassDensityAt`, `GrassDensityDry`..`GrassDensityWet`) and every flower stands in
an open-forest clump (`FlowerGenerator`, the `PlantClumps` rule), spread inside it at most one per
`MeadowFlowerSpacing` square patch at a spot off the patch's last row and column, so no two flowers touch
(`FlowerGenerator.IsSpreadSpot`) (`MeadowTests`).

**Cave biomes** (`WorldGen/Caves/`, plans/CAVE_BIOMES.md CB-1, tuning `CaveBiome*`/`CaveEcho*`/`CaveDeep*` in
`WorldGenerationConfig`) dress the overworld's carved caves. `CaveBiome` is its own enum, never the surface `Biome`.
Three 3D noises (seed offsets 179 echo drift, 180 deep drift, 181 selector) are sampled on a world-aligned stride-8
lattice and blended with `OreNoiseLattice.Trilinear`, and the one rule `CaveBiomes.Classify` turns them, the cell's y,
its depth under its column's top and the column's stored, dithered surface biome into a cave biome:
- **Echo**: from a jittered cover (`CaveEchoCoverBlocks` plus up to `CaveEchoCoverJitterBlocks`) under the column top
  down to `CaveEchoBottomY + CaveEchoDriftBlocks * drift`, a frozen column's caves are FrozenEcho (frost_stone,
  permafrost floors), a desert column's DesertEcho (sandstone), every other column's plain.
- **Sub-biomes**: under the echo, MagmaDeep (scorched_hellrock, magma_block accents) at or below
  `CaveDeepTopY + CaveDeepDriftBlocks * deepDrift`; above it the selector picks Crystal (stone with crystal_block
  accents), plain or Fungal (fungal_moss floors, fungal_soil ceilings, fungal_glow accents).
- Nothing at or below `BarrierTopY`, so the barrier and hell are untouched. Every palette block is reused (no new
  content id) and is solid, opaque and occluding; ice, sand, gravel and basalt are deliberately not used.

`CaveBiomes.At(x, y, z, surfaceBiome, surfaceHeight)` (the column's `ColumnData.Biomes[i]` and `Heights[i]`) equals
what `CaveBiomes.Fill` writes into the chunk's `CaveBiomeMap`, bit for bit across seams; the map is filled once, right after
the column fill (`Stage.CaveBiomeMap`), and read by every cave consumer below. The pass (`Stage.CaveBiomes`) runs after `UndergroundPools`
and before `RuinsUnderground`, gated by `CaveBiomes.WriteBand` and a `StampGateTests` row, and rewrites only cells that
are exactly `stone`, so ores, pockets, geodes, pools, lava and plants are untouched. It swaps a non-plain biome's whole
stone volume to its base stone and takes the floor, ceiling and accent from in-chunk openness (`CaveBiomes.RoleAt`),
so a cave face on a chunk plane still wears the biome stone. A per-lattice-cell `CellRule` decides most cells from the
corner ranges, exactly; only cells near a line pay their own blend, and a lattice cell wholly under every echo line is
classified a row at a time (`FillSubBandCell`). The cumulative worldgen cost against the build before the cave biomes is in
plans/CAVE_BIOMES.md (Reference, "Cumulative cost (CB-5)"). `CaveBiomeTests`, `CaveBiomeSpotTests` (prints a
spot per biome), `LayerBandTests.CaveBiomeBandsLieAboveTheBarrierInOrder`; `visual-examples/cave-biomes.json`
(`VOXEL_TIME=147`), which also films each sub-biome's mob idle in its own atmosphere.

**Cave shape and ores (CB-2)**, read from the same map: crystal cells open bigger cheese rooms, fungal cells wider spaghetti
tunnels and magma-touched cells both cheese and caverns (`CaveShape`, scaled per region by `NoiseCaves`' ninth channel), and a
worm node's radius is multiplied by its center's biome factor (`WormCarver.RadiusFactorAt`, exactly 1 above the sub band, so
worms, which rarely reach it, mostly keep their size); plain and echo caves carve exactly as before. The four Y-band ores shift
their thresholds per cell by biome (`CaveRichness`: malachite richer in crystal, coal in fungal and desert echo caves but leaner
in the magma deep, hematite richer there, tin in both echoes), and three exclusive ores (`CaveOres`, seeds 183-185, its own
`Stage.CaveOres` right after `Tin` with a `CaveOres.WriteBand` and a `StampGateTests` row) grow only in plain stone of their own
biome below their highest y, before the palette pass, so they survive inside the magma deep's scorched hellrock. Biome borders
step the cave size and ore richness hard, like the palette. `CaveShapeTests`, `CaveOreRichnessTests`, `CaveOreTests`,
`LayerBandTests.EveryExclusiveCaveOreGrowsInsideItsBiomesReach`.

Issue #10 regular spaghetti uses a 0.13 noise radius (variation 0.02), patch tunnels 0.18, and Y scale 1.2 instead of 1.6. Wider cores and less vertical compression provide standing clearance; surface gates and hell remain separate. Newly generated chunks intentionally change for existing seeds; edited saved chunks keep their original snapshots.

Intermediate cave pockets use seed offset 187, wavelength 36 and Y scale 1.2. Their threshold falls smoothly 0.64 to 0.38 over cover 6..320; cheese falls 0.66 to 0.46 over 8..280. The surface fade completes at cover 6 (starts at 2), retaining a protected roof outside entrances. This adds rooms between tunnels and large caverns and increases openness with depth without changing the surface heightmap.

Frozen echo volumes (issue #10) also occupy overlapping 3D ellipsoids: deterministic 128-block sites rooted in frozen surface-biome samples (the ordinary shared climate classifier), horizontal radius 96, vertical radius 112, clipped above -400 and at least 8 blocks below each local surface. This widens the material laterally into nearby columns and vertically below the original echo line, including entirely uncarved rock. `CaveEchoVolumes` applies the same containment to `At` and `Fill`; the site cache includes the seed and holds only pure values. Original echo bands remain present.

Desert sandstone uses the same 3D echo-volume rule and dimensions as frozen stone (issue #10), seeded from desert surface-biome samples. Both maps cover uncarved stone below the old echo band and may spread laterally under a different surface biome. The point query and map use the same deterministic site priority even if volumes overlap.

DesertEcho now uses its own carve: the existing two tunnel fields sampled with X scaled 0.28, radius 0.075 versus regular 0.13. Large ellipsoid pockets occupy 96x64x96 site cells, radii 25..36 and half-height 12..20, always with at least 3 blocks of wall before each cell boundary. Occupancy rises 0.35 to 0.8 with cover 20..320. Desert bypasses regular cheese/caverns; tunnels may link rooms but the rooms never create an endless open layer. Salt 0x5E81, no additional noise seeds; hell is unchanged.
