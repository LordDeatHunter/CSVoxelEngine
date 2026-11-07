# Inventory, equipment, chests and ruin loot
> Guide: plans/README.md

## Status
Design settled with the user on 2026-11-07; `I-1` (gear core) is done. Tracks gear (armor and accessories with rarity and
rolled modifiers), chests and ruins that hold loot. One phase per prompt: implement it, update this file, stop.
The gear tab, equipment panel and chest panel build on the UI kit from `plans/INVENTORY_UI.md` (U-1, U-2 first).

## In progress
Nothing.

## Open

### Phase 2: armor and modifiers
- [ ] **`I-2` Armor math, immunity, modifiers, rarity and sets** - P1, L.
  - [ ] armor: `damage = max(minDamage(source), hit - armor)`, `minDamage` per `DamageSource` in `PlayerConfig`, a
    missing source uses the `universal` value (default 1); drowning and magic damage-over-time bypass it; lives in
    `PlayerHealth.TakeDamage`; fix the source-less callers (`ServerPlayer` fall and cactus, `LocalPlayer` fall)
  - [ ] effect immunity or resistance gated at `StatusEffectSet.Apply` (authoritative side only)
  - [ ] passive triggers only, declared in data (on-hit, on-damaged, low-health); no ability key
  - [ ] rarity tiers on items (common to legendary: color, tooltip, slot border, loot weight)
  - [ ] `ModifiersComponent` (rarity plus immutable modifier id/value list) minted server-side, saved in `ToolData`;
    crafted gear is fixed by material tier, only loot gear rolls; rolled items get a name prefix or suffix
  - [ ] `assets/data/sets/<name>.json`: members and `[{pieces, modifiers}]`, same modifier vocabulary as gear
  - [ ] tooltips for rarity, modifiers and set progress
  **Hook:** `PlayerHealth.TakeDamage`, `StatusEffectSet`, `ItemStack`, `ItemTooltip`.

### Phase 3: visuals and wire
- [ ] **`I-3` Armor and visible accessories on the player model** - P1, L. Per-part armor meshes drawn right after each
  body part (Blockbench-authored), accessories flagged `visible`; first-person head skip and back offset apply; shadow
  pass reuses them. Equipment ids ride in the `PlayerList` entry (one protocol bump); rolled modifiers stay private.
  Item debugger gets an equipment mode.
  **Hook:** `PlayerPasses`, `PlayerRig.BuildMeshes`, `RemotePlayer`, `ShadowPass`, `NetProtocol.ProtocolVersion`.

### Phase 4: chests
- [ ] **`I-4` Chest block entity, UI and container protocol** - P1, L.
  - [ ] `ChestManager` on the `QuernManager` pattern (Capture/Restore, WriteNet/ReadNet, `NotifyBlockRemoved`,
    stations.json, `StationNetCodec` plus protocol bump, `ClientMirror`, `World`, `WorldPersistence`)
  - [ ] tiered sizes from block JSON: wooden, bronze and iron crafted chests, plus one ruin chest per realm tier
    (surface, underground, hell, sky) mapped to a loot table
  - [ ] shared and server-authoritative: anyone can open, several players at once, slot diffs; new open, move and
    close messages
  - [ ] chest panel beside the [E] owned list (world-opened, like the spell builder): click moves a stack,
    shift-click moves all, search works on both sides
  - [ ] breaking drops the contents on every edit path (mining, TNT, felling, spells, fluids); run
    `edit-path-checklist`; a never-opened ruin chest rolls its table by the seeded roll first
  - [ ] recipes, creative catalog, `BlockRenderer` entry and lid render if wanted
  **Hook:** `Simulation/QuernManager.cs` (template), `BlockInteractions`, `RightClickAction`, `BlockEditor`,
  `TntManager.Detonate`, `App/SpellBuilderInput` (UI template).

### Phase 5: ruins, loot and polish
- [ ] **`I-5` Ruins with loot, inventory polish** - P2, L.
  - [ ] seedable `LootTable.Roll(ctx, list, Random)` overload (the current one uses `Random.Shared`); ruin chests roll
    lazily at first open from hash(cell, world seed), then persist; untouched chests are never saved
  - [ ] loot tables per realm and rarity
  - [ ] four per-cell hashed stamps, each with a `StampBand` and a `StampGateTests` row: surface (small broken
    stone/brick shells, biome-flavored), underground (small abandoned mining base in caves), hell (charred hellrock
    outposts), sky (ruined temples on island tops)
  - [ ] inventory polish: sort, shift-click quick-move, search improvements (no slot-grid rewrite)
  **Hook:** `WorldGen/Features/DesertWellGenerator.cs` (template), `ChunkTerrainFill`, `StampBand`, `Content/Loot`.

### Future
- [ ] **`I-6` Equipment slots that grow with milestones** - P3, M. Needs its own brainstorm; not in scope for I-1..I-5.
  I-1 keeps slot counts in data/config so this stays possible.

## Reference
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
  cotton for now. Passives, immunities and sets are parsed already; their rules are `I-2`.
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
