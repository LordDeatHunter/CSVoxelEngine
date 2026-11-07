# Layers: Hell below, Sky above

> Guide: plans/README.md

## Status

Phase 1 (foundations) done; phase 2 under way (`L-5`, `L-6` done). A Nether- and Aether-like pair of realms inside the one world, as vertical
layers (not dimensions). Dig or build to reach them; no portals. Old saves do not matter (build as
day 0). The layer Y bands live in `WorldGenerationConfig` (`Barrier*`, `Hell*`, `CloudSlab*`,
`SkyIsland*`), the cloud sea in `SkyConfig.SkyCloudSea*` and the difficulty step in
`MobConfig.LayerDifficultyPerLayer`; `LayerBandTests` holds their order. The `iron_pickaxe` (power 3,
creative only) is the barrier's tool. The `softLanding` and `sink` block flags (`SinkLayer`,
`PlayerConfig.SinkBlockSpeed`) are ready for the cloud blocks of `L-11`; the server accepts the sink.
The eye's height already tints the sky and fog (`LayerClimate`, `FrameLighting`, no shader change):
red in hell, a deeper blue over the cloud slab. The barrier band is generated (`BarrierGenerator`, the
`barrier` block, iron pickaxe only): a gapless core from -230 to -270 that walls up caves and fluids
too, patchy noise ramps above and below it, stamped last. Under it hell is generated
(`HellGenerator`, `WorldGen/Hell/`): 96-block layers repeating downward forever, a cavern pocket on a
3D lattice noise over a 16-block slab of `hellrock` (iron pickaxe only), deeper layers more open with
more lava ponding their floors' dips; a wholly-hell chunk skips the overworld pipeline. Hellrock is
the only hell block so far: `L-8` completes the palette (`tools/generate-hell-textures.py` takes a
tile spec per block). The layer index is ready for the hell mobs' difficulty (`L-10`).
Last updated 2026-11-07.

## In progress

Nothing.

## Open

### Phase 2: Hell slice

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
  state on the wire, `entities.json`, loopback tests (the sink rule in `MovementRules` came with `L-3`).
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
