# Battle tower

> Guide: plans/README.md

## Status

BT-1 to BT-3 done; BT-4 next. BT-1 added the `unbreakable` block flag and the three worldgen-only tower bricks, BT-2
the tower generator (see "Tower generator (BT-2)" under Reference), BT-3 the undead skeleton with its melee rake, bow
and arrows, floor-tier difficulty and forced-spawn-only manager (see "Skeleton (BT-3)" and AGENTS.md "Skeleton").
Tracks the battle tower: a rare, huge (radius ~20+) cylindrical landmark of 4-8 floors with skeleton piles, chests, floor locks, a roof
altar that summons the Skeleton King, unique boss loot and a collapse into a ruin. One phase per prompt; each phase
leaves the game building and the tests green. **Content ids are tight: 17 left, see Reference.**
Last updated 2026-11-08.

## In progress

Nothing.

## Open

- [ ] **`BT-4` · Skeleton piles, floor locks and `TowerManager`** — P1, L.
  A worldgen-only pile block with a `SkeletonPileManager` (main thread, like the kiln registries): a player within ~8
  blocks makes it raise skeletons in waves, then it depletes. A worldgen-only barrier door block across each floor's
  stairwell; the `TowerManager` opens it when the floor's piles are depleted and its skeletons dead. State in
  `stations.json` (Capture/Restore) and the `StationState` frame (protocol bump); the client only mirrors it.
  The barrier door of floor k fills `TowerSite.DoorCells(k)`: the headroom cells of the stair tube over the two
  half-steps `DoorStep(k)` and `DoorStep(k) + 1`, past each landing's doorway (BT-2's flood-fill test proves that
  with those cells solid no floor reaches the next or the roof, even with every breakable cell mined).
- [ ] **`BT-5` · Chests and floor loot** — P1, S.
  Worldgen-only tower chests like `ruin_chest_*` in the floor templates; loot tables per floor tier
  (`loot/tower_floor_N`) rolled by `ChestLoot.Materialize`, richer going up. **Hook:** `Simulation/Chests/`.
- [ ] **`BT-6` · Skeleton King and the altar** — P1, XL.
  Big humanoid rig and brain: melee slam, skeleton wave summon, bone volley, phases by health. Right-clicking the altar
  (a claimed `RightClickOutcome`, like the waystone) starts a short ritual and the boss rises, once per tower. Uses the
  boss health bar HUD. Server-authoritative, saved, on the wire (`NetEntityKind`, protocol bump). **Hook:** the
  sandworm (`SandwormManager`) is the boss template; `RealmMob` is the frame.
  **The altar sits on an open roof** (a player can pillar up the outside), so the ritual must be refused until
  `TowerManager` (BT-4) reports every floor of that tower cleared. The altar cell is `TowerSite.AltarCell`.
- [ ] **`BT-7` · Boss loot and unique items** — P2, L.
  Exactly one guaranteed unique per boss, rolled from a pool, plus ingots and goodies from a boss table. The pool:
  a **weapon** whose kills of non-undead enemies burst into bone fragments dealing small damage to nearby enemies; an
  **accessory** with a very slight mana regen boost and +10% damage against undead (gear modifiers plus a new
  stat or passive, see `INVENTORY.md`); a **staff or spell scroll** teaching a new part. No armor for now.
- [ ] **`BT-8` · Collapse into a ruin** — P2, L.
  After the boss dies, a staged collapse (top floors first) replaces the tower with rubble and ruin blocks placed
  around where it stood, through a capped-per-frame writer like `StatusEffectBlockWriter` that calls every edit-path
  hook (journal, LOD, fluids, leaf decay, gravity). Written chunks become `ModifiedSinceSave`, so the ruin persists;
  hold the collapse until every footprint chunk is loaded, and record the tower as conquered in the world save so a
  chunk that loads late still gets the ruin. The writer is the one thing allowed past `unbreakable`: it writes through
  `Chunk.CollapseWriteAt` (already in place, hook-free) and must run every edit-path hook itself.
- [ ] **`BT-9` · Verify and document** — P2, S.
  Extend `visual-examples/battle-tower.json` (`VOXEL_TIME=147`, from BT-2) with skeletons, locks and the boss, loopback `GameServerTests` for skeletons, boss, piles and
  locks on a client, a perf pass, then fold the design into AGENTS.md (Layout and a "Battle tower" section) and the
  wiki, and delete this file's finished entries.
- [ ] **`BT-10` · Dark tower interiors** — P2, M.
  Tower interiors are sunlit: `ChunkLighting` seeds sun from the column heightmap, which stays the terrain's (the
  tower must not raise `ColumnData.Heights`, which the LOD, water and spawns read). Seed sun in a tower footprint from
  the real overhead blocks instead (or treat tower columns as covered from the roof down), so the floors are lit only
  by their lanterns and torches. Also look at surface mobs spawning on the ground floor once it is dark.

## Reference

- **Content id budget (budget constraint, after BT-3, which used 2: the `arrow` and `bone` items): 238 of 255 used
  (130 blocks, 107 items, air), 17 free.** Rough needs of the later phases: BT-4 skeleton pile and barrier door blocks (2, 3 if
  the door is two cells); BT-5 tower chest block(s) (1 if the loot keys on the floor through the cell or state, up to
  4 with one per tier); BT-6 altar block (1); BT-7 three uniques plus a boss material (~3-4 items; the bone fragment
  should reuse `bone`); BT-8 rubble or ruin block(s) (1-2). About 10-14 in total: the tower fits, but leaves little for `CAVE_BIOMES.md`.
  Prefer one chest block and reuse existing ruin blocks for the rubble.
- **How `unbreakable` is enforced** (BT-1): load rules (`BlockFieldRules.ValidateUnbreakable` refuses fluid, falls,
  needsSupport, replaceableByPlacement, flammable, meltsTo, the tree-wood and multiblock renderers, upperHalf and
  prime_tnt, and `BlockLinkResolver` refuses an unbreakable multiblock source) keep the block away from every raw-write
  path, plus guards in `Chunk.RemoveBlockAt`/`ClearBlockAt`, `Placement.CanOccupy` and the gameplay entry points
  (`Mining`, `LeftClickAction`'s creative branch, `BreakProgress`, `BlockEditor`, `TntManager.Detonate`,
  `FluidManager.SetCellImmediate`, `StatusEffectBlockRule`, `LookLabel`). `Chunk.SetBlockRaw` stays raw;
  `Chunk.CollapseWriteAt` is the only bypass, and BT-8's writer must call every edit-path hook.
- Tower brick tiles come from `tools/generate-tower-brick-textures.py` (stone_bricks.png with its five colors swapped
  one-to-one onto a palette per variant); a new biome variant is one more palette there and one more block JSON.
- **Tower generator (BT-2)**, 0 content ids used (the tower is the three BT-1 bricks plus existing blocks).
  - **Files** (`OpenGLTest/src/WorldGen/Structures/`): `TowerPlacement` (cells, rolls, the cached site verdict, the band,
    the stamp, the finders), `TowerSite` (one accepted tower and the hooks below), `TowerShell` (pure shell geometry),
    `TowerGenerator` (shell plus dressing merge, the point query), `TowerPalette`, `TowerRing`. Templates
    `assets/data/structures/tower_floor_{hall,library,barracks}.json` and `tower_altar.json`. Tests
    `TowerShellTests`, `TowerPlacementTests`, the `Towers` row of `StampGateTests`, the tower cases of `StructureTests`.
  - **Placement**: one candidate per 1536-block cell (floor division), `SplitMix64.Hash(cell, Seed ^ 0x5E7F)` (no noise,
    so nothing re-lays; salt recorded in `BIOMEGEN.md`). The hash rolls the chance, the jittered center (a margin keeps
    the footprint in its cell), radius, floors, turn, the altar template and a floor template plus turn per floor. The
    site verdict is the 13th `GenerationalCache` site (`TowerSiteCache*`, cleared by `WorldSeed.Apply`): outside the spawn
    clear (center at least `TowerSpawnClearRadius + Reach` out), center not Ocean and dry, top under the cloud slab, and a
    sparse grid over the disc plus a 3-block ring all dry and within `TowerMaxStep` of the center ground. A chunk off every
    footprint pays only hashes and box rejects; `TopReaching` lifts the early-out over a tower chunk.
  - **Shell rings** (integer `d2 <= r*r + r`): apron (an entrance walkway cut through higher ground on a cobblestone stoop),
    outer wall 2 thick with a crenellated parapet 2 high and a ground entrance, stair corridor 3 wide (solid brick but for a
    helical tube), inner wall 1 thick with one doorway per floor at its landing, the room with a slab every 7 blocks and
    the roof slab. A foundation 6 deep fills the disc under the ground slab, and every disc cell to `TopY` is written.
  - **Stair**: half-steps, 60 per turn, alternating `cobblestone_slab` (even) and full brick (odd), `StepY(t) = GroundY + 1 +
    t/2`, headroom 3; landings at `LandingStep(k) = 14k - 1` are level with `FloorY(k)`; the last step reaches the roof.
  - **Hooks** for BT-4/6/8: `TowerSite.FloorY(k)`, `RoofY`, `Tier(k)` (= k), `DoorCells(k)` (with `LandingStep`/`DoorStep`),
    `AltarCell` (center, `RoofY + 1`, left empty by the altar template), `BottomY`/`TopY`, `MinX..MaxZ`, the
    `MinChunkX..MaxChunkZ` and `MinChunkY..MaxChunkY` ranges; `TowerPlacement.TryGetSite(cellX, cellZ)` and `TryFindAt(x, z)`;
    `TowerGenerator.CellAt(x, y, z)` (-1 or `id | state << 8`). A main-thread or server consumer reads a cached site at load,
    never per frame and never on a client.
  - **Templates**: kinds `towerFloor` (at most `TowerFloorHeight - 1` = 6 layers) and `towerAltar` (at most 8), odd size,
    `groundLayer` 0, no satellite or waystone, an empty altar template center at layer 0; a `tower_bricks` cell becomes the
    site's palette brick; lanterns hang only under a solid cell (a floor's top layer sits under the next slab); dressing
    blocks are breakable decoratives and no chests (BT-5 adds them).
  - **Default seed**: the tower nearest the origin is cell (1, -1), center (2920, -1411), ground 10, radius 21, 5 floors,
    turns 2, plain bricks, slabs y 10/17/24/31/38, roof 45, altar (2920, 46, -1411), top 51
    (`visual-examples/battle-tower.json`, `VOXEL_TIME=147`).
  - **Tuning** (unchanged first guesses, `WorldGenerationConfig` "Battle tower" block): cell 1536, chance 0.6, radius
    20-24, floors 4-8, floor height 7, wall 2, stair 3, inner wall 1, 60 steps a turn, headroom 3, entrance 3 steps,
    doorway half-width 2, door offset 6, apron 3, parapet 2, altar height 8, foundation 6, max step 4, sample step 4,
    water ring 3, cache 256 x 4 generations.
  - **Cost** (`WorldgenCostProbe` `tower` class, default seed, ms a chunk): cold whole chunk 200-222 mean, of which tree
    growth about 180 and the Towers stage 0.28-0.30 (the stamp; the site build is paid in Columns by `TopReaching`); warm
    whole chunk median 0.44-0.50, warm Towers median 0.08 (the p90 of 1.6-2.7 is JIT tier 0 on unwarmed chunks; a steady
    full tower chunk stamps in 0.13-0.25). Surface chunks: Towers 0.001 cold, ~0 warm; Columns unchanged.
  - **Known gaps**: sunlit interiors (`BT-10`); surface mobs can spawn on the ground floor; far-LOD canopy bumps over the
    footprint; tree crowns and limbs cut at the disc edge leave floating leaves and orphan wood until a nearby edit schedules
    decay; the stair's `cobblestone_slab` half-steps are breakable (a mined one leaves a pit over brick, never a bypass, as
    the flood-fill test proves).

- **Skeleton (BT-3)**, 2 content ids (`arrow`, `bone`). See AGENTS.md "Skeleton" for the whole design.
  - **Hooks for BT-4**: `SkeletonManager.Spawn(position, tier)` (a body center; null at the hard cap, the only way a
    skeleton appears, since it has no ring spawn), `TowerDifficulty.PackSize(tier)` (the wave size, no caller yet),
    `TowerSite.Tier(k)` as the tier, `World.Skeletons` for the floor's living count.
  - **Despawn**: skeletons keep the shared realm-mob distance and idle despawn past `MobConfig.SkeletonDespawnRadiusBlocks`
    (96); a pile's skeletons may need to stay while their floor is locked, so BT-4 should revisit it.
  - **Tier on the wire and in saves**: `NetEntity.ItemId` (protocol 25) and `EntitySaveData.MobData.Tier`, clamped by
    `TowerDifficulty.Clamp`; the attack count's parity names the clip (melee even, shot odd).
  - **Known gaps**: arrows are not on the wire, skeletons are not drawn in the water mirror.

Decisions made while planning (2026-11-08):

- **Hybrid generation**, because templates cap at 17x17x24 and the shell is far bigger; the shell stays a pure
  function so a point query equals the stamp across chunk seams.
- **Unbreakable by data flag**, not by a check on a block name: the tower holds until the boss dies, so no player can
  dig around the fight or the locks.
- **Conquered state is the saved ruin itself.** The tower spans about 3x3 to 5x5 chunk columns, all inside the load
  radius when the player stands at the altar; saved chunks win over regeneration. The conquered record only covers a
  footprint chunk that was not loaded.
- **Locked until cleared** per floor, with a barrier door that opens by manager state (not mineable).
- **Palette swap only** per biome; the same skeleton, boss and layout everywhere.
- **Full scope from the start**: server-authoritative, saved, networked.
- **Order of the three efforts** (2026-11-08): accessories (done) first, this plan second, cave biomes
  (`CAVE_BIOMES.md`) third. `BT-7`'s accessory (slight mana regen, +10% damage against undead) needs the `undeadDamage` gear stat, which the
  accessories added.
- Tuning left open: the stair and door layout at radius 20+, the floor height rhythm, bone-burst and accessory
  numbers, skeleton variants per tier and pile wave sizes. These are first guesses to retune after a play-test.
