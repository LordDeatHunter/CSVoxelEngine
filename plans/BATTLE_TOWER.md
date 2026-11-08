# Battle tower

> Guide: plans/README.md

## Status

BT-1 done (the `unbreakable` block flag and the three worldgen-only tower bricks); BT-2 next. Tracks the battle
tower: a rare, huge (radius ~20+) cylindrical landmark of 4-8 floors with skeleton piles, chests, floor locks, a roof
altar that summons the Skeleton King, unique boss loot and a collapse into a ruin. One phase per prompt; each phase
leaves the game building and the tests green. **Content ids are tight: 19 left, see Reference.**
Last updated 2026-11-08.

## In progress

Nothing.

## Open

- [ ] **`BT-2` · Tower generator** — P1, L.
  Pure function of (cell, seed), stamped per chunk and clipped, like ruins and sky islands. Hashed cells of ~1500
  blocks, land biomes, near-level ground, outside the spawn clear; hash rolls radius (20-24), floor count (4-8) and
  turn. Procedural shell: cylinder wall, floor slabs, a spiral stair inside the wall, roof and an altar level. Floor
  interiors and the altar room are dressed by hand-authored JSON templates (`StructureTemplate`, pieces within the
  17x17x24 cap). Biome palette swap only. Needs a `WriteBand`, a `StampGateTests` row, a seed offset (see
  `BIOMEGEN.md`) and the `biome-worldgen` skill. The far LOD draws none. **Hook:** `WorldGen/Structures/`,
  `ChunkTerrainFill`. Cost check with `WorldgenCostProbe`.
- [ ] **`BT-3` · Skeleton mob** — P1, L.
  One `RealmMob`-style kind: melee up close, bow at range (arrow is a `ThrownProjectile` like goblin stones). Rig, clips
  and skin from a deterministic `tools/generate-skeleton-model.py` (`--check`) into a Blockbench `.bbmodel`; humanoid
  contract through `EntityContract.RequireHumanoid`. Floor-tier difficulty scales health, damage and pack size like
  `LayerDifficulty`. An undead tag the weapon and accessory read. Manager on `MobCaps`, saves in `entities.json`,
  `NetEntityKind` and protocol bump, item debugger ENTITIES modes, mirror attack clips.
- [ ] **`BT-4` · Skeleton piles, floor locks and `TowerManager`** — P1, L.
  A worldgen-only pile block with a `SkeletonPileManager` (main thread, like the kiln registries): a player within ~8
  blocks makes it raise skeletons in waves, then it depletes. A worldgen-only barrier door block across each floor's
  stairwell; the `TowerManager` opens it when the floor's piles are depleted and its skeletons dead. State in
  `stations.json` (Capture/Restore) and the `StationState` frame (protocol bump); the client only mirrors it.
- [ ] **`BT-5` · Chests and floor loot** — P1, S.
  Worldgen-only tower chests like `ruin_chest_*` in the floor templates; loot tables per floor tier
  (`loot/tower_floor_N`) rolled by `ChestLoot.Materialize`, richer going up. **Hook:** `Simulation/Chests/`.
- [ ] **`BT-6` · Skeleton King and the altar** — P1, XL.
  Big humanoid rig and brain: melee slam, skeleton wave summon, bone volley, phases by health. Right-clicking the altar
  (a claimed `RightClickOutcome`, like the waystone) starts a short ritual and the boss rises, once per tower. Uses the
  boss health bar HUD. Server-authoritative, saved, on the wire (`NetEntityKind`, protocol bump). **Hook:** the
  sandworm (`SandwormManager`) is the boss template; `RealmMob` is the frame.
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
  `visual-examples/battle-tower.json` (`VOXEL_TIME=147`), loopback `GameServerTests` for skeletons, boss, piles and
  locks on a client, a perf pass, then fold the design into AGENTS.md (Layout and a "Battle tower" section) and the
  wiki, and delete this file's finished entries.

## Reference

- **Content id budget (budget constraint, after BT-1): 236 of 255 used (130 blocks, 105 items, air), 19 free.**
  Rough needs of the later phases: BT-3 bone and arrow items (~2); BT-4 skeleton pile and barrier door blocks (2, 3 if
  the door is two cells); BT-5 tower chest block(s) (1 if the loot keys on the floor through the cell or state, up to
  4 with one per tier); BT-6 altar block (1); BT-7 three uniques plus a bone fragment or boss material (~3-5 items);
  BT-8 rubble or ruin block(s) (1-2). About 12-16 in total: the tower fits, but leaves little for `CAVE_BIOMES.md`.
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
