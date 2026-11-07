# Layers: Hell below, Sky above

> Guide: plans/README.md

## Status

Planned, nothing built. A Nether- and Aether-like pair of realms inside the one world, as vertical
layers (not dimensions). Dig or build to reach them; no portals. Old saves do not matter (build as
day 0). Last updated 2026-11-07.

## In progress

Nothing.

## Open

### Phase 1: Foundations

- [ ] **`L-1` Layer bands in config.** P0, S. Every Y range below (barrier, hell, cloud slab, sky
  islands) lives in `WorldGenerationConfig`/`SkyConfig`, nowhere else. **Hook:** config only.
- [ ] **`L-2` Iron pickaxe item.** P0, S. Item + `pickaxe` tag entry + tier above the copper one;
  creative catalog grants it. **No recipe, no smelting** (see Future ideas). **Hook:** `add-content`.
- [ ] **`L-3` Soft-landing and sink block flags.** P0, M. Block JSON flags (fall damage negated on
  landing; the body sinks slowly up to 50 % into the block), flat `BlockType` mirrors.
  **Hook:** `BlockDefinition`, `BlockLoader`, `PlayerPhysics`, `FallTracker`, server `MovementRules`
  (it must accept the sink and the landing).
- [ ] **`L-4` Depth-based fog and tint hook.** P0, M. Eye in a hell layer eases to a red-tinted fog
  (like the underwater/lava eye state); altitude shifts the sky palette. **Hook:** `FrameLighting`;
  the `SkySample` copies change together (`shader-sync` skill).

### Phase 2: Hell slice

- [ ] **`L-5` Barrier band.** P1, M. Y -200..-300, iron pickaxe to mine. Chance ramps 0 to 100 %
  over -200..-230 and back over -270..-300, modulated by coherent noise; 100 % from -230 to -270.
  A pure function of position and seed. **Hook:** `ChunkTerrainFill` stamp with a `StampBand`
  (and its row in `StampGateTests`), new noise seed offset (`biome-worldgen` skill).
- [ ] **`L-6` Hell pockets, endless.** P1, L. Below the barrier: big open cavern pockets stacked
  downward, separated by hellrock slabs (iron-pickaxe breakable, high hardness), repeating forever.
  Layer index scales lava, density and mob difficulty. 3D noise on a strided lattice like
  `NoiseCaves`. **Hook:** `ChunkTerrainFill`, new generator under `WorldGen/`.
- [ ] **`L-7` Hell biomes.** P1, M. 2-3 biomes chosen by **3D** noise (they vary by layer and xz).
- [ ] **`L-8` Hell palette.** P1, M. Full block palette in JSON: hellrock, surface and soil per
  biome, emissive blocks (new `BlockLightPalette` slots, 8-color cap), lava reuse. Mind the 255 id
  cap (`add-content` skill).
- [ ] **`L-9` Hell flora.** P1, M. Fungal/charred tree species (tree JSON), ground glow plants,
  hanging vines (`support: "above"`).
- [ ] **`L-10` Hell mobs.** P1, L. Two, harder than goblin/slime: a melee brute and a ranged/fire
  caster. Entity JSON, manager, clips (`item-debugger`), realm spawn search scaled by layer index.

### Phase 3: Sky slice

- [ ] **`L-11` Solid cloud patches (~Y200).** P1, M. Patchy real blocks, partly transparent, using
  the `L-3` flags. Above the relief ceiling (148) and the shader clouds (160).
- [ ] **`L-12` Sky islands (Y ~320-420).** P1, L. Island zones from the archipelago noise approach
  plus hashed island sites (`TerrainIslands`, `TerrainSites`); wide ocean-like gaps; rare big zones
  hold the hardest mobs. A second thick cloud sea under them (visual).
- [ ] **`L-13` Sky biomes and flora.** P1, M. Two by noise: meadow and crystal/frost highland; broad
  and crystal tree species, plants, vines; full sky palette.
- [ ] **`L-14` Flight in `Mob`.** P1, L. A flying movement mode plus animator support.
- [ ] **`L-15` Sky mobs.** P1, L. Two: a flyer and a tough ground bruiser; spawn rules scaled by
  island-zone size.

### Phase 4: Netcode and polish

- [ ] **`L-16` Network and saves.** P1, M. New entity kinds in `EntityList` (protocol bump), flight
  state on the wire, the cloud-sink rule in `MovementRules`, `entities.json`, loopback tests.
- [ ] **`L-17` Tuning and docs.** P2, S. Visual scenarios for every phase, tuning pass, an AGENTS.md
  section, then prune this file.

## Reference

### Decisions

- **Travel:** dig or build only. Barrier needs the iron pickaxe; the sky has no gate (skill
  progression through gaps and harder mobs).
- **LOD:** simplest possible for now (realms exist only in full-detail chunks, islands pop in).
  The real fix is `M-29` in `TODO.md`.
- **Hell light:** dark, lit by lava and emissive blocks, red fog.
- **Fall rules:** hell has no bottom, normal fall damage. Sky islands: just fall. The solid cloud
  layer negates fall damage and sinks the player to 50 %.

### Future ideas (discuss later)

Ores, structures/fortresses, loot, armor and tool tiers, iron smelting recipes (needs the iron
pickaxe recipe), heat, low gravity and void hazards, other realm mechanics, portals or natural
passages, a bottom to hell or deeper themes.
