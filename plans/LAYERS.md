# Layers: Hell below, Sky above

> Guide: plans/README.md

## Status

Phases 1 (foundations), 2 (the hell slice, `L-5` to `L-10`) and 3 (the sky slice: the solid cloud
patches `L-11`, the sky islands with their cloud sea `L-12`, the sky biomes and flora `L-13`, flight in the
shared mob layer `L-14` and the sky mobs `L-15`) done; phase 4 (netcode and polish) remains. A Nether- and Aether-like pair of realms inside the one world, as vertical
layers (not dimensions). Dig or build to reach them; no portals. Old saves do not matter (build as
day 0). The layer Y bands live in `WorldGenerationConfig` (`Barrier*`, `Hell*`, `CloudSlab*`,
`SkyIsland*`), the cloud sea in `SkyConfig.SkyCloudSea*` and the difficulty steps in
`MobConfig.LayerDifficultyPerLayer` and `MobConfig.SkyDifficultyPerTier`; `LayerBandTests` holds their order. The `iron_pickaxe` (power 3,
creative only) is the barrier's tool. The solid cloud patches (`L-11`, AGENTS.md "Solid clouds") fill
the `CloudSlab` band (198..203) with the translucent `cloud_block` (`softLanding`, `sink` 0.5: a fall is
free, the player sinks half a block and stands; it breaks by hand) where a 2D noise (seed offsets
166-167) outlines patches tens of blocks across, thicker in their cores (`WorldGen/Sky/CloudPatchGenerator`);
a sky chunk in the band takes a short path that stamps only the sky layers. Its tile comes from
`tools/generate-sky-textures.py`, whose spec list the sky palette of `L-13` extends (borrowing the hell
script's bark, rings, fern and vine painters). The sky islands
(`L-12`, AGENTS.md "Sky islands", `SkyIslandConfig`) float in 320..420 inside hashed zones with wide
gaps of open sky between them (`SkyIslands`, seed offsets 168-171): islands that never touch, a domed
grass top over a stone underside tapering to stalactites; `SkyIslands.ZoneAt` gives a zone's tier
(bigger is rarer), which the sky mobs' spawner scales by. Two sky biomes (`L-13`, AGENTS.md "Sky biomes":
`SkyBiome`, `SkyBiomes.At`, a 2D noise at seed offset 174, an island taking its center's biome) dress
the islands through their `SkyIslandPalette`: the meadow (grass, dirt, stone, tall grass and flowers,
the broad `sky_oak`) and the frost highland (`frost_grass`, permafrost, `frost_stone`, `crystal_block`
outcrops, `frost_fern`, the `crystal_tree` whose leaves glow pale cyan in a sixth light slot); both
hang `sky_vine`s under their undersides (`SkyFlora`, part of the one column rule) and grow their trees
one per 16x16 cell (`SkyTrees`, the twelfth `GenerationalCache`). Under them a thick, nearly continuous cloud sea of `cloud_block`
(290..301, offsets 172-173, a `CloudLayer` like the patches) catches a fall off an island.
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
are already on the wire (protocol 17) and in `entities.json`; `L-16` adds the loopback tests. Flight (`L-14`, AGENTS.md "Flying mobs") carries the sky's flyer: a `RiggedMob` kind built
with a `FlightTuning` flies (`RiggedMob.Fly`, `FlightSteering`: no gravity, fall damage or ledge caution, a
hover band over its target or the floor, a wall probe that slides and climbs, a 3D wander goal and knock, the
`fly` clip by 3D speed, pitch and bank); `Flies` comes from the kind, so it needs no wire bit. Two sky mobs
live on the islands (`L-15`, AGENTS.md "Sky mobs"): the diving flyer `galeraptor` and the slamming ground
bruiser `cloudgolem`, `SkyMob`s on the shared `RealmMob` base (the hell mobs' too) and the shared
`RealmMobManager` frame, spawned only on island tops inside the sky band (`MobSpawner.TryFindSkySpawn`) and
scaled by the zone's tier (`SkyDifficulty`: health, damage, pack size; the golem only in the big zones); both
drop the `cloud_shard` and are on the wire (protocol 18) and in `entities.json`. Id budget: 175 of 255 ids
used (108 blocks, 66 items, air), 80 left.
Last updated 2026-11-07.

## In progress

Nothing.

## Open

### Phase 4: Netcode and polish

- [ ] **`L-16` Network and saves.** P1, S. Loopback tests of the hell mobs (every realm kind already rides
  `EntityList` and `entities.json`, the sky kinds since `L-15` with a loopback test of their own; the sink
  rule in `MovementRules` came with `L-3`), and showing a mirrored mob's attack clip on a client.
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
