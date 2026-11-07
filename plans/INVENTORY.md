# Inventory, equipment, chests and ruin loot
> Guide: plans/README.md

## Status
Design settled with the user on 2026-11-07; `I-1` (gear core), `I-2` (armor, modifiers, rarity, sets) and `I-4` (chests) are done. Tracks gear (armor and accessories with rarity and
rolled modifiers), chests and ruins that hold loot. One phase per prompt: implement it, update this file, stop.
The gear tab, equipment panel and chest panel build on the UI kit from `plans/INVENTORY_UI.md` (U-1, U-2 first).

## In progress
Nothing.

## Open

### Phase 3: visuals and wire
- [ ] **`I-3` Armor and visible accessories on the player model** - P1, L. Per-part armor meshes drawn right after each
  body part (Blockbench-authored), accessories flagged `visible`; first-person head skip and back offset apply; shadow
  pass reuses them. Equipment ids ride in the `PlayerList` entry (one protocol bump); rolled modifiers stay private.
  Item debugger gets an equipment mode.
  **Hook:** `PlayerPasses`, `PlayerRig.BuildMeshes`, `RemotePlayer`, `ShadowPass`, `NetProtocol.ProtocolVersion`.

### Phase 5: ruins, loot and polish
- [ ] **`I-5` Ruins with loot, inventory polish** - P2, L.
  - [ ] ruin chests roll lazily at first open from hash(cell, world seed) through the seedable `LootTable.RollStacks`
    (done: the loot depth work, `ruin_*` tables), then persist; untouched chests are never saved
  - [x] four ruin placements on the M-15 template system (`RuinPlacement`, `data/structures`), each with a band, a stage and a
    `StampGateTests` row; more templates (biome-flavored shells, mining bases) are only JSON now
  - [ ] inventory polish: sort, shift-click quick-move, search improvements (no slot-grid rewrite)
  **Hook:** `WorldGen/Features/DesertWellGenerator.cs` (template), `ChunkTerrainFill`, `StampBand`, `Content/Loot`.

### Future
- [ ] **`I-6` Equipment slots that grow with milestones** - P3, M. Needs its own brainstorm; not in scope for I-1..I-5.
  I-1 keeps slot counts in data/config so this stays possible.

## Reference
- **What I-4 shipped.** A chest is a model block with a `chest` object (`slots`, optional `loot` table; `ChestProfile`,
  `BlockType.Chest`): the crafted `wooden_chest`, `bronze_chest` and `iron_chest` (the iron one is plated with crushed
  iron, since iron cannot be smelted yet) and the worldgen-only `ruin_chest_surface`, `_underground`, `_hell` and `_sky`
  (`hasItem: false`, picked as a wooden chest, drop nothing but their contents). `ChestManager` (`Simulation/Chests`, the
  quern pattern) holds a `ChestState` per used cell, created on first use: a ruin chest rolls its table through
  `ChestLoot.Materialize` from the hash of its cell and the world seed, so open-then-break and break-unopened give the same
  contents, and a never-used chest holds no state and is never saved (`stations.json` `Chests`, `ChestSaveCodec`, the
  inventory instance shape per slot). Breaking spills the contents (`BlockEditor`, the stale sweep in `ChestManager.Update` for
  a blast); a tool or gear drop loses its components, since a drop carries only an id. Moves are `ChestTransfer` rules
  (stackables merge up to `ChestConfig.MaxStack`, instances move whole through `ItemStack.CloneAs` and keep their
  components, nothing is created or lost) behind `ChestAccess.Apply`. Wire: protocol 20, `ChestOpen`/`ChestMove`/`ChestClose`
  from the client and a per-player `ChestContents` back (never the station broadcast); the server (`ChestSessions`) judges every
  move against the player's open chest and reach, sends the new contents to every viewer, and closes a chest that was
  broken or walked away from. UI: a right-click on a chest is a `RightClickOutcome.Container` claim, `ChestInput` opens the
  `ChestView` card (`ChestCardLayout`/`ChestCardRenderer`) beside the [E] list, which then deposits on a row click (one unit,
  all with shift); the chest card withdraws the same way and has its own search field and wheel scroll. Scenario ops
  `setblock`, `chest` and `chestrow`; `visual-examples/chests.json`. `ChestTests`, `ChestServerTests`. Known gaps: a chest
  is not mirrored to other viewers' panels when a *non-viewer* changes it (only viewers can); no lid animation.
- **What I-1 shipped.** Gear instances live in the same instance list as tools (`Inventory.Tools`, `ItemStack`)
  but have their own pool category (`InventoryCategory.Gear`, `GearConfig.GearCapacity`, `UsedOf`, `CapacityOf`,
  `GearEntries`), so every tool-instance lookup, the save and the wire carry them unchanged. `Equipment`
  (`inventory.Equipment`) holds one instance id per slot (5 armor in head, chest, legs, feet, gloves order, then 4
  accessories). A worn piece stays in the pool (a deviation from "outside the pool": the pool cap counts it),
  `RemoveTool` takes it off, and gear never goes on the hotbar. Item JSON gained `equip` (`slot`, `armor`,
  `modifiers`, `set`, `visible`, `immune`, `passives`) and `rarity`, parsed by `ItemLoader.ParseEquip` into a
  `GearInfo` on a `GearItem`. `GearStats` sums the worn modifiers (`GearStatsCalculator`, clamped) and is applied by
  `PlayerHealth.ApplyGear` (max health; mana max and regen where the pool is simulated), `Camera.GearMoveFactor`,
  the mining and attack pace (`LeftClickAction`, `BreakProgress`, `AttackPacing`) and the factor on
  `Combat.*Damage`. Wire: protocol 19, `NetInventoryAction.EquipGear` and `UnequipGear`, and
  `PlayerSaveData.InventoryData.Equipped`. UI: the Gear tab (rows marked "worn") and
  `EquipmentCardLayout`/`EquipmentCardRenderer`. Placeholder content: a leather and a bronze set and four
  accessories (`tools/generate-gear-textures.ps1`, `visual-examples/gear.json`); the leather set is crafted from
  cotton for now. - **What I-2 shipped.** Armor: `ArmorRules.Reduce` (`max(min(hit, minDamage(source)), hit - armor)`, per-source minimums
  in `PlayerConfig.ArmorMinDamage*`, drowning, magic and a source-less blow bypass it) applied in
  `PlayerHealth.TakeDamage(int, DamageSource, ...)`, with `ServerPlayer`'s fall and cactus now naming their source;
  `PlayerHealth.Gear` is the worn `Equipment`. Immunity: `StatusEffectSet.ImmunityCheck` gates every apply (so the
  spell, hell-bolt and passive paths all honor an item's `equip.immune`). Passives (`GearPassives`, no ability key):
  `on_hit` through `MobStrikes.StrikeMob` onto the struck mob; `on_damaged` and `low_health` fire on the mobs
  within `GearConfig.PassiveAuraRadiusBlocks` of the wearer from `MobDirector.FireWearerPassives` (singleplayer and
  each server player; low health has a cooldown). Rarity: `Rarity`/`Rarities` (color, loot weight, `PickByWeight`).
  Modifiers: `ModifiersComponent` on a gear stack (saved as `ToolData.Rolled`, clamped on restore), minted by the pure
  `GearRoller.Roll(gear, rarity, seed)` and credited with `Inventory.AddRolledGear`; crafted gear never rolls. Sets:
  `assets/data/sets/<name>.json` (`GearSetLoader`, `GearSetRegistry`; members are the items naming the set), summed by
  `GearStatsCalculator`. Tooltip: rarity-colored title, rolled lines, immunities, passives and set progress
  (`ItemTooltip.Lines(..., worn, out colors)`). Content: `ember_charm` (immune to burning, a 25% on-hit burning).
  `GearRulesTests`.
- **Decisions.** No durability on gear. Stats gear may change in v1: max health, max mana and mana regen, move speed,
  mining and attack speed and damage. Chest sizes come from block JSON. Gear uses a new pool because only the 8-slot
  Tools pool carries per-instance components today.
- **Code facts (2026-11-07).** `Inventory` pools: blocks and misc are counts (cap 2048 each), tools a list capped at 8;
  `ItemStack` holds struct components (`StaffComponent`, `CrucibleComponent`); `ItemStack.Enchantments` is saved but
  unread. The inventory wire form is the same JSON as `player.json`, so new `PlayerSaveData` fields sync for free.
  Every player hit ends at `PlayerHealth.TakeDamage`; mob damage in singleplayer is billed in
  `MobDirector.BillLocalPlayer`. `LootTable.Roll` uses `Random.Shared` and `LootContext` has no seed.
- **Phasing.** Plan was approved as five phases; the approved plan text is in
  `C:\Users\DH\.claude\plans\shimmering-stargazing-zebra.md`.
- **Verification.** Tests assert rules only: armor floor and per-source minimum, modifier roll determinism per seed,
  set thresholds, chest save and net round trip, seeded lazy loot equal on break and on open, stamps equal across chunk
  seams, `HandledComponentTypes` coverage. Loopback `GameServerTests` for equipment sync and two players in one chest.
  Scenarios with `VOXEL_TIME=147` for gear, chests and each realm's ruin.
