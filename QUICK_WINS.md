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
  - [x] *Slime splat:* goo-coloured chips on every hop, scaled by `SlimeSize.Scale`.
- [x] **Sprint footstep puffs.** `footstepDust` block flag (sand, snow, dirt, gravel), one puff per `PlayerModel.WalkPhase` footfall.
- [x] **Sprint FOV widening.** `SprintFov`, added to the base FOV and the punch pinch, capped at the on-foot sprint (`camera-feel.json`).
- [x] **Hit-stop on a landed punch.** `SwingHitStop` + `PlayerModel.HitStop`; the rest of the swing replays faster so it ends on time (`hit-stop.json`).
- [x] **Swim stroke bubbles.** `BubbleParticleSystem.TrackSwimStrokes`, one burst per stroke with the eye under water (`swim-bubbles.json`).
- [x] **Strafe camera tilt.** `StrafeTilt`, an eased roll into the strafe, off while flying or dead.
- [x] **Pickup pop on the hotbar.** `HotbarPickupPop`, on a rising count of the same item.
- [x] **Low-health vignette pulse.** `LowHealthVignette`, a heartbeat term in the damage vignette pass (`low-health.json`).

- [x] **Mob death poof.** A mob bursts into skin-coloured chips when its death animation ends:
  `GroundDust.DeathPoof` (`LandingDust.SkinColour`, `MobDeathPoof*`) at each manager's
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
- [x] **TNT blast pushes entities.** `BlastImpulse.At` (`BlastPushMaxSpeed` at the centre, zero at `BlastRadius`, tipped up by `BlastPushUpBias`) on other primed TNT, drops (`DropManager.ApplyBlastImpulse`), falling cubes, mobs (`Mob.Shove`) and players through `IMobTarget.TakeHit` (a server's queued knockback); creative, flying and dead players immune (`BlastEffectTests`).
- [x] **Falling block landing dust.** `FallingBlockManager.Landings` -> `GroundDust.LandBlock` -> `BlockDebrisSystem.Skirt`, sized by `LandingDust.FallingBlockCount/Speed` from `FallingBlock.FallBlocks`; a client puffs at a `BlockChanges` write beside a mirrored cube (`FallingBlockDustTests`).
- [x] **Tool break shatter.** A tool whose durability runs out shatters into chips of its
  sprite's mean colour at the hand (`ToolShatter.ColourOf` at `World.DamageTool`) and its hotbar
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
- [x] **Damage chromatic fringe.** `uDamageFringe` in `post-fragment.glsl` splits red and blue from the screen centre by `DamageFringe.Strength` of `PlayerHealth.DamageFlash` (its first 0.3 s) (`damage-fringe.json`).
- [x] **Biome colour grade.** `BiomeGrade.Tint` of the eye's climate from the column source (never worldgen), at most `BiomeGradeStrength` per channel, eased, neutral on an unknown column, applied as `uGrade` in the post pass (`biome-grade.json`).
- [x] **Aurora.** `AuroraCurtains` in `sky-fragment.glsl` after the stars, hidden by clouds, faded by `Aurora.Visibility` = eased coldness at the eye (`EyeClimate.TryAt` over the column source) times `StarField.Visibility` (`aurora.json`, `VOXEL_TIME=700` on the frozen plain).
- [x] **Deep-water darkness.** `DeepWaterFog.Dim(depth)` -> `uDeepWaterDim` from `Camera.EyeWaterSurfaceY - RenderEye.Y`: the underwater fog and the from-below reflection lose red first and never drop under 15 % (`deep-water.json`).
- [x] **Glass sheen.** `GlassSheen` + `uGlassLayer`/`uGlassSheen`: glass and panes fold `SkySample` times `SurfaceFresnel.Schlick` into the translucent blend with alpha `a + F - aF` (`glass-sheen.json`).
- [x] **Ore twinkle.** `OreTwinkle` + `uOreTwinkle*` in the ore material path: a hashed 4 % of texels flash once per 0.2-0.5 Hz cycle, gated like the snow glint, gone by 28 blocks (`ore-twinkle.json`).
- [x] **Selection outline pulse.** `SelectionPulse` + `uPulseDepth`/`uPulsePeriod`: 70-100 % over `SelectionPulsePeriodSeconds`, held steady while mining; the outline beams now cull back faces so the breath reads (`selection-pulse.json`).
- [x] **Moonlight.** `Moonlight` key-light rule: below `MoonlightSwitchSunElevation` the world's `uSunDir`/colour/intensity become the moon's (cool, scaled by `MoonPhase`, casting shadows), handed over with both lights at zero where the sunset glow ends; the night ambient is bluer and the sky keeps the true sun (`_skySunDir`) (`moonlight.json`). The moon disc itself is TODO.md's `V-6` (`RenderMoon`, `moon-fragment.glsl`, `moon.json`).
- [x] **Godrays.** `GodraysRenderer`: the underwater shafts' half-res march over air through the near cascade and the cloud shadows plus an open-sky term (`Godrays.Scatter`), composited radially toward the sun's screen position; strength `Godrays.Intensity`, its own `godPass` row (~0.35 ms GPU at 1080p), `GodraysEnabled` (`godrays.json` at 395/425/147).

## Blocks and items

- [x] **Natural cotton.** `cotton_plant` + `cotton`, clumped by the reusable `PlantClumps` rule on the dry forest fringe (`cotton.json`).
- [x] **Torch.** `torch`, light 14, `needsSupport`; branch + coal -> 4 (`torches.json`).
  - [x] *Wall-mounted:* `wall_torch` with `facing` + `support: "attached"`; `BlockSupport` is the one support rule.
- [x] **Charred torch top.** The torch's `up` faces wear a `torch_top` tile of their own (a
  charred square with a faint ember centre, written by `tools/generate-torch-texture.py`) over
  the whole tile, so the top no longer shows the side crop's char band and wood rows
  (`TorchFlameTests`).
- [x] **Glass pane.** `glass_pane`; 6 glass -> 16 (`panes.json`).
  - [x] *Neighbour connectivity:* `pane` renderer (`PaneShape` + `PaneEmitter`), arms toward panes and solid cubes, matching hitboxes.

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
- [ ] **Bandage.** Cotton -> bandage; using one restores some health, the first heal in the game.
  *Hook:* a new `use.action: "heal"` (`ItemUseAction` + `ItemFactory`), spending one, refused at
  full health. On a server it is the `Use` intent against that player's own `PlayerHealth`.
- [x] **Ash fertilizer.** Using `ash` on a tree grows it `AshGrowthPulses` pulses through the
  reactive growth path (`use.action: "grow_tree"`, `GrowTreeItem`, `TreeSpeciesRegistry.ForBlock`),
  spending one; refused on anything that is not a tree (`GrowTreeItemTests`).
- [x] **Clock.** While it sits in the hotbar the HUD shows the time of day and the day number in
  the compass's place (`Clock.DayNumber`/`Readout` over the lighting clock, `UIRenderer.DrawClock`,
  crafted from copper ingots and a mud brick; `ClockTests`).
- [ ] **Use time with a usage animation.** Usable items (the firestarter, the bandage and the
  fertilizer) take time: holding right-click plays a use animation, the use happens only when it
  completes, and letting go early cancels it and spends nothing. Do it after the bandage and the
  fertilizer. *Hook:* a `use.seconds` on the item JSON (the firestarter's `useDelay` stays the
  cooldown after a use), a hold timer beside the throw charge, a `PlayerModel` use pose (one
  generic pose first: a strike for the firestarter, a wrap for the bandage and a sprinkle for ash
  can follow) and a mode in the item debugger's `PlayerPreviewState`. On a server the `Use`
  intent arrives at completion; check the hold time like `BreakProgress` checks mining.

- [x] **Door.** `oak_door` + `oak_door_upper` through the `upperHalf` field (`TallBlock`): placement needs both cells, breaking either half removes both (`RemoveTallBlock`) and drops one door, both halves share one `DoorState` (`DoorTests`, `doors.json`).
- [ ] **Acorn.** An oak-leaf drop that plants a sapling when used on dirt or grass, which then grows
  stage by stage on its own. *Hook:* a `use.action: "plant_tree"` calling
  `World.TryGrowTree(species, x, y, z, 1)`, which already plants a sapling on solid ground, and the
  growth stages already exist (`TreeGrower.GrowFurther`, one pulse per step), but nothing ticks
  them: add a small main-thread sapling scheduler (a `FluidManager`-style scheduled-cell set) that
  runs one pulse every `SaplingPulseSeconds` (jittered) until the species' maturity step, through
  the same `TryGrowTree`. Like the station block entities it is in memory only, so a reload stalls
  the sapling at its stage until it is re-planted or fertilised: list that gap in AGENTS.md. Oak
  leaves drop it at a low chance (the leaves loot table, JSON); a stone pine cone and a spruce cone
  follow the same pattern. Test: a pulse count never exceeds maturity, and a sapling on
  unloaded ground waits.
- [ ] **Flint from gravel.** Mining gravel has a 10-20 % chance to drop flint. *Hook:* the gravel
  loot table in JSON, `item/flint`. Check first what the table drops today; flint is otherwise only
  carried by goblins.
- [ ] **Cactus prick.** Standing against a cactus deals 1 damage every 0.5 s. *Hook:* a
  `contactDamage` block field read where the body overlaps cells (beside the lava rule), applied to
  the camera's pool in singleplayer and to `ServerPlayer` on a server through the existing status
  path; creative and flying are immune.
- [ ] **Stone bricks.** 4 stone to 4 stone bricks, one decorative tile. *Hook:* JSON block + recipe
  + a tile from a `tools/` script (placeholder, listed in `USER_TODO.md`). Enables the ruined
  pillars.
- [x] **Cobblestone wall.** `cobblestone_wall`: the pane renderer with `"pane": {"post": 8, "arm": 6}`, full-height arms and `cullSelf` so stacked walls meet seamlessly (`PaneProfileTests`, `fences.json`).
- [ ] **Placement takes the plant's cell.** Right-clicking tall grass, a dead shrub, a flower or
  a cotton plant while holding a block should replace the plant with the block instead of
  filling the cell across the hit face. The flag already exists (`replaceableByPlacement`, read
  by `Placement.TargetCell`/`CanOccupy`; layered snow is its one user), so this is JSON plus a
  test. *Hook:* add `"replaceableByPlacement": true` to `tall_grass`, `dead_shrub`, `flower` and
  `cotton_plant` (all already `solid: false`, which the registry check wants). `PlacementTests`:
  aiming at a plant fills the plant's own cell and the plant is gone.
- [ ] **Attacks pass through plants.** A mob behind tall grass cannot be punched or sliced
  today: the line of sight (`BlockRaycaster.SegmentHitsBlock`) stops on the plant's hitbox.
  Plants should not be cover. *Hook:* a `blocksSight` block flag (default true, a `BlockType`
  mirror array like `Occludes`) read by the LOS walk only: `Raycast` gets a sight-only switch
  that `SegmentHitsBlock` sets, so a flagged cell is walked through, while the aim ray still
  targets it for mining. Set `"blocksSight": false` on `tall_grass`, `dead_shrub`, `flower` and
  `cotton_plant` (the goblin's throw check and `MobInterest` share the test, so a goblin throws
  through grass too). Test: a mob behind a plant is struck, one behind stone is not.

## Enemies

- [x] **Slime size variants.** `SlimeSize`, sizes 1-3 scaling body, health, damage and loot; saved and sent (`slime-sizes.json`).
  - [x] *One mob cap:* `MobCaps`, a natural and a hard cap shared by every kind.
  - [x] *Spawn rules:* `SlimeSize.Roll`, larger at night and underground.
  - [x] *Splitting:* on the half-health transition, children forced under the hard cap (`slime-split.json`).

- [x] **Mob fall damage.** `Mob.NoteLanding` bills `MobFallDamage.Damage`: free up to `MobSafeFallBlocks` (plus a slime's own hop peak), then a share of the mob's pool per block; free in water, authoritative sims only (`MobFallDamageTests`, `mob-hazards.json`).
- [x] **Mobs burn in lava.** `Mob.UpdateHazards` with `MobLava.BodyInLava` (the `PlayerBodyProbe.InLava` cell test over the body box) dealing a pool share every `MobLavaTickSeconds` with the hit flash (`MobLavaTests`, `mob-hazards.json`).
- [ ] **Goblins flee at low health.** Below a share of its health a goblin turns and runs.
  *Hook:* the stance in `Goblin.Update` (the backpedal branch already moves it away); it still
  despawns by distance as usual.
- [ ] **Goblins scavenge flint.** A goblin walking over a flint drop picks it up as ammo.
  *Hook:* `DropManager`'s multi-collector (`DropCollector`) with goblins as collectors that take
  only flint, capped at `GoblinStoneMax`.
- [x] **Sneaking shortens the notice range.** `MobTargets.Nearest` only picks targets inside `MobTargets.NoticeRadius` (`MobNoticeRadiusBlocks`, times `MobSneakNoticeFactor` while `IMobTarget.IsSneaking`; the camera's crouch, a `ServerPlayer`'s cosmetic flag) (`MobNoticeTests`, `mob-notice.json`).
- [x] **Slime merge.** `SlimeManager.MergeTouching` over `SlimeMerge`: two touching living slimes of one size below the largest, both older than `SlimeMergeCooldownSeconds`, become one forced spawn of the next size (`SlimeMergeTests`, `slime-merge.json`).
- [x] **Cave slimes.** `MobSpawner.TryFindCaveSpawn` (a dry, dark, loaded cave floor at least `SlimeUndergroundDepthBlocks` under the surface); `SlimeManager.TryFindSpawn` tries caves first for a player underground (`CaveSlimeTests`).
- [ ] **Goblin stones hit other mobs.** *Hook:* `GoblinManager.Stones` are plain
  `ThrownProjectile`s; test them against the mob list the way the player's throws are, skipping
  the thrower.
- [x] **Ledge caution.** `Mob.IsLedgeAhead` over `LedgeProbe` (live cells, not the heightmap): no step or hop landing more than `MobLedgeMaxDropBlocks` down unless the target is lower (`LedgeCautionTests`, `mob-hazards.json`).
- [ ] **Goblin dodge.** A goblin sidesteps when a charged throw is aimed at it. *Hook:* the
  throw-charge state and the player's facing, read in the goblin's stance. Singleplayer first.

- [ ] **Pack alert.** When a goblin notices the player, goblins within 12 blocks skip their
  first-throw delay. *Hook:* the noticing moment in `GoblinManager`, then one pass over nearby
  goblins; reuses `GoblinFirstThrowDelaySeconds`.
- [ ] **Pack spawning.** A goblin ring spawn rolls a group of 1-3 within 3 blocks of the chosen
  cell. *Hook:* `GoblinManager`'s call into `MobSpawner.TryFindSurfaceSpawn`, with a repeated
  search for the neighbours; every member respects `MobCaps`' natural cap.
- [x] **Torch-safe ground.** `MobSpawner.TryFindSurfaceSpawn` refuses a cell with block light at or over `MobSpawnBlockLightLimit` (`MobSpawner.IsLitByBlocks` over `EntityLight.Sample`) (`TorchSafeSpawnTests`).
- [x] **Size-scaled knockback.** `SlimeSize.ShoveSpeed` (the shove over the body scale) in `Slime.Shove`; a split child's launch is unscaled (`SlimeKnockbackTests`).
- [x] **Despawn fade.** Both managers' despawn branch calls `Mob.BeginDying()` without loot, so the mob plays its death pose and leaves `LivingCount` (`mob-hazards.json`).
- [x] **Giant slime.** `SlimeMaxSize` 4; `SlimeSize.Roll` takes the last step at `SlimeMaxSizeChanceFactor` of the usual threshold, so it is rarer than size 3 in every light (`GiantSlimeTests`, `slime-sizes.json`).
- [ ] **Goblin torches.** Each goblin may carry torches, which it holds in its off hand at night
  while the main hand still holds and throws the flint. *Hook:* at spawn a goblin rolls a torch
  count (0 or 1-2) with a chance that is very small by day, a lot higher at night
  (`DayCycle.NightFactor`, eased like the slime size roll) and highest underground
  (`SlimeSize.IsUnderground`'s depth rule, ready for when goblins spawn in caves); the three
  chances and the count go in `WorldGenerationConfig.Goblin*`. A goblin with torches **equips**
  one while it is dark (night or underground): the torch is drawn at the off hand's
  `EntityAnimator.HandAnchor` (`ArmL`, where the flint is `ArmR`) through the `Goblin.BodyEffect`
  matrix, put away by day, and its flame point joins `World.FireCells` (minus
  `FireParticleSystem.SpawnOffset`, as the kiln does) so it lights the area and sheds embers, a few
  blocks of radius like a campfire. **On death the carried torches drop** with the flint and the
  goblin's loot table. The count is saved (an optional `MobData` field, absent = 0, like a slime's
  size) and sent (check first that `NetEntity.ItemId` is unused for mobs; it can carry the count,
  so the client draws the torch). Test: the carry chance is higher at night and below ground than
  by day with the same roll, and a goblin never equips with none.
- [x] **Loot toss.** Every death drop goes through `DropManager.SpawnAt(id, pos, MobLoot.RandomToss())`, an up-and-out launch tuned by `MobLootToss*` (`MobLootTests`, `mob-hazards.json`).
- [x] **Size-scaled hops.** `SlimeSize.HopLift` / `HopReach` / `WindupScale` raise the body scale to the `SlimeHop*Exponent` knobs, read by `Slime.HopVelocity` and the crouch time (`SlimeHopTests`).
- [x] **Lose interest.** `Mob.PickTarget` = the notice rule plus a per-mob `MobInterest`: sight checked every `MobSightCheckSeconds`, dropped after `MobLoseInterestSeconds` without it, then the mob wanders toward `Mob.WanderGoal` and idles out through `Mob.ShouldIdleDespawn` (`MobInterestTests`, `mob-notice.json`).

## World

- [ ] **Fallen logs.** 3-5 oak log cells lying along X or Z on the forest floor. *Hook:*
  `WoodState.Detached` cubes (they never fell, weld or feed leaves), placed per cell hash in the
  stamp step like `CottonGenerator`. Like trees they sit above `height`; the far LOD shows none.
- [ ] **Boulders.** A rare 2-4 block blob of stone or cobblestone on the forest and taiga floor.
  *Hook:* one candidate per coarse cell hash (the `PlantClumps` cell pattern), stamped per chunk
  and clipped at its border; no far-LOD bump.
- [ ] **Reeds at shores.** A cross-model plant in clumps on the columns at the water's edge.
  *Hook:* `PlantClumps` with its own spec, gated on `height` against the column's `WaterLevel`;
  `needsSupport`, so a flow washes it away.
- [ ] **Lily pads on ponds.** A flat pad on still lake surfaces, not the ocean or rivers.
  *Hook:* a worldgen-only stamp on the cell above a lake's top water cell. `needsSupport` cannot
  hold on water without a fluid support kind, so leave it off.
- [ ] **Glow mushrooms.** A small mushroom with a dim coloured light on deep cave floors.
  *Hook:* `light.emission` + `light.color` (one of the 8 palette slots), stamped after the carvers
  on cave floors well under `height`.
- [ ] **Stalactites and stalagmites.** Stone points hanging from cave ceilings, with stalagmites
  rising from the floor under them. *Hook:* one model block each, `needsSupport` with the new
  `support: "above"` for the stalactite (build the hanging lantern first) and `below` for the
  stalagmite, stamped after the carvers so a stalagmite stands under its drip point.
- [ ] **Snow on canopies.** Taiga spruce crowns get snow on their top leaves. *Hook:*
  `SnowGenerator` already runs last; let it treat a leaf top as ground in a cold column.
- [ ] **Desert well.** One rare hand-stamped structure: a sandstone ring around water. *Hook:* a
  per-cell hash, stamped clipped to each chunk the way trees cross chunk borders; no template
  system.
- [ ] **Geode.** A rare hollow sphere deep in rock, lined with a new crystal block. *Hook:* a
  stage after the carvers (it adds blocks, which the carvers never do). A home for `P-28` /
  `P-47`.
- [ ] **Meteor crater.** A rare bowl lined with obsidian. *Hook:* a hashed site that cuts the
  heightmap the way `TerrainGenerator.TryLakeSite` does, so the far LOD agrees.
- [ ] **Oasis.** A rare green pocket deep in the desert: a pond ringed by grass, with denser
  flora and its own tree. *Hook:* a hashed site like `TryLakeSite`, allowed only where
  `SurfaceWaterGate` keeps ordinary lakes out today, carving its pond into the heightmap and its
  water level into `ColumnData`. The far LOD wears sand on every desert cell, so the oasis surface
  needs its own column mark for the LOD to show grass.
  - [ ] *Sub-step (flora):* reeds (above), tall grass and flowers inside the site, through
    `PlantClumps` and the existing generators, gated to the oasis disc.
  - [ ] *Sub-step (palm tree):* a `palm` species file: a tall, gently leaning, branchless trunk
    with a fan crown at the top. Likely a new `IGrowthLogic` (no forks until the top) and a fan
    leaf cluster shape, with tiles from `tools/generate-tree-textures.ps1` like the baobab's.
    `worldgen.biomes` cannot tell an oasis from the desert, so the oasis stamp plants it (or a
    new oasis gate in `TreeWorldgenRules`).

- [ ] **Mountain snow caps.** Peaks above a height get snow even in warm biomes. *Hook:*
  `SnowGenerator`'s cold weight gets an altitude term over `MountainRockHeight`, and the far LOD's
  snow rule must read the same function; a pure-function test pins that the two agree.
- [ ] **Gravel and sand pockets.** Blobs of gravel or sand inside stone, so mining can cause a
  small collapse. *Hook:* a stage after the carvers (like `LavaGenerator.Stamp`) with a 3D noise
  gate and a depth band. They are `falls` blocks, so `FallingBlockManager` handles the cascade.
- [ ] **Flower meadows.** Dense flower patches in open forest. *Hook:* the `PlantClumps` rule with
  a larger cell and radius than cotton, gated by `TerrainGenerator.ForestOpenness` so they appear
  only in the open stretches; reuses the existing flower block.
- [ ] **Dead trees.** Leafless trunks scattered through the desert and frozen plains. *Hook:* a
  `dead_tree` species file reusing the oak wood and growth logic, with `leaves` omitted (supported
  for leafless species) and a sparse `worldgen` block listing the desert and frozen biomes.
- [ ] **Ruined pillars.** Rare broken stone-brick columns, 2-4 blocks tall, with some bricks
  missing. *Hook:* a per-cell hash like the desert well, stamped clipped to the chunk. Needs stone
  bricks.
- [ ] **Hot springs.** A small pond in cold biomes that does not freeze, with steam rising. *Hook:*
  a hashed site like `TryLakeSite` gated to `ColdWeight`, exempt in `SnowGenerator`'s freeze;
  steam from a dedicated `FireParticleSystem` instance fed from water-top vent cells, like the lava
  vents. The far LOD draws it as water.
- [ ] **Underground pools.** Water pockets on cave floors between sea level and the lava band.
  *Hook:* a mirror of `LavaGenerator.Stamp`: a patch mask plus a per-cell roll, above `LavaMaxY`
  and buried under terrain, with source water; the fluid wake path already handles breaches.
- [ ] **Climate-scaled grass.** Tall grass is denser in wet climates and sparse in dry ones.
  *Hook:* the density roll in `FoliageGenerator` reads wetness from `Climate` /
  `ColumnData.CornerWetness`.
- [ ] **Cairns.** A small cobblestone stack on mountain tops as a landmark. *Hook:* a per-cell
  hash, placed only on a local height maximum above `MountainRockHeight`, 3 blocks tall with a
  one-block hat. A slice of `P-58`.
- [ ] **Sea stacks.** Rock pillars rising out of the shallow ocean near the coast. *Hook:* a hashed
  site that raises the heightmap in `TerrainGenerator.Compute` within a small radius (so the LOD
  agrees), only where the column is Ocean and shallow; the top is stone with a sand skirt.

## UI

- [x] **Hotbar item name fade.** `HotbarNameFade` over the hearts; `select` scenario op (`hotbar.json`).
- [x] **Floating damage numbers.** `DamageNumberPool` + `DamageNumberRenderer`; a client reads the health drop between snapshots (`damage-numbers.json`).

- [ ] **Crosshair hit marker.** A brief X flash on the crosshair when a hit lands. *Hook:* the
  connect signal the hit-stop uses, drawn beside `UIRenderer.DrawCrosshair` and faded through
  `MathUtils`.
- [ ] **Crosshair spreads while charging.** While a throw charges, the centre dot stays put and
  the four outer pips slide outward with the charge, snapping back on release. No new art:
  `assets/textures/crosshair.png` is 16x16 with five separate pieces (a 4x4 centre dot and four
  pills, an empty row and column between each). *Hook:* five meshes cropped with `UvRect`
  (`Primitives.CreateRectangle(..., uv:)`, as the hearts are), the four arms offset by the eased
  charge in `UIRenderer.DrawCrosshair`.
- [ ] **Mining progress ring.** A circle around the crosshair fills as the targeted block breaks.
  *Hook:* the break loop's damage over `Mining.HardnessOf`, drawn as segments in `UIRenderer`;
  hidden at zero and in creative.
- [ ] **Look-at label.** Looking at a block always shows what it is: its name and a small 3D
  render of it under the crosshair, the name in red when the held tool cannot mine it. A block the
  player has **not obtained yet** shows the name "Unknown" and the render as a flat dark silhouette;
  creative mode always shows everything; with `X` on, the label adds debug lines (block id, state
  byte, sun and block light, the chunk and local cell). Hidden with no target. *Hook:* the
  selection raycast, `BlockType.DisplayName`, `Mining.CanMine` and `BlockCubeCache`'s cube mesh
  drawn through the UI pass (the silhouette is the same mesh with a flat colour and no texture).
  "Obtained" is a per-player set of discovered item ids, filled in `Inventory.Add`, saved in
  `player.json` (an optional field, so no new file and an old save reads as empty) and carried in
  the `InventoryNetCodec` snapshot (the save JSON shape), so a server's player and a client agree.
  A block without an item (`hasItem` false: pebbles, piles, snow layers, kiln parts) is always
  known. Test: a block is unknown until its item has been added, and a creative player always
  knows.
- [ ] **Chat fades when closed.** Today the last lines stay on screen for good. *Hook:* an arrival
  time per `ChatMessage`, and `UIRenderer.DrawChat` fading each line (and its backdrop) with
  `MathUtils.HoldThenFade` while the chat is closed; opening it shows them all again.
- [ ] **`/time` chat command.** `/time day`, `/time night` and `/time <seconds>` set the clock.
  *Hook:* a small command table checked in `Program.SubmitChat` before `SendChat` (a line starting
  with `/` is never echoed as chat), then `World.SetLightingTime`. `day` and `night` are fixed
  points of the cycle (noon and midnight), `<seconds>` uses `VOXEL_TIME`'s units (a cycle is
  `DayLengthSeconds`), and an unknown command answers with a system line. On a server the line
  goes up as `ChatSend`, `GameServer` runs it for an operator only (`OperatorList`) and sends a
  `TimeSync` at once. A slice of `U-7`; the next command is one more table entry.
- [ ] **Day toast.** "Day 4" fades in at dawn. *Hook:* `DayCycle` on the lighting clock, with a
  `HotbarNameFade`-style hold-then-fade.
- [ ] **New item toast.** The first pickup of an item shows "New: Cotton". *Hook:* the discovered
  set of the look-at label: `Inventory.Add` of an id not in it adds it and raises the toast (never
  at world load, so a restored inventory does not toast).
- [ ] **Screenshot key.** F2 saves a PNG beside the saves (`%APPDATA%/CSVoxelEngine/screenshots/`).
  *Hook:* the scenario `screenshot` op's capture. A slice of `M-20`.
- [ ] **Hide-HUD key.** F1 toggles the HUD. *Hook:* the scenario screenshot's `hideHud` path.
- [ ] **Tab player list.** Holding Tab in multiplayer lists who is online. *Hook:* the
  `PlayerList` data `World.SyncRemotePlayers` already keeps.
- [ ] **Chat commands: help, tp, give.** `/help` lists the commands, `/tp x y z` teleports and
  `/give <item> [count]` grants items. `/tp` and `/give` are creative-or-operator only. *Hook:* the
  same command table as `/time`. `/tp` sets the camera position in singleplayer and sends a
  `PlayerCorrection` on a server (`ServerPlayer.IssueCorrection`); `/give` takes the creative-grant
  path with the item resolved by name. A bad argument or unknown item answers with a system line.
  Tab-completion for these lives in `TODO.md` (`U-22`).
- [ ] **Item tooltips.** Hovering any slot or row shows a panel with the display name, kind, tool
  power, damage, durability left and fuel time when present. *Hook:* the fields already on `Item` /
  `ItemStack`, drawn by `UIRenderer` after the panel. A slice of `U-1`.
- [ ] **Inventory sort.** A button on each tab sorts it by name, then count. *Hook:*
  `InventoryView` and the pool order in `Inventory`. Singleplayer first: on a server it needs a new
  `NetInventoryAction`.
- [ ] **Number-key slot assign.** Pressing 1-9 while hovering an inventory row equips that item into
  that hotbar slot. *Hook:* the keyboard path in `Program` while the panel is open, calling the
  existing equip handler the hotbar click uses.
- [ ] **HUD scale.** A settings slider scales the HUD from 75 % to 150 %. *Hook:* `UiCanvas`'s
  uniform scale, a `SettingsMenu` slider row and `GameSettings`; every layout already goes through
  the canvas. A slice of `U-20`.
- [ ] **Sprint toggle option.** A setting that makes the sprint key toggle instead of hold. *Hook:*
  a `SettingsMenu` toggle row and the sprint read in `Program` / `Camera`; the toggle clears on
  stop or on a sneak.
- [ ] **Worlds list details.** Each row also shows the last played date and the seed. *Hook:*
  `WorldInfo` already holds the timestamps and seed; a second line in
  `UIRenderer.DrawMainMenu`'s row.
- [ ] **Death message.** On death a system chat line reads "Player was slain by Goblin" (or "fell",
  "burned"). *Hook:* the damage source at the lethal hit (mob type via `IMobTarget.TakeHit`, fall
  via `FallTracker`, lava) feeding `ChatKind.System`; broadcast on a server.
- [ ] **Inventory-full warning.** A drop in pickup range that the full pool cannot take shows
  "Inventory full" over the hotbar **once**: it fires on the transition into the blocked state and
  stays silent until the pool has had room again (a slot freed or an item count fell), however many
  drops hover nearby or how long they stay. *Hook:* the "one nobody can take lets go" branch in
  `DropManager`'s collector for singleplayer; a client detects it itself from its inventory mirror
  and the drops near it in `EntityList`, so no new message. A `HotbarNameFade`-style hold-then-fade.
  Test: the warning fires once per full episode and re-arms only after room appears.
