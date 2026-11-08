# Accessories
> Guide: plans/README.md

## Status
AC-1, AC-2 and AC-3 done (ability flags `equip.abilities` cached on `Equipment.Abilities`, the `undeadDamage` stat and
`Mob.IsUndead`; sets count head, chest and legs only; the cloud boots' air jump, with the server's airtime rise budget in
`MovementRules.AirRiseBudget`); the rest is designed, nothing else built yet. Tracks accessory abilities (cloud boots, charms), the metal ring ladder and the set-bonus rule
change, on top of the gear core in `INVENTORY.md` (`I-1`, `I-2`). First of three efforts, in order: accessories (`AC-*`), the
battle tower (`BATTLE_TOWER.md`), cave biomes (`CAVE_BIOMES.md`). One phase per prompt; each leaves the game building and the
tests green. Last updated 2026-11-08.

## In progress
Nothing.

## Open
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
  Extend `visual-examples/gear.json` or add `accessories.json` (`VOXEL_TIME=147`: double jump, feather fall), then fold the design into AGENTS.md ("Gear and equipment") and
  delete this file's finished entries. Tests assert rules only: the air jump grants exactly one extra jump and resets; the server
  accepts the legal hop and rejects a second; feather negates fall billing; sets ignore boots and gloves; ability flags invalidate
  when gear changes; ring armor sums through `GearStatsCalculator`.

## Reference
- Decisions (2026-11-08): boots and gloves are free-standing armor; the air jump does not pump the bellows; thresholds shift with
  numbers kept; ability accessories drop from chests and mobs; metal rings are armor only and the charm-like items take non-metal names.
- AC-3 decisions (2026-11-08): `cloud_boots` armor 2 (iron boots 3), rarity rare, no set; recipe 1 `iron_boots` + 4
  `cloud_shard`; sprite from `tools/generate-gear-textures.ps1` (a `cloud` palette, the iron boots' shape plus wing puffs). The air
  jump takes a fresh Space press, never in water or flight nor on the frame a creative double tap toggles flight (creative on foot
  may air jump); the charge refills on the ground, in water and in flight, so walking off a ledge still allows one. It has its own
  `AirJumpCount` and never moves `JumpCount`, so it never works the bellows. Protocol unchanged (24).
- The server's air hop rule is a height budget per airtime (`MovementRules.AirRiseBudget`: one jump apex per jump plus
  `NetAirRiseSlackBlocks`), reset by support, by a descent-then-rise with ground within reach below
  (`PlayerBodyProbe.GroundedWithin`), widened by a bounce block under the takeoff (`BounceWithin`) and by a queued knockback lift
  (`ServerPlayer.TakeHit` -> `MovementState.GrantLift`, `NetLiftCreditSeconds`). A correction keeps the sum, a teleport resets it.
  Accepted gaps: a non-wearer may hop once after walking off a ledge (height-based budget); a hop within one sample step of the
  ground counts as a landing; a client that double jumps before the server applied its equip intent is corrected once; iceberg
  mirror lag can still mis-judge a rider's bunny hop.
- Still open: which realm or mob drops which charm, whether the wiki lists them.
- `I-3` (visible accessories on the model) and `I-6` (slots that grow) stay in `INVENTORY.md`.
