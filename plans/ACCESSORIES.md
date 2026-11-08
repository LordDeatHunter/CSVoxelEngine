# Accessories
> Guide: plans/README.md

## Status
AC-1..AC-4 done (ability flags `equip.abilities` cached on `Equipment.Abilities`, the `undeadDamage` stat and
`Mob.IsUndead`; sets count head, chest and legs only; the cloud boots' air jump, with the server's airtime rise budget in
`MovementRules.AirRiseBudget`; five loot-only ability charms, with the no-fall, extra-air and magnet consumers wired); the rest
is designed, nothing else built yet. Tracks accessory abilities (cloud boots, charms), the metal ring ladder and the set-bonus rule
change, on top of the gear core in `INVENTORY.md` (`I-1`, `I-2`). First of three efforts, in order: accessories (`AC-*`), the
battle tower (`BATTLE_TOWER.md`), cave biomes (`CAVE_BIOMES.md`). One phase per prompt; each leaves the game building and the
tests green. Last updated 2026-11-08.

## In progress
Nothing.

## Open
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
- AC-4 decisions (2026-11-08): all five are rare accessories with no armor or stats, loot-only (no recipe):
  - Feather Charm (`feather_charm`, `no_fall_damage`): `ruin_sky` gear pool (weight 2, rare..legendary), `galeraptor` (own
    pool, chance 0.04).
  - Gill Charm (`gill_charm`, `extra_air`, the lung `GearConfig.ExtraAirFactor` = 2x): `ruin_surface` gear pool (weight 1,
    common..uncommon), `ruin_underground` gear pool (weight 2, uncommon..rare).
  - Frostward Charm (`frost_charm`, immune to frostburn): `ruin_sky` gear pool (weight 2, rare..legendary), `cloudgolem` (own
    pool, chance 0.06).
  - Fireward Charm (`fire_charm`, immune to burning; the ember charm stays the on-hit variant): `ruin_hell` gear pool (weight 2,
    rare..epic), `cinderwitch` (own pool, chance 0.04), `hellbrute` (own pool, chance 0.03).
  - Lodestone Charm (`magnet_charm`, `magnet`, `GearConfig.MagnetRangeFactor` = 3x `DropManager.PickupRange`):
    `ruin_underground` gear pool (weight 2, uncommon..rare), `sandworm` (own pool, chance 0.25).
  - Consumers: `PlayerHealth.TakeDamage` skips a `Fall` blow while `NegatesFallDamage`, and the server's
    `ObserveAcceptedState` reads `Inventory.Equipment.Has` before billing; `Breath.MaxAir` follows `ApplyGear` (a full lung stays
    full, otherwise the seconds left are kept, so swapping the charm under water adds no air; the 0..40 wire step stays a share of
    the receiver's lung, protocol 24); `DropCollector.Range` per collector, and the nearest collector whose own range holds a drop
    wins over a nearer one out of its range.
  - Accepted gaps: a magnet-latched drop is pulled through walls like any attracted drop; a charm from a mob table is an unrolled
    instance (no rarity roll); a client's bubble row can lag an equip by one status.
- Still open: whether the wiki lists the charms.
- `I-3` (visible accessories on the model) and `I-6` (slots that grow) stay in `INVENTORY.md`.
