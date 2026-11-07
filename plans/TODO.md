# TODO: gameplay roadmap

> Guide: plans/README.md

## Status

Gameplay and content only: performance is in `PERF.md`, tree residue in `TREES.md`, and
architecture, content schema and threading rules in `AGENTS.md`. Last updated 2026-11-05.
**Current priority:** playable loop first. Finish the Act I survival floor (Milestone 1:
persistence, furnace, metal tiers, forge, armor), then the first dungeon slice (Milestone 2), then
one slice per remaining arc to prove the dual identity. No food/hunger system is planned.
Worldgen work (`BIOMEGEN.md` phase E is parked) happens only where gameplay needs it: ore depth
bands for `P-25`, structure sites for the dungeon. The engine is far ahead of the content; the
next wins are content and progression, not systems.

## In progress

Nothing.

## Open

### Suggested milestones

#### Milestone 1 — Act I survival floor (do first)

The smallest bundle that makes the world a *game*: stakes and an advance (no food system).
Mostly content JSON plus small systems; high feel, low engine risk. In order:

1. **`P-3` generic furnace / smelter** — `sand -> glass`, `mud -> brick`, and the iron bloom;
   generalizes the kiln pattern and becomes the automation seed. Lands before `P-2` gating.
2. **`P-43` anvil / forge** — pulled into this milestone: iron goes `crushed_iron` -> furnace
   bloom -> forge-shaped parts (this replaces the earlier idea of routing iron through the kiln's
   molten path).
3. **`P-7` / `P-25` metal tiers** — iron, tin, bronze, steel as the tier ladder (`P-44` covers the
   alloys). Progression, processing requirements and per-tier recipes are **TBD, defined later**.
4. **Armor (in `P-7`)** — flat damage reduction from summed armor points (capped), equipped from
   the inventory panel, shown in HUD/tooltip, with worn armor drawn on `PlayerModel` and remote
   players (this pulls `V-14` in). *Hook:* the damage pipeline (`PlayerHealth`, `IMobTarget.TakeHit`).

#### Milestone 2 — Arc A vertical slice: the first dungeon

Prove the fantasy half. Infrastructure first, in order: `M-15` (structure templates), `M-17`
(spawner block), then author one crypt (`P-16`, with `M-16` loot depth as needed), then the boss
(`P-17`), then the first magic slice, the spell builder with a few parts (`P-14`, `MAGIC.md`). A single hand-authored crypt with loot, a spawner
room and a boss is enough to make health, combat and loot matter and to give survival a
destination. Placing it needs a world-site hook, the one worldgen touch this milestone allows.

#### Milestone 3 — Arc B vertical slice: first power

Prove the automation half. Needs `P-19` (hand crank -> water wheel -> one powered machine),
`P-21` (one multi-step chain with a byproduct) and `P-20` (the crudest item movement). This also
validates "machine tick" as a reusable concept beyond the quern/kiln/casting one-offs.

#### Milestone 4 — cross-pollination proof

Ship one link from the **Cross-pollination** section end-to-end (`P-47` shared exotic resource,
`P-52` dungeons as gated resource nodes) and confirm either arc can reach its own endgame without
the other while still being faster together.

### 1. Progression

#### Act I — the shared primitive-tech spine

- [ ] **`P-2` · Crafting stations gate recipes** — P1, M. *(after the furnace, `P-3`)*
  The `E` panel crafts any recipe from anywhere. Add a `station` field and require the matching
  station (crafting table, campfire, quern, furnace). The shapeless list-and-feed model stays;
  this is gating, not a grid rewrite (see `U-3`).
- [ ] **`P-3` · Generic furnace / smelter** — P1, M.
  A timed, fuel-consuming station modeled on the campfire's `cookSeconds` and the quern's
  block-entity pattern. First jobs: `sand -> glass`, `mud -> brick`, and a plain
  `crushed_copper -> copper_ingot` route that does not need the full kiln. Feeds `P-6` and is
  step zero of the automation chain.
- [ ] **`P-5` · Light progression: torch -> lamp -> fire as fuel** — P1, S–M.
  A cheap **torch** block (emission ~14, `needsSupport`) before the existing `lamp` (emission
  15). Wire placed lights into the shader's 8 point-light budget so a torch casts dynamic light,
  not just voxel light.
- [ ] **`P-6` · Fuel & fire economy** — P2, M.
  Fire as station fuel; per-wood burn times; campfire already burns ~30 s. Add `P-41` charcoal
  as the renewable, portable fuel. Feeds `P-3` and the kiln.
- [ ] **`P-7` · Metal tiers beyond copper** — P1, L. **Milestone 1.**
  Tier ladder: iron, tin, bronze (`P-44`), steel; anvil/forge (`P-43`), tool tiers 2–4, richer ore
  gating (`P-25`). Progression, processing requirements and recipes are TBD, defined later.
  Includes armor: flat capped damage reduction, equip from the inventory panel, HUD/tooltip, and
  worn models on `PlayerModel`/remote players (`V-14`).
- [ ] **`P-8` · Advancements / "beats" list** — P1, S–M.
  In-world goals (first flint, first fire, first copper, first spell, first fusion) give a
  session direction without requiring combat, and double as onboarding. Needs the `M-19` backend.
- [ ] **`P-9` · Character progression (shared RPG spine)** — P2, L.
  XP from gathering/crafting/killing; perks that feed both Arc A and Arc B (`P-15`).
- [ ] **`P-30` · Universal mining tool (power glove / drill)** — P3, S–M.
  A tool that passes every block's `tools` gate no matter its kind (a powered glove or drill),
  built on the tool-tag gating. Also an Arc B -> Arc A crossover (`P-53`).

#### Survival basics

- [ ] **`P-12` · Farming** — P2, M.
  Seeds, tilled soil, growth stages, harvest -> food and crafting ingredients. Cross-links
  `P-38`, `P-42`, `P-45`.
- [ ] **`P-29` · Death-mitigation gear** — P2, M.
  Counter a death-drop rule (a death currently drops nothing): a **backpack** that expands inventory and drops as one
  item on death; **binding** enchantments that keep an enchanted item through death. Ties into
  `P-18`.
- [ ] **`P-38` · Animal husbandry & butchering** — P2, L.
  Tame/breed farm animals (`F-2`), produce **meat** (food), **leather** (`P-39`) and **wool**
  (clothing/bedding). The missing Act I food source.
- [ ] **`P-39` · Leatherworking & clothing** — P2, M.
  Leather/cloth clothing that gives warmth and the first *defense* layer before metal armor.
  Vintage Story flavor; feeds `P-7` armor and `F-5` cold.
- [ ] **`P-40` · Clay, pottery & bricks** — P2, M.
  Clay deposits + a pottery wheel/crucible craft, so crucibles and molds have a real supply
  chain instead of one recipe each. Feeds `P-3`, `P-44`.
- [ ] **`P-41` · Charcoal & fuel economy** — P2, S–M.
  A charcoal kiln (log pile -> charcoal) as the renewable fuel, plus burn-time data per wood.
- [ ] **`P-42` · Branch / handle / tool-economy** — P2, S.
  Branches as the tool handle and a general cheap component: makes the flint and copper tool
  recipes need a handle, and gives the copper tool assembly its handle.
- [ ] **`P-45` · Food spoilage & preservation** — P3, M. *(taste call; can be dropped)*
  Aging food, salt/smoking/drying, an ice box. Vintage Story depth; only worth it if survival
  is meant to be a long act.
- [ ] **`P-46` · Research / tech-tree unlock** — P2, L.
  Factorio-style unlock progression: some recipes are discovered rather than free. Shared spine —
  an RPG can find schematics in dungeons (`P-55`) where an automation player researches them.

#### Arc A — magicraft adventure RPG

- [ ] **`P-14` · Magic system core** — P2, L. **Tracked in `MAGIC.md` (`MG-*`).**
  Mana, staffs with bound slots, a station-built spell builder (element + shape + modifiers +
  trigger), scrolls and research that unlock parts. The first slice is the builder with a few
  parts (fire, frost, nature) plus a data-driven status-effect system; the fireball is one of its
  signature example spells, shared with `F-4`.
- [ ] **`P-15` · Skill tree / character build** — P2, L.
  Spend XP (`P-9`) across branches; informs both combat and non-combat perks. Needs `U-12`.
- [ ] **`P-16` · Dungeons & towers (structured POIs)** — P2, L.
  Deterministic multi-room structures (vaults, towers, crypts) with loot and spawners; overlaps
  `P-26` and needs `M-15`. **Milestone 2.**
- [ ] **`P-17` · Boss encounters** — P2, XL.
  Scripted multi-phase fights whose loot gates progression (`P-52`). Needs `V-15`.
- [ ] **`P-18` · Enchanting / item magic** — P3, L.
  Spell effects bound to gear; a crafting-station cousin of the magic system. Cross-links `P-29`,
  `P-48`.
- [ ] **`P-43` · Forge & anvil (metal shaping)** — P1, M. **Milestone 1.**
  After casting: hammer ingots into shaped parts, the gate to iron/steel tiers (`P-7`).
- [ ] **`P-44` · Alloys** — P2, M.
  Bronze from copper + tin (tin ore, the 3x3x3 alloy furnace, bronze tools) shipped (see `AGENTS.md`
  "Recipes, cast molds and metalworks"); steel remains. Combines with `P-25`.

#### Arc B — expert automation

- [ ] **`P-19` · Power & rotation** — P2, L.
  Hand crank -> water wheel -> windmill -> steam; drives machines. Generalizes the quern and
  worm patterns into a reusable **machine tick** concept. **Milestone 3.**
- [ ] **`P-20` · Logistics: belts / pipes / carts** — P2, L.
  Move items and fluids between machines. Belts and pipes are new systems; carts touch moving
  collision (`F-10`).
- [ ] **`P-21` · Machine chains & processing** — P2, L.
  Crusher, sawmill, smeltery, assembler; multi-step recipes with byproducts. The quern is step
  zero; `P-3` is step one.
- [ ] **`P-22` · Electronics / logic (redstone-ish)** — P2, L.
  Scheduled-tick logic on top of the `FluidManager` pattern: sensors, gates, moving collision
  (pistons).
- [ ] **`P-23` · Late game: chemistry -> reactors -> fusion** — P3, XL.
  Tiered energy and processing; fusion as the automation endgame. Depends on `P-31`–`P-35`.
- [ ] **`P-31` · Storage & logistics network** — P2, L.
  Chests/barrels/crates, mass storage and a routed inventory network; the substrate that makes
  automation legible.
- [ ] **`P-32` · Chemistry & materials** — P3, L.
  Acids, plastics, composites; byproduct chains (`P-21`) that feed tools, armor and machines.
- [ ] **`P-33` · Oil & refining** — P3, L.
  Pumped/refined fuel as the bridge from steam to electric; lights, plastics, lubricant.
- [ ] **`P-34` · Electronics & circuits** — P3, L.
  Circuit-board tier sitting above `P-22`; gates electric machines and control systems.
- [ ] **`P-35` · Robotics & drones** — P3, XL.
  Mobile automation: drones for mining/farming/defense, and golem-style workers (`P-54`).
- [ ] **`P-36` · Space, stations & other worlds** — P3, XL.
  On top of `P-27`: launch vehicles, orbital stations, planetfall, per-planet resource sets.
  The automation endgame's destination.
- [ ] **`P-37` · Terraforming & off-world bases** — P3, XL.
  Shape a hostile world into a habitable one; combines `P-36` and worldgen control.

#### Cross-pollination — the hybrid spine

The section that makes the game *this* game rather than "Minecraft plus mods." Every entry here
must respect the dual-identity contract: mandatory for the hybrid player, optional for a
single-arc player.

- [ ] **`P-47` · Shared exotic resource** — P2, M.
  One rare crystal/reagent that is both a spell reagent and a reactor/machine component (and a
  potion base). The single most important cross-link; gate it behind `P-52` with a manual
  slow-farm fallback.
- [ ] **`P-48` · Enchanting via automation** — P3, M.
  A machine-run imbuing/enchanting station that mass-produces the blanks a mage hand-makes
  slowly (`P-18`), so a factory player can gear an RPG character faster.
- [ ] **`P-49` · Mana as power** — P3, L.
  Arcane generators convert mana/reagents into rotation or electricity, and machines back-feed
  spell charging. The deepest magic <-> tech fusion.
- [ ] **`P-50` · Magical logistics** — P3, L.
  Portals, teleport belts, rune-routed item flow as a late alternative to `P-20` belts; also a
  fast-travel network for Arc A (`P-27`).
- [ ] **`P-51` · Automation of RPG consumables** — P3, M.
  Production lines for potions (`P-42`), ammo (`F-4`), food and gear, so a factory
  trivializes the RPG's supply grind — the intended "dabbling helps" payoff.
- [ ] **`P-52` · Dungeons as gated resource nodes** — P2, M.
  A reactor catalyst / blueprint drops **only** in POIs, so an automation bottleneck routes
  through exploration. Pair with a slow manual fallback (a rare overworld node or a costly
  synthesis) so pure automateers still advance.
- [ ] **`P-53` · Tech-enhanced adventuring** — P3, M.
  Alloy weapons (`P-44`), mining drill (`P-30`), jetpack/flight assist, powered armor. The
  automation player's answer to a dungeon.
- [ ] **`P-54` · RPG-enhanced tech** — P3, L.
  Enchanted machines, summoned helpers, golem/drone labor (`P-35`), wards that protect a base.
  The magic player's answer to a factory.
- [ ] **`P-55` · Blueprint / schematic economy** — P3, M.
  Tech trees learned by research (`P-46`) or found as dungeon schematics (`P-16`); the portable
  knowledge that ties exploration to construction.

#### Exploration & world content

- [ ] **`P-24` · Biomes** — P1, M.
  Grass, leaves and spruce leaves are already climate-tinted through the `biomeTint` block field.
  Open: falling-leaf particles should sample the foliage field; flowers and water can opt into
  `biomeTint`'s `tile` form; and the biome roster itself can grow (swamp, jungle, volcanic,
  underground biomes).
  Terrain relief (highlands, ranges, massifs, lone peaks) is in `BIOMEGEN.md` §6.
- [ ] **`P-25` · More ores + deeper strata** — P1, M.
  Tin/iron/gold/… veins placed deterministically like trees/pebbles/malachite; tiers gate tools
  (`P-7`, `P-44`).
- [ ] **`P-26` · Structures & ruins** — P2, L.
  Huts, mineshaft scraps, camps, towers; routes through `DropManager` and needs `M-15`.
- [ ] **`P-27` · Second dimension + portals** — P3, XL.
  Separate seed/noise sets on the same chunk pipeline; new LOD palette. Base for `P-36`.
- [ ] **`P-28` · Caves as destinations** — P2, M.
  Larger caverns, underground biomes, rare crystals and hazard zones; builds on the existing
  cave/worm/lava generation. A natural home for `P-47`'s crystal.
- [ ] **`P-56` · Ocean & island content** — P3, M.
  Islands, shipwrecks, reefs, deep-sea POIs; makes the ocean a destination rather than a border.
  Ties `F-19`.
  Island, archipelago and great lake *shape* is in `BIOMEGEN.md` §6; the remaining ideas are its §12.
- [ ] **`P-57` · World events** — P2, M.
  Blood moons, raids, meteors, wandering bosses, seasonal changes; drives `F-5` and gives the
  base a reason to be defended (`P-54`).
- [ ] **`P-58` · Landmarks & navigation** — P3, S–M.
  Rare named landmarks, obelisks, map fragments; feeds `U-17` waypoints and exploration goals.

### 2. Fun

- [ ] **`F-1` · Mobs — ambient critters** — P2, M.
  Fireflies, butterflies, birds. Could share the particle infrastructure — a cheap first step
  into entities.
- [ ] **`F-2` · Mobs — friendly / neutral (farm animals)** — P2, L.
  Cows/sheep/chickens; wandering, breeding, drops, leads. Needs `P-38`. Depends on entity AI.
- [ ] **`F-3` · Mobs — hostiles (night / caves)** — P2, XL.
  The slime (hop AI, contact damage) and goblin (throw-then-melee raider, shared `Mob` base,
  `MobSpawner`) landed. Still open: **light/time-keyed and cave spawns**, **voxel pathing**
  (both currently walk/auto-jump straight at the player), more hostile kinds, and spawners
  (`M-17`).
- [ ] **`F-4` · Combat depth** — P2, L.
  Melee landed: punch, the two-phase obsidian-blade slice (`SliceCombo`), knockback, per-item
  `entityDamage`, and mobs that hit back. Still open: a **bow + arrows** (animation `V-15`), a throwable
  **spear** (reuses the throw animations, `V-16`), **right-click
  shield/block**, the **fireball** bridging to `P-14`, attack cooldown, and armor (`P-7`).
- [ ] **`F-5` · Weather & day/night threat** — P1, M.
  Rain/snow, thunder, cold at night so shelter matters; drives `V-1` and `P-39`. Pairs `P-57`.
- [ ] **`F-6` · Fire spread & extinguishing** — P2, M.
  Leaves/logs catch, water/rain puts out; emergent danger. The campfire is the seed.
- [ ] **`F-7` · Climbing — ladders, vines, ropes** — P2, M.
- [ ] **`F-8` · Sandbox toys — bounce, launch pads, fans** — P3, M.
- [ ] **`F-9` · Fishing** — P3, M. Ties `P-56` and food.
- [ ] **`F-10` · Boats / minecarts / transport** — P3, L.
  Moving collision and passenger handling; engine-heavy. Feeds `P-20` carts.
- [ ] **`F-11` · Ambience moments** — P2, S.
  Rare animal calls, wind, cave drips; ties into `M-2`.
- [ ] **`F-12` · Mounts** — P3, L.
  Rideable horses/creatures (Skyrim-flavor); needs moving collision (`F-10`).
- [ ] **`F-13` · Taming & companions** — P3, M.
  Tame wild animals/pets that follow and fight; a non-tech combat support for Arc A.
- [ ] **`F-14` · Villages & NPC traders** — P2, L.
  Generated villages with static NPCs and barter/shops; the RPG's social layer and an item sink.
- [ ] **`F-15` · Factions & reputation** — P3, L.
  Standing with groups that gates quests, prices and safe zones; fed by `F-14`, `F-16`.
- [ ] **`F-16` · Quests / bounty board** — P2, L.
  Radiant and hand-authored quests (fetch, hunt, clear a dungeon) as session direction. Needs
  `U-15`.
- [ ] **`F-17` · Music & instruments** — P3, S–M.
  Playable instruments, biome/day-night music layers, jukebox-style discs.
- [ ] **`F-18` · Village raids & defense** — P3, L.
  Hostile waves against player bases/villages; gives `P-54` wards and `F-3` a purpose.
- [ ] **`F-19` · Ocean life & hazards** — P3, M.
  Fish schools, hostile sea mobs, currents, drowning depth (`P-13`); pairs `P-56`.

### 3. UI

- [ ] **`U-1` · Item tooltips** — P1, S.
  Name, durability, what it is for. `ItemType.Name` already carries the "future tooltips" note.
- [ ] **`U-2` · Health & hunger HUD** — P1, M.
  Health hearts landed; add clearer damage feedback (no hunger system is planned).
- [ ] **`U-3` · Crafting UI polish** — P1, M.
  Keep the shapeless list-and-hand-feed model; polish it (search, "what this makes / what uses
  this", clearer fed/needed counts). Stations reuse this UI, get their own, or craft in-world as
  the campfire/quern do (see `P-2`).
- [ ] **`U-4` · Inventory QoL** — P1, M.
  Shift-click, stack move/split, drag, and a bound drop key (`ThrowAction.ThrowSelected` exists but
  appears unbound).
- [ ] **`U-7` · Creative palette + dev commands** — P1, M.
  Searchable all-blocks inventory; `/give`, `/tp`, `/time`, `/gamemode`, `/seed` for testing and
  content authoring. Huge for iteration speed.
- [ ] **`U-8` · Key rebinding + controller** — P2, M.
- [ ] **`U-9` · F3 / debug expansion** — P2, S.
  Biome, light level, chunk coords, facing, targeted block. Coordinates already exist.
- [ ] **`U-10` · Map / minimap** — P3, L. Needs a top-down render of explored chunks.
- [ ] **`U-11` · Help screen** — P2, S.
  Key list plus a short "how to survive your first night".
- [ ] **`U-12` · Skill / perk tree UI** — P3, L. For `P-15`/`P-9`.
- [ ] **`U-13` · Spell wheel / magic hotbar** — P3, M. For `P-14`; the selection options live in `MAGIC.md` `MG-15`.
- [ ] **`U-14` · Tech / research tree UI** — P3, L. For `P-46`, Factorio-style.
- [ ] **`U-15` · Quest journal** — P3, M. For `F-16`.
- [ ] **`U-16` · Bestiary / codex** — P3, M. Mobs, blocks, recipes, lore as discoverable entries.
- [ ] **`U-17` · Waypoints & compass markers** — P3, S–M. Feeds `P-58`, `P-50`.
- [ ] **`U-18` · Storage / logistics UI** — P3, M. Chests, sorting, filters; for `P-31`.
- [ ] **`U-19` · Dedicated station UIs** — P3, M. Furnace, anvil, kiln, casting, enchanting.
- [ ] **`U-20` · Accessibility options** — P2, M.
  Color-blind palettes, subtitles, FOV/motion toggles, UI scale, difficulty assists.
- [ ] **`U-21` · Crafting grid (optional)** — P3, M.
  Decide whether a shaped grid is ever wanted; the current system is deliberately shapeless.
  Parked, not planned — revisit only if content demands it.
- [ ] **`U-22` · Chat command autocomplete** — P2, S–M. Pairs `U-7`; for the quick-win `/time`,
  `/tp`, `/give` and `/help`.
  Tab completes in the chat input. *Hook:* the command table those commands share (each entry gains
  an argument spec). Tab on `/` completes the command name (a unique prefix completes at once,
  several cycle through the matches with Tab and Shift+Tab, shown as a suggestion row above the
  input like the inventory search's dropdown); after the name, Tab completes the argument from its
  spec: `/time` offers `day` and `night`, `/give` the item names (`ContentRegistry` blocks and
  items by display name and file name, owned items first), `/tp` offers nothing (numbers) and later
  commands can offer online player names. `ChatView` owns the suggestion state (GL-free, tested: the
  completion of a prefix, the cycle order and wrap, no completion for an unknown command);
  `ChatRenderer.DrawChat` draws the row. Multiplayer: complete against the client's own content, and
  against the player list for names; the server still judges every command.

### 4. Visual & particles

- [ ] **`V-1` · Weather visuals — rain/snow particles, darkening, lightning** — P1, M.
  Pairs `F-5`; the particle ring and cloud/day-night passes are the base.
- [ ] **`V-2` · Water particles** — P1, M.
  Entry/exit splash, shore foam, walking wake, rain ripples. The water passes already expose the
  surface and thickness data this needs.
- [ ] **`V-3` · Footstep dust & landing puffs** — P1, S. Cheap, high feel; texture-tinted per
  surface.
- [ ] **`V-4` · Torch & fire particles** — P1, S. Flame and smoke; the fire/campfire systems
  already produce these.
- [ ] **`V-5` · Item pickup sparkle & interaction feedback** — P2, S.
- [ ] **`V-7` · Sunrise/sunset color grading** — P2, M.
- [ ] **`V-8` · Post-processing: bloom, underwater fog gradient, damage/heal flash** — P2, L.
  Bloom for lamps/campfire/sun.
- [ ] **`V-9` · Third-person camera** — P1, M.
  `PlayerModel` exists and is currently never seen; also helps showcase shots.
- [ ] **`V-10` · Mob / entity rendering** — P2, L.
  The goblin is the first articulated rig (nested Blockbench groups, keyframe clips, layered
  `EntityAnimator`), and the item debugger previews every clip. Still open: the slime is one
  baked mesh with a procedural squash, **no death animation**, and no hurt pose beyond the flash.
- [ ] **`V-11` · Ambient critter particles (fireflies, insects)** — P2, S.
- [ ] **`V-12` · Spell & ability VFX** — P3, L. Projectiles, cast gestures, impact, wards; for
  `P-14` (`MAGIC.md` `MG-10`), `P-18`.
- [ ] **`V-13` · Machine & station animation** — P3, M.
  Spinning wheels, belts, pistons, smoke/steam particles, running lights; for `P-19`–`P-22`.
- [ ] **`V-14` · Armor & weapon models** — P1 for armor (Milestone 1, with `P-7`), P3 for weapons, M.
  Worn armor layers, held-weapon meshes.
- [ ] **`V-15` · Boss intro & death animations** — P3, M. For `P-17`.
- [ ] **`V-16` · Weather depth** — P3, M. Lightning bolts, thunder flash, storm clouds, snow
  accumulation; builds on `V-1`.
- [ ] **`V-17` · Camera & movement feedback** — P2, S. Sprint FOV, head-bob, landing camera
  dip, hurt kick; builds on the existing trauma shake.
- [ ] **`V-18` · Block & environment detail pass** — P3, M.
  Connected textures, more material atlas entries, emissive/flicker variants.
- [ ] **`V-19` · Item drop presentation & quality tiers** — P3, M.
  Drops hover, bob and spin, with a faint glow or pulse for rarer items. The glow wants an item
  **quality / tier** concept (a tier on `ItemDefinition`, shown on the drop, the tooltip and the
  inventory row), so do the two together. Cosmetic half only is not worth a ticket on its own.

### 5. Misc / foundations

- [ ] **`M-2` · Gameplay SFX** — P1, M.
  `SfxManager`/`SfxMixer` play per-block hit groups and pickup, with an SFX volume slider. Still
  open: **place/step/splash/fire/UI** sounds. Note: audio is NAudio + NLayer, not Silk.NET.
- [ ] **`M-25` · Mining sound pitch by hardness** — P3, S. *(reconsider before doing)*
  Pitch the block-hit sound a little lower on harder blocks so stone sounds heavier than dirt;
  one multiplier in `SfxManager`. Open question: whether per-hardness pitch beats authoring
  distinct hit groups (`sfx` on the block JSON), and how it interacts with random variants.
- [ ] **`M-26` · Footstep sounds by surface** — P2, S.
  Every footfall plays a step sound from the surface under the feet, quieter or silent while
  sneaking. *Hook:* `FootstepDust.FootfallsBetween` already finds the footfalls in
  `PlayerModel.WalkPhase` (the puffs only fire while sprinting, the sounds should on every grounded
  step), and `SfxManager.Play(group)` plays a group. Add a `stepSfx` block field naming the step
  group, beside `sfx` (the hit group); a block without one is silent. Remote players can step from
  their own `RemotePlayer` model later.
  **Needed from you:** step clips in `assets/sfx/`, named `<group>_<n>.wav` (2-4 variants per
  group, one picked at random per step), in exactly the format of the existing clips: 16-bit PCM,
  stereo, 44100 Hz. `SfxManager` skips any other format. Each should be short (about 0.1-0.3 s)
  and start right on its transient so it lands on the footfall. Groups: `step_grass`,
  `step_dirt` (also mud), `step_sand`, `step_gravel`, `step_stone` (stone, cobblestone,
  sandstone, ores, bricks), `step_wood` (logs, later planks), `step_snow`, `step_leaves` and
  `step_glass` (glass, panes, ice).
- [ ] **`M-3` · Keep content JSON-only** — ongoing.
  New blocks/items/recipes/structures stay data, not code, per AGENTS.md. Add schema fields as
  the systems above need them (tool, station, harvest tier, food, spell, machine...).
- [ ] **`M-4` · Multiplayer hardening** — P3, L.
  The headless server, streaming, intents and prediction exist (`AGENTS.md` "Multiple players and
  chat"). Still open: authentication and encryption (players are keyed by name), a persisted
  operator list command, and the known gaps listed there.
- [ ] **`M-5` · World options on new game** — P2, S.
  The seed picker landed with the create-world page; still open: **world type, difficulty, and a
  survival/creative choice** (the old main-menu/seed-entry item landed).
- [ ] **`M-6` · Per-world config + game rules** — P2, S.
  World name/seed/timestamps/format already live in `world.json`, and player settings persist.
  Still open: per-world game rules and difficulty (`M-5`, `M-18`).
- [ ] **`M-7` · Localization / text pipeline** — P3, M.
- [ ] **`M-8` · Fix: falling blocks do not slide off a single block** — P2, S. Carried from the
  old TODO.
- [ ] **`M-9` · Mod / data-pack behavior hooks** — P3, XL.
  Data packs already exist; consider a scripting/behavior hook (`M-21`).
- [ ] **`M-10` · `TreeCanopy.CanopyDisturbed` grows with every felled tree** — P3, S.
  The interim fix landed: the markers are one bit per fine column in four 64-bit words per chunk
  column (`TreeCanopy.MarkDisturbed`), so nothing is cleared wholesale and the LOD never redraws a
  felled crown. Still open: the set is edit state that only grows (a fell is ~1000 columns, and
  `lod-edits.bin` still lists them column by column, 8 bytes each, so a very long session writes a
  large file); the real fix derives it from stored block data or writes the per-chunk-column words,
  and is entangled with save/load. **Must NOT** move to `GenerationalCache` (eviction = data
  loss). Sibling of `M-11`.
- [ ] **`M-11` · `LodColumnCaps.ColumnCaps` grows unbounded** — P2, L, blocked on a design.
  A process-global, never-shrinking `ConcurrentDictionary` of ~1 KB per edited chunk column.
  Edit state, not a cache (eviction pops mined terrain back into the far LOD); cannot ride chunk
  unload (LOD outlives chunks). A real fix re-derives caps from stored block data, so it is
  entangled with save/load. Caps now persist per world (`LodEditState`); the growth stands. Must
  preserve `LowerColumnBelow`'s publish ordering. **Expect `LodBuildPoolTests` /
  `LodHeightSamplingTests` intermittents; never "fix" them by touching `ColumnCaps`.**
- [ ] **`M-12` · Persist in-flight entities** — P3, M.
  The station registries now save (`stations.json`; **a new station registry must add its own
  `Capture`/`Restore` and a list in `StationSaveData`**), but primed TNT, falling blocks,
  projectiles, toppling trees and the sapling clock are still lost on a save & quit. All are
  transient and self-resolving; primed TNT mid-fuse is the only one with a real argument.
- [ ] **`M-27` · Iceberg gaps** — P3, M.
  Drifting icebergs (`AGENTS.md` "Icebergs") ignore item drops, mobs, projectiles, TNT blasts, the
  punch/slice cones and the block raycast; the server judges a rider's hover against them but not
  the endpoint collision; a body resting against a berg nudges it slightly; client mirrors
  dead-reckon without collision; an imported (supplied) sea spawns none; and a block that lands or
  hardens inside a berg's bounds is not refused.
- [ ] **`M-28` · Great-lake gaps** — P3, M.
  The great-lake tier (`BIOMEGEN.md` §6 great lakes, `WorldGen/GreatLakes.cs`) leaves three gaps: an
  elevated great lake (surface above `SeaLevel`) mirrors against the single sea-level reflection
  plane (`RenderManager.WaterPlaneY`), like an elevated pond; an elevated lake's shore (and its
  stepped ramp rivers) gets no beach sand, since `IsBeachHeight` keys on `SeaLevel`; and a hot
  spring placed on a great-lake shore or inside a river ramp overrides the stored water level there
  (`TerrainGenerator.Compute` applies hot springs after the lake carve and the ramp's bank raise),
  which the column tests skip, so a leak there would go unflagged.
- [ ] **`M-14` · Wide-trunk residue** — P3, M.
  Cutting a wide core while its shells stand strands the crown for reactive growth (undecided
  behavior: bridge the gap in `TreeGraph.FromWorld`, or fell the tree). Mirrored in `TREES.md`.
- [ ] **`M-15` · Structure templates / schematic system** — P2, L.
  A loadable, placeable blueprint format (jigsaw or simple offsets) that `P-16`, `P-26`, `F-14`
  and `P-37` all build on. Prerequisite for all generated POIs.
- [ ] **`M-16` · Loot table depth** — P2, M.
  Done: nested tables (`"table"` entries, cycles refused), a seedable `LootTable.Roll(ctx, list, Random)` and
  `RollStacks`, gear entries with a rarity range, pools limited to a world-Y band (`minY`/`maxY`), and the four
  `ruin_*` realm tables (`INVENTORY.md`). Left: biome, luck and held-tool conditions, and guaranteed pools.
- [ ] **`M-17` · Spawner block / entity** — P2, M.
  A placeable spawner the dungeon and structure generators can seed; keyed by light/time and
  mob kind. Needed by `P-16`, `F-3`.
- [ ] **`M-18` · Difficulty & game rules** — P2, M.
  Peaceful/normal/hard, mob multiplier, day-night length, keep-inventory, weather toggles;
  per-world (`M-6`).
- [ ] **`M-19` · Advancements / achievements backend** — P2, M. Data-defined goals + triggers
  for `P-8`.
- [ ] **`M-20` · Photo mode / screenshot tools** — P3, S.
  Free camera, HUD hide (the screenshot op already supports `hideHud`), filters; helps marketing.
- [ ] **`M-21` · Scripting / behavior hook API** — P3, XL. Extends `M-9`; the real mod story.
- [ ] **`M-22` · Save migration & versioning** — P2, M.
  `world.json` carries a format version; add migration paths so old saves survive schema growth
  (critical now that `stations.json` exists).
- [ ] **`M-24` · Data-driven AI behaviors** — P3, L.
  Compose mob behaviors (goals, tasks, senses) from JSON so new hostiles need no C#; backs
  `F-3`, `F-13`, `F-14`.

## Reference
- [ ] **`M-29` Far LOD for 3D terrain.** P2, XL. Rework the far LOD to batch real blocks into groups instead of a heightmap shell, so cavern pockets, hell layers and floating sky islands show at distance (`plans/LAYERS.md` ships without it). **Hook:** `Lod/LodMeshBuilder`.

### North star

One world, three acts.

**Act I — Primitive-tech survival** (Minecraft x Vintage Story): knap flint, build a fire,
grind ore, cook food, survive weather and the dark. This is the shared foundation for both
endgames, so it is built first and built to serve both.

From that foundation the game branches into two parallel, open-ended arcs. Both live in the
same world, and a player can move between them freely — neither arc gates the other:

- **Arc A — Magicraft adventure RPG** (Skyrim-flavored): dungeons, towers, spellcraft,
  skills, boss encounters.
- **Arc B — Expert automation** (modded-Minecraft "expert" pack x Factorio): power,
  logistics, machine chains, up to fusion reactors and off-world colonies.

### The dual-identity contract (design rule)

The whole pitch is that the two arcs share a world and **dabbling in one makes the other
easier** — not that they are two games glued together. That only happens if cross-links are
designed in, not retrofitted. Keep these four rules in mind whenever adding content:

1. **Shared inputs.** Some resource must matter to both arcs (a crystal that is a spell
   reagent *and* a machine/reactor component; food that is both survival and a potion base).
2. **Cross-tools.** Each arc should build something the other wants: magic that moves/mines/
   smelts mass material, and automation that mass-produces RPG consumables (potions, ammo,
   enchanted blanks, gear).
3. **Gated destinations.** Dungeons are the natural place to gate an automation input (a
   reactor catalyst, a blueprint), so an RPG run feeds an automation bottleneck, and a machine
   build opens access to deeper RPG content.
4. **No forced crossover.** Every gating above must have a slow, manual fallback, so a pure
   Arc A or pure Arc B player can always finish their arc alone.
