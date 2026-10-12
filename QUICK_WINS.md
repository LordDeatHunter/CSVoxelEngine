# QUICK_WINS — one-thing features

> Plug-n-play ideas: each entry is **one self-contained feature** you can start and finish in a
> sitting. Not a category, not a system, and nothing that drags in sub-features.
> The big roadmap lives in **TODO.md**, performance in **PERF_TODO.md**.

## What qualifies

- One sentence says what it is. If it needs "and also", split it or drop it.
- It reuses what exists (a block flag, an existing manager, an existing animation hook).
- No new station, no new protocol message, no new save file, no new shader pass unless that
  *is* the feature.
- It can be verified with one scenario or one small test.
- A new block, item or enemy is fine, as long as it is a single one that needs no machinery
  around it. (The kiln was not: multiblock, fuel, crucible, molds and casting.)

Mark done with `[x]` and a short note, or delete the line. Add a new idea as one line plus a
**Hook** (where it plugs in) so the next person can start at once.

Art a feature needs is generated as a placeholder (a deterministic script under `tools/`) and
listed in `USER_TODO.md` (local, never committed) for replacement. A feature that needs sound
files waits in TODO.md instead, since there are none to use yet.

## Feel and feedback

- [x] **Landing dust puffs.** `GroundDust` + `LandingDust`, chips of the block landed on, scaled by the drop; player and mobs (`ground-dust.json`).
  - [x] *Slime splat:* goo-colored chips on every hop, scaled by `SlimeSize.Scale`.
- [x] **Sprint footstep puffs.** `footstepDust` block flag (sand, snow, dirt, gravel), one puff per `PlayerModel.WalkPhase` footfall.
- [x] **Sprint FOV widening.** `SprintFov`, added to the base FOV and the punch pinch, capped at the on-foot sprint (`camera-feel.json`).
- [x] **Hit-stop on a landed punch.** `SwingHitStop` + `PlayerModel.HitStop`; the rest of the swing replays faster so it ends on time (`hit-stop.json`).
- [x] **Swim stroke bubbles.** `BubbleParticleSystem.TrackSwimStrokes`, one burst per stroke with the eye under water (`swim-bubbles.json`).
- [x] **Strafe camera tilt.** `StrafeTilt`, an eased roll into the strafe, off while flying or dead.
- [x] **Pickup pop on the hotbar.** `HotbarPickupPop`, on a rising count of the same item.
- [x] **Low-health vignette pulse.** `LowHealthVignette`, a heartbeat term in the damage vignette pass (`low-health.json`).

- [x] **Mob death poof.** A mob bursts into skin-colored chips when its death animation ends:
  `GroundDust.DeathPoof` (`LandingDust.SkinColor`, `MobDeathPoof*`) at each manager's
  `IsRemovable` removal, singleplayer and a client, never a server (`MobDeathPoofTests`).
- [x] **Placement pop.** A placed block pops in a little too big and settles with one small
  overshoot: `PlacePop.Grow` (EaseOutBack) fired at `PlaceBlockAtFace` and drawn through the
  mining pop's `RenderBlockPop` in the select pass, independent of the mining hit's pulse
  (`PlacePopTests`).
- [x] **Water splash for every entity.** A `WaterSplashTracker` per body (player, mobs, drops, projectiles, falling cubes, primed TNT) stepped by `World.UpdateSplashes`; `WaterSplash.Count/Speed` grow along `ImpactStrength` from `WaterSplashMinSpeed`, pale chips off the fluid column top, never off lava (`WaterSplashTests`, `world-events.json`).
- [x] **Damage direction arc.** `DamageDirection` keeps the blow's world direction (fed by `Camera.TakeHit`, on a client by the `PlayerStatus` knockback that came with lost health) and the vignette pass draws it as `uHitArc`; falls and lava show none (`damage-arc.json`).
- [x] **Mob hit sparks.** A punch, slice or throw that connects throws pale chips from the
  contact point: `BlockDebrisSystem.Spark` at `World.StrikeMob` (singleplayer) and at the client's
  predicted connect, the same one the hit-stop reads; never a server (`MobHitSparkTests`).
- [x] **Drop landing bounce.** A drop that lands hops back up once or twice (`BounceDamping`
  restitution, dying off under `BounceMinImpact`) instead of stopping dead (`DropBounceTests`).
- [x] **Steam on lava hardening.** `FluidManager.HardenedCells` (both hardening rules) -> `World.PuffSteam`, a pale `FireParticleKind.Steam` through `FireParticleSystem.Puff`; a client puffs where a `BlockChanges` entry turns a fluid into its `hardensTo` (`SteamPuffTests`, `world-events.json`).
- [x] **Tree fall thud.** `TopplingTree.TryTakeLanding` fires once at flat -> `World.ThudTree`: a leaf burst off the landed canopy, `GroundDust.TreeThud` under the trunk, a `ShakeFalloff` shake scaled by the wood that fell (`TreeThudTests`).
- [x] **TNT blast shake.** `ShakeFalloff.Trauma` (full inside `BlastRadius`, zero at `BlastShakeRangeBlocks`) from `TntManager.Blasts` through `World.ShakeForBlast`; a client shakes where a primed mirror vanishes with its fuse spent (`BlastEffectTests`); the `prime` scenario op strikes a cube without the firestarter's miss chance.
- [x] **TNT fuse halved.** `TntManager.PlayerFuseSeconds` 1 s, the chain fuse unchanged; `PrimedTnt.WhiteFlash` speeds its strobe over the fuse it gets (`FirestarterTests`).
- [x] **TNT blast pushes entities.** `BlastImpulse.At` (`BlastPushMaxSpeed` at the center, zero at `BlastRadius`, tipped up by `BlastPushUpBias`) on other primed TNT, drops (`DropManager.ApplyBlastImpulse`), falling cubes, mobs (`Mob.Shove`) and players through `IMobTarget.TakeHit` (a server's queued knockback); creative, flying and dead players immune (`BlastEffectTests`).
- [x] **Falling block landing dust.** `FallingBlockManager.Landings` -> `GroundDust.LandBlock` -> `BlockDebrisSystem.Skirt`, sized by `LandingDust.FallingBlockCount/Speed` from `FallingBlock.FallBlocks`; a client puffs at a `BlockChanges` write beside a mirrored cube (`FallingBlockDustTests`).
- [x] **Tool break shatter.** A tool whose durability runs out shatters into chips of its
  sprite's mean color at the hand (`ToolShatter.ColorOf` at `World.DamageTool`) and its hotbar
  slot flashes white and fades (`HotbarPickupPop.Flash`, `ToolShatterTests`,
  `HotbarPickupPopTests`).
- [x] **Pickup sparkle.** A few bright motes (`FireParticleKind.Spark`) pop at the chest where a
  drop is collected, singleplayer only (`PickupSparkle*`, `FireParticleTests`).
- [x] **Surfacing droplets.** `SurfacingDroplets` (timing and a seed per surfacing, read in `Camera.RefreshEyeWaterState`); hashed discs with trails as the vignette pass's `uDroplets` term, none from lava (`surfacing-droplets.json`).
- [x] **Goblin notice pip.** A "!" billboard pops over a goblin's head the moment it first
  notices a player (`Goblin.NoticePipProgress` from its first sight; `NoticePipRenderer` over the
  HUD font like the damage numbers) (`GoblinNoticePipTests`).
- [x] **Water depth states.** `WaterImmersion` / `WaterPhase` (Dry, Wade, Float, Swim) beside `Camera.IsInWater`; `WadeSpeedFactor`, a buoyancy target that rests the body about 70 % under (`WaterFloatRestShare`), Ctrl dives; the `SURFACE` float pose (`ITEM_DEBUGGER_PLAYER_ANIM=surface`); the cosmetic `NetPlayerFlags.Floating` bit, no protocol bump (`water-depth.json`).
- [x] **Throw charge zoom.** `ThrowZoom`, a spring-driven FOV term beside `SprintFov` following `Camera.ThrowCharge` (`throw-zoom.json`).

## Shaders

- [x] **Tall grass roots stay put.** Sway weight in the vertex alpha, 0 at a plant's base to 1 at its tip (`plant-sway.json`).
- [x] **Leaf particles follow the wind.** Falling leaves ease toward `Wind.Direction` scaled by `Wind.Gust` (`leaf-wind.json`).
- [x] **Water edge foam.** One water-fragment term over thin view-ray depth, top faces only (`water-foam.json`).
- [x] **Stars.** Dome-only star field, turned with the sun and hidden by clouds (`night-sky.json`).
  - [x] *Twinkle:* per-star rate and phase hashed from its cell.
- [x] **Stars hold still.** The spin is gone (`StarField.SpinOf`/`ToStarSpace`, `uStarSpin`,
  `SkyDomeFrame.StarSpin` deleted; the shader feeds the view ray straight to `Stars`), leaving
  `StarField.Visibility` as the only clock (`StarFieldTests`).
- [x] **Sun lens glare.** `SunGlare.Intensity` from alignment, sun height and cloud cover (`night-sky.json`).
- [x] **Snow sparkle.** Hashed per-texel glints on the snow tile; only shows with a low sun, since noon snow clips to white (`snow-sparkle.json`, `VOXEL_TIME=405`).

- [x] **Leaf backlight.** `LeafBacklight` + `uLeafBacklight*`: sway faces (a flat `fSwayFlag`) glow by a `pow(dot(view, sun))` lobe times a shadow-map read `LeafBacklightReachBlocks` toward the sun, since the faces seen against the sun point away from it (`leaf-backlight.json`, `VOXEL_TIME=1100`).
- [x] **Eye adaptation.** `EyeAdaptation` sets the exposure target from `EntityLight.Sample` at the eye: 1 under sky or in a lit room, up to `EyeAdaptMaxExposure` in the dark, slower into the dark than out; `uExposure` set every frame (`eye-adaptation.json`).
- [x] **Grass wind shimmer.** `GrassShimmer` + `uGrassShimmer*`: bands across `uWind` on biome-tinted up faces, read at the position minus `uWindOffset`, weighted by the sway's gust, gone by 112 blocks (`grass-shimmer.json`).
- [x] **Ice glint.** `IceGlint` + `uIceLayer`/`uIceGlint`/`uIceShine`/`uIceSheen`: a fresnel sky mirror plus a sun highlight gated by the face's direct sun (`ice-glint.json`, `VOXEL_TIME=405`).
- [x] **Underwater wobble.** `uUnderwater` sways the read point in `post-fragment.glsl` (`UnderwaterWobble.Offset`, a wrapped clock); FXAA reads around the same swayed point, filtered bilinearly (`underwater-wobble.json`).
- [x] **Cloud silver lining.** A forward-scatter lobe in `cloud-fragment.glsl` times the cloud's thinness along the view ray (`CloudThinness` from the cloud-shadow map), strength `CloudSilverLining.Strength`, zero at and below the horizon (`cloud-silver-lining.json`).
- [x] **Sunset horizon glow.** `SkyPalette.GlowAmount` gives the `SkySample` glow lobe a warm value only while the sun crosses the horizon (`SkyDuskGlowStrength`, `SkyGlow*SunElevation`); fog toward the sun blends into the band (`sunset-glow.json`, `SkyPaletteTests`).
- [x] **Sun glitter on water.** `WaterGlitter` + `uWaterGlitter*`: five hashed facets per block tilted off the wave normal, lit by the snow glint's half-vector test as one round point per cell, re-rolled 3x a second; off from below, underwater, in shade and past 128 blocks (`water-glitter.json`, `VOXEL_TIME=360`).

- [x] **Lava glow pulse.** `LavaPulse` + `uLavaPulse`: the lava glow (`isLava`/`uLavaLayer`) scaled by a slow two-octave value-noise field after the display-space clip (`lava.json` pulse shots).
- [x] **Damage chromatic fringe.** `uDamageFringe` in `post-fragment.glsl` splits red and blue from the screen center by `DamageFringe.Strength` of `PlayerHealth.DamageFlash` (its first 0.3 s) (`damage-fringe.json`).
- [x] **Biome color grade.** `BiomeGrade.Tint` of the eye's climate from the column source (never worldgen), at most `BiomeGradeStrength` per channel, eased, neutral on an unknown column, applied as `uGrade` in the post pass (`biome-grade.json`).
- [x] **Aurora.** `AuroraCurtains` in `sky-fragment.glsl` after the stars, hidden by clouds, faded by `Aurora.Visibility` = eased coldness at the eye (`EyeClimate.TryAt` over the column source) times `StarField.Visibility` (`aurora.json`, `VOXEL_TIME=700` on the frozen plain).
- [x] **Deep-water darkness.** `DeepWaterFog.Dim(depth)` -> `uDeepWaterDim` from `Camera.EyeWaterSurfaceY - RenderEye.Y`: the underwater fog and the from-below reflection lose red first and never drop under 15 % (`deep-water.json`).
- [x] **Glass sheen.** `GlassSheen` + `uGlassLayer`/`uGlassSheen`: glass and panes fold `SkySample` times `SurfaceFresnel.Schlick` into the translucent blend with alpha `a + F - aF` (`glass-sheen.json`).
- [x] **Ore twinkle.** `OreTwinkle` + `uOreTwinkle*` in the ore material path: a hashed 4 % of texels flash once per 0.2-0.5 Hz cycle, gated like the snow glint, gone by 28 blocks (`ore-twinkle.json`).
- [x] **Selection outline pulse.** `SelectionPulse` + `uPulseDepth`/`uPulsePeriod`: 70-100 % over `SelectionPulsePeriodSeconds`, held steady while mining; the outline beams now cull back faces so the breath reads (`selection-pulse.json`).
- [x] **Moonlight.** `Moonlight` key-light rule: below `MoonlightSwitchSunElevation` the world's `uSunDir`/color/intensity become the moon's (cool, scaled by `MoonPhase`, casting shadows), handed over with both lights at zero where the sunset glow ends; the night ambient is bluer and the sky keeps the true sun (`_skySunDir`) (`moonlight.json`). The moon disc itself is TODO.md's `V-6` (`RenderMoon`, `moon-fragment.glsl`, `moon.json`).
- [x] **Godrays.** `GodraysRenderer`: the underwater shafts' half-res march over air through the near cascade and the cloud shadows plus an open-sky term (`Godrays.Scatter`), composited radially toward the sun's screen position; strength `Godrays.Intensity`, its own `godPass` row (~0.35 ms GPU at 1080p), `GodraysEnabled` (`godrays.json` at 395/425/147).

## Blocks and items

- [x] **Natural cotton.** `cotton_plant` + `cotton`, clumped by the reusable `PlantClumps` rule on the dry forest fringe (`cotton.json`).
- [x] **Torch.** `torch`, light 14, `needsSupport`; branch + coal -> 4 (`torches.json`).
  - [x] *Wall-mounted:* `wall_torch` with `facing` + `support: "attached"`; `BlockSupport` is the one support rule.
- [x] **Charred torch top.** The torch's `up` faces wear a `torch_top` tile of their own (a
  charred square with a faint ember center, written by `tools/generate-torch-texture.py`) over
  the whole tile, so the top no longer shows the side crop's char band and wood rows
  (`TorchFlameTests`).
- [x] **Glass pane.** `glass_pane`; 6 glass -> 16 (`panes.json`).
  - [x] *Neighbor connectivity:* `pane` renderer (`PaneShape` + `PaneEmitter`), arms toward panes and solid cubes, matching hitboxes.

- [x] **Torch flame particles.** The `flame` block field (turned with the facing, `BlockType.FlamePoints`) -> `ChunkMeshData.FlamePoints` (dropped headless) -> `World.UpdateTorchFlames`, a third `FireParticleSystem` with spawn-scatter and scale parameters: small embers plus smoke, no point light (`TorchFlameTests`, `torches.json` flame shots at 700).
- [x] **Planks.** `oak_planks`, four from an `oak_log`; tile from `tools/generate-plank-texture.py` (`planks.json`).
- [x] **Slab.** `cobblestone_slab` and `oak_planks_slab` on a `block/slab` parent, colliding by a 0..0.5 `hitbox` (`BuildingBlockTests`, `planks.json`).
- [x] **Stairs.** `cobblestone_stairs` / `oak_planks_stairs`: `facing: "horizontal"` plus the new `facingFromLook` flag, which `Placement.LookFacing` reads from the placing `PlayerContext`'s view (`StairTests`).
- [x] **Fence.** `oak_fence`: the `pane` block object (`post`, `arm`, `rails` in px) becomes a `PaneProfile` that `PaneShape`/`PaneEmitter` read; panes join only their own shape (`PaneProfileTests`, `fences.json`).
- [x] **Trapdoor.** `oak_trapdoor`: the `open` block object (model + hitbox) makes a block openable, open bit = state bit 2 (`DoorState`), toggled by `World.TryToggleOpenable` in the right-click chain (`OpenableTests`, `doors.json`).
- [x] **Hanging lantern.** `lantern` (torch + copper ingot, emission 15) on the general `support: "above"` rule (`BlockType.SupportAbove`); tile from `tools/generate-lantern-texture.py` (`HangingBlockTests`, `lanterns.json`).
- [x] **Slime block.** A `bounce` block flag (a slime block, nine slimeballs): a body that lands
  on it is sprung back up at `SlimeBlockBounceRestitution` of its impact and bills no fall
  damage (`FallTracker`'s cushioned landing); mobs and drops later. Tile from
  `tools/generate-slime-block-texture.py` (`SlimeBlockTests`).
- [x] **Bandage.** `HealItem` + `use.action: "heal"` (`PlayerHealth.Heal`, refused at full health, `BandageHealAmount`), sprite from `tools/generate-bandage-texture.py`; `HealItemTests`.
## Enemies

- [x] **Slime size variants.** `SlimeSize`, sizes 1-3 scaling body, health, damage and loot; saved and sent (`slime-sizes.json`).
  - [x] *One mob cap:* `MobCaps`, a natural and a hard cap shared by every kind.
  - [x] *Spawn rules:* `SlimeSize.Roll`, larger at night and underground.
  - [x] *Splitting:* on the half-health transition, children forced under the hard cap (`slime-split.json`).

- [x] **Mob fall damage.** `Mob.NoteLanding` bills `MobFallDamage.Damage`: free up to `MobSafeFallBlocks` (plus a slime's own hop peak), then a share of the mob's pool per block; free in water, authoritative sims only (`MobFallDamageTests`, `mob-hazards.json`).
- [x] **Mobs burn in lava.** `Mob.UpdateHazards` with `MobLava.BodyInLava` (the `PlayerBodyProbe.InLava` cell test over the body box) dealing a pool share every `MobLavaTickSeconds` with the hit flash (`MobLavaTests`, `mob-hazards.json`).
- [x] **Goblins flee at low health.** Below `GoblinFleeHealthShare` (a quarter) of its pool a
  goblin turns and runs from the player at `GoblinFleeSpeedFactor` of its walk, never throwing or
  swinging, and still despawns by distance as usual (`Goblin.IsFleeing`; `GoblinFleeTests`).
- [x] **Goblins scavenge flint.** A live goblin with ammo room joins the world's drop pass as a
  `DropCollector` that takes only flint (`GoblinManager.AppendCollectors`/`AwardScavenged`,
  capped at `GoblinStoneMax`; a client's mirrors never scavenge) (`GoblinScavengeTests`).
- [x] **Sneaking shortens the notice range.** `MobTargets.Nearest` only picks targets inside `MobTargets.NoticeRadius` (`MobNoticeRadiusBlocks`, times `MobSneakNoticeFactor` while `IMobTarget.IsSneaking`; the camera's crouch, a `ServerPlayer`'s cosmetic flag) (`MobNoticeTests`, `mob-notice.json`).
- [x] **Slime merge.** `SlimeManager.MergeTouching` over `SlimeMerge`: two touching living slimes of one size below the largest, both older than `SlimeMergeCooldownSeconds`, become one forced spawn of the next size (`SlimeMergeTests`, `slime-merge.json`).
- [x] **Cave slimes.** `MobSpawner.TryFindCaveSpawn` (a dry, dark, loaded cave floor at least `SlimeUndergroundDepthBlocks` under the surface); `SlimeManager.TryFindSpawn` tries caves first for a player underground (`CaveSlimeTests`).
- [x] **Goblin stones hit other mobs.** The stone pass feeds `ThrownProjectile.Update` the live
  goblins (each stone skips its own thrower) and slimes: a struck mob takes `GoblinStoneDamage`
  and a shove and the stone stops, while a miss shatters as before (`GoblinManager.UpdateStones`,
  `GoblinStoneTests`).
- [x] **Ledge caution.** `Mob.IsLedgeAhead` over `LedgeProbe` (live cells, not the heightmap): no step or hop landing more than `MobLedgeMaxDropBlocks` down unless the target is lower (`LedgeCautionTests`, `mob-hazards.json`).
- [x] **Goblin dodge.** A charged throw (at least `GoblinDodgeChargeThreshold`) aimed at a goblin
  (inside `GoblinDodgeConeDegrees` and within `GoblinDodgeRangeBlocks`) makes it sidestep out of
  the line while the aim holds, its side fixed at spawn; singleplayer passes the local arm's aim
  (`GoblinManager.IsAimedAt`, `Goblin.Decide`; `GoblinDodgeTests`).

- [x] **Pack alert.** A goblin that notices a player alerts every other goblin within
  `GoblinPackAlertRadiusBlocks` (12), which skip their first-throw delay so the pack throws
  together (`Goblin.TakeJustNoticed`/`AlertPack`, `GoblinManager.AlertPackAround`;
  `GoblinPackAlertTests`).
- [x] **Pack spawning.** A ring spawn rolls one to `GoblinPackMaxSize` (3) goblins, the rest of
  the pack landing within `GoblinPackSpawnRadiusBlocks` of the found cell, each member re-checked
  against the shared natural cap (`GoblinManager.TryFindPackMate`; `GoblinPackSpawnTests`).
- [x] **Torch-safe ground.** `MobSpawner.TryFindSurfaceSpawn` refuses a cell with block light at or over `MobSpawnBlockLightLimit` (`MobSpawner.IsLitByBlocks` over `EntityLight.Sample`) (`TorchSafeSpawnTests`).
- [x] **Size-scaled knockback.** `SlimeSize.ShoveSpeed` (the shove over the body scale) in `Slime.Shove`; a split child's launch is unscaled (`SlimeKnockbackTests`).
- [x] **Despawn fade.** Both managers' despawn branch calls `Mob.BeginDying()` without loot, so the mob plays its death pose and leaves `LivingCount` (`mob-hazards.json`).
- [x] **Giant slime.** `SlimeMaxSize` 4; `SlimeSize.Roll` takes the last step at `SlimeMaxSizeChanceFactor` of the usual threshold, so it is rarer than size 3 in every light (`GiantSlimeTests`, `slime-sizes.json`).
- [x] **Goblin torches.** A spawn may carry up to `GoblinTorchMaxCount` torches (a roll whose
  chance is small by day, larger at night and highest underground), equips one while it is dark
  (drawn at the off hand's `ArmL` anchor through `BodyEffect`, its flame point joining
  `World.FireCells` for light and embers), and drops the carried torches on death with the flint;
  the count is saved (`MobData.Torches`) and sent (the mob `NetEntity.ItemId` byte)
  (`GoblinTorches`, `Goblin.TorchFlamePoint`; `GoblinTorchTests`).
- [x] **Loot toss.** Every death drop goes through `DropManager.SpawnAt(id, pos, MobLoot.RandomToss())`, an up-and-out launch tuned by `MobLootToss*` (`MobLootTests`, `mob-hazards.json`).
- [x] **Size-scaled hops.** `SlimeSize.HopLift` / `HopReach` / `WindupScale` raise the body scale to the `SlimeHop*Exponent` knobs, read by `Slime.HopVelocity` and the crouch time (`SlimeHopTests`).
- [x] **Lose interest.** `Mob.PickTarget` = the notice rule plus a per-mob `MobInterest`: sight checked every `MobSightCheckSeconds`, dropped after `MobLoseInterestSeconds` without it, then the mob wanders toward `Mob.WanderGoal` and idles out through `Mob.ShouldIdleDespawn` (`MobInterestTests`, `mob-notice.json`).

## World

- [x] **Fallen logs.** A rare detached oak log of 3-5 cells lying along X or Z on the forest
  floor, decided per 32-block cell from a pure hash so chunks agree at borders and clipped there
  (`FallenLogGenerator`, `WoodState.Detached`; `SceneryTests`).
- [x] **Boulders.** A rare 2-4 cell blob of stone or cobblestone on the forest and taiga floor,
  per 32-block cell from a pure hash, into air only (`BoulderGenerator`; `SceneryTests`).
  *Hook:* one candidate per coarse cell hash (the `PlantClumps` cell pattern), stamped per chunk
  and clipped at its border; no far-LOD bump.
- [x] **Reeds at shores.** `ReedGenerator` over `PlantClumps`, gated to the shallow water edge, replacing the shallow cell; `PondPlantTests`.
## UI

- [x] **Hotbar item name fade.** `HotbarNameFade` over the hearts; `select` scenario op (`hotbar.json`).
- [x] **Floating damage numbers.** `DamageNumberPool` + `DamageNumberRenderer`; a client reads the health drop between snapshots (`damage-numbers.json`).

- [x] **Crosshair hit marker.** `HitMarker` flashes at the two connecting-hit sites the hit-stop uses (a landed punch or slice) and `UIRenderer.DrawCrosshair` draws the X, fading over `HitMarker.Seconds` (`HitMarkerTests`).
- [x] **Crosshair spreads while charging.** the crosshair is drawn as five pieces cropped out of its 16x16 texture (a center dot and four pills); the arms slide outward with `Camera.ThrowCharge` (eased) while the dot stays put, and snap back on release.
- [x] **Mining progress ring.** a ring of `CrosshairRingSegments` dots around the crosshair, the lit share following `World.Crosshair.BreakProgress` (0 in creative or with no target, so it never shows there).
- [x] **Look-at label.** The block under the crosshair shows its name and a small spinning 3D
  render of it at the top of the screen, below the compass or clock strip while one is equipped
  (`UIRenderer.DrawLookLabel` and its `LookLabelTop` over `BlockCubeCache`), the name in red while
  the held item cannot mine it, and "Unknown" with a flat dark silhouette until its item has been
  obtained; creative mode and a block with no item at all (`hasItem` false) always know, and `X`
  adds id, state byte, sun/block light and chunk/cell debug lines. `LookLabel` is the rule,
  `World.LookAt` the raycast's state, and "obtained" is `Inventory`'s own set, marked in `Add`,
  saved as names in `player.json` (an optional field) and riding the `InventoryNetCodec` snapshot
  (`LookLabelTests`, `InventoryTests`, `look-label.json`).
- [x] **Chat fades when closed.** `ChatView` keeps an arrival clock per line and `AlphaAt` holds it opaque through `HoldSeconds`, eases it out over `FadeSeconds` and pins it at 1 while the log is open (the draw skips fully faded lines) (`ChatViewTests`).
- [x] **`/time` chat command.** the `ChatCommands` table parses `/time day|night|<seconds>` (day = noon, night = midnight, a number is `VOXEL_TIME` units) and both Program and GameServer run it through `World.SetLightingTime`; a server needs operator rights and sends a `TimeSync` at once (`ChatCommandsTests`).
- [x] **Day toast.** `HudToast` (a hold-then-fade line drawn above the item-name row) shown by the world's `DayToast.CrossedDawn` watch on the lighting clock, so "Day N" fades in the moment the sun rises through dawn, once a day (`HudToastTests`).
- [x] **New item toast.** The drop pickup and `World.CreditItem` (the /give path) toast "New: <name>" on a first credit, read off the inventory's own obtained set (`Inventory.Knows`, the same one the look-at label reads) before the add marks it, and a restored inventory comes back with its obtained names so loading never toasts (`InventoryTests`).
- [x] **Screenshot key.** F2 reads the finished frame back through `VisualCapture` and saves it to `ScreenshotPaths.Directory` as a millisecond-stamped PNG (`ScreenshotPathTests`).
- [x] **Hide-HUD key.** F1 toggles `hudVisible`, the same gate the scenario screenshot's `hideHud` drives (`ScreenshotPathTests` covers the shared path naming).
- [x] **Tab player list.** holding Tab over the HUD draws the panel (`UIRenderer.DrawPlayerList` over `World.RemotePlayers`, the local player first), a server session only, and never while the chat, a menu or the panel owns the keyboard.
- [x] **Chat commands: help, tp, give.** `/help` answers with `ChatCommands.HelpText`, `/tp x y z` teleports (creative locally, `ServerPlayer.Teleport` + a correction on a server) and `/give <item> [count]` grants through `Inventory.Add` with the name resolved by block then item; both need rights (`ChatCommandsTests`).
- [x] **Item tooltips.** `ItemTooltip.Lines` builds the hover panel from the fields the item already has (name, kind, tool power and hit pace, block/entity damage, durability left from the hovered stack, fuel seconds, a crucible's capacity), and Program shows it over the [E] panel's rows and the hotbar slots (`ItemTooltipTests`).
- [x] **Inventory sort.** already covered: `Inventory.StackEntries` builds the panel rows sorted by display name, now with the fuller stack first as the count tiebreak (`InventoryTests`); a sort button would be a no-op, since the rows are sorted by construction and the pools are keyed by id.
- [x] **Number-key slot assign.** with the [E] panel open, 1-9 (and 0) equip the hovered row into that hotbar slot through the same equip a click performs (`Program.AssignHoveredRowToHotbar`, sent as an `Equip` intent on a server).
- [x] **HUD scale.** already covered: the settings page's GUI Scale row (`GameSettings.GuiScaleIndex` over `GuiScales`, 0.5x..2x, `RequestedGuiScale` feeding `UiCanvas.EffectiveScale`) scales the HUD across the 75-150 % band and beyond; a continuous slider would duplicate the tested setting.
- [x] **Sprint toggle option.** the `Sprint Toggle` controls setting plus `SprintToggle.Step` driving `Camera.SprintHeld` (a fresh press flips the latch; off, the key is held as before) (`SprintToggleTests`).
- [x] **Worlds list details.** the row's second line is `MainMenuLayout.WorldDetailText`: the seed plus the last-played moment (`WorldInfo.LastPlayedUnixMs`, local `yyyy-MM-dd HH:mm`), or "never played" (`MainMenuLayoutTests`).
- [x] **Death message.** `DeathMessages.For` over the blow recorded in `PlayerHealth` (`DamageSource` plus the mob's display name through `IMobTarget.TakeHit`; the fall, lava and the cactus name themselves); `World.TakeDeathMessage` drains it into the chat, and a server broadcasts its own once per death (`DeathMessageTests`).
- [x] **Inventory-full warning.** `DropManager.BlockedCollectors` reports a drop within a collector's reach that its pool refused, and the world shows "Inventory full" through `HudToast` on the transition into the blocked state only (a goblin's own refusal is its index, never the player's; a client reads its own mirror the same way) (`InventoryFullWarningTests`).
