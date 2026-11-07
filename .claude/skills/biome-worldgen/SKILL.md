---
name: biome-worldgen
description: Rules and checklist for changing CSVoxelEngine biome, climate, terrain-shape or water-shape generation - adding a biome, a noise, a hashed site (peak, lake, island), or retuning ramps. Principles every worldgen change must keep, the new-biome checklist, seed-offset and tuning traps. Read BIOMEGEN.md for the current state first.
---

# Biome and terrain-shape worldgen

`plans/BIOMEGEN.md` is the map of what exists today, every tuning constant and the §9 invariants. This skill is the procedure and the principles; read the BIOMEGEN sections it points to before editing.

## Principles (every change keeps these)

- Pure function of (x, z, seed). Smooth weights summed or lerped, never switched; classify only at the end.
- Every new term is exactly zero where its mask is zero, and returns its input float unchanged there, so untouched land stays bit-identical (prove it with an identity sweep against a copy of the tree before the change; a seed re-lays only inside the masked region).
- Relief is not a biome: the `Biome` enum does not grow for height features. Altitude reaches rock and snow only through `MountainRockHeight` / `SnowGenerator.ColdWeightAt`, so the desert/snow separation invariant is never touched.
- Large features respect the spawn clearing (`TerrainRelief.SpawnClear`, `ReliefSpawnClear*`).
- The far LOD follows heightmap and `WaterLevel` by construction, but re-verify LOD level nesting and `LodSkyExposure` against any new height range (`LodReliefTests`).
- Climate is read only through `BiomeClassifier.SampleClimate` (the one warped sample). Never warp anywhere else, and never warp `ContinentNoise` for climate (it re-lays every coast).
- Hashed sites go through `TerrainSites` + a `GenerationalCache` verdict; acceptance tests must never read the function they feed (`LandHeight`), or they recurse. Cache factories are static non-capturing delegates; only pure-of-key values live in the cache.
- Price each phase with the `perf-capture` skill: `LandHeight` and `BiomeWeights` run per column cell, per far-LOD cell and per tree candidate. Record the numbers in `plans/PERF.md`, not in BIOMEGEN.
- One phase per prompt (plan-and-implement workflow), ending with a green Release build and test run, new shots in `visual-examples/terrain-shape.json` or `biomes.json`, and BIOMEGEN §4/§6/§7/§9/§11 plus AGENTS.md "Biomes" updated.

## Traps

- **Next free noise seed offset is 168** (153-161 are relief, mountains, peaks, islands, great lakes; 162 the barrier band's patches; 163 the hell caverns; 164 the hell biomes; 165 the hell pools; 166-167 the solid cloud patches). A new `TerrainNoise` instance needs a new unused offset. `SnowGenerator.DriftNoise` shares offset 89 with `BiomeDitherNoise`; do not "fix" it, it re-lays snow drift.
- Changing any frequency, octave count, warp amplitude or seed offset re-lays the whole world for existing seeds; existing saves show seams at the edge of edited chunks. Say so in BIOMEGEN §11.
- The origin's raw temperature and humidity are exactly 0 (OpenSimplex at the lattice origin); the semi-dry ramp midpoint sits just below 0 to keep spawn Forest. A change moving the origin's climate sample breaks `OriginColumnIsForest`; relax the test to its real rule, never tune toward one seed.
- Widen the temperate band by raising both `TemperatureWarm*` constants together; never move the cold ramp. The climate warp is bounded by stretch <= 2 and by the desert/snow load-radius separation (the tighter limit); if the separation test fails lower the warp or `TemperatureFrequency`, never the rule.
- `BiomeAt()` is undithered; `ColumnData.Biomes[]` is dithered. Read the latter for "what the world placed".
- Const toggles are written as multipliers (like `BiomesEnabled`) so the compiler does not flag dead branches.
- Keep the tallest land under `SkyConfig.CloudHeight` (160) and the far LOD under the AddQuad vertex ceiling over the steepest highland.

## Checklist: adding a biome

1. Append the `Biome` enum value (never reorder: ordinals ride `ColumnFileCodec`, the network column and `TreeGenerator.SpeciesByBiome`).
2. `BiomeClassifier.Classify` plus any new weight/ramp (read through `SampleClimate`); config in `WorldGenerationConfig`. Keep ramps smooth and exclusive where they share an axis, borders sloped, the desert line at 0.5. Decide whether `DitherBiome` touches it.
3. `LandHeight` if it has its own profile (blend by a weight, don't switch).
4. `ColumnCache.GenerateColumn` surface/soil, and `BiomeTraits` (`ShowsBareRock`, `RimsWaterWithSand`) if it shares the grass fill.
5. `WaterBodies.CanHoldSurfaceWater` / `WaterLevelFor` if water behaves differently.
6. Features: tree species JSON `worldgen.biomes` (check tree reach), generators that gate on biome (flowers, tufts, cotton, boulders), `MobSpawner.SpawnsMobs`.
7. LOD (`LodMeshBuilder` + `LodFaceEmitter`) so distance matches near terrain, and the world-source-supplied column path.
8. Tint palette / grade if it should differ (they key on climate, not biome; a new palette row must keep the grass monotone).
9. Relational tests in `BiomeTests` computed from the seams; update `AGENTS.md` and `plans/BIOMEGEN.md`; frame it in `visual-examples/biomes.json`.
