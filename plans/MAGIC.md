# MAGIC: the spell builder

> Guide: plans/README.md

## Status

Tracks the magic system of Arc A (`TODO.md` `P-14`, which points here): mana, staffs, the station-built
spell builder, the unlock chain and the spell rules. Design interview done (two rounds). Last updated
2026-11-07. Everything below is a design decision, not a measurement. The first slice is the builder with
a few parts (`MG-1`..`MG-10`, plus the `MG-4b` status effects, shipped), not a lone fireball. Work them
in ID order.

`MG-1` shipped: the mana pool (`Player/PlayerMana.cs`, owned by `PlayerHealth`; tuning in
`Config/ManaConfig.cs`) regenerates slowly, refills on respawn, is spent through `TrySpend` (free in
creative, refused when short), grows through `SetKnownParts` (no caller yet, see `MG-8`), is saved as
`Mana` in `player.json` (the max is not saved), rides the end of `PlayerStatus` (protocol 11) and draws
as a bar under the hotbar (`HudRenderer.DrawMana`). The 100 start, +10 per known part, 300 cap and
150 s refill are first guesses.

`MG-2` shipped: the spell part data (`Content/Spells/`, `assets/data/spell/<kind>/<name>.json`, loaded by
`SpellPartLoader` into `SpellPartRegistry`; `SpellRecipe.Compile` gives the verdict, cost, fizzle chance
and effect factors). The slice-1 modifiers are **power** (raises damage and cost) and **range**
(lengthens projectile flight and raises cost). Every slice-1 part is tagged `basic`. Cost is the parts' summed `costShare` times `StartMax`, fizzle comes from the
summed complexity (`SpellConfig`). The shares, complexities and fizzle numbers are first guesses.

`MG-3` shipped: an item whose JSON declares `staff` (`slots`, `tier`, `tags`, checked by
`ItemLoader.ParseStaff`) is a `StaffItem`, stored in the Tools pool one instance at a time. Each slot
holds one whole spell recipe: the `StaffComponent` on its `ItemStack` carries the part keys
(`<kind>/<name>`) per slot plus the selected slot, immutable. `StaffBinding` holds the rules: `TryBind`
binds only a recipe that `SpellRecipe.Compile` finds valid against the staff's tags (so a tier gates its
parts by tag), plus `Clear`, `Cycle` (wraps both ways) and `Select`; `MG-4` cycles and selects in
play, and only binding is left without a caller (`MG-7` binds). Two staffs ship: the wooden staff (tier 1, 2 slots, `basic`; two sticks
and a crystal block) and the crystal staff (tier 2, 4 slots, `basic` and `advanced`; the wooden staff,
two crystal blocks and a copper ingot); no `advanced` part ships yet. Bindings are saved as
`ToolData.Staff` in `player.json` by part key, re-bound on load against this run's parts (what no longer
binds is dropped with a warning), and ride the inventory snapshot in the same shape (no protocol bump).
Crafting consumes the least-bound staff first. Known gap: a dropped staff loses its spells, like a
crucible loses its metal, because an `ItemDrop` carries only an id.

`MG-4` shipped: right-click with a staff casts its selected slot through the one seam,
`Magic/SpellCaster` (owned by `World`), with the rules in `CastRules`, the pacing in `CastPacing` and the
staff lookup in `HeldStaff`; the right-click chain runs stations first, then the cast, so a staff never
places. Left Ctrl + wheel cycles the slot while a staff is held (the plain wheel still moves the hotbar)
through the `SelectStaffSlot` inventory action, which the server owns: the client predicts and the
inventory snapshot keeps or reverts it. The server replays the `Cast` world intent (protocol 12) and
refuses a cast without a staff, on a slot that is not the selected one or is unbound, with an invalid
recipe, inside `SpellConfig.CastCooldownSeconds` (less `NetworkConfig.NetCastSlackSeconds`) or short of
mana; it bills exactly the compiled cost, free in creative, and a cast also draws one edit token as a
spam cap. The effect is the `World.SpellEffect` hook, which `MG-5`'s `SpellEffects` fills. A client
predicts only: it checks and paces, never spends and never fires the effect. The HUD shows
`StaffSlotIndicator` (the selected slot and its parts, or empty) beside the hotbar. The gesture is a
punch swing until `MG-10`, and nothing rolls the fizzle chance yet (`MG-6`). The 0.5 s cooldown and
0.1 s slack are first guesses.

`MG-4b` shipped: status effects are data, `assets/data/effects/<name>.json`, compiled by
`StatusEffectLoader` into `StatusEffectRegistry` (`Content/StatusEffects/`; id = file-name order, at
most 8, since every wire form is a byte mask) before the spell parts, and an element part names its
effect in a new `effect` field (fire -> `burning`, frost -> `frostburn`, nature none), validated at load.
A target's effects are a GL-free `Status/StatusEffectSet` (on `Mob` and `PlayerHealth`, exposed through
`IMobTarget.Effects`): a refresh never stacks, damage over time is `DamageSource.Magic` (the effect's
`deathMessage` phrase is the death line), the lowest move and attack factor wins, and only the
authoritative simulation ticks (a mob in `UpdateHazards`, the player in `LocalPlayer.UpdateHealth` or
`ServerPlayer.TickStatus`; creative, flying or dead players are cleared). `MG-5` applies the element's
effect through `Status/StatusEffects.Apply(target, name, durationScale)`. Block interaction is data too: blocks gain
`flammable`, `freezesTo` and `meltsTo`, effects `blocks.ignite/freeze/thaw`, and the authoritative
`StatusEffectBlockWriter` (drained by `MobDirector`) writes cells with every edit-path hook, capped per
pulse and per frame. Protocol 13 ends each `EntityList` entry with an effect mask and `PlayerStatus` with
the player's; a client mirrors them. Visuals: the `uStatusTint` mob tint, embers or steam through the
campfire particles, HUD icons left of the hearts (`tools/generate-effect-icons.py`), and the scenario op
`effect` (`visual-examples/status-effects.json`). First guesses: burning 6 s, 5% a second, ignites and
thaws; frostburn 8 s, 2% every 2 s, half speed, 0.6 attack, freezes. Known gaps: effects are not saved,
other players' effects are not shown, the player slowdown is client-trusted, blade swing durations are
not scaled by the attack factor, a blast does not consult effects, a slime's split children do not
inherit them, and icebergs are not consulted when freezing.

`MG-5` shipped: what a cast does is part data. A shape names its `delivery` (`projectile`, the default,
or `self`), an element its `impact` flags (`ignite`, `freeze`, `thaw`, `prime`, `light`, `grow`) and, with
`grow`, a `plant` tree species, all validated by `SpellPartLoader`; fire is ignite, thaw, prime and light,
frost freeze, nature grow with oak. `Magic/SpellEffects` (owned by `World`, built only where the world is
authoritative, never on a client) is the `World.SpellEffect` hook. A projectile is a `SpellProjectile`:
straight, no gravity, sub-stepped, colliding through `BoxSweep` like a thrown item, stopping at the first
non-air cell its center enters or on a mob (`ThrownProjectile.FindMobHit`, shared), and spent after
`ProjectileRangeBlocks` times the recipe's `RangeFactor`. A mob hit deals `BaseDamage` times the
`DamageFactor` (at least 1) through `MobStrikes.StrikeMob` and applies the element's effect; a cell hit runs
`SpellBlockImpact`: prime through `WorldItemUse.PrimeTnt` (a dry TNT block), light a fueled kiln or a full
unlit branch pile, grow the aimed tree or plant the element's species (`World.TryGrowTree`), and
ignite/freeze/thaw the cells within `ImpactReachBlocks` through the new `StatusEffectBlockWriter.ApplyAt`
(a freeze only at or below the impact's level), every write with its edit-path hooks. A self cast applies
the effect to the caster (the camera or the server's player, so a creative caster is immune) and the block
conversions around its feet. Frost hardens lava because `lava.json` now freezes to cobblestone. The
scenario op `cast` (part keys, no staff or mana) drives `visual-examples/spells.json`. First guesses:
speed 24, range 24 blocks, base damage 4, impact reach 1. Known gaps: bolts are not sent to clients (only
their consequences, until `MG-10`) and not saved; a self cast never primes, lights or grows (nature + self
does nothing); bolts never hit players; a frostburned body standing over lava crusts it to cobblestone.

## In progress

Nothing.

## Open

### Slice 1 — the builder with a few parts

Three elements (fire, frost, nature), two shapes (projectile, self), two modifiers (power and range), one station
UI, one bindable staff (two tiers). In order:

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
- **Open questions.** Which example combo the hint scroll teaches; chain depth and mana scaling
  (`MG-12`); which blocks a utility spell may touch (`MG-11`); what the mana item and the `P-47` reagent are; whether research is its own
  station.
