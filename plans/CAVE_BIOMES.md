# Cave biomes
> Guide: plans/README.md

## Status
CB-0 (barrier shift) and CB-1 (the cave biome field, its palette pass and the debug scenario) done: barrier -300..-400, core
-330..-370, hell top -401 (a chunk edge, so no chunk straddles hell); the overworld caves wear a frozen or desert echo down to
about -200 and crystal, fungal or magma-touched sub-biomes from there to the barrier. Tracks 3D cave biomes: caves that stop
being biome-blind, plus the barrier shift that makes room for them. Third of three efforts, in order: accessories (done), the
battle tower (`BATTLE_TOWER.md`), cave biomes. One phase per prompt; each leaves the game building and the tests green. Last
updated 2026-11-09.

## In progress
Nothing.

## Open
- [ ] **`CB-2` Shape and ores** - P1, M.
  A 9th `NoiseCaves` lattice channel scaling the thresholds and radius; a worm radius multiplier at the node center in
  `WormCarver`. Keep the Y-band ores, weight richness by biome and add one exclusive ore per biome (each its own
  `IOreLatticeRule` on `OreNoiseLattice`). The ores run before the palette pass and host only on stone, so an exclusive ore
  that should sit in biome stone must either host on the palette stone or run after `Stage.CaveBiomes`.
- [ ] **`CB-3` Atmosphere and transport** - P1, M.
  An eased cave-biome fog, ambient and particle target in `FrameLighting` beside `_hellT`/`_altitudeT` and `LayerClimate`. The
  client has no seed, so the server sends a cave-biome byte at the player's eye in `PlayerStatus` (protocol bump); singleplayer
  computes it directly (`CaveBiomes.At` with the eye column's stored biome and height).
- [ ] **`CB-4` Mobs and loot** - P2, XL.
  A biome filter at the final `if` of `MobSpawner.TryFindCaveSpawn`, biome-gated slimes and goblins, and **one new mob per biome**
  (a cave mob manager on `MobCaps`, `RealmMob` frame, Blockbench rigs from deterministic generator scripts with `--check`, saves,
  `NetEntityKind`, item debugger modes), plus per-biome chest loot tables.
- [ ] **`CB-5` Document** - P2, S.
  A `BIOMEGEN.md` section on the cave biomes (the seed table already lists 179-181), a final AGENTS.md pass once CB-2..CB-4 land;
  then delete this file's finished entries.

## Reference
- Decisions (2026-11-08): hybrid model, a surface echo biome that drifts into 3D-noise cave biomes with depth; per-biome palette,
  shape, ores, loot, mobs and atmosphere; new blocks only where needed (11 content ids left as of CB-1; reuse first); pixel-art
  palettes per tile; every mob a Blockbench `.bbmodel`.
- Findings: `ChunkTerrainFill` runs fill, `NoiseCaves`, `WormCarver`, ores, water, lava, then geodes, glow mushrooms and pools; stone
  is chosen only in `ColumnCache.GenerateColumn` and ore `IsHost` tests `block == RockBlock`. `NoiseCaves` already has a per-column
  intensity and an 8-channel stride-4 lattice. `WormCarver` bakes per 64-block cell, so biome enters through the start position or a
  node-center multiplier. The client receives only `ColumnTerrain` (heights, water levels, warmth, wetness).
- Hell's 2D-lattice-per-layer trick does not fit a continuous depth drift, hence the true 3D lattice.
- **CB-1 as built** (`WorldGen/Caves/`: `CaveBiome`, `CaveCellRole`, `CaveBiomePalette`, `CaveBiomes`; knobs `CaveBiome*`,
  `CaveEcho*`, `CaveDeep*`, `CaveSubBiome*`, `Cave*AccentRate` in `WorldGenerationConfig`):
  - Field: three 3D noises, echo drift (seed offset 179, 1/160), deep drift (180, 1/120) and the sub-biome selector (181, 1/96, 2
    octaves), sampled on a world-aligned stride-8 lattice and blended with `OreNoiseLattice.Trilinear` at the cell center. One rule,
    `CaveBiomes.Classify(y, depth, surface biome, drift, deepDrift, selector)`, each noise clamped to -1..1; `At(x, y, z, surface
    biome, surface height)` takes the column's stored, dithered `ColumnData.Biomes[i]` and ravine-cut `Heights[i]`, so the point
    query equals the stamp bit for bit across every seam (`CaveBiomeTests`).
  - Bands: nothing at or below `BarrierTopY` (-300), so the barrier and hell are untouched. Echo above `-200 + 24 * drift`, under a
    cover of `8 + 6 * (drift + 1) / 2` blocks below the column top: a frozen column echoes as FrozenEcho, a desert one (oases included,
    stored Desert) as DesertEcho, every other column stays plain. Under the echo: MagmaDeep at or below `-264 + 16 * deepDrift`, above
    it Crystal where the selector is over 0.12, Fungal under -0.12, plain between. `LayerBandTests` holds the order (barrier < deep
    line < echo line < sea level at their extreme drifts).
  - Palettes (all reused blocks, 0 new content ids): FrozenEcho frost_stone with permafrost floors; DesertEcho sandstone; Crystal stone
    with crystal_block accents (1 face cell in 10); Fungal stone with fungal_moss floors, fungal_soil ceilings and fungal_glow accents
    (1 in 60); MagmaDeep scorched_hellrock with magma_block accents (1 in 24). The glowing accents keep their own `BlockLightPalette`
    slots, so no light slot was used. Excluded: ice (translucent, would show through), sand and gravel (`falls`: a collapse on every
    dig), basalt (the iron-gated barrier block, so a cave would gate like the barrier). Every palette block is solid, opaque, occluding
    and mined by a pickaxe the barrier refuses (`CaveBiomeTests.PaletteBlocksAreSafeHosts`).
  - Volume swap plus skin accents, not a skin-only rewrite: a chunk cannot see its neighbor's carved air, so a skin-only pass would
    leave plain-stone stripes on every cave face lying on a chunk plane. So every stone cell of a non-plain biome becomes its base
    stone (a no-op for crystal and fungal), and only the floor, ceiling and accent come from in-chunk openness (`CaveBiomes.RoleAt`:
    a non-occluding cell above is a floor, below a ceiling, beside a wall). At a seam only an accent or a floor block can be missed.
  - Pass position: `Stage.CaveBiomes` in `ChunkTerrainFill`, after `UndergroundPools` and before `RuinsUnderground`, gated by
    `CaveBiomes.WriteBand` (`[BarrierTopY + 1, max(-176, MaxHeight - 8 in a frozen or desert chunk column)]`) and a `StampGateTests`
    row. It rewrites only cells that are exactly `stone`, so every ore, geode, pocket, pool, lava cell, plant and support-needing block
    is left as it was and ore counts are unchanged; the underground ruins and waystone crypts still overwrite it afterwards.
  - Stamp cost trick: each lattice cell's eight corners give each noise a range, and a `CellRule` built from the range ends decides
    every cell whose ranges do not straddle a line (exact, since every line is a monotone float expression of its noise); only cells
    near a band line or a selector threshold pay their own blend. That halved the deep-band cost.
  - Known gaps: hard vertical edges in rock where the stored column biome flips between frozen or desert and anything else (a chunk has
    no climate gradient per cell); frozen and desert cliffs and ravine walls deeper than the cover wear the echo stone, while the far
    LOD still draws stone there; accents and floors are missed only on a chunk seam; the client derives nothing from the cave biome yet
    (CB-3).
  - Scenario: `visual-examples/cave-biomes.json` (`VOXEL_TIME=147`), at the spots `CaveBiomeSpotTests` prints under the default seed.
- **CB-1 cost** (2026-11-09, dev machine, `WorldgenCostProbe.ChunkFillCostPerStage`, median of 3 runs each, mean ms per chunk; cold
  is dominated by the column cache and unchanged within noise). Ceiling: at most +25% on underground chunks.

  | class | warm before | warm after | CaveBiomes stage (warm) | cold before | cold after |
  |---|---|---|---|---|---|
  | deepStone | 0.672 | 0.655 | 0.015 (+2%) | 159.5 | 162.3 |
  | caveHeavy | 0.543 | 0.565 | 0.020 (+4%) | 111.9 | 114.6 |
  | echoCave | 0.591 | 0.671 | 0.072 (+12%) | 80.7 | 82.7 |
  | deepBand | 0.576 | 0.701 | 0.115 (+20%) | 141.9 | 141.1 |

  The stage share against the before mean is the steadier figure (the whole-chunk warm means swing by 5-10% between runs). The first
  build, which blended every stone cell, cost 0.18 ms on the deep band (+31%); the per-lattice-cell rule brought it under the ceiling.
  A microbenchmark of the stamp alone, run past JIT tiering, gave 0.05-0.16 ms per deep-band chunk, so the probe's few calls overstate it.
