# MAGIC: the spell builder

> Guide: plans/README.md

## Status

Tracks the magic system of Arc A (`TODO.md` `P-14`, which points here): mana, staffs, the station-built
spell builder, the unlock chain and the spell rules. Design interview done (two rounds). Last updated
2026-11-06. Everything below is a design decision, not a measurement. The first slice is the builder with
a few parts (`MG-1`..`MG-10`, plus `MG-4b` status effects), not a lone fireball. Work them in ID order,
`MG-4b` between `MG-4` and `MG-5`.

`MG-1` shipped: the mana pool (`Player/PlayerMana.cs`, owned by `PlayerHealth`; tuning in
`Config/ManaConfig.cs`) regenerates slowly, refills on respawn, is spent through `TrySpend` (free in
creative, refused when short), grows through `SetKnownParts` (no caller yet, see `MG-8`), is saved as
`Mana` in `player.json` (the max is not saved), rides the end of `PlayerStatus` (protocol 11) and draws
as a bar under the hotbar (`HudRenderer.DrawMana`). The 100 start, +10 per known part, 300 cap and
150 s refill are first guesses.

## In progress

Nothing.

## Open

### Slice 1 — the builder with a few parts

Three elements (fire, frost, nature), two shapes (projectile, self), two modifiers (the implementer picks
and flags them; power and range are the simple default), a data-driven status-effect system, one station
UI, one bindable staff (two tiers). In order:

- [ ] **`MG-2` Spell part data model** — P1, M.
  Parts are JSON like blocks and items: `assets/data/spell/<kind>/<name>.json` with kind `element`,
  `shape`, `modifier`, `trigger`. A `SpellRecipe` (one element + one shape + modifiers + optional
  trigger) compiles to a cost, a validity verdict and a fizzle chance, GL-free and tested. A new part
  must be data, never a `switch` on a name. **Hook:** `Content/ContentRegistry.cs` (a loader beside
  `RecipeLoader`), `add-content` skill.
- [ ] **`MG-3` Staff item and slots** — P1, M.
  An item whose JSON declares `staff` (slot count, allowed part tags, tier). The bound recipes ride the
  item instance as a component, like `CrucibleComponent`; a higher tier has more slots and opens more
  parts. Two tiers: tier 1 a wooden staff (about 2 slots, basic parts), tier 2 (about 4 slots, more
  parts). Crafted from sticks plus a crystal-like part through plain recipe JSON, never found as loot only.
  **Hook:** `Items/Item.cs`, `ItemFactory`, `InventoryPanel/ItemStack.cs`, item schema in
  `AGENTS.md`.
- [ ] **`MG-4` Cast pipeline, server-authoritative** — P1, L.
  Plain right-click with a staff casts the selected slot. **Left Ctrl (crouch) + scroll** cycles the
  slot while a staff is held; scroll without Ctrl still changes the hotbar item, and Shift/sprint is
  untouched (a HUD slot indicator shows the selection). A new `Cast` world
  intent carries slot and aim; the server checks the held staff, the bound recipe, mana and a rate
  limit, then runs the effect and bills mana, and answers with the usual correction/status. The client
  predicts only cosmetics. **Hook:** `Networking/WorldIntent*.cs`, `ServerSide/NetValidation.cs`,
  `RightClickAction`, `PlayerContext`. **Invariant:** a client never edits a cell or damages a mob.
- [ ] **`MG-4b` Status effects** — P1, L.
  A full data-driven system: `assets/data/effects/<name>.json`, loaded beside the other content (a
  loader like `RecipeLoader`, `add-content` skill). An effect may declare damage over time (tick
  interval, damage share), a movement and attack speed factor (strongest wins on stacking), block
  interaction (burning ignites flammable blocks, frost freezes water or melts ice), and visuals (mob
  tint, particle kind, HUD icon for the player). Effects apply to mobs and the player, tick only in the
  authoritative simulation and sync (`EntityList` entries, `PlayerStatus`; `MG-1` took protocol 11,
  so this bumps it again). Fire applies burning, frost applies frostburn,
  nature has none. Damage uses the new `DamageSource.Magic` (own `DeathMessages` line, e.g. burned by
  their own fireball). **Hook:** `Entities/Mob.cs`, `Player/PlayerHealth.cs`, `PlayerStatusCodec`,
  `NetEntity`, `HudRenderer`, `BillboardPasses`/`Particles/`. **Invariant:** block interaction writes
  cells, so it follows the `edit-path-checklist` skill and runs server-side only.
- [ ] **`MG-5` Shapes and elements** — P1, L.
  Projectile (reuse `ThrownProjectile`/`ThrowAction` flight) and self first; beam, cone, area, touch and
  rune later. Elements: fire ignites TNT, campfires, kilns and leaves and applies burning; frost freezes
  water, hardens lava and applies frostburn; nature grows saplings (`TreeGrowthWriter`). No
  element-vs-element rules and no mob weaknesses for now. **Hook:** `TntManager`, `FluidManager`,
  `SaplingManager`, `MobStrikes`. Any block write follows the `edit-path-checklist` skill.
- [ ] **`MG-6` Risk: self-damage and fizzle** — P1, S–M.
  Your own fire and blasts can hurt you; an over-complex or conflicting recipe fizzles and wastes mana
  instead. The rules are pure functions with a gear bypass hook for later. Self-damage uses
  `DamageSource.Magic`. **Hook:**
  `IMobTarget.TakeHit`, `PlayerHealth`, `DeathMessages`.
- [ ] **`MG-7` Builder station UI** — P1, L.
  A crafting station (not a tab) where the player picks parts they know and binds the result to a staff
  slot; shows cost, validity and fizzle risk live. The station is one ordinary block (e.g.
  `spell_table`, JSON only) whose right-click opens the UI through the station click chain; it has no
  block entity and no multiblock, since the UI only binds to the staff in hand. **Hook:** `Interaction/BlockInteractions.cs` (station
  click chain), a new `UI/SpellBuilder/` folder, `InventoryPanelInput` for the pattern. Gating by
  station follows `P-2`.
- [ ] **`MG-8` Known parts and unlocks** — P1, M.
  The player's set of known parts, saved as names (the `Inventory.Knows` pattern) and riding the
  inventory net snapshot. Slice 1 source: scrolls only, items that teach a part (research is later and
  gets its own station decision). Creative knows every part. A survival world starts with **one
  scroll**: a hint scroll that teaches the parts of one handpicked example combo and shows its recipe at
  the table. No signature presets and no pre-bound staffs: the player explores combinations. The known
  part count sizes the mana pool: whenever it changes call `PlayerMana.SetKnownParts` (and set
  `ServerPlayer.StatusDirty` on a server), and apply the known parts before the saved mana is restored, or a
  saved value above the starting max is clamped down on load. **Hook:** `Inventory.cs`,
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
  four elements (arcane is later). Self-damage and fizzle both exist.
- **Decided in the interview (2026-11-06).** Slice 1 ships fire, frost and nature. Damage is
  `DamageSource.Magic` plus element status effects from a full data-driven effect system (`MG-4b`).
  The station is a plain block. Casting is plain right-click; Left Ctrl + scroll cycles slots (Shift is
  sprint and stays free). The mana pool is small with slow regen. The staff is crafted, in two tiers.
  Survival starts with one hint scroll; creative knows everything. The user runs a task-by-task
  workflow that picks its own tier, so each `MG-*` is a self-contained sitting.
- **Not wanted for now.** Element-vs-element rules, per-mob weaknesses, a live Magicka-style combo
  queue.
- **Open questions.** Which two modifiers ship (implementer picks and records it here); which
  example combo the hint scroll teaches; chain depth and mana scaling (`MG-12`); which blocks a utility
  spell may touch (`MG-11`); what the mana item and the `P-47` reagent are; the exact save and net
  shape of bound recipes on the staff component; whether research is its own station.
