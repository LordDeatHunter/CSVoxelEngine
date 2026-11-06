# MAGIC: the spell builder

> Guide: plans/README.md

## Status

Tracks the magic system of Arc A (`TODO.md` `P-14`, which points here): mana, staffs, the station-built
spell builder, the unlock chain and the spell rules. Design interview done, no code yet. Last updated
2026-11-06. Everything below is a design decision, not a measurement. The first slice is the builder
with a few parts (`MG-1`..`MG-10`), not a lone fireball.

## In progress

Nothing.

## Open

### Slice 1 — the builder with a few parts

Two elements (fire, frost), two shapes (projectile, self), two modifiers, one station UI, one bindable
staff. In order:

- [ ] **`MG-1` Mana pool** — P1, M.
  `PlayerMana` beside `PlayerHealth`/`Breath`: passive regen, a max that grows with progression, a HUD
  bar, saved in `player.json`, server-owned and sent in `PlayerStatus` (a protocol bump; the client
  only mirrors). Creative ignores cost. **Hook:** `Player/PlayerHealth.cs`, `PlayerStatusCodec`,
  `PlayerSaveCodec`, `HudRenderer`.
- [ ] **`MG-2` Spell part data model** — P1, M.
  Parts are JSON like blocks and items: `assets/data/spell/<kind>/<name>.json` with kind `element`,
  `shape`, `modifier`, `trigger`. A `SpellRecipe` (one element + one shape + modifiers + optional
  trigger) compiles to a cost, a validity verdict and a fizzle chance, GL-free and tested. A new part
  must be data, never a `switch` on a name. **Hook:** `Content/ContentRegistry.cs` (a loader beside
  `RecipeLoader`), `add-content` skill.
- [ ] **`MG-3` Staff item and slots** — P1, M.
  An item whose JSON declares `staff` (slot count, allowed part tags, tier). The bound recipes ride the
  item instance as a component, like `CrucibleComponent`; a higher tier has more slots and opens more
  parts. **Hook:** `Items/Item.cs`, `ItemFactory`, `InventoryPanel/ItemStack.cs`, item schema in
  `AGENTS.md`.
- [ ] **`MG-4` Cast pipeline, server-authoritative** — P1, L.
  Right-click with a staff casts the selected slot (selection by key or wheel). A new `Cast` world
  intent carries slot and aim; the server checks the held staff, the bound recipe, mana and a rate
  limit, then runs the effect and bills mana, and answers with the usual correction/status. The client
  predicts only cosmetics. **Hook:** `Networking/WorldIntent*.cs`, `ServerSide/NetValidation.cs`,
  `RightClickAction`, `PlayerContext`. **Invariant:** a client never edits a cell or damages a mob.
- [ ] **`MG-5` Shapes and elements** — P1, L.
  Projectile (reuse `ThrownProjectile`/`ThrowAction` flight) and self first; beam, cone, area, touch and
  rune later. Elements interact with existing systems only: fire ignites TNT, campfires, kilns and
  leaves; frost freezes water and hardens lava; nature grows saplings (`TreeGrowthWriter`). No
  element-vs-element rules and no mob weaknesses for now. **Hook:** `TntManager`, `FluidManager`,
  `SaplingManager`, `MobStrikes`. Any block write follows the `edit-path-checklist` skill.
- [ ] **`MG-6` Risk: self-damage and fizzle** — P1, S–M.
  Your own fire and blasts can hurt you; an over-complex or conflicting recipe fizzles and wastes mana
  instead. The rules are pure functions with a gear bypass hook for later. **Hook:**
  `IMobTarget.TakeHit`, `PlayerHealth`, `DeathMessages`.
- [ ] **`MG-7` Builder station UI** — P1, L.
  A crafting station (not a tab) where the player picks parts they know and binds the result to a staff
  slot; shows cost, validity and fizzle risk live. **Hook:** `Interaction/BlockInteractions.cs` (station
  click chain), a new `UI/SpellBuilder/` folder, `InventoryPanelInput` for the pattern. Gating by
  station follows `P-2`.
- [ ] **`MG-8` Known parts and unlocks** — P1, M.
  The player's set of known parts, saved as names (the `Inventory.Knows` pattern) and riding the
  inventory net snapshot. Sources: scrolls as loot that teach a part, and research at a station. A few
  fixed signature spells ship as worked examples that teach the system. **Hook:** `Inventory.cs`,
  `InventorySaveCodec`, `InventoryNetCodec`, loot tables.
- [ ] **`MG-9` Mana restoration items** — P1, S.
  Items restore mana through a `use.action` (the `heal` pattern). Optional shared-reagent hook for
  `P-47`, never required. **Hook:** `Items/HealItem.cs`, item schema.
- [ ] **`MG-10` Cast feedback** — P2, M.
  Cast gesture, projectile and impact particles, a few SFX; the item debugger gets a cast mode. **Hook:**
  `Posing/ThrowPose.cs`, `Particles/`, `item-debugger` skill, `V-12`.

### Later

- [ ] **`MG-11` Utility spells that edit blocks** — P2, L.
  Mining, bridging and building spells; combat spells never edit blocks. Needs a reach and hardness
  rule, protected blocks and the full edit-path hook set. **Hook:** `BlockEditor`, `BlockPlacer`.
- [ ] **`MG-12` Trigger and chaining** — P2, L.
  A spell fires another on impact, on a timer or on death. Needs a depth cap, a cost that scales with
  the chain and a per-cast simulation budget.
- [ ] **`MG-13` Remaining shapes and elements** — P2, L.
  Beam, cone, area, touch, rune; arcane and nature elements; more modifiers (homing, piercing,
  lingering).
- [ ] **`MG-14` Gear that bypasses side effects** — P3, M. Armor or trinkets that cut self-damage or
  fizzle risk. Pairs with `P-7` armor and `P-18`.
- [ ] **`MG-15` Spell selection UX** — P3, M. Choose between key/scroll, a radial wheel (`U-13`), a
  separate spell hotbar and hold-to-charge. Slice 1 uses the simplest.

## Reference

- **Dual identity.** Magic is Arc A and must not depend on Arc B. A crossover (`P-47` shared reagent,
  `P-49` mana as power, `P-48` automated enchanting) is optional and needs a slow manual fallback.
- **Decided.** Mana pool, passive regen, max grows with progression, items restore it. Spells are built
  at a station from element + delivery shape + modifiers + trigger, then bound to a staff slot;
  casting is one click, not a live combo queue. The staff holds the slots and its tier limits which
  parts may combine. Scrolls and research unlock **parts**. Fire, frost, arcane and nature are the
  four elements. Self-damage and fizzle both exist.
- **Not wanted for now.** Element-vs-element rules, per-mob weaknesses, a live Magicka-style combo
  queue.
- **Open questions.** Which starting parts and signature spells; chain depth and mana scaling; which
  blocks a utility spell may touch; what the mana item and the `P-47` reagent are; the exact save and
  net shape of bound recipes; whether research is its own station.
