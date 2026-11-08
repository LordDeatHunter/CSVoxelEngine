# Battle tower

> Guide: plans/README.md

## Status

Designed, nothing built yet. Tracks the battle tower: a rare, huge (radius ~20+) cylindrical landmark of 4-8 floors
with skeleton piles, chests, floor locks, a roof altar that summons the Skeleton King, unique boss loot and a collapse
into a ruin. One phase per prompt; each phase leaves the game building and the tests green.
Last updated 2026-11-08.

## In progress

Nothing.

## Open

- [ ] **`BT-1` · Unbreakable flag and tower bricks** — P1, S.
  A block data flag `unbreakable: true` that no path can remove: `Mining.CanMine`, the server's `BreakProgress` and
  `BreakComplete`, blasts (`TntManager.Detonate`), felling/pops, fluid washing, creative breaking, buckets. Only the
  collapse writer (`BT-8`) may replace such a block. Tower brick variants (stone bricks, sandstone, frosted stone), JSON
  only. **Hook:** `BlockType`, `BlockDefinition`, `BlockFieldRules`; run the `add-content` and `edit-path-checklist`
  skills. Tests: each removal path refuses the block.
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
  chunk that loads late still gets the ruin. The writer is the one thing allowed past `unbreakable`.
- [ ] **`BT-9` · Verify and document** — P2, S.
  `visual-examples/battle-tower.json` (`VOXEL_TIME=147`), loopback `GameServerTests` for skeletons, boss, piles and
  locks on a client, a perf pass, then fold the design into AGENTS.md (Layout and a "Battle tower" section) and the
  wiki, and delete this file's finished entries.

## Reference

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
- **Order of the three efforts** (2026-11-08): accessories (`ACCESSORIES.md`) first, this plan second, cave biomes
  (`CAVE_BIOMES.md`) third. `BT-7`'s accessory (slight mana regen, +10% damage against undead) needs the new stat or passive from
  `AC-1`.
- Tuning left open: the stair and door layout at radius 20+, the floor height rhythm, bone-burst and accessory
  numbers, skeleton variants per tier and pile wave sizes. These are first guesses to retune after a play-test.
