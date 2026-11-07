# Layers: Hell below, Sky above

> Guide: plans/README.md

## Status

Phases 1 (foundations) and 2 (the hell slice, `L-5` to `L-10`) done; phase 3 (the sky) has begun with the
solid cloud patches (`L-11`). A Nether- and Aether-like pair of realms inside the one world, as vertical
layers (not dimensions). Dig or build to reach them; no portals. Old saves do not matter (build as
day 0). The layer Y bands live in `WorldGenerationConfig` (`Barrier*`, `Hell*`, `CloudSlab*`,
`SkyIsland*`), the cloud sea in `SkyConfig.SkyCloudSea*` and the difficulty step in
`MobConfig.LayerDifficultyPerLayer`; `LayerBandTests` holds their order. The `iron_pickaxe` (power 3,
creative only) is the barrier's tool. The solid cloud patches (`L-11`, AGENTS.md "Solid clouds") fill
the `CloudSlab` band (198..203) with the translucent `cloud_block` (`softLanding`, `sink` 0.5: a fall is
free, the player sinks half a block and stands; it breaks by hand) where a 2D noise (seed offsets
166-167) outlines patches tens of blocks across, thicker in their cores (`WorldGen/Sky/CloudPatchGenerator`);
a sky chunk in the band takes a short path that stamps only the cloud. Its tile comes from
`tools/generate-sky-textures.py`, the spec list the sky palette of `L-12`/`L-13` extends.
The eye's height already tints the sky and fog (`LayerClimate`, `FrameLighting`, no shader change):
red in hell, a deeper blue over the cloud slab. The barrier band is generated (`BarrierGenerator`, the
`barrier` block, iron pickaxe only): a gapless core from -230 to -270 that walls up caves and fluids
too, patchy noise ramps above and below it, stamped last. Under it hell is generated
(`HellGenerator`, `WorldGen/Hell/`): 96-block layers repeating downward forever, a cavern pocket on a
3D lattice noise over a 16-block slab of `hellrock` (iron pickaxe only), deeper layers more open with
more pools ponding their floors' dips; a wholly-hell chunk skips the overworld pipeline. Three hell
biomes (`HellBiome`, `HellBiomes.At`: a 3D noise at seed offset 164 plus a hashed lean per layer, so a
layer mixes them and the next differs) dress the pockets: Ashlands (ash, cinder soil, scorched
hellrock, few lava pools), Magma Caverns (basalt, magma blocks, every pool lava) and Fungal Grove
(fungal moss and soil, nearly dry); floor, ceiling and wall skins a few cells deep, ceiling lights
(`ember_crystal`, `fungal_glow`) in two new light colors (5 of 8 palette slots used). The palette is
nine JSON blocks with tiles from `tools/generate-hell-textures.py` (a spec per block). The pockets
carry flora per biome (`L-9`, AGENTS.md "Hell flora"): vines and ground plants as part of hell's one
pure rule (`HellFlora`), and two tree species (`fungal_tree`, `charred_tree`) grown one per 8x8 cell
and layer by `HellTrees`. Two hell mobs live in the pockets (`L-10`, AGENTS.md "Hell mobs"): the melee
`hellbrute` and the fire-casting `cinderwitch`, `HellMob`s on the shared `RiggedMob` base (the goblin's
too), spawned only on dim hell floors (`MobSpawner.TryFindHellSpawn`) and scaled by the layer index
(`LayerDifficulty`: health, damage, cast rate, pack size); their bolts set the player `burning`. They
are already on the wire (protocol 17) and in `entities.json`; `L-16` adds the loopback tests and the
flight state. Id budget: 166 of 255 ids used (100 blocks with `cloud_block`, 65 items, air), 89 left for
the sky palette (`L-12`..`L-15`, ~24).
Last updated 2026-11-07.

## In progress

Nothing.

## Open

### Phase 3: Sky slice

- [ ] **`L-12` Sky islands (Y ~320-420).** P1, L. Island zones from the archipelago noise approach
  plus hashed island sites (`TerrainIslands`, `TerrainSites`); wide ocean-like gaps; rare big zones
  hold the hardest mobs. A second thick cloud sea under them (visual).
- [ ] **`L-13` Sky biomes and flora.** P1, M. Two by noise: meadow and crystal/frost highland; broad
  and crystal tree species, plants, vines; full sky palette.
- [ ] **`L-14` Flight in `Mob`.** P1, L. A flying movement mode plus animator support.
- [ ] **`L-15` Sky mobs.** P1, L. Two: a flyer and a tough ground bruiser; spawn rules scaled by
  island-zone size.

### Phase 4: Netcode and polish

- [ ] **`L-16` Network and saves.** P1, M. The sky mobs' kinds in `EntityList` (protocol bump), flight
  state on the wire, `entities.json`, loopback tests of the realm mobs (the hell kinds already ride
  `EntityList` and `entities.json` since `L-10`; the sink rule in `MovementRules` came with `L-3`).
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
