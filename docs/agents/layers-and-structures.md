# Layers: hell below, sky above; structure templates

The realm layers map, structure templates, ruins and waystone satellites.

## Layers: hell below, sky above

Two realms share the one world with the overworld as vertical layers, a Nether-like hell under it and an
Aether-like sky over it: no dimensions, no portals. This is the map; each part has its own section, named
in quotes. `plans/LAYERS.md` keeps the design decisions and the ideas left for later.

- **Bands** (`WorldGenerationConfig`, their order held by `LayerBandTests`): the overworld, whose caves wear
  their "Cave biomes" (a frozen or desert echo down to a wavy line near `CaveEchoBottomY` -200, then crystal,
  fungal or the magma-touched deep under a wavy line near `CaveDeepTopY` -264), down to the
  barrier band `BarrierTopY..BarrierBottomY` (-500..-600, a gapless core `BarrierFullTopY..BarrierFullBottomY`,
  -530..-570, between patchy ramps); hell from `HellTopY` (-601) down forever in `HellLayerPeriodBlocks` (96)
  layers, each a cavern pocket over a 16-block hellrock slab; above every peak (`ReliefCeilingHeight` 148) and
  the drawn cloud layer (160), the solid cloud slab `CloudSlabBottomY..CloudSlabTopY` (198..203); the cloud sea
  (`SkyConfig.SkyCloudSeaHeight`, `SkyCloudSeaThickness`: 290..301); the sky islands
  `SkyIslandBottomY..SkyIslandTopY` (320..420), tuned in `SkyIslandConfig`.
- **Getting there**: dig down or build up. The barrier and hell's slabs (`basalt`, `hellrock`) need the
  `iron_pickaxe` (power 3, made from wrought iron smelted in the bloomery, see "The bloomery and the anvil"), see "The barrier". The sky has no gate: the
  solid clouds and the cloud sea catch a fall (`softLanding`, and `sink` holds the player half a block deep,
  "Sink and soft-landing flags"); hell has no bottom and its falls hurt as usual.
- **Worldgen**, a pure function of position like the rest (each chunk alone, the point query equal to the
  stamp; the write rules are in the worldgen invariants under "Architecture"): "The barrier"
  (`WorldGen/Features/Underground/BarrierGenerator.cs`, stamped last but for the "Vulcanite" veins over its and hell's
  basalt, densest by the lava); "Hell", "Hell biomes" (Ashlands,
  Magma Caverns, Fungal Grove) and "Hell flora" (vines, plants, the `fungal_tree` and `charred_tree` through
  `HellTrees`) under `WorldGen/Hell/`, where a chunk wholly in hell skips the overworld pipeline; "Solid
  clouds", "Sky islands" (with the cloud sea) and "Sky biomes" (meadow and frost highland, their flora and the
  `sky_oak` and `crystal_tree` through `SkyTrees`) under `WorldGen/Sky/`, where a chunk in the sky takes a
  short path that stamps only the sky. Both realms' trees are `GenerationalCache` sites cleared by
  `WorldSeed.Apply`.
- **Look**: "Realm layer fog" (`Effects/Sky/LayerClimate.cs`: red fog and no sky in hell, a deeper blue
  aloft, read by `FrameLighting` with no shader change). Hell is lit only by lava and its glowing blocks, in
  the ember and fungal teal block-light colors; the sky's `crystal_leaves` add pale cyan (six of the eight
  `BlockLightPalette` slots used).
- **Mobs**: "Hell mobs" (the hellbrute and the fire-casting cinderwitch) and "Sky mobs" (the diving
  galeraptor and the slamming cloud golem), all `RealmMob`s run by a `RealmMobManager` (`HellMobManager`,
  `SkyMobManager`) under the world's shared mob caps, each spawning only in its own realm
  (`MobSpawner.TryFindHellSpawn`, `TryFindSkySpawn`); "Flying mobs" is the flight every mob kind can opt
  into. The overworld's cave sub-biomes have their own ("Cave mobs", scaled by a depth tier). **Difficulty** grows with depth and zone: `LayerDifficulty` (`1 + layer *
  MobConfig.LayerDifficultyPerLayer`, capped at `LayerDifficultyMaxMultiplier`) by the hell layer and
  `SkyDifficulty` (`1 + tier * MobConfig.SkyDifficultyPerTier`) by the island zone's tier scale health,
  damage, a caster's rate and the pack, read from where the mob spawned, so a save restore and a client mirror
  agree. On a server every realm mob rides `EntityList` and is mirrored with its attack clips ("A mirror's
  attack clip"); all four realm kinds and the three cave mobs (shardling, sporeling, magmaborn) are saved in
  `entities.json`.
- **Far LOD**: the realms exist only in full-detail chunks (the far LOD is a heightmap shell), so the clouds
  and islands pop in within the load sphere and hell never shows at distance (TODO `M-29`).
- **Budgets**: content ids are `ushort` (up to 65,535 shared by blocks and items), so they are no longer a
  budget; entities take no id. Status effects: 3 of the 8 slots (`burning`, `frostburn`, `spore`). Seed offsets
  162 (barrier ramps), 163 (hell caverns), 164 and 165 (hell biomes and pools), 166 and 167 (cloud patches),
  168 to 171 (sky zones and islands), 172 and 173 (cloud sea), 174 (sky biomes), 175 to 178 (sky landmasses), 179 to 181
  (cave biomes), 182 (cave shape scale) and 183 to 185 (cave ores); the whole table is in
  `plans/BIOMEGEN.md`.
- **Visual checks** (each with `VOXEL_TIME=147`): `barrier.json`, `cave-biomes.json`, `cave-mobs.json`, `hell.json`, `hell-flora.json`,
  `hell-mobs.json`, `layers-fog.json`, `clouds-solid.json`, `sky-islands.json`, `sky-density.json`,
  `sky-biomes.json`, `sky-mobs.json`, and `waystones.json` (a waystone structure in each realm).

### Structure templates and ruins

A structure is a hand-authored template under `assets/data/structures/<name>.json`, loaded after the blocks by
`StructureLoader` into `StructureRegistry` (`Content/Structures/`; errors name the file): `kind` (`surface`, `underground`,
`hell`, `sky`), `weight`, optional `biomes` (surface only), `groundLayer` (the layer standing on the anchor floor level;
the layers under it are dug in), a `palette` (a key to a block name, or `{"block", "facing"}`) and `layers`, bottom first, each
an array of rows north to south of palette keys west to east (a space writes nothing, `.` writes air). A footprint is square, at
most `StructureRegistry.MaxSize` (17) wide and `MaxHeight` (24) tall. A compiled cell is an `int` packed by
`Content/Structures/StructureCell` (the block id in bits 0-15, the state byte in 16-23, never negative; `StructureCell.None`,
-1, writes nothing), the one encoding the templates, `StructureStamper`, `TowerShell`, `TowerGenerator`, `TowerRuin` and
`TowerCollapse` share; a stateless cell is the bare block id. A template precomputes its four clockwise quarter turns,
mapping (x, z) to (size - 1 - z, x) and turning a `facing` cell low two state bits through `TurnedFacing` (the engine
north/south/west/east order is not cyclic). Ruin loot chests are ordinary palette entries (`ruin_chest_<kind>`), so there are
no marker keys and no id cost. `StructureStamper.Stamp` writes a template part inside one chunk, clipped at the border and to a
world-y window, so every chunk a footprint crosses agrees with no cross-chunk writes.

`RuinPlacement` (`WorldGen/Structures/`) decides where: a hashed cell of `RuinCellShift` blocks per kind rolls its chance, its
site (jittered), its template and its turn from the hash, a pure function of (cell, seed). **Surface** ruins (stage
`RuinsSurface`, after the wells) need a land biome, dry ground and a footprint within `RuinSurfaceMaxStep` of level.
**Underground** rooms (`RuinsUnderground`) sit at a hashed depth between the barrier and `RuinUndergroundCover` under the
ground at their center and are sealed by their own walls; they (and their waystone chambers) are stamped after the geodes, the
cave life, the gravel pockets and the pools, so none of those is left inside a room. Their `ruin_chest_underground` is a
tiered chest (`chest.tiers` in `CaveBiome` ordinal order: `ruin_underground` for plain, then `_frozen`, `_desert`, `_crystal`,
`_fungal`, `_magma`, each nesting `ruin_underground` plus a flavor pool and one uncommon..rare charm or ring): the stamp ORs the
chest cell's own cave biome into its state as the tier (`StructureStamper.Stamp(..., chestBiomes:)`, fed the chunk's
`CaveBiomeMap` through `RuinPlacement.StampUnderground(..., caves)`; a null map is all plain, tier 0), so opening and breaking
unopened roll the same table and nothing reads worldgen at open time (`CaveChestTests`). **Hell** ruins (`RuinsHell`, hooked into both the wholly-hell early
path and the straddle path through `StampHellRuins`) stand on the first solid pocket floor scanning down from a hashed row,
per layer, with the whole footprint level and clear above, and their cells stay inside their layer. **Sky** ruins (`RuinsSky`,
inside `StampSky` after the sky trees) take at most one per island, on its center top when the top is within
`RuinSkyMaxStep` of level across the footprint. Every placement has a band (`SurfaceBand`, `UndergroundBand`, `SkyBand`, hell
`HellGenerator.WriteBand`) and a `StampGateTests` row; `StructureTests` holds the template, seam and site rules, and `visual-examples/ruins.json` (`VOXEL_TIME=147`) frames a surface ruin. The far LOD
draws none. A new ruin is a JSON file. Two more kinds, `towerFloor` and `towerAltar`, are never ruins: they dress a battle
tower's rooms and roof (`TowerGenerator`, plans/BATTLE_TOWER.md), and the loader holds them to an odd size, `groundLayer` 0,
no satellite or waystone, at most a floor's or the altar's height, and an empty altar cell at the altar template's center;
a `tower_bricks` cell is swapped for the site's palette brick.

**Waystone satellites (W-4).** A template with `"satellite": true` is a waystone site, never a ruin: `StructureLoader` refuses
a satellite that does not hold exactly one waystone block (a lower half; every two-cell block in a template must stand whole, its
upper half right above it) and a ruin that holds any, and `StructureRegistry.Of(kind)` lists ruins
only while `SatellitesOf(kind)` lists satellites (`TryPick(..., satellite: true)`). Four ship, one per realm, each its own layout:
`waystone_pavilion` (surface, 5x5 and 10 layers: four wall-post pillars on a stone-brick platform under a stepped cobblestone
roof, a lantern over the waystone), `waystone_crypt` (underground: a sealed stone-brick chamber with wall lamps, the waystone on a slab dais),
`waystone_ember_ring` (hell: a basalt disc ringed by basalt pillars crowned with ember crystal, on a two-layer hellrock foundation)
and `waystone_sky_arch` (sky: a frost-stone plaza under a crystal-keyed arch). `WaystonePlacement` (`WorldGen/Structures/`) places
them beside ruins and, on the surface, out from a battle tower's entrance (`TryFindTower`: each tower rolls `WaystoneTowerChance` from its own hash, then up to `WaystoneSiteTries` spots straight out of the entrance strips, within `WaystoneTowerSpreadRadians` and `WaystoneTowerExtraDistance`, through the same `TrySurfaceGround`): every placed ruin's hash, mixed with salt `0x5E7E` (`SatelliteHash`), rolls `WaystoneSatelliteChance`, the
template and its turn, and up to `WaystoneSiteTries` distances (`WaystoneMinDistance..MaxDistance` between centers) and directions,
each pushed out until the two footprints keep `WaystoneRuinGap` empty columns apart; the first try whose site passes the realm's
rule wins. The rules are the ruins' own, shared through `RuinPlacement`: surface `TrySurfaceGround` (a land biome, dry, within
`RuinSurfaceMaxStep`; the template is picked by the ruin's biome), underground `TryUndergroundOrigin` (its own sealed chamber at
a hashed depth), hell `TryHellFloor` in the ruin's layer, scanned from the pocket top and level within the template's
`groundLayer` (its foundation fills dips that deep and the next layers write over or carve bulges; the ruins keep step 0, strictly
level), sky `TrySkyTop` on the ruin's own island, the distance rolled across the room the island's disc leaves. A chunk checks the
ruin cells within `MaxCenterDistance` plus `RuinPlacement.Reach` of it and rolls the cheap hashes (and whether any try's ray can
reach it) before searching a site, so a chunk off every satellite pays only hashes; the sky reuses the ruins' island collection,
since a sky waystone stays inside its island's disc. Stages `WaystonesSurface`, `WaystonesUnderground`, `WaystonesSky` and
`WaystonesHell` follow their ruins' stages, with the ruins' bands. `WaystonePlacement.TryFindSurface`/`TryFindUnderground`/
`TryFindHell`/`TryFindSky` return the ruin and its waystone as `PlacedStructure`s; `RuinPlacement.SkyRuinOwns` counts a sky
waystone's cells too. Measured over the default seed (3600 ruin cells a kind): a satellite beside 24% of surface ruins, 27% of
underground rooms, about 35% of the (rare) hell ruins and 11% of sky ruins (islands are often too small or uneven), at a mean
center distance of 20-22 blocks (10 on islands). `WaystonePlacementTests`; `visual-examples/waystones.json` (`VOXEL_TIME=147`)
films the nearest satellite of each realm on the default seed.

Issue #10 translates the entire barrier band and hell entry down exactly 200 blocks. The existing magma-deep entry stays at -264, extending the overworld deep band by 200 blocks; cave/ore bands, ruins, LOD underground bounds, mob spawns, fog, difficulty and waystone realms all read these shared bounds. Hell noise remains world-aligned; newly generated terrain changes, while modified saved chunks keep their snapshots.
