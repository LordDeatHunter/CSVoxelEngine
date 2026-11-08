# Cave biomes
> Guide: plans/README.md

## Status
Designed, nothing built yet. Tracks 3D cave biomes: caves that stop being biome-blind, plus the barrier shift that makes room for
them. Third of three efforts, in order: accessories (`ACCESSORIES.md`), the battle tower (`BATTLE_TOWER.md`), cave biomes. One phase
per prompt; each leaves the game building and the tests green. Last updated 2026-11-08.

## In progress
Nothing.

## Open
- [ ] **`CB-0` Barrier shift** - P1, M.
  Push the barrier 100 blocks down: `-300..-400` (core `-330..-370`), hell top `-401`. Update the band constants in
  `WorldGenerationConfig` (`LayerBandTests` holds their order), the waystone realm classifier, the hematite and lava Y limits, the
  scenarios `barrier.json`, `hell*.json`, `layers-fog.json`, and the layer numbers in AGENTS.md. Old saves need no compatibility work.
- [ ] **`CB-1` `CaveBiomes` field, palettes, debug scenario** - P1, L.
  `WorldGen/Caves/`: `CaveBiome` enum (its own, never the surface `Biome`), `CaveBiomePalette`, `CaveBiomes.At(x, y, z)`, pure, on a
  true 3D lattice like `OreNoiseLattice` (seed offsets 179 and up), so the point query equals the stamp bit for bit across seams.
  First set: unique cave echoes for the **frozen and desert** surface biomes, plus the universal sub-biomes (crystal, fungal, and
  the magma-touched deep). Flooded caves are out. **Bands:** `echo` is the surface biome's cave from the surface down to about
  `-200`; `own` (the universal sub-biomes) and `deep` (magma-touched) fill `-200..-300`; hell is unchanged below the new barrier.
  A palette pass rewrites wall and floor cells after the carvers and before the ores (ore hosts accept the biome stones, or the
  ores run first). **Hook:** `ChunkTerrainFill`, `ColumnCache.GenerateColumn`, `HellBiomes` as the template. Needs a `WriteBand`, a
  `StampGateTests` row and the `biome-worldgen` skill.
- [ ] **`CB-2` Shape and ores** - P1, M.
  A 9th `NoiseCaves` lattice channel scaling the thresholds and radius; a worm radius multiplier at the node center in
  `WormCarver`. Keep the Y-band ores, weight richness by biome and add one exclusive ore per biome (each its own
  `IOreLatticeRule` on `OreNoiseLattice`).
- [ ] **`CB-3` Atmosphere and transport** - P1, M.
  An eased cave-biome fog, ambient and particle target in `FrameLighting` beside `_hellT`/`_altitudeT` and `LayerClimate`. The
  client has no seed, so the server sends a cave-biome byte at the player's eye in `PlayerStatus` (protocol bump); singleplayer
  computes it directly.
- [ ] **`CB-4` Mobs and loot** - P2, XL.
  A biome filter at the final `if` of `MobSpawner.TryFindCaveSpawn`, biome-gated slimes and goblins, and **one new mob per biome**
  (a cave mob manager on `MobCaps`, `RealmMob` frame, Blockbench rigs from deterministic generator scripts with `--check`, saves,
  `NetEntityKind`, item debugger modes), plus per-biome chest loot tables.
- [ ] **`CB-5` Verify and document** - P2, S.
  `StampGateTests` rows, point query equals stamp, determinism per seed, band monotonicity, a `WorldgenCostProbe` before and after
  on underground chunks (set the ms ceiling), `visual-examples/cave-biomes.json` (`VOXEL_TIME=147`, `VOXEL_SPAWN` into each biome),
  a `BIOMEGEN.md` section and seed table, AGENTS.md; then delete this file's finished entries.

## Reference
- Decisions (2026-11-08): hybrid model, a surface echo biome that drifts into 3D-noise cave biomes with depth; per-biome palette,
  shape, ores, loot, mobs and atmosphere; new blocks only where needed (about 32 of 255 content ids left, reuse basalt, sandstone,
  ice and crystal where possible); pixel-art palettes per tile; every mob a Blockbench `.bbmodel`.
- Findings: `ChunkTerrainFill` runs fill, `NoiseCaves`, `WormCarver`, ores, water, lava, then geodes, glow mushrooms and pools; stone
  is chosen only in `ColumnCache.GenerateColumn` and ore `IsHost` tests `block == RockBlock`. `NoiseCaves` already has a per-column
  intensity and an 8-channel stride-4 lattice. `WormCarver` bakes per 64-block cell, so biome enters through the start position or a
  node-center multiplier. The client receives only `ColumnTerrain` (heights, water levels, warmth, wetness).
- Hell's 2D-lattice-per-layer trick does not fit a continuous depth drift, hence the true 3D lattice.
