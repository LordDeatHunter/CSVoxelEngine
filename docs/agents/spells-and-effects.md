# Spell parts and status effects

The spell part schema, what a cast does, and the status effect schema and rules.

### Spell part schema (`assets/data/spell/<kind>/<name>.json`)

The folder is the part's kind (`element`, `shape`, `modifier`, `trigger`; `SpellPartKind`, folder name
`SpellPart.FolderOf`), never a field. Parts are keyed `<kind>/<name>` (`SpellPart.Key`), so an element
and a modifier may share a file name, and they take no content ids. A missing `spell/` folder or kind folder
is no parts (no trigger ships); an unknown kind folder or a `.json` lying directly in `spell/` is an error
naming it, and every part error names its file. `SpellPartLoader` (`Content/Spells/`) runs in
`ContentRegistry.Load` right after the status effects (which follow the tree species) and publishes `SpellPartRegistry` (`TryGet`,
`TryGetByKey`, `Require`, `All`), which `ContentRegistry.Reset` clears. Tests read temp folders through the pure
`SpellPartLoader.ReadParts`/`Compile`, never by replacing the published registry.

| field | default | meaning |
|---|---|---|
| `name` | file name | display name |
| `costShare` | required | the share of `ManaConfig.StartMax` the part adds to a spell's cost, in 0..1, exclusive of 0 |
| `complexity` | required | 0 or more; summed over a recipe it feeds the fizzle chance |
| `tags` | required | at least one non-blank, distinct, lowercase tag (compared ordinally): what a staff tier allows (MG-3). Their own namespace, unrelated to the item tool tags in `data/tags` |
| `damageFactor` | 1 | modifier only, finite and > 0: multiplies the spell's damage |
| `rangeFactor` | 1 | modifier only, finite and > 0: multiplies the spell's range (projectile flight) |
| `effect` | none | element only: the status effect the element applies on hit (`SpellPart.Effect`, read by `SpellEffects`), the name of a loaded `assets/data/effects` file; an unknown or blank name, or an `effect` on any other kind, is refused naming the file. `fire` -> `burning`, `frost` -> `frostburn`, `nature` none |
| `impact` | none | element only: the list of things the element does to the cell it lands on (`SpellPart.Impact`, a `SpellImpact` flag set): `ignite` burns `flammable` blocks to air, `freeze` sets fluid sources at or below the impact to their `freezesTo`, `thaw` melts `meltsTo` blocks to a source (these three within `SpellConfig.ImpactReachBlocks` and, for a self cast, around the caster's feet), `prime` primes a dry primable block (TNT), `light` lights a fueled kiln or a full, unlit branch-pile campfire, `grow` grows the aimed tree or plants `plant`. An unknown or repeated name, or an `impact` on any other kind, is refused naming the file. `fire` ignite, thaw, prime, light; `frost` freeze; `nature` grow |
| `plant` | none | element only, and only with `grow`: the tree species (a `data/trees` file name) a `grow` impact plants where it lands on ground holding no tree; an unknown or blank species is refused. `nature` -> `oak` |
| `delivery` | `projectile` | shape only: how the shape delivers its element (`SpellPart.Delivery`), `projectile` (a straight bolt, `SpellProjectile`) or `self` (the caster); anything else, or a `delivery` on another kind, is refused naming the file. `self` -> `self` |

`SpellRecipe` (a flat list of parts) compiles through `Compile(allowedTags)` to a `SpellStats`, GL-free
and pure (no roll, no registry read). The verdict (`SpellVerdict`) checks, in order: exactly one
element, exactly one shape, at most one trigger, no modifier twice, and, when a tag set is given (the
staff's), every part allowed by it — a part passes when **any** of its tags is in the set. `Cost` is
`ManaConfig.StartMax` times the summed `costShare` (the unit `PlayerMana.TrySpend` takes), so every
added part raises it; `FizzleChance` is `SpellRecipe.FizzleChanceFor` of the summed complexity: 0 up to
`SpellConfig.FizzleFreeComplexity`, then `FizzleChancePerComplexity` a unit, capped at
`FizzleMaxChance` — a chance only, the roll belongs to the cast (`SpellCaster`, rules in `Magic/CastRisk`: a fizzle fires no effect and backfires on the caster). `DamageFactor` and
`RangeFactor` are the products of the parts' factors. Every number is computed even for an invalid
recipe, so the spell builder shows it live; a cast must check `IsValid` before spending. A new part
is a JSON file, never a `switch` on its name. Slice 1 ships the elements `fire`, `frost` and `nature`,
the shapes `projectile` and `self` and the modifiers `power` (raises damage and cost) and `range`
(lengthens flight and raises cost), all tagged `basic`; every valid combination costs 20-40% of the
starting pool (the `ManaConfig` design rule). `SpellRecipeTests`, `SpellPartLoaderTests`.

**What a cast does (MG-5).** `Magic/SpellEffects` (owned by `World`, built only where the world is
authoritative, so never on a client) is the `World.SpellEffect` hook. A `projectile` shape launches a
`SpellProjectile` from just in front of the eye: straight at `SpellConfig.ProjectileSpeed` with no gravity,
sub-stepped (`ProjectileMaxStepBlocks`), stopped by a solid cell through `BoxSweep`, by the first non-air
cell its center enters (water, lava, leaves, a plant) or by a mob (`ThrownProjectile.FindMobHit`), and
spent after `SpellImpactRules.RangeFor` (`ProjectileRangeBlocks` times the `RangeFactor`). Bolts tick in
`World.Update` right after the thrown items and never hit players. A mob hit deals
`SpellImpactRules.DamageFor` (`BaseDamage` times the `DamageFactor`, at least 1) through
`MobStrikes.StrikeMob` and applies the element's `effect`; a cell hit runs `SpellBlockImpact` (the
`impact` table above). A `self` shape applies the effect to the caster (the camera or the server's
player, so a creative or flying caster is immune) and the element's ignite/freeze/thaw around its feet;
prime, light and grow are aimed only. A non-server world shows a bolt as sparks of its effect's tint
(`SpellSparks`). Known gaps: bolts are not sent to clients (the caster's own client flies a cosmetic copy,
`SpellBoltPreview`, see "Cast feedback" below) and not saved; a frostburned body over lava
crusts it. `SpellImpactRulesTests`, `SpellProjectileTests`, `SpellEffectsTests`.

### Status effect schema (`assets/data/effects/<name>.json`)

One file per effect; the file name is its name (`StatusEffectRegistry.Require(name)`), and an effect's
byte id is its index in file-name order (`StatusEffect.Bit` = `1 << Id`). At most
`StatusEffect.MaxKinds` (8) may exist, because every wire form is a one-byte mask; a ninth file is
refused naming the folder. `StatusEffectLoader` (`Content/StatusEffects/`) runs in
`ContentRegistry.Load` right **before** the spell parts, so an element's `effect` validates against it,
and every error names its file. Tests compile in-memory effects through the pure
`StatusEffectLoader.Compile`/`ReadEffects`.

| field | default | meaning |
|---|---|---|
| `name` | file name | display name |
| `durationSeconds` | required | finite and > 0: how long one application lasts (times the caller's scale, clamped to 0..`StatusEffectConfig.MaxDurationScale`) |
| `tickSeconds` | none | finite and > 0, required exactly when the effect deals damage or has a block flag, refused otherwise: the pulse interval |
| `damageShare` | none | in (0, 1]: damage per pulse as a share of the target's max health, at least 1 (`StatusEffectSet.DamagePerTick`) |
| `moveFactor` | 1 | in (0, 1]: scales the walk and swim speed (the player's `PlayerPhysics`, a goblin's walk, a slime's hop clock) |
| `attackFactor` | 1 | in (0, 1]: slows the local player's hit gap (`Mining.HitGapSeconds(held, factor)`: punch cadence, mining hits, the axe chop), a goblin's throw and melee cooldowns and a slime's contact cooldown |
| `blocks.ignite` / `blocks.freeze` / `blocks.thaw` | false | what each pulse does to the cells around the body (`StatusEffectBlockAction`): burn `flammable` blocks to air, freeze a fluid source under the feet into its `freezesTo`, thaw a block into its `meltsTo` fluid source |
| `tint` | none | sRGB `[r, g, b]`, 0..255 each: the mob tint (`uStatusTint`, decoded to linear) |
| `particle` | none | a `FireParticleKind` name (`ember`, `smoke`, `steam`, ...), matched case-insensitively: what an affected mob sheds |
| `icon` | none | a `.png` path under `assets/`: the player's HUD icon |
| `deathMessage` | none | the phrase after the player's name in a death line ("burned to death") |

An effect must do something: damage, a factor below 1 or a block flag. Three ship: `burning` (fire; 6 s,
5% a second, ignite and thaw, orange, embers), `frostburn` (frost; 8 s, 2% every 2 s, half speed, 0.6
attack, freeze, pale blue, steam) and `spore` (the sporeling's burst, see "Cave mobs"; 6 s, 3% every 1.5 s, 0.75
speed, 0.85 attack, fungal green, smoke, no icon), first guesses.

**Rules.** A target's effects are one `StatusEffectSet` (`Status/`, GL-free, allocation-free after the
first apply): re-applying an active effect refreshes its remaining time to the larger of the two and
never stacks its damage; damage lands once per `tickSeconds` and never after the duration; the movement
and attack factors are the **lowest** among the active effects. Effects tick **only in the
authoritative simulation** (singleplayer and the server): a mob's in `Mob.UpdateHazards` (cleared once it
is dying), the player's as under "Status effects" above; a client's sets are mirrors
(`SetMirrorMask`, unknown bits dropped) that never tick. **The one API** MG-5 calls is
`StatusEffects.Apply(target, effectName, durationScale)`, with overloads for a `Mob` (refused while
dying or dead), an `IMobTarget` (its default `Effects` member is null, and an immune, dead or effect-less
target refuses) and a `PlayerHealth` (refused while immune or dead); never call it on a client. The
visual-scenario `effect` op (`visual-examples/status-effects.json`, `VOXEL_TIME=147`) applies one to every
mob or to the player.

**Block interaction.** Each pulse ORs the effect's block flags into the set's pending actions, which
`MobDirector` drains after the mob managers run (mobs at their feet, targets at the eye less
`Camera.EyeHeight`) into `StatusEffectBlockWriter`, built only when not a client. The writer scans the
cells within `StatusEffectConfig.BlockReachBlocks` horizontally of the feet, from the cell under them to
the one above, asks the pure `StatusEffectBlockRule` (freeze only a fluid source whose top is at or below
the feet, so a body never freezes itself in), skips an unloaded cell, a cell that holds a
support-fragile neighbor up (`HoldsSomethingUp`, so it never needs a pop path) and a fluid cell a
falling cube overlaps, and stops at `MaxBlockWritesPerPulse` per body and `MaxBlockWritesPerFrame` in
all. Each write is `Chunk.SetBlockRaw` (saved, journaled to clients on a server) followed by every
edit-path hook: `UnmarkPlayerPlaced`, an asynchronous `MarkAreaDirty`, `FluidManager.ScheduleAround`,
`LeafDecayManager.ScheduleAround`, `FallingBlockManager.TryTopple` above, the LOD edit hook, and for a
tree block `TreeGenerator.PoisonCanopy` plus `LodManager.MarkEditedColumn`. No loot, no
`World.BlockEdited`. `StatusEffectBlockTests`.

**Visuals.** An affected mob is tinted by `uStatusTint` (a `vec4` in `test-fragment.glsl` mixed into
the albedo before the hit flash at `StatusEffectConfig.MobTintStrength`, zero by default and reset every
frame on the plain program, so everything else is byte-identical; `MobPasses` sets it from
`StatusEffectSet.TryGetTint`, the lowest-id tinted effect) and sheds its effects' particles through the
campfire `FireParticleSystem.Puff` (`AmbientEffects.UpdateStatusParticles`, nearest first, at most
`MaxParticleEmitters` mobs, `ParticlesPerSecond` each, never on a server). The HUD icons are committed 16x16 art. Tests: `StatusEffectLoaderTests`, `StatusBlockFlagTests`,
`StatusEffectSetTests`, `StatusEffectApplyTests`, `StatusEffectFactorTests`, `StatusEffectBlockTests`.

Known gaps: effects are not saved (`player.json`, `entities.json`); other players' effects are not shown
(the `PlayerList` carries none); the player's slowdown is client-trusted (the server rubberband only
caps speed from above); sword and slice swing durations are not scaled by the attack factor; a blast
does not consult effects; a slime's split children do not inherit its effects; and icebergs are not
consulted, so a frostburned rider may freeze water inside a berg's submerged boxes.
