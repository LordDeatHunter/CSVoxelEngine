# Accessories
> Guide: plans/README.md

## Status
AC-1 and AC-2 done (ability flags `equip.abilities` cached on `Equipment.Abilities`, the `undeadDamage` stat and
`Mob.IsUndead`; sets count head, chest and legs only); the rest is designed, nothing else built yet. Tracks accessory abilities (cloud boots, charms), the metal ring ladder and the set-bonus rule
change, on top of the gear core in `INVENTORY.md` (`I-1`, `I-2`). First of three efforts, in order: accessories (`AC-*`), the
battle tower (`BATTLE_TOWER.md`), cave biomes (`CAVE_BIOMES.md`). One phase per prompt; each leaves the game building and the
tests green. Last updated 2026-11-08.

## In progress
Nothing.

## Open
- [ ] **`AC-3` Cloud boots and the air jump** - P1, M.
  A `feet` item, crafted from `cloud_shard` plus iron boots, visible armor (`ArmorLook`). One free air jump, resets on landing; the
  same height as a normal jump, off while swimming or flying, sprint momentum kept like a regular jump. **The air jump never pumps
  the bellows** and never counts for `JumpCount`/`PumpOnJump`. The server tolerates exactly one extra hop (`MovementRules` rise and
  hover rules, `ServerPlayer.ObserveAcceptedState`); equipment is already server-owned, so no wire change is expected.
  **Hook:** `PlayerPhysics`, `MovementRules`, `ServerPlayer`.
- [ ] **`AC-4` Ability charms** - P1, M.
  Loot-only, never crafted: feather (fall damage fully negated; `FallTracker` consumers `LocalPlayer.UpdateHealth`,
  `ServerPlayer.TickStatus`), gill (about double `PlayerConfig.AirSeconds`; `PlayerHealth.UpdateBreath`), frost and fire charms
  (`equip.immune` frostburn and burning; the ember charm stays the on-hit variant), magnet (about 3x pickup radius through
  `DropCollector` in `DropPickup`/`DropManager`). Drops mix ruin chest tables by realm with rare mob and boss drops (sandworm fang,
  hell and sky mobs); the exact table per charm is open. Sprites via `tools/generate-gear-textures.ps1` (small palettes).
- [ ] **`AC-5` Metal ring ladder** - P2, S.
  Full-metal rings are armor only, crafted from ingots: tin +1, copper +1, bronze +2, iron +2. **Hook:** `assets/data/items`,
  `assets/data/recipes`.
- [ ] **`AC-6` Retheme the stat accessories** - P2, S.
  `copper_ring` (+15% mana regen) and `tin_charm` (+20 max mana) stop being metal-named: rename to themed names (for example Ring of
  Focus, Charm of Reserves; the user may veto the names). `amulet_of_vigor`, `miners_belt` and `ember_charm` stay as loot items.
- [ ] **`AC-7` Verify and document** - P2, S.
  Extend `visual-examples/gear.json` or add `accessories.json` (`VOXEL_TIME=147`: double jump, feather fall), loopback
  `GameServerTests` for a client double-jumping without a rubberband, then fold the design into AGENTS.md ("Gear and equipment") and
  delete this file's finished entries. Tests assert rules only: the air jump grants exactly one extra jump and resets; the server
  accepts the legal hop and rejects a second; feather negates fall billing; sets ignore boots and gloves; ability flags invalidate
  when gear changes; ring armor sums through `GearStatsCalculator`.

## Reference
- Decisions (2026-11-08): boots and gloves are free-standing armor; the air jump does not pump the bellows; thresholds shift with
  numbers kept; ability accessories drop from chests and mobs; metal rings are armor only and the charm-like items take non-metal names.
- Still open: cloud boots' armor value, which realm or mob drops which charm, whether the wiki lists them.
- `I-3` (visible accessories on the model) and `I-6` (slots that grow) stay in `INVENTORY.md`.
