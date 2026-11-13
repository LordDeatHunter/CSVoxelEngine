# Entities

The mob base, shared mob rules, the slime and the goblin.

## Entities

Entities (mobs) are data-driven like blocks: `assets/data/entities/<name>.json` gives a display
`name`, an `hp` pool (optional: an entity with none has no health pool, `EntityType.MaxHealth` 0,
and the `Mob` constructor refuses it), an optional `loot` table rolled on death, and an optional
`model` Blockbench project (default `assets/models/entity/<name>.bbmodel`), or instead of `model`
a non-empty `variants` list of projects, one per size class or look (`EntityType.Variants` /
`VariantPaths`, whose first entry is always `Model`; the icebergs use it), and an optional `held` item every spawn
holds: `{ "item", "part", "display" }`, a pure item drawn from its own extruded sprite at the hand of that group
(`EntityAnimator.HandAnchor`, the bottom of its lowest cube, so it swings with the limb), posed by the item file's
`display` shape and parser (`ItemLoader.ParseDisplay`, `Item.ComposeDisplay`). The loader checks the item and that every
variant has the group; `EntityType.HeldItemId`/`HeldPart`/`HeldTransform` carry it and `EntityType.HeldMatrix` is the one
world matrix the entity pass (`MobPasses.DrawRigParts`), the shadow casters (`ShadowPass.DrawRigCaster`) and the item
debugger's entity preview draw it with (the skeleton warrior's copper dagger). `ContentRegistry.Load` parses them into
`EntityType`s published on `EntityRegistry` (`RequireEntity`/`TryGet`), and parses each entity's
`.bbmodel` once, GL-free, into `EntityType.Model` (a `BbmodelData`: parts, skin pixels and animation
clips). The entity, the renderer and the item debugger all share that one parse. A broken entity model
therefore fails content load, and with it every test through `TestContent`. Entity models reuse
`BbmodelLoader`, which takes an optional required-group list: the player rig passes `RequiredParts`,
entities pass none. The slime draws through `EntityModel`, which bakes the whole project into one mesh
plus its skin. The goblin draws per part: `PlayerRig.FromBbmodel(type.Model)` +
`PlayerRig.BuildMeshes`, one mesh per group, each placed by its `EntityAnimator`.

Every hostile derives from the abstract **`Mob`** (`Mob.cs`). It exposes `Type`, `Position` (the body
center), `Health`/`IsDead`, `Facing` (yaw, the model faces -Z at 0), `HitFlash`, and the body box as
`BodyHalfExtent`/`BodyHalfHeight`. `HeadAboveCenter` is where the healthbar sits. It also has
`TakeDamage` and `Shove`. A boss (`IsBoss`: the sandworm, the Skeleton King) is shown by the HUD's boss bar instead
(`BossSelection.Nearest`: the nearest living boss within its `Mob.BossBarRange`, unlimited by default). `World`'s `MobDirector` rebuilds one `_mobs` list each frame (`MobDirector.RefreshMobs`), after every mob
manager has run and before `ThrowAction.UpdateThrown`, so a mob killed or despawned this frame is never hit or given
a healthbar. The punch, the slice, the player's thrown items (`ThrownProjectile.Update`, an AABB overlap
against each mob's body box, so a tall goblin is hit through its head and shins) and the healthbars
(`EntityHealthbarRenderer`, buffers sized by `MobConfig.MobsMaxAlive`, the hard cap
below) all read that one list. The render passes keep one list per kind, because the meshes differ; `ReflectionEntities`
carries both. Ring spawning on dry, loaded forest surface goes through `Simulation/MobSpawner.cs`
(`TryFindSurfaceSpawn`), and each manager wraps it with its own radii and body-center offset.
**Two global caps** bound every kind together (`MobCaps`, owned by `World` and handed to each manager,
which registers itself as an `IMobPopulation`; a manager built alone caps itself): the ring spawners
stop at the **natural cap** (`MobCaps.NaturalCap`: `MobNaturalCap` for each player in play, up to
`MobNaturalCapMaxPlayers` of them; `MobDirector.Update` sets `MobCaps.Players` each frame), and a **forced** spawn — a manager's public `Spawn`
(a slime splitting, a scenario, a save restore, a client mirroring its server) — may pass it but
returns null at the **hard cap** (`MobHardCap`, `MobHardCapFactor` times the natural one, which is
`MobsMaxAlive`). Both count living mobs only (`LivingCount`, dying excluded), so a new kind needs no
cap of its own: it registers with the world's `MobCaps`.
**Spawn pacing** keeps a moving player in company (`SpawnPacingTests`). Every ring spawner's timer runs
faster the emptier the world is (`MobCaps.SpawnHaste`: 1 at the natural cap, up to 1 + `MobSpawnRefillHaste`
with nothing alive). A ring search starts from a `SpawnOrigin` (a target's eye and velocity,
`SpawnOrigin.Of`; a plain position converts to one at rest), and around a target moving at
`MobSpawnAheadMinSpeed` or faster `MobSpawner.RingColumn` puts `MobSpawnAheadShare` of its columns inside
`MobSpawnAheadHalfAngleDegrees` of the heading, so mobs appear where the player is going. A mob stops
counting as in play (`MobTargets.OutOfPlay`, the one despawn test of the slime, goblin and realm managers)
once every target is past its despawn radius or has left it behind: the target runs, and the mob is more
than `MobLeftBehindBlocks` away in the rear third of the circle around its heading. `IMobTarget.Velocity`
feeds both: the camera reports its physics velocity, a `ServerPlayer` an estimate from its accepted
positions (`MotionEstimate`, fed by `MobDirector.Update`).
The managers never read the camera: they take a list of `IMobTarget`s (the camera in
singleplayer, every connected player on a server), chase the nearest non-immune one, spawn rings
round-robin over them under the global caps and despawn by the distance to the nearest (see "Mobs
follow real players" under "Multiple players and chat"); the `Update(..., Camera)` overloads wrap
the camera as a one-target list.

**Shared mob rules** live once in `Mob` (or a GL-free helper beside it), so each manager applies them
with one call, and they run only in the authoritative simulation (singleplayer and the server; a
client's mirrors are advanced by `Mob.AnimateMirror` alone). **Fall damage**: every landing a mob's
physics reports (`Mob.NoteLanding`, which also feeds the landing dust) bills `MobFallDamage.Damage`
over the drop its impact speed implies: free up to `Mob.SafeFallBlocks` (`MobSafeFallBlocks`; a slime
adds its own hop's apex, so its hops never hurt it), then `MobFallDamagePerBlockShare` of the mob's
full health (at least 1) per whole block past it, and nothing when the feet were in water
(`MobFallDamageTests`). **Lava**: each manager calls `Mob.UpdateHazards` right after the mob's own
update; a body box touching a burning fluid (`MobLava.BodyInLava`, sharing the cell test
`BurningFluid.AnyInBox` with `PlayerBodyProbe.InLava`) takes `MobLava.DamagePerTick` (`MobLavaDamageShare` of its full health, at
least 1) every `MobLavaTickSeconds` with the hit flash, leaving it clears the partial tick, and a
death it causes rolls the loot as usual (`MobLavaTests`). `visual-examples/mob-hazards.json`
(`VOXEL_TIME=147`) films the falls and the lava bath. **Body collision**: after the managers run,
`MobSeparation.Separate` (called by `World.Update` over the rebuilt `World.Mobs`) pushes every two
living mobs whose body boxes overlap apart along the shallower horizontal axis, each taking half unless
a wall blocks it; a pushed body slides through `BoxSweep` (`Mob.Nudge`), so no pack can stack in one
silhouette and no push enters a solid cell. A pair is left with only a hair of overlap
(`MobSeparation.TouchOverlap`), so `SlimeMerge` still sees two slimes as touching. A dying mob and a
boss are left alone, and a pair that only meets at different heights is not pushed
(`MobSeparationTests`). **Despawn fade**: a mob past every target's
despawn radius is not removed outright; the manager calls `Mob.BeginDying` without rolling any loot,
so it plays its death pose and is dropped once that has run, and like any dying mob it leaves
`LivingCount`, the saves and the server's broadcast at once (`SlimeTests`, `GoblinManagerTests`,
`MobTargetTests`). **Loot toss**: every item a killed mob drops (its table's rolls, a goblin's carried
flint) is spawned with `DropManager.SpawnAt`'s toss set to `MobLoot.RandomToss()`, a random outward
heading at `MobLootTossMinSpeed..MaxSpeed` plus `MobLootTossLift` on top of the drop's own pop, so
the loot arcs out of the body (`MobLootTests`). **Notice range**: `MobTargets.Nearest` only picks a
target within its `MobTargets.NoticeRadius`, `MobNoticeRadiusBlocks` horizontally, times
`MobSneakNoticeFactor` while the target sneaks (`IMobTarget.IsSneaking`: the camera's own crouch, a
`ServerPlayer`'s cosmetic `NetPlayerFlags.Sneaking`, untrusted but only ever helping the one who
claims it); a target past it is neither chased nor faced, though despawning still measures every
target (`MobNoticeTests`). **Lose interest**: each manager picks the chased target through
`Mob.PickTarget`, the notice rule plus the mob's `MobInterest`, which checks the line of sight from
`Mob.SightOrigin` to the target's eye every `MobSightCheckSeconds` (`BlockRaycaster.SegmentHitsBlock`)
and gives the target up once the line has stayed blocked for `MobLoseInterestSeconds`; a clear check
wins it back and a different target starts the count over. A mob chasing nobody (none noticed, or
interest lost) **wanders**: `Mob.WanderGoal` picks a point `MobWanderMin..MaxRadiusBlocks` away, anew
every `MobWanderRepickSeconds` or on arrival, which a slime hops toward at its usual cadence and a
goblin strolls to at `MobWanderSpeedFactor` of its walk (`Goblin.Update`'s `wander`); with nobody in
play at all it stands still as before. Idle for `MobIdleDespawnSeconds` and farther than
`MobIdleDespawnMinDistanceBlocks` from every target, it despawns through the same fade
(`Mob.ShouldIdleDespawn`; `MobInterestTests`). **Torch-safe ground**: the ring spawn search refuses a
column whose standing cell holds `MobSpawnBlockLightLimit` or more block light
(`MobSpawner.IsLitByBlocks`, read through `EntityLight.Sample` from the light each chunk publishes
with its mesh; a headless server publishes it too, and a cell without a snapshot reads as unlit), so
torches and lamps keep a base clear of new mobs (`TorchSafeSpawnTests`). **Ledge caution**: a mob
will not step (a goblin's next step, `MobLedgeProbeBlocks` past its body, zeroing its move in
`Goblin.Update`) or hop (where a slime's flat-ground hop would land, sitting that hop out) onto ground
more than `MobLedgeMaxDropBlocks` under its feet unless its target stands lower than that
(`Mob.IsLedgeAhead` over `LedgeProbe`, which reads the live cells rather than the generated heightmap
`World.TryGroundHeight` returns, so caves and dug pits count; an unloaded cell or lava below is a
bottomless drop and water a soft landing), so a cautious mob never takes the fall damage above
(`LedgeCautionTests`).

The first entity is the **slime** (`Slime` + `Simulation/SlimeManager.cs`), owned by `World`. It hops
toward the player, damages on contact only while airborne (a slime merely bobbing in water is
harmless), is shoved (harmlessly) by the player when grounded, floats to the surface of water like an
item drop (its water hops keep a land hop's total speed but halve the vertical and move it into the
horizontal, so it skims forward as a low jumping slide), and rolls its `loot` table on death. Natural
spawns appear on dry forest surface in a 20-40 block ring around the player; `SlimeManager` holds the
cadence, damage and shove numbers via `MobConfig.Slime*`. Slimes come in **sizes**
(`Slime.Size`, `SlimeSize.Min`..`Max`, fixed for life): every scaled stat is the size-1 value times
`1 + (size - 1) * step`, one `SlimeSize*Step` per stat — the body scale (`SizeScale`: the drawn model
in `MobPasses.SlimeTransform`, multiplied over the squash spring, the collision box
`BodyHalfExtent`/`BodyHalfHeight`, the healthbar height), the health pool (`Mob.MaxHealth`, virtual,
over the JSON `hp`; the save and net clamps read it), the contact damage and the loot rolls (the table
rolled `LootRolls` times). The size is saved (`EntitySaveData.MobData.Size`, absent = 1) and sent (a
slime entry's `NetEntity.Ammo`). A ring spawn rolls its size (`SlimeSize.Roll`, a pure function of the
night, an underground flag and one uniform roll): size 2 is reached with the large-size
chance, which eases from `SlimeLargeChanceDay` to `SlimeLargeChanceNight` with
`DayCycle.NightFactor(World.LightingTime)` — the same sun `FrameLighting.SetLighting` lights the world
by — and is the night value for ground at least `SlimeUndergroundDepthBlocks` under its column's
surface (`SlimeSize.IsUnderground`; the surface-only ring never finds such a spot today). Sizes past 2 are reached only in the dark, so by day a ring spawn is size 1 or rarely size 2, and the ring's timer waits `SlimeSpawnIntervalDaySeconds` in full day instead of `SlimeSpawnIntervalSeconds` (eased by the same night factor). A rolled body
that would overlap a block beside the cleared column steps down a size until it fits. A slime larger
than the smallest **splits** on the transition, never on being low (`SlimeSize.SplitsOnHit`): a hit that
takes it from above half its health to at or below half while it survives marks it in
`Slime.TakeDamage`, and `SlimeManager.Update` then spawns `SlimeSplitChildren` of the next smaller size
inside its body, shoved outward with a small hop, as forced spawns (only as many as the hard cap has
room for). The parent lives on, so a killing blow, a second hit below half and the smallest slime never
split. Only the authoritative simulation deals damage, so a client's mirror never splits.
`visual-examples/slime-split.json` (`VOXEL_TIME=147`) hits a large slime across its half line and films
the children and their own split. The player fights back with
the left-click punch (`LeftClickAction.UpdatePunch` + `Combat`): a mob inside the forward 3-block, 30°-half-angle
cone with line of sight takes the held item's `entityDamage` and a knockback shove, and while one is in
the cone mining stands down (attack-first). `Mining.EntityDamage` is the bare-hand baseline (1) an item
overrides with its `entityDamage` (primitive tools 2, copper tools 5, obsidian blade 6). The obsidian
blade (`attack: "slice"`) instead swings a two-phase sweep (`LeftClickAction.UpdateSlice` + `SliceCombo`): each
swing hits the nearest two mobs in a forward hemisphere of the same reach with line of sight, the
buffered return swing is at half power, and the damage lands at the swing's impact frame rather than on
the click, with the arm and blade carried across the front by `PlayerModel`. The iron sword
(`attack: "sword"`) is the long blade: `LeftClickAction.UpdateSword` runs `SwordCombo`, a chain of
three swings (`SwordSwing`: the cut, the back cut, the chop) that a click buffers while one plays and
a press within `SwordCombo.ChainWindowSeconds` of the last swing's end continues, a later press
starting over at the cut. Each swing has its own rules in `SwordSwings`: the cut and the back cut
sweep the forward hemisphere for the nearest `SweepTargets` mobs (the back cut at reduced power), the
chop takes only the one mob in the punch cone at the highest power (`Combat.SwordDamage`) with a
double shove and the heaviest hit-stop, and each has its own length and impact frame
(`PlayerModel.SwordDuration`/`SwordImpactProgress`), so the chain paces itself. All three blade
paths land through one `LeftClickAction.StrikeNearest` (nearest first, line of sight required) and
`MobStrikes.StrikeMob`, whose shove speed a chop doubles. A mob mid-attack (`Mob.IsMidAttack`: a rigged mob's attack or throw clip is playing) takes the damage but no shove, so a hit never knocks it out of reach of its own swing.

A larger slime is pushed less: every shove (a punch, a slice, a grounded player walking into it) is
divided by its body scale (`SlimeSize.ShoveSpeed`, applied in `Slime.Shove`), while a split child's
launch (`Slime.Pop`) is not (`SlimeKnockbackTests`). A larger slime also hops higher and farther after
a longer crouch: the hop's vertical and horizontal launch speeds and the crouch time are the size-1
ones times the body scale to `SlimeHopLiftExponent`, `SlimeHopReachExponent` and
`SlimeHopWindupExponent` (`SlimeSize.HopLift`/`HopReach`/`WindupScale`, read by `Slime.HopVelocity`
and the squash spring's windup; `SlimeHopTests`). The largest size (`SlimeMaxSize`, 4) is the rare
**giant**: `SlimeSize.Roll` takes the last step into it with only `SlimeMaxSizeChanceFactor` of the
usual threshold, so it is rarer than the size below it in every light (`GiantSlimeTests`), and it
splits into that size like any other; `visual-examples/slime-sizes.json` stands it beside the other
three. **Slime sounds**: a hop plays `slime_jump` (a random clip of `slime_jump_1`/`_2`) through `SlimeManager.PlaySound` at the slime (set where the slimes are simulated: singleplayer plays it, a server sends it to the players near it, and a client's mirrors never sound, see "World sounds"); a landing plays `slime_land`; a landed hit plays `slime_hit` and a killing one `slime_death`, through the slime's `HitSoundGroup`/`DeathSoundGroup`. **Cave slimes**: slimes also spawn on dark cave floors in the same ring
(`MobSpawner.TryFindCaveSpawn`, any biome): each try scans a column down from a random height within
`SlimeCaveSearchHalfHeight` of the player's feet for at most `SlimeCaveScanDepth` cells and takes the
first solid floor under two clear, dry cells, if it lies at least `SlimeUndergroundDepthBlocks` under
its column's surface, its standing cell has at most `SlimeCaveMaxSkyLight` sky light (a cell without a
published light snapshot reads as full sun) and it is not torch-lit. `SlimeManager.TryFindSpawn`
searches caves first for a player standing underground and the surface first otherwise, each falling
back to the other, and the size roll treats a cave spawn as underground (night chance); this makes the
"surface-only ring never finds such a spot" note above out of date (`CaveSlimeTests`). **Slime
merge**, the split's inverse: after its main loop `SlimeManager.Update` merges every pair of living,
same-sized slimes below the largest whose bodies touch and which have both lived
`SlimeMergeCooldownSeconds` (`SlimeMerge.CanMerge` over `Mob.AgeSeconds`, so a split's children never
merge straight back): both vanish without loot and one slime of the next size pops in midway on the
lower feet (only where its body fits), a forced spawn the hard cap always has room for since the pair
leaves first, carrying the pair's share of their pools (`SlimeMerge.MergedHealth`). Each slime merges
at most once an update; only the authoritative simulation merges, and a client sees the pair leave
its snapshot (playing their death pop) and the merged slime arrive (`SlimeMergeTests`).

The second entity is the **goblin** (`Goblin` + `Simulation/GoblinManager.cs`, owned by `World`), a short
forest raider that throws the flint it carries and then closes for a club swing. It spawns like the
slime: dry forest surface, a 20-40 block ring, under the shared natural mob cap and despawn beyond
`GoblinDespawnRadiusBlocks`. It has no light or time keying. Its **ammo** is a count of the ordinary
`flint` item, rolled at spawn (`GoblinStoneMin..Max`). The stones still carried drop as flint on death,
on top of the goblin's own loot table. A stone that misses **shatters**: it leaves no drop, so thrown
stones never pile up as pickups. The rules:
- **Stance.** With ammo, the goblin walks in beyond `GoblinThrowMaxRange`, backpedals inside
  `GoblinThrowMinRange` and holds position inside the band.
- **When it throws.** Only while grounded, so the auto-jump can never lift the throw origin over a
  wall, and only when the throw cooldown has run out. A fresh goblin waits an unjittered
  `GoblinFirstThrowDelaySeconds`, so it visibly notices the player first; later throws use the
  jittered cooldown.
- **Line of sight.** A throw needs a clear segment from `ThrowOrigin` to the player's chest
  (`BlockRaycaster.SegmentHitsBlock`). A blocked check is retried only every `GoblinLosRetrySeconds`, and
  while the line stays blocked the goblin keeps walking at the player.
- **The throw.** It aims at the chest seen when the throw starts, plays the `throw` clip, and spawns
  the stone only at that clip's `release` event (`Goblin.TryTakeThrowRelease`, one stone per release).
- **Stones.** A stone is a plain `ThrownProjectile` owned by the manager (`GoblinManager.Stones`).
  `ThrowAction.RefreshProjectiles` merges the stones with the player's throws into one projectile list,
  so they render and mirror with no
  new plumbing. A stone deals `GoblinStoneDamage` when it touches the player's body.
- **Melee.** Out of ammo, the goblin closes to `GoblinStandoffBlocks` and plays the `melee` clip when
  in reach and off `GoblinMeleeCooldownSeconds`. The damage and knockback land at that clip's `impact`
  event, and only if the player is still in reach (`TryTakeMeleeImpact` + `Goblin.InMeleeReach`).
- **Immunity.** A creative or flying player takes no damage from stones or swings, which is what lets
  `visual-examples/goblins.json` film them from a hovering camera.
- **Movement.** Goblins auto-jump a one-block step they walked into, float and paddle in water like
  the slime, and are shoved away when they overlap the player on the ground.
The launch velocity comes from **`GoblinAim.TryLaunchVelocity`**, the low arc of the exact ballistic
solve under `ThrownProjectile.Gravity`. Always pass that constant, never a second gravity. It is an alias of `BodyMotion.Gravity`, the one gravity every loose body (drops, primed TNT, falling blocks, debris, goblins, slimes) falls under. The level
range of a stone is `v²/g` (`GoblinStoneSpeed` 20 against gravity 28 gives about 14.3 blocks). The
throw band must stay inside it, with margin for a chest-height target and the semi-implicit Euler drop.
`GoblinAimTests` locks that relation down, so retuning the speed or the band cannot silently make edge
throws fall short. An out-of-range target still gets a 45-degree maximum-range toss toward it. Every
goblin number lives under `MobConfig.Goblin*`.

**Leading a moving target.** Every ranged mob (goblin, skeleton archer, cinderwitch, shardling) owns an
**`AimTracker`**: `Lock(target, chunks)` when the attack clip starts, `Track` every frame of the wind-up
(from `TickCooldowns`, or the goblin's `Update`), `Solve` at the release event. The shot is therefore aimed
from where the target is and how it moves *at the release*, never from the wind-up's first frame. The
horizontal velocity is averaged over `RangedAimSmoothingSeconds`, so a steady runner is led in full and a
player jiggling left and right is shot where they stand. `GoblinAim.TryLeadVelocity` iterates flight time
against aim point (the flight is measured to the led point), and an airborne target (a jump, a fall) follows
the player's parabola down to the ground the tracker probed under it (`LedgeProbe.GroundBelow`, at most
`RangedAimFloorScanBlocks` down); a flyer's vertical speed is not led. The Skeleton King's volley is not led.
`AimLeadTests` holds the rules.

A goblin's life has more rules than its AI. **Flee**: below `GoblinFleeHealthShare` (a quarter) of
its pool it turns and runs from the player at `GoblinFleeSpeedFactor` of its walk, never throwing or
swinging (`Goblin.IsFleeing`, `GoblinFleeTests`). **Scavenge**: a live goblin with ammo room joins
the world's drop pass as a `DropCollector` that takes only flint (`GoblinManager.AppendCollectors`,
`AwardScavenged`, capped at `GoblinStoneMax`; a client's mirrors never scavenge)
(`GoblinScavengeTests`). **Dodge**: a charged throw (at least `GoblinDodgeChargeThreshold`) aimed at
a goblin inside `GoblinDodgeConeDegrees` and within `GoblinDodgeRangeBlocks` makes it sidestep out of
the line while the aim holds, its side fixed at spawn (`GoblinManager.IsAimedAt`, `Goblin.Decide`;
`GoblinDodgeTests`). **Pack alert**: a goblin that notices a player alerts every other goblin within
`GoblinPackAlertRadiusBlocks`, which skip their first-throw delay so the pack throws together
(`Goblin.TakeJustNoticed`/`AlertPack`, `GoblinPackAlertAround`; `GoblinPackAlertTests`). **Pack
spawning**: a ring spawn rolls one to `GoblinPackMaxSize` goblins, the rest landing within
`GoblinPackSpawnRadiusBlocks` of the found cell, each re-checked against the shared natural cap
(`GoblinManager.TryFindPackMate`; `GoblinPackSpawnTests`). **Torches**: a spawn may carry up to
`GoblinTorchMaxCount` torches (a roll whose chance is small by day, larger at night and highest
underground), equips one while it is dark (drawn at the off hand's `ArmL` anchor through
`BodyEffect`, its flame point joining `World.FireCells` for light and embers), and drops the carried
torches on death with the flint; the count is saved (`MobData.Torches`) and sent (the mob
`NetEntity.ItemId` field) (`GoblinTorches`, `GoblinTorchTests`). **A notice pip**: a "!" billboard
pops over a goblin's head the moment it first spots a player (`GoblinNoticePipTests`).

**Goblin camps** (`Simulation/Camps/GoblinCamps.cs`, owned by `MobDirector`, ticked after the goblins on authoritative
worlds only; tuning `MobConfig.GoblinCamp*`): once the night is dark enough (`GoblinCampNightFactor` of
`DayCycle.NightFactor`), every `GoblinCampCheckSeconds` a roll of `GoblinCampChancePerCheck` may pitch the night's one camp
(the day clears it), far from a random player (`GoblinCampMinDistanceBlocks..MaxDistanceBlocks`, inside the despawn
radius) on the dry surface the ring spawns take and with bare air over its ground, so a player exploring at night
stumbles on it. A camp is its fire first: a full `branch_pile` placed through `BlockPlacer.PlaceBlockAtFace` (every
placement hook runs, the cell journals to clients) and lit with a first branch that burns `GoblinCampFireSeconds`
(`CampfireManager.Ignite(x, y, z, firstBranchSeconds)`, then it burns down like any campfire); no fire, no goblins. Then
`GoblinCampMinGoblins..MaxGoblins` goblins rise on dry spots `GoblinCampRingMinBlocks..RingMaxBlocks` round it (forced
spawns, never past the hard cap), each carrying a torch, lit at night, and wandering about the fire (`Mob.WanderAnchor`:
its wander goals are picked round the fire instead of where it stands) until one notices a player and the pack joins in.
The anchor is not saved and the camp keeps no record: after a reload, or once the goblins despawn, the fire is just a
campfire. The `goblincamp` scenario op pitches one at once (`MobDirector.PitchGoblinCamp`); `GoblinCampTests`;
`visual-examples/goblin-camp.json` (`VOXEL_TIME=700`).

Every mob shares three more hazards. **Fall damage**: `Mob.NoteLanding` bills `MobFallDamage.Damage`
over the drop its impact speed implies — free up to `Mob.SafeFallBlocks` (plus a slime's own hop
peak), then `MobFallDamagePerBlockShare` of the pool per whole block, nothing when the feet were in
water (`MobFallDamageTests`). **Lava**: `Mob.UpdateHazards` with `MobLava.BodyInLava` deals a pool
share every `MobLavaTickSeconds` with the hit flash, a death it causes rolling loot as usual
(`MobLavaTests`). **Ledge caution**: `Mob.IsLedgeAhead` over `LedgeProbe` (live cells, not the
heightmap) stops a step or a hop that would land more than `MobLedgeMaxDropBlocks` down unless the
target stands lower (`LedgeCautionTests`).

### Wildlife

The animals of the wild are `Animal`s (`Entities/Wildlife/`): `RealmMob`s that are not hostile by nature, run by one
`WildlifeManager` (a `RealmMobManager<Animal>`). `WildlifeSpecies` is the one table of them (`WildlifeKind`, entity
name, factory, content contract, body size), so the manager, the saves, the client mirror, the renderer and the item
debugger take a new species from it instead of naming it; adding one means a class, a row there, a
`WildlifeSpawnRules` rule, a `NetEntityKind` after the last one and its numbers in `Config/WildlifeConfig`.

- **Budget.** Animals fill their own cap beside the hostile one (`IMobPopulation.IsWildlife`, `MobCaps.WildlifeCap`:
  `WildlifeConfig.WildlifeCap` per player, scaled like the natural cap, with its own `WildlifeSpawnHaste`), so a herd
  never takes an enemy's place nor the other way round; both still count toward the one hard cap.
- **Spawning.** Each spawn turn searches a dry surface column in the `SpawnMin..MaxRadiusBlocks` ring around a target
  on the surface (`MobSpawner.TryFindSurfaceSpawn`, leaning toward its heading like every ring), picks the species
  for the column's biome and whether surface water is near (`WildlifeSpawnRules.Pick` over
  `MobSpawner.SurfaceBiomeAt` and `IsNearSurfaceWater`: deer in forest, taiga and steppe; boars in forest, taiga and
  savanna; the rare dire boar in forest and taiga; crows on any land; dragonflies only at the waterside), and places
  that one animal (never a group), its body only where it fits (`BoxSweep.IsColliding` at
  `MobSpawner.StandingCenterY`, a flyer `AirSpawnLiftMin..MaxBlocks` up in the open air). An animal fades out only when no
  target keeps it in play (`MobTargets.OutOfPlay`); it never despawns for idling
  (`RealmMobManager.DespawnsWhenIdle`).
- **Reacting.** The manager hands an animal a target only when it heeds the players near it (`Animal.Heeds`, judged
  against the nearest non-immune one, so a creative or flying player concerns nobody); otherwise it wanders and its
  idle habit plays (`Animal.IdleTick`). `RealmMob.FacesTarget` (false while fleeing: it faces the way it runs) and
  `RealmMob.ActsThroughAction` (an idle clip does not hold it still) are the two hooks this needs.
- **Gaits.** A walker with a run clip switches its locomotion pair by its speed with hysteresis
  (`Animal.ChooseGait` over `EntityAnimator.SetGait`, which fades between pairs), on the server and on a mirror
  alike since only the velocity is read; breaking into the run cuts an idle clip short (`EntityAnimator.CutAction`).
  Its other clips are counted actions (`RealmMob.StartAttack(variant)`), so the attack count replays them on clients.
- **Strikes.** Every attack lands through one shape: `Animal.TryTakeStrike` (the clip's `impact` event) and
  `StrikeReaches`, which the manager bills to each non-immune target with the species' `StrikeDamage` and
  knockback.
- **Flyers that perch** (`FlyingAnimal`): in the air it steers like any flyer (`FlyToward`); left alone for
  `FlyerAirMin..MaxSeconds` over solid ground within `FlyerLandDropBlocks` it lands (its `land` clip) and is then a
  walker standing still under gravity, playing its ground clips for `FlyerPerchMin..MaxSeconds`; it takes off (its
  `takeoff` clip, with a kick upward) when the rest is over, when hurt, or when it heeds somebody it will not sit
  still for (`StaysPerchedNear`). The locomotion pair follows the state through `SetGait`, and since a mirror
  cannot tell a hovering body from a sitting one, the state rides `Animal.Posture` in the entry's item byte.
- **Crow** (`Crow`): a thief in flocks. It never attacks until hurt; for `CrowAngerSeconds` after a blow, a player
  within `CrowNoticeRangeBlocks` draws it: it circles overhead and swoops (its `swoop` clip flown at the chest),
  raking whoever is within `CrowStrikeReach` at the impact; a hurt crow, or any crow going after a player, also calls its flock
  (`WildlifeManager.RallyFlocks`, so a calm crow that wanders into earshot joins too): every calm, unprovoked crow within `CrowFlockCallRangeBlocks` gets the same anger
  (`Crow.JoinFlock`) and so heeds the nearest player like the victim does (crows already angry are left alone); on the ground it pecks an ankle within `CrowPeckReach`.
  Calm, it flies down to the nearest loose item within `CrowThiefRangeBlocks` (`WildlifeManager.SendThieves`) and takes
  it within `CrowThiefReach` (`TakeLoot`), then rests `CrowThiefCooldownSeconds`. Calm in the air (to a wander
  point, a perch or nowhere) it never holds a height: on its `glide` pose it falls under `CrowGlideGravity` until it
  sinks at `CrowGlideSinkSpeed` (`RiggedMob.Fly`'s `glide`), and nearing a wander point it already picks the next
  (`Mob.ForgetWanderGoal`), so it never stalls over one. While below the height `Fly` aims for (`RiggedMob.FlightAimHeight`: the goal under the
  hover-band floor rule) it flaps (`Crow.TickFlaps`). One flap is one play of the `flap` clip, and only its downstroke's
  `beat` event lifts it (climb set to `CrowFlapBoostSpeed`): every `CrowFlapMinSeconds`..`Max`, back to back while more
  than `CrowFlapClimbBlocks` below its aim, never above it. Its rests prefer a tree top
  (`FlyingAnimal.ChoosePerchSpot`: leaf or wood within `FlyerPerchSearchRadiusBlocks`, given up after
  `FlyerPerchSeekSeconds`). It soars on its `glide` clip and pecks the dirt or caws while resting.
  Its hits and kills play `crow_hit` and `crow_death` clips (`Mob.HitSoundGroup`/`DeathSoundGroup`). Separately, `CrowCaws`
  (`src/Entities/Wildlife/`, ticked by `World`) plays a random `crow_caw` clip every `CrowCawMinSeconds`..`Max` (`AudioConfig`)
  from the nearest live crow within `CrowCawRangeBlocks` of the camera, positionally.
- **Dragonfly** (`Dragonfly`): the man-sized hunter of the waterside. A player within `DragonflyNoticeRangeBlocks` is
  prey: it holds itself over their head and darts in its `bite` clip, biting whoever is within `DragonflyBiteReach`
  at the impact. On the ground it plays `perch`. Riding one is a later task (`plans/TODO.md`).
- **Deer** (`Deer`): watches a player inside `DeerAlertRangeBlocks` (its `alert` clip once), bolts from one inside
  `DeerFleeRangeBlocks` or one that hurts it for `DeerFleeSeconds` at `DeerRunSpeed`, veering so a herd scatters;
  grazes when left alone. It never attacks.
- **Boar and dire boar** (`Boar`): ignores everyone until hurt or crowded (a player within `BoarCrowdRangeBlocks`
  for `BoarCrowdSeconds`, while it stares), then plays `threat` once and charges in its run, goring at the `gore`
  clip's `impact` whoever is in `InGoreReach` (billed by the manager, never an immune target). Its anger lasts
  `BoarAngerSeconds` past its last sight of the target. The dire boar is the same class oversized and is provoked
  by anyone it notices within `DireBoarNoticeRangeBlocks`.
- **Drops.** Each species has a loot table of its own name (`WildlifeDropTests`): raw venison and hide from the
  deer; raw boar meat, hide and sometimes a tusk from the boar; a pile of all three plus its trophy `dire_tusk`
  from the dire boar; feathers from the crow; a wing from the dragonfly. The meats cook on a campfire into food
  that heals (`heals`), a hide makes leather gear with fewer units than cotton (`recipes/leather_*_from_hide.json`),
  a feather fletches six arrows where a bone makes four, and a tusk splits into bone.
- **Health bars.** An animal shows its bar only once hurt (`Animal.ShowsHealthbar`); a provoked boar and the dire
  boar always do.
- **Network and saves.** One `NetEntityKind` per species from `Deer` on (`NetEntity.KindOf` / `TryGetWildlife`,
  protocol 36, the flyers and the posture byte 37); a mirror is spawned by its species name and removed like any
  realm mob. Saves capture them like the
  sky mobs and restore them settled. `WildlifeTests` drives the rules headless and
  `visual-examples/wildlife.json` (`VOXEL_TIME=147`) films a herd calm, then reacting to a player on foot, and
  `visual-examples/wildlife-flyers.json` the crows aloft and resting and a dragonfly coming in.
