# TODO — Gameplay Roadmap

> Performance work lives in **PERF_TODO.md**; this file is gameplay and content only.
> Architecture, content schema and threading rules live in **AGENTS.md**.

## North star

One world, three acts.

**Act I — Primitive-tech survival** (Minecraft x Vintage Story): knap flint, build a fire,
grind ore, cook food, survive weather and the dark. This is the shared foundation for both
endgames, so it is built first and built to serve both.

From that foundation the game branches into two parallel, open-ended arcs. Both live in the
same world, and a player can move between them freely — neither arc gates the other:

- **Arc A — Magicraft adventure RPG** (Skyrim-flavoured): dungeons, towers, spellcraft,
  skills, boss encounters.
- **Arc B — Expert automation** (modded-Minecraft "expert" pack x Factorio): power,
  logistics, machine chains, up to fusion reactors.

## How to read

- Priority: **P0** foundation/do first · **P1** next · **P2** later · **P3** someday/XL.
- Effort: **S** under a session · **M** a few sessions · **L** a week+ · **XL** weeks.
- IDs (`P-1`, `U-3`, ...) are stable references; keep them when reordering.
- **Current priority: `M-1` (save/load)** — nothing built above it persists until it lands.

### Suggested first wave

1. `M-1` save/load — the foundation everything else hangs off.
2. `P-1` real tools — the mechanic landed (tool tags, `requiresTool` gate, pace/damage);
   what is left is tool durability and the shovel/hoe/sword kinds.
3. `M-2` SFX + `U-1` tooltips + `V-3` footstep dust — cheap, high-feel wins.
4. `P-10` health + `P-11` hunger + `P-13` fall damage/drowning — the survival skeleton.
5. `P-24` biomes + `P-25` ores + `V-1` weather — make the world worth exploring.
6. `U-6` main menu/world select + `U-7` dev commands — needed to test everything after.

---

## 1. Progression

### Act I — the shared primitive-tech spine

- [ ] **`P-1` · Real tools: tool tags, required-tool gating, mining speed, durability** — P1, M.
  `primitive_pickaxe` currently has no effect; mining is pure block hardness. Add a tool kind
  (pickaxe/axe/shovel/hoe/sword) and tier to items, `harvestTool`/`harvestTier` to blocks, a
  speed multiplier while mining, and per-instance durability (the `Inventory.DamageTool` /
  `MaxDurability` machinery already exists for the firestarter). Unlocks axe/shovel/hoe and the
  whole metal tier ladder.
  - Progress: tool tags (`assets/data/tags/pickaxe.json`, `axe.json`), the `requiresTool` +
    per-block `tools` tier gate, per-item `tool.power` / `hitDelayMs` mining pace and
    `blockDamage` all landed (`Mining`, `MiningTests`); `entityDamage` and `attack` came with
    them (see `F-4`). Flint and copper axe/pickaxe exist and log hardness was raised so the axe
    matters. Still open: per-tool durability (only the firestarter spends uses today) and the
    shovel/hoe/sword kinds.
- [ ] **`P-2` · Crafting stations gate recipes** — P1, M.
  The `E` panel today crafts any recipe in the list from anywhere. Add a `station` field to
  recipes and require the player to be at the matching station (crafting table, campfire, quern,
  furnace). A station may reuse the same list-and-feed UI, get a unique UI later, or — like the
  campfire and quern today — craft in the world itself. The shapeless feeding model stays; this is
  gating, not a grid rewrite (see `U-3`).
- [ ] **`P-3` · Furnace / kiln + smelting** — P1, M.
  A timed, fuel-consuming station modelled on the campfire's `cookSeconds` and the quern's
  block-entity pattern. First jobs: `malachite → copper`, `sand → glass`, `mud → brick`.
- [ ] **`P-4` · Finish the copper chain** — P1, M.
  `crushed_copper` currently dead-ends. Wire it to a copper ingot and copper tools/blocks, a tier
  above flint. This is the first real "I advanced" moment.
  - Progress: the quern's `malachite → crushed_copper` crush is in, and the Copper Ingot plus
    copper pickaxe/axe items exist with correct tiers and art. Nothing crafts the ingot from
    crushed copper or the tools from the ingot yet, so `crushed_copper` still dead-ends.
- [ ] **`P-5` · Light progression: torch → lamp → fire as fuel** — P1, S–M.
  Torch block (cheap, emission ~14, `needsSupport` on walls/floor) before the existing
  `lamp` (emission 15). Wire placed lights into the shader's 8 point-light budget so a torch
  actually casts dynamic light, not just voxel light.
- [ ] **`P-6` · Fuel & fire economy** — P2, M.
  Fire as station fuel; per-wood burn times; campfire already burns ~30 s. Feeds furnaces and
  `P-3`.
- [ ] **`P-7` · Metal tiers beyond copper** — P2, L.
  Bronze/iron/steel, anvil/forge station, tool tiers 2–4, richer ore gating.
- [ ] **`P-8` · Advancements / "beats" list** — P1, S–M.
  In-world goals (first flint, first fire, first copper, first spell, first fusion) give a
  session direction without requiring combat. Also good onboarding.
- [ ] **`P-9` · Character progression (shared RPG spine)** — P2, L.
  XP from gathering/crafting/killing; perks that feed both Arc A and Arc B. See `P-15`.
- [ ] **`P-30` · Universal mining tool (power glove / drill)** — P3, S–M.
  A tool that passes every block's `tools` gate no matter its kind, so nothing is ever refused
  (a powered mining glove or drill). Built on `P-1`'s tool-tag + `requiresTool` gating: either a
  flag that bypasses the gate or one item that belongs to every tool tag.

### Survival basics

- [ ] **`P-10` · Player health + damage + death/respawn** — P1, M–L.
  Health pool, damage sources (fall, drowning, lava, mobs, starvation), death screen, respawn at
  spawn/bed. Default death drops **everything**; the items that soften that come later (`P-29`).
  Prerequisite for hostiles and for Arc A.
  - Progress (first cut): 100 HP pool (`PlayerHealth`), 10-heart HUD above the hotbar
    (`heart` over `heart_empty`, 1px side padding cropped, half-heart steps), damage
    vignette + trauma-based screen shake, fall damage ((blocks-4)*10 HP from 5+ blocks,
    water landings cushion, creative/flight immune), lava DoT (10 HP/s in half-second
    ticks, body contact via emissive-fluid check, no burning yet), simple respawn at spawn
    keeping inventory. Slimes now deal contact damage (12 HP) and knockback, so mob damage is
    live too. Still open: death screen (`U-5`), drop-everything on death,
    drowning/hunger/starvation, regen.
- [ ] **`P-11` · Hunger / food** — P1, M.
  Food items, eating, hunger/saturation drain. Note cooking currently produces a building
  material (`mudball → mud_brick`), so no edibles exist yet; add the first real foods here.
- [ ] **`P-12` · Farming** — P2, M.
  Seeds, tilled soil, growth stages, harvest → food and crafting ingredients.
- [ ] **`P-13` · Fall damage + drowning** — P1, S.
  Turn swimming into a real hazard. Depends on `P-10`; the water systems already expose
  `IsInWater` / `IsEyeInWater` / `IsSubmerged`.
  - Progress: fall damage landed with `P-10` (5+ blocks, water cushions, creative/flight
    immune). Drowning is still open, so swimming is currently harmless.
- [ ] **`P-29` · Death-mitigation gear** — P2, M.
  Counter `P-10`'s drop-everything rule once it starts to hurt: a **backpack** that expands
  inventory and, on death, drops as a single item holding its whole contents; **binding**
  enchantments that keep the enchanted item through death. Ties into `P-18`.

### Arc A — magicraft adventure RPG

- [ ] **`P-14` · Magic system core** — P2, L.
  Mana (or an equivalent resource), spell slots, casting from a staff/tome, schools
  (fire/frost/arcane), spell scrolls as dungeon loot. First spell to ship is a **fireball**,
  shared with the combat first cut (`F-4`).
- [ ] **`P-15` · Skill tree / character build** — P2, L.
  Spend XP across branches; informs both combat and non-combat perks (`P-9`).
- [ ] **`P-16` · Dungeons & towers (structured POIs)** — P2, L.
  Deterministic multi-room structures (vaults, towers, crypts) with loot and spawners; overlaps
  `P-26`.
- [ ] **`P-17` · Boss encounters** — P2, XL.
  Scripted multi-phase fights whose loot gates progression.
- [ ] **`P-18` · Enchanting / item magic** — P3, L.
  Spell effects bound to gear; a crafting-station cousin of the magic system.

### Arc B — expert automation

- [ ] **`P-19` · Power & rotation** — P2, L.
  Hand crank → water wheel → windmill → steam; drives machines. Generalizes the quern and worm
  patterns into a reusable "machine tick" concept.
- [ ] **`P-20` · Logistics: belts / pipes / carts** — P2, L.
  Move items and fluids between machines. Belts and pipes are new systems; carts touch moving
  collision (`F-10`).
- [ ] **`P-21` · Machine chains & processing** — P2, L.
  Crusher, sawmill, smeltery, assembler; multi-step recipes with byproducts. The quern is step
  zero of this chain.
- [ ] **`P-22` · Electronics / logic (redstone-ish)** — P2, L.
  Scheduled-tick logic on top of the `FluidManager` pattern: sensors, gates, and moving collision
  (pistons). Carries the old "redstone/pistons" item.
- [ ] **`P-23` · Late game: chemistry → reactors → fusion** — P3, XL.
  Tiered energy and processing; fusion as the automation endgame.

### Exploration & world content

- [ ] **`P-24` · Biomes** — P1, M.
  2D noise masks in `TerrainGenerator` (desert/snow/swamp/…). Palette and LOD are already
  data-driven, so per-biome surfaces, wood, plants and water colour are mostly content.
- [ ] **`P-25` · More ores + deeper strata** — P1, M.
  Coal/iron/gold/… veins placed deterministically like trees/pebbles/Malachite; tiers gate tools
  (`P-1`, `P-7`).
- [ ] **`P-26` · Structures & ruins** — P2, L.
  Huts, mineshaft scraps, camps, towers; routes through `DropManager` like everything else.
- [ ] **`P-27` · Second dimension + portals** — P3, XL.
  Separate seed/noise sets on the same chunk pipeline; new LOD palette.
- [ ] **`P-28` · Caves as destinations** — P2, M.
  Larger caverns, underground biomes, rare crystals and hazard zones; builds on the existing
  cave/worm/lava generation.

---

## 2. Fun

- [ ] **`F-1` · Mobs — ambient critters** — P2, M.
  Fireflies, butterflies, birds. Could share the particle infrastructure, so it is a cheap first
  step into entities.
- [ ] **`F-2` · Mobs — friendly / neutral (farm animals)** — P2, L.
  Cows/sheep/chickens; wandering, breeding, drops, leads. Needs entity AI + spawning.
- [ ] **`F-3` · Mobs — hostiles (night / caves)** — P2, XL.
  Spawn rules keyed to light and time, simple voxel pathing, attacks, loot. Depends on `P-10`.
  - Progress (first cut): the slime landed — data-driven entity with a Blockbench model, hop AI
    that homes on the player, airborne contact damage and knockback, a shove when grounded,
    floats on water, death loot (`slimeball`), despawn and a spawn cap. It spawns on dry forest
    surface in a 20-40 block ring only: not yet light/time-keyed, no cave spawns, hop-chase
    rather than voxel pathing, and it is the only hostile.
- [ ] **`F-4` · Combat** — P2, L.
  First cut: a **sword** — left-click swings, and left-clicking mid-swing chains a swing-back
  left-to-right combo — plus **right-click to shield/block**. A **bow** with arrows covers ranged,
  and **one magic spell (fireball)** bridges into Arc A (`P-14`). Later passes add attack
  cooldown, armor, knockback and mob drops. Watch the right-click conflict with place/use and
  `World.ThrowSelected`.
  - Progress (first cut): melee landed. Left-click punches the nearest slime in the forward cone
    for the held item's `entityDamage` (bare hand 1, flint tools 3, copper 5) with knockback; the
    obsidian blade (`attack: "slice"`) swings the two-phase, two-target sweep (`SliceCombo`) and
    the arm carries it across the front. Still open: right-click shield/block, bow + arrows, and
    the fireball bridging to `P-14`; attack cooldown and armor also remain.
- [ ] **`F-5` · Weather & day/night threat** — P1, M.
  Rain/snow, thunder, cold at night so shelter matters; drives the visual pass (`V-1`).
- [ ] **`F-6` · Fire spread & extinguishing** — P2, M.
  Leaves/logs catch, water/rain puts out. Emergent danger; the campfire is the seed.
- [ ] **`F-7` · Climbing — ladders, vines, ropes** — P2, M.
- [ ] **`F-8` · Sandbox toys — bounce, launch pads, fans** — P3, M.
- [ ] **`F-9` · Fishing** — P3, M.
- [ ] **`F-10` · Boats / minecarts / transport** — P3, L.
  Moving collision and passenger handling; engine-heavy.
- [ ] **`F-11` · Ambience moments** — P2, S.
  Rare animal calls, wind, cave drips; ties into `M-2`.

---

## 3. UI

- [ ] **`U-1` · Item tooltips** — P1, S.
  Name, durability, what it is for. `ItemType.Name` already carries the "future tooltips" note.
- [ ] **`U-2` · Health & hunger HUD** — P1, M.
  Bars plus damage feedback; depends on `P-10` / `P-11`.
- [ ] **`U-3` · Crafting UI polish** — P1, M.
  Keep the shapeless list-and-hand-feed model; polish it (recipe search, "what this makes / what
  uses this", clearer fed/needed counts) and add QoL. Stations reuse this UI, get a unique one
  later, or craft in-world as the campfire and quern already do (see `P-2`).
- [ ] **`U-4` · Inventory QoL** — P1, M.
  Shift-click, stack move/split, drag, and a bound drop key (`World.ThrowSelected` already
  exists but appears unbound).
- [ ] **`U-5` · Death / respawn screen** — P1, S. Pairs with `P-10`.
- [ ] **`U-6` · Main menu / world select / seed entry** — P1, M.
  Currently launches straight into a world; required once `M-1` lands.
- [ ] **`U-7` · Creative palette + dev commands** — P1, M.
  Searchable all-blocks inventory; `/give`, `/tp`, `/time`, `/gamemode`, `/seed` for testing and
  content authoring. Huge for iteration speed.
- [ ] **`U-8` · Key rebinding + controller** — P2, M.
- [ ] **`U-9` · F3 / debug expansion** — P2, S.
  Biome, light level, chunk coords, facing, targeted block. Coordinates already exist.
- [ ] **`U-10` · Map / minimap** — P3, L. Needs a top-down render of explored chunks.
- [ ] **`U-11` · Help screen** — P2, S.
  Key list plus a short "how to survive your first night". Carries the old help-screen item.

---

## 4. Visual & particles

- [ ] **`V-1` · Weather visuals — rain/snow particles, darkening, lightning** — P1, M.
  Pairs with `F-5`; the particle ring and cloud/day-night passes are the base.
- [ ] **`V-2` · Water particles** — P1, M.
  Entry/exit splash, shore foam, walking wake, rain ripples. The water passes already expose the
  surface and thickness data this needs.
- [ ] **`V-3` · Footstep dust & landing puffs** — P1, S.
  Cheap, high feel; texture-tinted per surface.
- [ ] **`V-4` · Torch & fire particles** — P1, S.
  Flame and smoke on torches and campfires; the fire/campfire systems already produce these.
- [ ] **`V-5` · Item pickup sparkle & interaction feedback** — P2, S.
- [ ] **`V-6` · Sky: stars, moon phases, shooting stars, aurora** — P1, M.
  Clouds and the sun disc exist; finish the sky.
- [ ] **`V-7` · Sunrise/sunset colour grading** — P2, M.
- [ ] **`V-8` · Post-processing: bloom (lamps/campfire/sun), underwater fog gradient, damage/heal flash** — P2, L.
- [ ] **`V-9` · Third-person camera** — P1, M.
  `PlayerModel` exists and is currently never seen; also helps showcase shots.
- [ ] **`V-10` · Mob / entity rendering** — P2, L.
  Models and animation for `F-1`–`F-3`.
- [ ] **`V-11` · Ambient critter particles (fireflies, insects)** — P2, S.

---

## 5. Misc / foundations

- [ ] **`M-1` · Save/load — region files, seed + block deltas** — **P0, L. Current top priority.**
  Persist block edits, inventory, position, time of day and block-entity state (campfires,
  querns, TNT). Worldgen stays a pure function; only deltas + the seed are written. Nothing above
  this line has lasting meaning until it ships.
- [ ] **`M-2` · Gameplay SFX** — P1, M.
  Break/place/step/splash/fire/UI sounds on the existing NAudio stack (day/night ambience already
  crossfades there). Note: the old plan said "Silk.NET audio" — the code actually uses
  NAudio + NLayer, so build on that.
  - Progress (first cut): `SfxManager` + `SfxMixer` play a random clip of a group on block hits
    (per-block `sfx` groups `hit_rock` / `hit_wood` / `hit_default`) and item pickup, with an SFX
    volume slider beside the ambience one. Still open: place/step/splash/fire/UI.
- [ ] **`M-3` · Keep content JSON-only** — ongoing.
  New blocks/items/recipes/structures stay data, not code, per AGENTS.md. Add schema fields as
  the systems above need them (tool, station, harvest tier, food, biome...).
- [ ] **`M-4` · Multiplayer (headless server + chunk packets)** — P3, XL.
  The threading model already isolates sim from GL; still weeks (auth, prediction, edit
  conflicts).
- [ ] **`M-5` · World options on new game** — P2, S.
  Seed picker, world type, difficulty, survival/creative.
- [ ] **`M-6` · Per-world config + settings persistence** — P2, S.
- [ ] **`M-7` · Localisation / text pipeline** — P3, M.
- [ ] **`M-8` · Fix: falling blocks do not slide off a single block** — P2, S. Carried from the
  old TODO.
- [ ] **`M-9` · Mod / data-pack behavior hooks** — P3, XL.
  Data packs already exist; consider a scripting/behavior hook.
- [ ] **`M-10` · Fix: `TreeGenerator.CanopyDisturbed` loses felled-canopy markers at its cap** —
  P2, S. **It is edit state, not a cache.** `TreeGenerator.PoisonCanopy` (`TreeGenerator.cs:477-488`)
  records that a player cleared canopy so the far LOD stops drawing a bump there, writing
  `(2 * TreeCanopyPoisonRadius + 1)^2` = **289 entries per edited wood/leaf block**
  (`TreeCanopyPoisonRadius = 8`) and wholesale-`Clear()`ing the whole dictionary at 16384 entries.
  So after roughly **57 felled trees** every marker is thrown away at once and the LOD starts
  drawing canopy bumps back over ground the player has already cleared — a visible correctness bug,
  not a cost. There is a second, smaller defect in the same method: the `Count >= limit` test sits
  **inside** the 289-iteration loop, so every mined leaf block evaluates
  `ConcurrentDictionary.Count` — which acquires *every lock in the table* — 289 times, on the main
  thread, during mining. **This must NOT be "fixed" by moving it onto `GenerationalCache`** (the
  type introduced by PERF_TODO item D2): that type is for values which are a pure function of their
  key, where an eviction costs only a recompute. Here nothing can regenerate a marker from the key,
  so any eviction is data loss. The real fix derives the disturbed set from stored block data, and
  is therefore entangled with `M-1` (save/load); the cheap interim is to stop the unbounded growth
  at the source (store one entry per edited chunk column rather than 289 per block) and to hoist
  the `Count` test out of the loop. The same argument applies to `LodRegion.ColumnCaps`, tracked
  below as `M-11`.
- [ ] **`M-11` · `LodRegion.ColumnCaps` grows unbounded, and it must NOT take D2's scheme** —
  P2, L, blocked on a design. **It is edit state, not a cache**, and it is the sibling of `M-10`
  above. `LodRegion.ColumnCaps` (`LodRegion.cs:130`) is a **process-global** `ConcurrentDictionary`
  holding one dense `Chunk.Size * Chunk.Size` `int[]` — 256 ints, **~1 KB, per *edited chunk
  column*** with `Chunk.Size = 16` — written by `LowerColumnBelow` (`:147-172`) whenever a removal
  would expose buried LOD, and **never removed by anything**. Over a long editing session it only
  grows. The fix PERF_TODO's `D2` shipped for the worldgen caches (`GenerationalCache`) is actively
  wrong here, because this is a **different problem: growth, not eviction**.
  - **It cannot go on `GenerationalCache`.** A cap entry encodes an *irreproducible player edit*; no
    generator can rebuild one from its key. Evicting a cap while its column is still LOD-relevant
    makes mined terrain **pop back** in the far rings — a visible correctness bug, not a recompute.
    `GenerationalCache`'s invariant 10 names this site explicitly as one that must never be stored
    there, and AGENTS.md carries the same standing rule.
  - **Nor can eviction ride chunk unload.** LOD regions cover terrain far outside the loaded chunk
    sphere (`ChunkRadius` 16 chunks vs rings out to 2048 blocks), so a cap must **outlive** its
    chunk. "Drop the cap when the chunk unloads" is the obvious fix and it is wrong.
  - **Whatever is done must preserve `LowerColumnBelow`'s publish ordering** (`LodRegion.cs:165-172`):
    the bucket is published into the `ConcurrentDictionary` first and the value written with
    `Volatile.Write` after, which the code's own comment records as load-bearing for a future
    multi-threaded LodGen. A fix that replaces the container must carry that ordering across.
  - **A real fix needs caps re-derivable from stored block data** — i.e. it is entangled with
    save/load (`M-1` above), because only persisted edits let a cap be dropped and recomputed on
    demand. That makes this a **correctness item waiting on a design**, not a perf item, and larger
    than it looks.
  **Observed cost today, not hypothetical:** because `ColumnCaps` is global with no reset hook and
  `BuildMeshData` gates on `hasCaps = !ColumnCaps.IsEmpty` (`:302`, read at `:312`), any test class
  that mines or drops a block near the origin flips a *later* build of the same region, which is what
  makes `LodBuildPoolTests.RegionBuildsAreIdenticalWhicheverThreadRunsThem` and
  `LodHeightSamplingTests.SeparableMinMatchesTheNestedWindowAtEveryLevel` **intermittent in
  full-suite runs** (reproduced on pristine HEAD, so not a regression from any perf round — see
  PERF_TODO's "Measurement: D2"). `LodColumnCapTests.cs` and `LodHeightSamplingTests.cs` already dodge
  it with far region coordinates ((777, 777) and (555, -555)) and say so in comments; those two
  classes do not. **Expect the two intermittents; never "fix" them by touching `ColumnCaps`.**
  Arrived here from PERF_TODO's `D2b` when that file was closed out: its entries are irreproducible
  player edits, which makes it a **correctness** item entangled with save/load, not a perf item.

---

## Done (kept for context)

- [x] Pause menu, settings and F3-style coordinates HUD.
- [x] Clouds (translucent layer with reflection reuse).
- [x] Leaf particles.
- [x] JSON-only content drop: malachite ore, flowers, pebbles, branch piles.
- [x] Crafting system, campfire cooking and the quern (old "crafting + furnace" item, partly).
- [x] Day/night ambience audio (looping, crossfaded).

> Old TODO item 1 (perf: PERF_TODO 8/10, `SustainedLowLatency`, PGO, delta-time) now lives in
> **PERF_TODO.md** only.
