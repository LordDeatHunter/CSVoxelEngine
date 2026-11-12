# Hell, flying, sky and cave mobs, crawlers

The hell mobs, the flight rules, the sky mobs and the cave mobs.

### Hell mobs

Every hell mob and the magmaborn are immune to `burning`: an entity file's `immune` list (`EntityDefinition.Immune`, `EntityType.Immune`, checked against the effects by `EntityLoader.ValidateImmunities`) makes `Mob.Effects` refuse those effects.

Hell's cavern pockets hold two harder kinds (plans/LAYERS.md `L-10`), both `HellMob`s
(`src/Entities/HellMobs/`), which are `RealmMob`s (the chase frame they share with the sky mobs: facing,
`Decide`, `Locomote`, the throttled line of sight) and like the goblin `RiggedMob`s: a walking body animated from its
`.bbmodel` clips through an `EntityAnimator` (`RiggedMob` owns the walk, auto-jump and float physics, the
facing, the death, spawn and hurt clips, a mirror's animation and `BodyEffect`; `Goblin` derives from it
too). Both models, their skins and the `ember_shard` sprite come from `tools/generate-hell-mob-models.py`
(deterministic, `--check`); both are humanoid rigs (`EntityContract.HumanoidParts`, head and arms under the
torso) checked at startup (`Hellbrute.ValidateContent`, `Cinderwitch.ValidateContent` over
`EntityContract.RequireHumanoid`).
- **The hellbrute** (`hellbrute.json`, hp 60, loot ember shards and coal) is slow and tanky: it walks at
  its target, charges at `HellbruteChargeSpeedFactor` once it sees it (a clear line from its eye to the
  chest, `RealmMob.SeesTarget`, throttled) within `HellbruteChargeRangeBlocks`, and in reach plays its
  `melee` clip, whose `impact` event bills `MeleeDamage` with a heavy knockback to every non-immune target
  still in reach (`HellMobManager`). A player walking into it barely moves it (`HellbruteShoveSpeed`).
- **The cinderwitch** (`cinderwitch.json`, hp 24, loot ember shards) keeps
  `HellCasterMinRange..HellCasterMaxRange` and, grounded off its cooldown with a clear line, plays its cast
  (the `throw` clip) **even inside the band**: a target that hugs it still gets burned instead of the caster
  only backing away; at its `release` event the manager launches a **fire bolt**: a `ThrownProjectile`
  of the `ember_shard` item on `GoblinAim`'s low arc (the band stays inside `v^2/g`), merged into the
  projectile list like the goblin stones (`HellMobManager.Bolts`, flown and touch-tested by the shared
  `MobMissiles`, which the skeleton's arrows use too), shedding embers in flight. A bolt that
  touches a non-immune target deals the caster's `BoltDamage` and applies `burning` through
  `StatusEffects.Apply(IMobTarget, "burning", HellBoltBurnScale)`; any touched target or block stops it.
- **Layer difficulty** (`LayerDifficulty`, GL-free): `1 + layer * MobConfig.LayerDifficultyPerLayer`,
  capped at `LayerDifficultyMaxMultiplier`, scales a mob's pool (`HellMob.MaxHealth`), its damage and a
  caster's cooldown, from the hell layer of its spawn height (`HellMob.Layer`, fixed for life, so a save
  restore and a client mirror at the same spot agree); a ring spawn rolls a pack of up to
  `LayerDifficulty.PackSize(layer)`.
- **Spawning** (`HellMobManager`, one manager for both kinds on the shared `RealmMobManager<TMob>` frame,
  joined to the world's `MobCaps`): every
  `HellSpawnIntervalSeconds`, under the natural cap, around a target standing in hell only,
  `MobSpawner.TryFindHellSpawn` scans ring columns for an open cavern floor whose two standing cells are in
  hell and whose block light is at most `HellSpawnMaxBlockLight` (no skylight reaches hell); the overworld
  cave slimes stop above `HellTopY`. `HellCasterShare` of the spawns are casters. Despawn and the shared
  hazards work as for the goblin.
- **Plumbing**: `World.HellMobs`, `MobDirector` (update, damage billing, `RefreshMobs`, the `spawn` op names),
  `MobPasses.RenderRealmMobs` and the near shadow cascade (no mirror: hell lies far under the water plane),
  `entities.json` by kind name, the `EntityList` kinds above and `ClientMirror`'s reconcile, and the item
  debugger's ENTITIES tab. Tests: `HellMobTests`, `HellMobManagerTests`, the hell round trip in
  `WorldLifecycleTests.EntitiesRoundTripThroughSaveAndReload`, `NetworkEntitySyncTests` and the loopback
  `GameServerTests.HellMobsReachTheClientsEntityListAndItsMirrorKeepsTheirLayerAndAttacks` (a deep, scaled
  mob keeps its scaled pool on the client, since the mirror reads its layer from where it stands).
- **A mirror's attack clip** (every realm mob, hell, sky and the tower's skeleton alike): a kind starts its attack clip
  (`RealmMob.AttackClip`: the brute's melee, the witch's cast, the raptor's dive, the golem's slam) through
  `RealmMob.StartAttack`, which counts it (`AttackCount`). A kind with several attack clips declares
  `AttackVariants` and `AttackClipOf(variant)` (the skeleton: 0 its melee, 1 its shot): `StartAttack(variant)` moves
  the count to the next value whose remainder modulo the variant count is that variant (so by 1 up to n; with one
  variant it is the plain +1), and the count's remainder names the clip (`AttackClipFor(count)`); the server sends that count in the mob's
  `EntityList` `Ammo` field (no protocol bump: the layout is unchanged), and `ClientMirror` hands it to
  `RealmMob.MirrorAttackCount`, which plays `AttackClipFor(count)` whenever the count moves (the first count it sees only
  sets it, a dying mirror keeps its death), so the mirror swings, casts, dives and slams with the server and
  falls back to its locomotion when the clip ends. Its events are never consumed there: the blows, bolts and
  shockwaves stay the server's. `RealmMobMirrorTests`, `NetEntityCodecTests` (the entry codec,
  `NetEntityCodec`, the server's writer and the client's reader).
- Known gaps: the bolts, like the goblin stones, are not on the wire (a client sees only their damage and
  the burning); a goblin's mirror still shows no throw or melee clip; the casters walk.

### Flying mobs

The shared mob layer can fly (plans/LAYERS.md `L-14`): the galeraptor (see "Sky mobs") is the shipped flyer,
and the rules are also exercised by a test-only flyer (`FlightTests`). A `RiggedMob` kind
opts in by passing a `FlightTuning` to the base constructor (cruise and vertical speed, the
`MathUtils.Damp` acceleration, a hover band, a minimum clearance and a bob; `Validate` refuses a misordered
one). `Mob.Flies` follows from that, so it is a property of the kind and needs no wire bit. A flyer's update
calls `RiggedMob.Fly(goal, chasing, speedFactor, ...)` instead of `Walk`:
- **No ground rules.** No gravity (`BodyMotion.Steer` eases the 3D velocity toward the steered one), no
  ground friction, auto-jump or buoyancy, no fall damage (`Mob.NoteLanding` never bills a flyer) and no ledge
  caution (`Mob.IsLedgeAhead` is false). A dying flyer drops out of the sky under gravity.
- **Steering** (`FlightSteering`, GL-free and state-free): a chased target's goal is
  `ChaseGoal(eye, HoverHeight)` (each flyer rolls its own `HoverHeight` inside the band at spawn, so a flock
  spreads out); with no goal it hovers where it is at `HoverHeight` over the floor. `GroundBelow` probes down
  (up to `MobFlightGroundProbeBlocks`) for the first solid or fluid cell, and `GoalHeight` keeps the goal in
  the hover band over that floor while idle or wandering and above the minimum clearance while chasing (so it
  follows a target up a cliff); `BobOffset` adds the gentle bob first. `DesiredVelocity` flies at cruise
  toward the goal, easing off within `MobFlightArriveBlocks`, and climbs by `MobFlightAltitudeGain` per block
  of height error; `AvoidSolids` probes `MobFlightProbeBlocks` ahead on each axis, dropping a component into a
  solid (so it slides along a wall) and climbing over a wall it is blocked by. The body then sweeps through
  `BoxSweep` like every mob, so it never enters a solid cell. It faces its goal.
- **Wander**: `Mob.WanderGoal(count, dt, chunks)` gives a flyer a 3D goal (`MobInterest.FlyingWanderPoint`,
  up to `MobFlightWanderRiseBlocks` above or below), rerolled up to `MobFlightWanderTries` times until it is
  an air cell, reached in 3D. Interest, idle despawn and the notice rule are unchanged (horizontal).
- **Hazards and hits**: `Mob.UpdateHazards` still burns a flyer touching lava. `Mob.Shove` knocks a flyer
  along the whole direction (a ground mob keeps the horizontal part): `MobStrikes.StrikeMob` passes the 3D
  direction from the striker for a flyer, and `MobDirector.PushFromBlast` the whole blast impulse. The punch
  and slice cones, the projectile overlap and the healthbar were already 3D.
- **Animation**: `EntityAnimator`'s moving clip is chosen per mob (`moveClip`: `WalkClip`, or `FlyClip`,
  `fly`, for a flyer) and crossfaded with idle by the speed the owner hands it, horizontal for a walker and 3D
  for a flyer (`RiggedMob`, its mirror too). `BodyEffect` pitches a flyer into its speed and nose-up in a
  climb and banks it into a turn (the velocity lagging the facing), about its body center; a walker's effect
  is unchanged.
- **Mirrors**: a client mirror takes the snapshot's full `Vector3` position and velocity
  (`NetworkSetState`) and never integrates or ground-snaps it, so a flyer keeps its height between snapshots.

### Sky mobs

The sky islands hold two kinds (plans/LAYERS.md `L-15`), both `SkyMob`s (`src/Entities/SkyMobs/`): `RealmMob`s
like the hell mobs, scaled by the **tier of the island zone** they spawned in (`SkyIslands.ZoneAt`, read once
from the spawn column into `SkyMob.Tier`, so a save restore and a client mirror over the same column agree).
Both models, their skins and the `cloud_shard` sprite come from `tools/generate-sky-mob-models.py`
(deterministic, `--check`; it loads the hell script's rig, UV, PNG and clip machinery from that file, whose
`main` takes the outputs to write). Both rigs carry the humanoid groups, head and arms under the torso.
- **The galeraptor** (`galeraptor.json`, hp 24, loot a cloud shard sometimes) is a flyer
  (`Galeraptor.Tuning`, a `FlightTuning`; `EntityContract.RequireFlyer`: idle, `fly`, and `dive` with an
  `impact` event; its wings are `ArmL`/`ArmR` and its talons the legs). It hovers `HoverHeight` (inside
  `GaleraptorHoverMin..MaxBlocks`) over its target's eye (`FlightSteering.ChaseGoal`) and, off its cooldown
  with a clear line within `GaleraptorDiveRangeBlocks` horizontally, plays `dive` and stoops at the chest at
  `GaleraptorDiveSpeedFactor` of its flight (`Galeraptor.Locomote` overrides the walk with `Fly`); at the
  clip's `impact` the manager bills `DiveDamage` to every non-immune target whose chest is within
  `GaleraptorDiveReach` (`Galeraptor.InDiveReach`), then it climbs back to its hover. Idle it holds its hover
  band over the island; wandering it flies its 3D wander goal.
- **The cloud golem** (`cloudgolem.json`, hp 150, loot cloud shards and cobblestone) is a slow ground bruiser,
  tougher and harder-hitting than a first-layer hellbrute. In `CloudgolemSlamTriggerReach` and off its
  cooldown it plays `slam`; at its `impact` the **shockwave** bills `SlamDamage` with a big shove and lift to
  every non-immune target within `CloudgolemShockwaveRadius` horizontally whose feet stand within
  `CloudgolemShockwaveHeight` of its own (`Cloudgolem.InShockwave`, over the shared `MobTargets.InShockwave(feet, eye, radius, height)` the Skeleton King's
  slam uses too), none beyond. It walks with the shared
  ledge caution, so the island's edge (a drop to the cloud sea far below, or unloaded cells: bottomless)
  stops it unless its target is lower; a player barely shoves it.
- **Zone difficulty** (`SkyDifficulty`, GL-free): `1 + tier * MobConfig.SkyDifficultyPerTier` scales a sky
  mob's pool and damage (through `LayerDifficulty.ScaleBy`), a ring spawn rolls a pack of up to
  `PackSize(tier)` (`SkyPackExtraPerTier` more per tier, capped), and only a big zone (`SkyZone.BigTier`)
  takes the golem (`AllowsBruiser`, `KindFor`: `SkyBruiserShare` of its spawns); a galeraptor spawns in any
  zone.
- **Spawning** (`SkyMobManager` on the shared `RealmMobManager<TMob>` frame, joined to the world's `MobCaps`):
  every `SkySpawnIntervalSeconds`, under the natural cap, around a target whose feet stand in the sky band
  (`MobSpawner.InSkyBand`, `SkyIslandBottomY..SkyIslandTopY`) only, `MobSpawner.TryFindSkySpawn` scans ring
  columns for an island top: solid ground under two clear dry cells, all three in the band (so never the cloud
  sea, the overworld or hell), inside an island zone (islands also float in the open sky outside every zone,
  and those hold no mobs) and not torch-lit. A golem stands on it; a galeraptor
  appears in open air `SkyFlyerSpawnLiftMin..Max` blocks over it. The overworld's surface and cave searches
  never land on an island (the surface search reads the terrain heightmap, the cave search wants a floor under
  its column's surface). Despawn and the shared hazards work as for the other mobs.
- **Plumbing**: `World.SkyMobs`, `MobDirector` (update, damage billing, `RefreshMobs`, the `spawn` op names
  `galeraptor`/`cloudgolem`, exact feet with `[x, y, z]`), `MobPasses.RenderRealmMobs` and the near shadow
  cascade, `entities.json` by kind name, the `EntityList` kinds (protocol 18) and `ClientMirror`'s reconcile,
  and the item debugger's ENTITIES tab (its `fly` mode moves the mirror like `walk`). Tests: `SkyMobTests`,
  `SkyMobManagerTests`, the realm round trip in `WorldLifecycleTests.EntitiesRoundTripThroughSaveAndReload`,
  `NetworkEntitySyncTests.SkyMobsMirrorByKindAndAFlyerKeepsItsHeight` and the loopback
  `GameServerTests.SkyMobsReachTheClientsEntityListByKindAndAFlyerAtItsHeight`.
  `visual-examples/sky-mobs.json` (`VOXEL_TIME=147`) films the ring's own spawns, then both kinds on a
  frost highland island in a zone core.
- A client's mirror plays the dive and the slam with the server (see "A mirror's attack clip" under "Hell
  mobs").
- Known gaps: the slam's shockwave hits players only (no mobs, no blocks); `cloud_shard` has no use yet; the overworld ring spawners still spawn on the ground far below a
  player standing on an island.

### Cave mobs

The overworld's cave sub-biomes (see "Cave biomes") each hold one kind (plans/CAVE_BIOMES.md `CB-4`), all `CaveMob`s
(`src/Entities/CaveMobs/`): `RealmMob`s like the hell and sky mobs, scaled by a **depth tier**. Their models and palette skins
come from `tools/generate-cave-mob-models.py` (deterministic, `--check`; on the hell script's rig, UV, PNG and clip machinery,
with its own small-palette painters), placeholder art listed in `USER_TODO.md`; all three are humanoid rigs (head and arms
under the torso) checked at startup (each kind's `ValidateContent` over `EntityContract.RequireHumanoid`).
- **The shardling** (`shardling.json`, hp 24, loot amethyst ore and sometimes a crystal block; crystal caves) is a caster
  copied from the cinderwitch: it keeps `CaveShardlingMinRange..MaxRange` (inside the level range of `CaveShardSpeed`), and
  grounded, off its cooldown (a fresh one waits `CaveShardlingFirstCastDelaySeconds`) with a clear line, also inside the band,
  plays its `throw` clip; at its `release` event the manager launches a `crystal_block` **shard** on `GoblinAim`'s low arc
  through `MobMissiles` (`CaveMobManager.Shards`, merged into `ThrowAction.RefreshProjectiles`), which deals `ShardDamage` and
  applies `frostburn` (`CaveShardChillScale`) to the non-immune target it touches.
- **The sporeling** (`sporeling.json`, hp 32, loot jade ore, sometimes fungal glow and a bone; fungal caves) walks at its
  target and, within `CaveSporelingTriggerReach` and off its cooldown, plays `melee`; its `impact` is a **spore burst**: every
  non-immune target within `CaveSporeBurstRadius` horizontally and `CaveSporeBurstHeight` of its feet (`Sporeling.InBurst`,
  the shared `MobTargets.InShockwave`) takes `BurstDamage` with a shove and the `spore` status effect.
- **The magmaborn** (`magmaborn.json`, hp 70, loot cinnabar ore, ember shards and sometimes coal; the magma deep) is a slow
  brute: in `CaveMagmabornMeleeReach` and off its cooldown it plays `melee`, and at its `impact` every non-immune target still
  in reach takes `MeleeDamage`, a heavy knockback and `burning` (`CaveMagmabornBurnScale`). A player barely shoves it.
- **The depth tier** (`CaveDifficulty`, GL-free): 0 at or above `WorldGenerationConfig.CaveEchoBottomY`, one more per
  `MobConfig.CaveDifficultyBandBlocks` below it, clamped to `CaveMaxTier`; `1 + tier * CaveDifficultyPerTier`, capped at
  `CaveDifficultyMaxMultiplier`, scales the pool and damage through `LayerDifficulty.ScaleBy`. It is fixed at spawn
  (`CaveMob.Tier`): a ring spawn takes the tier of its floor, the `spawn` op the tier of its feet unless its `tier` is above 0, a
  save restore the saved `MobData.Tier` (an old file the saved position's) and a client mirror the `ItemId` byte, so a mirror
  and a restore keep the server's pool wherever they stand.
- **Spawning** (`CaveMobManager`, `src/Simulation/Caves/`, on the shared `RealmMobManager<TMob>` frame, joined to the world's
  `MobCaps`): every `CaveMobSpawnIntervalSeconds`, under the natural cap, around a target standing underground
  (`MobSpawner.TargetUnderground`) outside hell, `MobSpawner.TryFindCaveSpawn` searches a dark cave floor (a `CaveSpawnSearch`
  with the `CaveMob*` radii and light limits and `CaveSpawnRules.CaveMobMask`); the cave biome it reports picks the kind
  (`CaveSpawnRules.CaveMobKindFor`: plain and the echoes hold none) and a pack of up to `CaveMobPackMaxSize` is ringed on the
  leader's floor with the mask narrowed to its biome, at its tier.
- **The cave spawn filter** is shared: `TryFindCaveSpawn` resolves the standing cell's cave biome last, only for a floor that
  passed the floor, hell, depth and light checks (`CaveBiomes.At` over the world source's stored column, or an injected
  `CaveBiomeResolver`), and refuses it unless the search's mask allows it, so it costs one `CaveBiomes.At` per light-passing
  try and nothing per frame. Cave slimes take `CaveSpawnRules.SlimeMask` (everywhere but the magma deep); goblins now spawn in
  caves for a target underground (caves first, then the night-gated surface path), skipping the night gate and accepted against
  a roll of `CaveSpawnRules.GoblinWeight` (favored in crystal and fungal caves, `GoblinCaveOtherWeight` in plain and the
  echoes, never the magma deep), their pack mates held to the leader's biome. The fungal caves' glowing accents do not starve
  their spawns at the shared torch-safe limit (measured in plans/CAVE_BIOMES.md).
- **Plumbing**: `World.CaveMobs`, `MobDirector` (update, damage billing, status block actions, `RefreshMobs`, the `spawn` op
  names `shardling`/`sporeling`/`magmaborn` with the optional `tier`), `ThrowAction` (the shards), `MobPasses.RenderRealmMobs`
  and the near shadow cascade, `entities.json` by kind name with the tier, the `EntityList` kinds (protocol 29) and
  `ClientMirror`'s reconcile (a different tier replaces the mirror), and the item debugger's ENTITIES tab (tier 0). Tests:
  `CaveMobTests`, `CaveMobManagerTests`, `CaveSpawnFilterTests`, `CaveSpawnRulesTests`, `GoblinCaveSpawnTests`, the realm round
  trip in `WorldLifecycleTests.EntitiesRoundTripThroughSaveAndReload`, `RealmMobMirrorTests`,
  `NetworkEntitySyncTests.ACaveMobMirrorTakesItsTierFromTheSnapshotNotWhereItIsFirstSeen` and the loopback
  `GameServerTests.CaveMobsReachTheClientsEntityListByKindAndAMirrorKeepsItsTierPool`. `visual-examples/cave-mobs.json`
  (`VOXEL_TIME=147`) films each kind at its biome's spot.
- A client's mirror plays the cast and the blows with the server (see "A mirror's attack clip" under "Hell mobs").
- Known gaps: the shards, like goblin stones and hell bolts, are not on the wire (a client sees only the damage and the
  chill); cave mobs are not drawn in the water mirror; the burst and blows hit players only; the numbers are first guesses.

### Spiders

The crawlers (`src/Entities/Crawlers/`) are the mobs that walk on every surface on solved legs: the spiders here and the
scorpion (see "Scorpion" below). What they share is the abstract `CrawlerMob` (a `CaveMob`, so the depth tier scales the
pool and the blows; shared numbers in `CrawlerConfig`): the clinging, the legs, the drawn body, the knock off a wall and
the wire stance, everything in this section that is not the bite or the spawning. A kind adds its body size, speed,
attacks and death roll.
The giant spider (`spider.json`, hp 18, loot spider silk) and the broodmother (`spider_broodmother.json`, hp 150, a
miniboss) are one class, `Spider` (numbers in `SpiderConfig`). All crawler models were built in Blockbench through its
MCP plugin (no generator, the skin embedded; `USER_TODO.md`).
- **Clinging** (`SurfaceCrawler`, GL-free): the body is a cube (`SpiderBodySize` 0.64, the broodmother 1.5), so it
  collides the same on a floor, a wall and a ceiling, and every move is a `BoxSweep`. It holds one face of the grid at a
  time (`Normal`) and follows the part of its goal direction that lies in that face; a goal straight through or away
  from the face (under `MinTangentShare`) keeps the heading it has, which is what carries it up a wall toward a target
  beyond it. Walking into a wall that carries `ClimbShare` of its heading it **climbs** it (the new heading is the old
  normal); walking off an edge it **wraps** onto the face it left (`TryWrap`: the body is tried `EdgeDropBlocks` past the
  edge and the face behind it there is taken, heading on along the old down), keeps that heading for `CommitSeconds` and
  may hang unsupported for `WrapGraceSeconds` while it rounds the corner. With nothing to hold it **falls** (gravity,
  buoyancy and paddling in water, `BodyMotion`), lands on a floor or grabs the first wall it is carried into (out of
  water it climbs up). A floor under water is let go of, so it floats rather than walking a lake bed.
- **The mob**: `CrawlerMob.Locomote` replaces the realm walk: it aims the crawl at the chased target's chest (or the wander
  point), lets go of a ceiling once the target stands under it within `CeilingDropRadiusBlocks`, and lets go of a wall
  or ceiling when dying. `Mob.Facing` is the heading's angle on the held face (`CrawlFrame`: on a floor the usual yaw).
  In `BiteReach` of the chest (from any side) and off its cooldown it plays `bite`; at the clip's `impact` the manager
  bills every non-immune target still in reach `BiteDamage` (tier-scaled) with a shove and the `venom` status effect
  (`VenomScale`). Out of bite reach, holding a surface, with the chest `WebMinRange`..`WebMaxRange` from `ShotOrigin` in
  a clear line and off its own cooldown (`WebFirstDelaySeconds` after it appears, then `WebCooldownSeconds` jittered),
  it shoots a **web** (attack variant 1, replayed on the `bite` clip until it has one of its own): at the `impact` the
  manager launches a `MobMissiles` missile drawn as `spider_silk` (`CrawlerMob.ReleaseShot`, aimed through an
  `AimTracker` locked at the clip's start, `WebSpeed`), which stings for `WebMinDamage`..`WebMaxDamage` (a random roll,
  not tier-scaled) and applies the `slowness` status effect (`WebSlownessScale`). A blow knocks it off a wall or ceiling (`Shove` lets go and throws it); on a floor it slides like
  any walker, and only there does a player walking into it shove it. It never takes fall damage (`SafeFallBlocks`
  infinite).
- **Getting unstuck** (`CrawlerNavigator`, `CrawlerPathfinder`, both GL-free): the straight crawl hovers level with a
  target off the face it holds (a crawler on a wall beside a player across the room keeps turning back past the
  player's height) or presses into a dead end. The navigator watches the chase: no `StallProgressBlocks` closer to the
  chest in `StallSeconds` is a stall. A stalled crawler first **leaps** (`CrawlerMob.TryLeap`): off
  `LeapCooldownSeconds`, holding a surface, the target's feet `LeapMinBlocks`..`LeapMaxBlocks` off in a clear line, on
  the arc `SurfaceCrawler.LeapVelocity` solves (the horizontal distance at `LeapHorizontalSpeed`, in the air long
  enough to come down onto the spot, `LeapMinSeconds`..`LeapMaxSeconds`), launched away from the held face and no
  faster than `LeapMaxSpeed`; it then flies as a falling crawler, landing on a floor or grabbing the wall it hits. Else
  it **plans a path**: A* over `CrawlerFoothold`s (an open cell against one solid neighbor's face; a body wider than a
  cell also needs the cell in front) whose moves are the crawler's own (along the face, the climb onto a wall in the
  same cell, the wrap round an edge, and from a wall or a ceiling a fall of up to `PathMaxDropBlocks` onto the floor
  below), at most `PathMaxNodes` expanded and one plan every `PathRetrySeconds`, ending within the kind's melee reach
  of the chest or at the foothold nearest it (refused without a block's gain). The navigator hands out the next
  foothold as the crawl's aim (into the new face for a turn in the same cell; a fall step lets go), skipping ahead to
  wherever the crawler is found, and gives the path up when it reaches no further foothold for `PathStallSeconds` or
  the target moves `PathRetargetBlocks` from where it was planned to. Tests: `CrawlerNavigationTests`.
- **Legs** (`CrawlerRig`, `CrawlerLegs`, `LegIk`, `CrawlerPose`): the model contract is four legs a side, `LegR1..4` on
  +X and `LegL1..4` on -X, each a **top-level** group whose cubes run straight out along X from its origin (the hip),
  with a child `...Lower` whose origin (the knee) sits straight out from the hip (`Spider.ValidateContent` checks it at
  startup through `CrawlerMob.RequireCrawler`, with `Body`, `Head`, `Abdomen`, idle, walk and `bite` with `impact`). The legs rest flat because the loader
  has no rest rotations. Each foot stays planted where it took hold while the body moves over it and steps once its
  rest point (fanned by `LegAzimuthDegrees`, `LegStanceShare` of the leg out) is `LegStepShare` of the leg away, to a
  **hold probed in the world** (`FindHold`: a wall between hip and rest point, else the ground under the rest point,
  else the side of the ledge the body stands on, else it hangs), unless that hold is the one it already has; the two
  alternating groups of four take turns. `LegIk.Solve` (analytic two-bone, knee toward the model's up) turns the holds
  into bone rotations, and `CrawlerPose` hands a leg bone back to the clip by the action's weight when the running
  action keys it (the raised front legs of `bite`, every curled leg of `death`). The drawn body eases toward the held
  face leaned into the plane of the planted feet (`FeetNormalWeight`), so it tilts over steps and corners.
- **Shared hooks it added**: `RiggedMob.PartMatrix` (virtual; `RigMeshes.PartMatrix` and so the entity and shadow
  passes ask the mob), a virtual `RiggedMob.BodyEffect` (a crawler pops in and has no lean; a spider rolls onto its back to die, `DeathRollRadians`),
  `Mob.AnimateCosmetics` (called for every mob by `AmbientEffects.UpdateSplashes`, so never on a server: the spider
  turns its drawn body and walks its legs there) and `EntityAnimator.ActionKeys`.
- **Spawning** (`CaveMobManager`): with spiders registered the cave ring search accepts every cave biome and
  `CaveSpawnRules.KindFor` rolls the kind: `SpiderConfig.CaveSpawnChance` of the floors in a cave with no kind of its own
  (plain and the echoes; the rest stay empty) and `BiomeCaveSpawnChance` where a kind lives, in packs of up to
  `CavePackMaxSize`. On the surface they are rare: at night only, every `SurfaceSpawnIntervalSeconds` one try with
  `SurfaceSpawnChance` on the dry surface ring of a target that is not underground. **The broodmother never spawns
  naturally**: it waits for the spider den (plans/TODO.md `P-60`); the `spawn` op and the item debugger raise it.
- **Plumbing**: the cave mobs' own (saves by kind name with the tier, the `spawn` op names `spider` and
  `spider_broodmother`, `MobPasses`' rig list), plus `NetEntityKind.Spider` 64 and `SpiderBroodmother` 65 (protocol
  39; since protocol 48 the attack count names bite and web shot by its remainder of 2): the item field packs the
  held face above the tier byte (`CrawlerMob.PackStance`, 0 = airborne) and the yaw is
  the heading's angle on that face, which `ClientMirror` hands to `CrawlerMob.MirrorSurface`. The item debugger's ENTITIES tab
  shows both with the legs walking on the spot (no world: the ground slides back under the body). Tests: `SpiderTests`.
  `visual-examples/spiders.json` (`VOXEL_TIME=147`) films a surface chase, a climb up a pillar and both kinds in a cave.
- Known gaps: the web shot replays the `bite` clip and is drawn as a spider silk sprite (no clip or sprite of its own
  yet); webs, like every mob missile, are not sent to clients; the pathfinder's room check for the broodmother's
  1.5-block body is only the cell in front of the face, so she may plan through a gap she cannot pass; a mirror's
  falling spider has no heading on the wire beyond its yaw; the healthbar sits above the body
  in world up, so under a ceiling it is inside the rock; spiders are not drawn in the water mirror; the legs do not
  avoid each other; silk has no use yet; the numbers are first guesses.

### Scorpion

The giant scorpion (`scorpion.json`, hp 34, loot sometimes a scorpion stinger, sometimes a bone) is the desert's
crawler: `Scorpion` (`ScorpionConfig`), a `CrawlerMob` like the spiders, so it clings, climbs and steps exactly as
they do (see "Spiders"). Its model adds claws (`ArmL/R` > `ClawL/R` > `PincerL/R`) and a tail chain (`Tail1..4` >
`Stinger`) under `Body` > `Abdomen`, all clip-driven; `Scorpion.ValidateContent` requires `Body`, `ClawL`, `ClawR`,
`Tail1`, `Stinger`, the leg contract and `pinch` and `sting`, each with `impact`.
- **Three attacks** over one count (`AttackVariants` 3: pinch, sting and spit by its remainder, the spit on the
  sting's clip, so a mirror replays the right clip; protocol 48): in
  `AttackReach` of the target's chest it **stings** when the sting is off its `StingCooldownSeconds` (`StingDamage`,
  tier-scaled, and `venom` for `StingVenomScale`), else **pinches** when the pinch is off its own (`PinchDamage` and a
  hard shove, no venom). Each lands at its clip's `impact` (`TryTakeStrike` reports which) on every non-immune target
  still in reach. Out of reach, holding a surface, with the chest `SpitMinRange`..`SpitMaxRange` off in a clear line
  and off its own cooldown (`SpitFirstDelaySeconds`, then `SpitCooldownSeconds` jittered) it **spits** venom: at the
  `impact` (`TryTakeStrike` reports a `ScorpionStrike`) the manager launches a missile drawn as `scorpion_stinger`
  (`SpitSpeed`, aimed like the spider's web) carrying `SpitDamage` (tier-scaled) that applies `venom`
  (`SpitVenomScale`). It gets unstuck like the spiders (see "Spiders"). A fresh scorpion's first blow is therefore the sting. It dies upright (no roll): the body drops,
  the legs curl and the tail falls forward.
- **Spawning** (`CaveMobManager`, `CaveSpawnRules`): in the desert's echo caves the first `CaveSpawnChance` of the kind
  roll is a scorpion's (`KindFor`; the rest of the roll is judged as in any plain cave, so spiders still turn up), in
  packs of up to `CavePackMaxSize`. On the surface one try runs every `SurfaceSpawnIntervalSeconds` around a target not
  underground, always at night and with `DaySpawnChance` by day (`ScorpionSurfaces`), and is kept only on a desert
  column (`ScorpionSurfaceBiome`), at tier 0.
- **Plumbing**: as the spiders', plus `NetEntityKind.Scorpion` 66 (protocol 40) and the `spawn` op name `scorpion`.
  Tests: `ScorpionTests`. `visual-examples/scorpions.json` (`VOXEL_TIME=147`) films one on the deep desert.
- Known gaps: the spit replays the `sting` clip and is drawn as a stinger sprite (no venom-glob clip or sprite yet);
  the tail and claws do not collide (the tail passes through a low ceiling); the stinger has no use yet;
  the numbers are first guesses.
