# TODO — Gameplay Roadmap

> Performance work lives in **PERF_TODO.md**; this file is gameplay and content only.
> Architecture, content schema and threading rules live in **AGENTS.md**.
> Tree work lives in **TREES_PLAN.md**.

## North star

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

## How to read

- Priority: **P0** foundation/do first · **P1** next · **P2** later · **P3** someday/XL.
- Effort: **S** under a session · **M** a few sessions · **L** a week+ · **XL** weeks.
- IDs (`P-1`, `U-3`, ...) are stable references; keep them when reordering.
- **Current priority:** finish the **Act I survival floor** (Milestone 1 below), then ship one
  vertical slice per arc to prove the dual identity. The engine is far ahead of the content;
  the next wins are content and progression, not systems.

## Suggested milestones

### Milestone 1 — Act I survival floor (do first)

The smallest bundle that makes the world a *game*: stakes, food, and an advance. Mostly content
JSON plus small systems; high feel, low engine risk.

1. **`P-11` food + hunger + eating** — add the first edibles and repoint the campfire from
   mud bricks to food. (No edibles exist today.)
2. **`P-10` death done properly + `U-5` death screen** — decide and ship the drop rule (default
   drop-everything for the hardcore framing), a respawn point / bed, and the death UI.
3. **`P-4` complete the copper chain + `P-1` tool durability** — assembly recipes (head + handle
   -> tool) and per-tool wear; the bronze-age payoff currently dead-ends.
4. **`P-3` a real furnace / smelter** — `sand -> glass`, `mud -> brick`, generic smelting with
   fuel; generalizes the kiln pattern and becomes the automation seed.
5. **`P-7` / `P-25` tier-2 ores + armor** — iron/tin at real depth bands, and armor (part of
   `P-7`) as a defense stat. The first difficulty curve and the first reason to descend.

### Milestone 2 — Arc A vertical slice: the first dungeon

Prove the fantasy half. Needs `M-15` (structure templates), `P-16` (dungeons), `P-17` (a boss),
`P-14` (one spell — fireball), and `F-3` spawners. A single hand-authored crypt with loot, a
spawner room and a boss is enough to make health, combat and loot matter and to give survival a
destination.

### Milestone 3 — Arc B vertical slice: first power

Prove the automation half. Needs `P-19` (hand crank -> water wheel -> one powered machine),
`P-21` (one multi-step chain with a byproduct) and `P-20` (the crudest item movement). This also
validates "machine tick" as a reusable concept beyond the quern/kiln/casting one-offs.

### Milestone 4 — cross-pollination proof

Ship one link from the **Cross-pollination** section end-to-end (`P-47` shared exotic resource,
`P-52` dungeons as gated resource nodes) and confirm either arc can reach its own endgame without
the other while still being faster together.

---

## 1. Progression

### Act I — the shared primitive-tech spine

- [ ] **`P-1` · Finish real tools: durability + shovel/hoe/sword kinds** — P1, M.
  Tool tags, the `requiresTool` + per-block `tools` tier gate, `tool.power` / `hitDelayMs`
  mining pace and `blockDamage` landed (`Mining`, `MiningTests`). Still open: **per-instance
  durability** (only the firestarter spends uses today; `Inventory.DamageTool` / `MaxDurability`
  already exist) and the **shovel/hoe/sword** kinds.
- [ ] **`P-2` · Crafting stations gate recipes** — P1, M.
  The `E` panel crafts any recipe from anywhere. Add a `station` field and require the matching
  station (crafting table, campfire, quern, furnace). The shapeless list-and-feed model stays;
  this is gating, not a grid rewrite (see `U-3`).
- [ ] **`P-3` · Generic furnace / smelter** — P1, M.
  A timed, fuel-consuming station modeled on the campfire's `cookSeconds` and the quern's
  block-entity pattern. First jobs: `sand -> glass`, `mud -> brick`, and a plain
  `crushed_copper -> copper_ingot` route that does not need the full kiln. Feeds `P-6` and is
  step zero of the automation chain.
- [ ] **`P-4` · Complete the copper chain** — P1, M.
  The quern crushes `malachite -> crushed_copper`, and the metalworks (kiln crucible + fired
  molds) already **casts** the ingot and the pickaxe/axe **heads**. What is missing is the
  head -> tool **assembly** recipe, so the heads dead-end. Add `copper_pickaxe` / `copper_axe`
  from head + handle (`P-42` branch), plus the durability from `P-1`. This is the first real
  "I advanced" moment.
- [ ] **`P-5` · Light progression: torch -> lamp -> fire as fuel** — P1, S–M.
  A cheap **torch** block (emission ~14, `needsSupport`) before the existing `lamp` (emission
  15). Wire placed lights into the shader's 8 point-light budget so a torch casts dynamic light,
  not just voxel light.
- [ ] **`P-6` · Fuel & fire economy** — P2, M.
  Fire as station fuel; per-wood burn times; campfire already burns ~30 s. Add `P-41` charcoal
  as the renewable, portable fuel. Feeds `P-3` and the kiln.
- [ ] **`P-7` · Metal tiers beyond copper** — P2, L.
  Bronze (`P-44`), iron, steel; anvil/forge (`P-43`), tool tiers 2–4, richer ore gating
  (`P-25`).
- [ ] **`P-8` · Advancements / "beats" list** — P1, S–M.
  In-world goals (first flint, first fire, first copper, first spell, first fusion) give a
  session direction without requiring combat, and double as onboarding. Needs the `M-19` backend.
- [ ] **`P-9` · Character progression (shared RPG spine)** — P2, L.
  XP from gathering/crafting/killing; perks that feed both Arc A and Arc B (`P-15`).
- [ ] **`P-30` · Universal mining tool (power glove / drill)** — P3, S–M.
  A tool that passes every block's `tools` gate no matter its kind (a powered glove or drill),
  built on `P-1`'s tag gating. Also an Arc B -> Arc A crossover (`P-53`).

### Survival basics

- [ ] **`P-10` · Death + respawn, done properly** — P1, M.
  Health, fall damage, lava DoT, mob damage, the damage vignette/shake and simple respawn landed.
  Still open: a **death screen** (`U-5`), the **drop rule** (default drop-everything for the
  hardcore framing, relieved later by `P-29`), a **respawn point / bed**, and health regen.
- [ ] **`P-11` · Hunger / food / eating** — P1, M.
  Food items, eating, hunger/saturation drain. Cooking currently only makes a building material,
  so **no edibles exist yet** — add the first real foods here (`P-38`, `P-12`, `P-42`).
- [ ] **`P-12` · Farming** — P2, M.
  Seeds, tilled soil, growth stages, harvest -> food and crafting ingredients. Cross-links
  `P-38`, `P-42`, `P-45`.
- [ ] **`P-13` · Drowning** — P1, S.
  Fall damage landed with `P-10`; **drowning is still open**, so swimming is harmless. The water
  systems already expose `IsInWater` / `IsEyeInWater` / `IsSubmerged`.
- [ ] **`P-29` · Death-mitigation gear** — P2, M.
  Counter `P-10`'s drop-everything rule: a **backpack** that expands inventory and drops as one
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
  recipes need a handle, and gives `P-4` its assembly step.
- [ ] **`P-45` · Food spoilage & preservation** — P3, M. *(taste call; can be dropped)*
  Aging food, salt/smoking/drying, an ice box. Vintage Story depth; only worth it if survival
  is meant to be a long act.
- [ ] **`P-46` · Research / tech-tree unlock** — P2, L.
  Factorio-style unlock progression: some recipes are discovered rather than free. Shared spine —
  an RPG can find schematics in dungeons (`P-55`) where an automation player researches them.

### Arc A — magicraft adventure RPG

- [ ] **`P-14` · Magic system core** — P2, L.
  Mana (or equivalent), spell slots, casting from a staff/tome, schools (fire/frost/arcane),
  spell scrolls as dungeon loot. First spell to ship is a **fireball**, shared with `F-4`.
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
- [ ] **`P-43` · Forge & anvil (metal shaping)** — P2, M.
  After casting: hammer ingots into shaped parts, the gate to iron/steel tiers (`P-7`).
- [ ] **`P-44` · Alloys** — P2, M.
  Bronze from copper + tin, and later steel; the alloy step that makes mixed mining worthwhile.
  Combines with `P-25`.

### Arc B — expert automation

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

### Cross-pollination — the hybrid spine

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
  Production lines for potions (`P-42`), ammo (`F-4`), food (`P-11`) and gear, so a factory
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

### Exploration & world content

- [ ] **`P-24` · Biomes** — P1, M.
  Grass, leaves and spruce leaves are already climate-tinted through the `biomeTint` block field.
  Open: falling-leaf particles should sample the foliage field; flowers and water can opt into
  `biomeTint`'s `tile` form; and the biome roster itself can grow (swamp, jungle, savanna,
  volcanic, underground biomes).
- [ ] **`P-25` · More ores + deeper strata** — P1, M.
  Tin/iron/gold/… veins placed deterministically like trees/pebbles/malachite; tiers gate tools
  (`P-1`, `P-7`, `P-44`).
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
- [ ] **`P-57` · World events** — P2, M.
  Blood moons, raids, meteors, wandering bosses, seasonal changes; drives `F-5` and gives the
  base a reason to be defended (`P-54`).
- [ ] **`P-58` · Landmarks & navigation** — P3, S–M.
  Rare named landmarks, obelisks, map fragments; feeds `U-17` waypoints and exploration goals.

---

## 2. Fun

- [ ] **`F-1` · Mobs — ambient critters** — P2, M.
  Fireflies, butterflies, birds. Could share the particle infrastructure — a cheap first step
  into entities.
- [ ] **`F-2` · Mobs — friendly / neutral (farm animals)** — P2, L.
  Cows/sheep/chickens; wandering, breeding, drops, leads. Needs `P-38`. Depends on entity AI.
- [ ] **`F-3` · Mobs — hostiles (night / caves)** — P2, XL.
  The slime (hop AI, contact damage) and goblin (throw-then-melee raider, shared `Mob` base,
  `MobSpawner`) landed. Still open: **light/time-keyed and cave spawns**, **voxel pathing**
  (both currently walk/auto-jump straight at the player), more hostile kinds, and spawners
  (`M-17`). Depends on `P-10`.
- [ ] **`F-4` · Combat depth** — P2, L.
  Melee landed: punch, the two-phase obsidian-blade slice (`SliceCombo`), knockback, per-item
  `entityDamage`, and mobs that hit back. Still open: a **bow + arrows**, **right-click
  shield/block**, the **fireball** bridging to `P-14`, attack cooldown, and armor (`P-7`).
- [ ] **`F-5` · Weather & day/night threat** — P1, M.
  Rain/snow, thunder, cold at night so shelter matters; drives `V-1` and `P-39`. Pairs `P-57`.
- [ ] **`F-6` · Fire spread & extinguishing** — P2, M.
  Leaves/logs catch, water/rain puts out; emergent danger. The campfire is the seed.
- [ ] **`F-7` · Climbing — ladders, vines, ropes** — P2, M.
- [ ] **`F-8` · Sandbox toys — bounce, launch pads, fans** — P3, M.
- [ ] **`F-9` · Fishing** — P3, M. Ties `P-56` and food (`P-11`).
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

---

## 3. UI

- [ ] **`U-1` · Item tooltips** — P1, S.
  Name, durability, what it is for. `ItemType.Name` already carries the "future tooltips" note.
- [ ] **`U-2` · Health & hunger HUD** — P1, M.
  Health hearts landed; add the hunger bar and clearer damage feedback. Depends on `P-10`/`P-11`.
- [ ] **`U-3` · Crafting UI polish** — P1, M.
  Keep the shapeless list-and-hand-feed model; polish it (search, "what this makes / what uses
  this", clearer fed/needed counts). Stations reuse this UI, get their own, or craft in-world as
  the campfire/quern do (see `P-2`).
- [ ] **`U-4` · Inventory QoL** — P1, M.
  Shift-click, stack move/split, drag, and a bound drop key (`ThrowAction.ThrowSelected` exists but
  appears unbound).
- [ ] **`U-5` · Death / respawn screen** — P1, S. Pairs `P-10`. **Milestone 1.**
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
- [ ] **`U-13` · Spell wheel / magic hotbar** — P3, M. For `P-14`.
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
  `UIRenderer.DrawChat` draws the row. Multiplayer: complete against the client's own content, and
  against the player list for names; the server still judges every command.

---

## 4. Visual & particles

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
- [ ] **`V-6` · Sky: stars, moon phases, shooting stars, aurora** — P1, M.
  Clouds, the sun disc, the sun glare and twinkling stars exist; finish the sky.
  - [x] **Moon.** `RenderManager.RenderMoon` draws the sun's quad with `moon-fragment.glsl` and
    `moon.png` opposite the sun; `MoonPhase` steps at noon (day 0 full, an 8-day cycle), the disc
    fades like the stars, reaches the mirror and the Snell window and hides the stars behind it
    (`moon.json` at 700, 3100 and 4300; `MoonPhaseTests`).
  - [ ] **Shooting stars.** A rare streak across the night dome. *Hook:* `sky-fragment.glsl`'s
    main after the stars, on hashed time slots (a slot index from `uStarTime`, a hashed start
    direction, heading and length per slot, most slots empty), faded by `StarField.Visibility`
    and hidden by the cloud cover like the stars, with a GL-free twin of the slot hash beside
    `StarField`. No art needed.
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
  `P-14`, `P-18`.
- [ ] **`V-13` · Machine & station animation** — P3, M.
  Spinning wheels, belts, pistons, smoke/steam particles, running lights; for `P-19`–`P-22`.
- [ ] **`V-14` · Armor & weapon models** — P3, M. Worn armor layers, held-weapon meshes.
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

---

## 5. Misc / foundations

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
- [ ] **`M-4` · Multiplayer (headless server + chunk packets)** — P3, XL.
  The threading model already isolates sim from GL; still weeks (auth, prediction, edit
  conflicts).
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
- [ ] **`M-10` · Fix: `TreeGenerator.CanopyDisturbed` loses felled-canopy markers at its cap** —
  P2, S. It is edit state, not a cache: 289 entries per edited block and a wholesale `Clear()` at
  16384 entries, so after roughly 57 felled trees the LOD redraws cleared canopy, and the `Count`
  test sits inside the 289-iteration loop (a per-block lock storm). The markers now persist per
  world (`LodEditState`) but the in-session `Clear()` bug stands. **Must NOT** move to
  `GenerationalCache` (eviction = data loss). The real fix derives the set from stored block data
  and is entangled with save/load; the interim is one entry per chunk column and hoisting the
  `Count` test. Sibling of `M-11`.
- [ ] **`M-11` · `LodRegion.ColumnCaps` grows unbounded** — P2, L, blocked on a design.
  A process-global, never-shrinking `ConcurrentDictionary` of ~1 KB per edited chunk column.
  Edit state, not a cache (eviction pops mined terrain back into the far LOD); cannot ride chunk
  unload (LOD outlives chunks). A real fix re-derives caps from stored block data, so it is
  entangled with save/load. Caps now persist per world (`LodEditState`); the growth stands. Must
  preserve `LowerColumnBelow`'s publish ordering. **Expect `LodBuildPoolTests` /
  `LodHeightSamplingTests` intermittents; never "fix" them by touching `ColumnCaps`.**
- [ ] **`M-12` · Persist station block entities + in-flight entities** — P1, M.
  The save format covers chunks, LOD edit state, the player and mobs/drops, but not the in-memory
  station registries (campfire, quern, kiln, casting), primed TNT, falling blocks, projectiles or
  toppling trees. Add a side-car per registry or one `block-entities.json`, restored like mobs
  once the cell's chunk loads.
- [ ] **`M-13` · Far-LOD canopy for grown trees** — P3, M.
  `PoisonCanopy` only removes canopy, so a grown tree shows no far canopy until reload; needs an
  add-canopy override keyed by column. Also spends `CanopyDisturbedLimit` budget per grow.
- [ ] **`M-14` · Wide-trunk residue (TREES_PLAN phase 7)** — P3, M.
  (1) a felled tree's mesh is truncated to one 16³ scratch chunk (`CellBatchMesher.Build` is a
  ready multi-chunk replacement); (2) cutting a wide core while its shells stand strands the
  crown for reactive growth; (3) a beveled shell's cap can go stale when an edit lands in an
  edge-diagonal chunk that face-adjacent dirtying does not remesh.
- [ ] **`M-15` · Structure templates / schematic system** — P2, L.
  A loadable, placeable blueprint format (jigsaw or simple offsets) that `P-16`, `P-26`, `F-14`
  and `P-37` all build on. Prerequisite for all generated POIs.
- [ ] **`M-16` · Loot table depth** — P2, M.
  Conditions (biome, luck, tool), nested tables, guaranteed/weighted pools, and dungeon-tier
  loot; generalizes the current pools.
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
  (critical once `M-12` lands).
- [ ] **`M-23` · Dedicated-server hosting** — P3, XL. Depends on `M-4`.
- [ ] **`M-24` · Data-driven AI behaviors** — P3, L.
  Compose mob behaviors (goals, tasks, senses) from JSON so new hostiles need no C#; backs
  `F-3`, `F-13`, `F-14`.
