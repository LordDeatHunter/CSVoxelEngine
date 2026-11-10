# Skeleton, battle tower, sandworm, icebergs

The skeleton, the battle tower with its king, the sandworm boss and the icebergs.

### Skeleton

Ambiently, `SkeletonRattle` (`src/Entities/TowerMobs/`, ticked by `World`) plays a random `skeleton_rattle_N` clip every
12-35 s (`AudioConfig`) while a live skeleton is within 24 blocks of the camera; the sound is not positional. A landed hit plays a `skeleton_hit` clip and a killing one a `skeleton_death` clip, via
`Mob.HitSoundGroup`/`DeathSoundGroup` (null for silence on other mobs) read by `MobStrikes.ShowLandedHit`.

The battle tower's undead (plans/BATTLE_TOWER.md `BT-3`): one `Skeleton` class (`src/Entities/TowerMobs/`), a
`RealmMob` like the hell and sky mobs but with no realm base, run by `SkeletonManager` (`src/Simulation/Tower/`) on the
shared `RealmMobManager<TMob>` frame, joined to the world's `MobCaps`, in two kinds: the **archer** (`skeleton.json`,
"Skeleton Archer", hp 40) and the **warrior** (`skeleton_warrior.json`, "Skeleton Warrior", hp 56), its `Role`
(`SkeletonRole`) named by the kind (`Skeleton.RoleOf`). Their models, palette skins and the `bone` sprite come from
`tools/generate-skeleton-model.py` (deterministic, `--check`; it loads the hell script's rig, UV, PNG and clip machinery
like the sky script), both built from one set of proportions (`bones`): the archer lanky and tall (32 px, long thin
limbs, a narrow ribcage) holding a bow modeled as cubes under `ArmL`, the warrior shorter and bulkier (28 px, a wide
deep ribcage, thick limbs) empty handed in the model: its copper dagger is the real `copper_dagger` item, drawn from its
sprite at `ArmR` by the entity file's `held` (grip in the fist, the blade turned onto -Z pointing forward, its flat to
the sides; see "Entities"), so it swings with the arm through every clip and casts a shadow; both humanoid
(head and arms under the torso, checked by `Skeleton.ValidateContent` over `EntityContract.RequireHumanoid`) with the same
clips: idle, walk, `melee` (an `impact` event), `shoot` (a `release` event) and the common hurt, death and spawn. Both
share `MobConfig.SkeletonHeight` as their body box.
- **Behavior** (loot bones and sometimes arrows; the warrior's own table `loot/skeleton_warrior.json` adds a 5% chance at the `copper_dagger` item it carries, a `slice` weapon, `CopperDaggerLootTests`): in `MobConfig.SkeletonMeleeReach` and off its cooldown it strikes (the
  `melee` clip), and at its `impact` the manager bills `MeleeDamage` with `MeleeKnockbackSpeed` to every non-immune
  target still in reach (`Skeleton.InMeleeReach`): the warrior's dagger stab (`SkeletonMeleeDamage`, `SkeletonMeleeCooldownSeconds`)
  or the archer's weaker punch (`SkeletonArcherPunch*`). The **warrior** never draws a bow: out of reach it walks in. The
  **archer**, inside `SkeletonBowMinRange..SkeletonBowMaxRange`, grounded, off its jittered cooldown (a fresh one waits
  `SkeletonFirstShotDelaySeconds`) and with a clear line from `ShotOrigin` to the chest (`RealmMob.SeesTarget`), draws
  (`shoot`); at its `release` (`TryTakeShotRelease`) an **arrow** leaves on `GoblinAim`'s low arc at
  `SkeletonArrowSpeed` (the band stays inside `v^2/g`). Nearer than the band it backs away (punching a target that keeps
  up), farther or without a line it walks in.
- **Half and half, and armored** (`SkeletonManager.Spawn(position, tier)`, every pile wave and king summon): archers and
  warriors take turns, so a tower's waves are half each, and each rolls an **outfit** (`SkeletonOutfit.Roll`, one of 16
  head/chest/legs combinations of `SkeletonArmor` None/Leather/Copper): a warrior wears leather with
  `SkeletonWarriorLeatherChance` and a copper piece with `SkeletonWarriorCopperChance`, an archer a single leather piece
  with `SkeletonArcherLeatherChance`. Each piece takes `SkeletonLeatherArmorShare`/`SkeletonCopperArmorShare` off every
  blow (`Skeleton.ScaleDamage`, never below 1) and is drawn as a tinted shell over its rig part (`RiggedMob.ArmorPieceOn`,
  `MobPasses.DrawArmorShell` on `PlayerRig.BuildArmorMesh`, the player's own shells: leather in the leather piece's
  sprite color, copper in the copper ingot's; a helmet leaves its front open so the skull shows; the arms stay bare). The
  pieces are a look, not items (no copper armor exists, and the id budget is short): they never drop. The outfit is saved
  (`MobData.Outfit`) and rides the wire with the role in the tier byte (`Skeleton.PackLook`, protocol 31), so a mirror
  draws the same skeleton. A scenario's `spawn` names the kind (`skeleton` or `skeleton_warrior`) and rolls the outfit.
- **Arrows** are `ThrownProjectile`s of the `arrow` item (`throwAligned`, so they fly point first like the spear, which
  a player may also hand-throw; `recipes/arrow.json` makes four from a branch, a flint and a bone), flown by the shared
  `MobMissiles` (`SkeletonManager.Arrows`, merged into `ThrowAction.RefreshProjectiles`): a non-immune target touched
  takes the skeleton's `ArrowDamage` and a small shove, an immune one stops the arrow unhurt.
- **Tower difficulty** (`TowerDifficulty`, GL-free): the tier is the tower floor index (0..`MobConfig.TowerMaxTier`,
  `TowerSite.Tier(k)`), and `1 + tier * TowerDifficultyPerTier`, capped at `TowerDifficultyMaxMultiplier`, scales its
  pool and both damages through `LayerDifficulty.ScaleBy`; `PackSize(tier)` is the pile's wave hook for `BT-4`. The tier
  is a constructor argument fixed for life: a mirror cannot derive it from where it stands, so it is saved
  (`EntitySaveData.MobData.Tier`, absent reads as 0) and sent (the low bits of the entry's `ItemId`, see protocols 25 and
  31), clamped on both.
- **Forced spawn only**: `SkeletonManager.Spawn(position, tier)` is the one way a skeleton appears (the `spawn`
  scenario op's `tier`, a save restore, a client mirror, a tower's skeleton piles) and returns null at the hard cap
  (`MobCaps.ForcedRoom`); there is no ring spawn. It keeps the shared distance and idle despawn, past a generous
  `SkeletonDespawnRadiusBlocks`; a pile's skeleton is **bound** to its floor (`Skeleton.Floor`, a `TowerFloorRef`, saved,
  never sent), and its despawn is reported so the floor owes it; one idle too long crumbles back into its floor's piles
  (see "Battle tower"). `visual-examples/tower-idle-shrapnel.json` (`VOXEL_TIME=147`) films a hurt wave from a spent
  pile, the floor empty after it idled away, one of it risen again at full health (a spent pile raises only owed
  skeletons), then the king's slam and its bone shrapnel from the shoulder and from above.
- **Undead**: `IsUndead` is true, so `MobStrikes.StrikeMob` adds the striker's `undeadDamage` gear stat
  (`Combat.VersusUndead`) to every blow on it (`UndeadStrikeTests`).
- **Two attack clips over one count**: `AttackVariants` 2 (0 the rake, 1 the shot), so a mirror plays the right clip
  from the replicated count (see "A mirror's attack clip").
- **Plumbing**: `World.Skeletons`, `MobDirector` (update, damage billing, status block actions, `RefreshMobs`, the
  `spawn` op name `skeleton` with its optional `tier`), `ThrowAction` (the arrows), `MobPasses.RenderRealmMobs` and the
  near shadow cascade (`ShadowPass.RenderShadows`' `skeletons`), `entities.json` by kind name with the tier, the
  `EntityList` kind (protocol 25) and `ClientMirror`'s reconcile, and the item debugger's ENTITIES tab (tier 0). Tests:
  `SkeletonTests`, `SkeletonManagerTests`, `UndeadStrikeTests`, the realm round trip in
  `WorldLifecycleTests.EntitiesRoundTripThroughSaveAndReload`, `RealmMobMirrorTests`,
  `NetworkEntitySyncTests.ASkeletonMirrorTakesItsTierFromTheSnapshotAndIsReplacedWhenTheTierDiffers` and the loopback
  `GameServerTests.SkeletonsReachTheClientsEntityListByKindWithTheirTier` (tier, role and outfit); the variants'
  rules in `SkeletonTests` (a warrior never draws a bow and stabs harder, an archer backs away, the look byte round
  trip, armor softening, the outfit rolls by role) and `SkeletonManagerTests.OrdinarySpawnsAreHalfArchersAndHalfWarriorsEachOfItsOwnKind`.
  `visual-examples/skeletons.json` (`VOXEL_TIME=147`) films them idle, shooting and raking in an open forest clearing.
- Known gaps: the arrows, like the goblin stones and hell bolts, are not on the wire (a client sees only the damage);
  skeletons are not drawn in the water mirror.

### Battle tower

The battle tower's generator (`WorldGen/Structures/Tower*`) and its skeleton are above ("Structure templates and ruins",
"Skeleton"). The battle tower is done (`BT-1`..`BT-10`); plans/BATTLE_TOWER.md keeps the decisions, the per-phase
reference and the measured costs ("Costs (BT-9)": the sky-cover mesh build, the tower worldgen and the collapse's
per-frame step, with their ceilings). This section is the **floor locks** (`BT-4`), the **chests** (`BT-5`), the **roof
altar with the Skeleton King** (`BT-6`) and its **loot** (`BT-7`), the **collapse** (`BT-8`) and the **dark interiors**
(`BT-10`). The tower's last protocol was 27 (the altar byte and the king; the collapse needs no wire change); 28
(the cave biome byte, CB-3) and 29 (the cave mobs, CB-4, the current one) came after it. The tower's
content ids left 244 of the 255 used, before the cave ores took 3 more (see "Budgets" under "Layers").
- **Dark interiors (BT-10)**: the floors get no sun. The three palette bricks carry `coversSky`, so a mesh build over a
  tower chunk starts every sun seed above the slab over it ("Sky cover (BT-10)" under "Chunk meshing"); a floor is lit only
  by its template lanterns and the torches a player brings, identically in singleplayer, on a server and on a client. The
  ruin's rubble does not cover, so the collapse's remesh lets the sun back in.
- **The sealed roof**: the parapet stands `TowerParapetHeight` (4) over the roof slab, solid but for its merlon layer, and
  a forcefield dome closes it: `TowerDome` (GL-free, pure) is half a spheroid standing on that merlon layer, as wide as the
  tower (the outer wall's `r² + r` edge, so its base is the tower's disc) and `TowerDomeHeight` tall over the center, and
  `TowerGenerator.Merge` writes `tower_forcefield` (`TowerPalette.ForcefieldBlock`) on every air cell of its 6-tight
  shell (`TowerDome.Holds`: inside, with a face neighbor outside), so walls and the roof's dressing always win and the
  point query still equals the stamp. The roof is reached only by the stair through every floor's door: a player can
  neither climb in from outside nor jump off mid-fight. The block is worldgen-only, `unbreakable`, solid, `translucent`,
  `cullSelf`, passes light and covers no sky (the roof stays sunlit, the dome casts no shadow); the world shader draws it
  as a faint shimmering film ("Forcefield shimmer" under "Rendering"). `TowerSite.TopY` is the dome's apex (or the altar
  dressing, if ever taller) and `TowerPlacement.MaxHeightAboveGround` counts the dome, so the stamp band, the chunk range,
  `TopReaching`, the cloud-slab rule and the collapse scan all include it. The collapse clears it first (it is above every
  stump, and the scan runs top-down), and `TowerCollapseEffects.ShowsCollapse` skips a translucent block, so it winks out
  with no chips. Tests: `TowerShellTests.TheParapetAndTheDomeSealTheRoofFromTheOutside` and
  `EveryAltarDressingStaysUnderTheDomeAtEveryTurn` (a flood fill over the generated tower with every breakable cell and
  pile gone), `TowerPlacementTests.TheDomesForcefieldStopsBodiesButLetsTheSunOntoTheRoof`;
  `visual-examples/tower-dome.json` films it. Known gap: a tower in chunks saved before the dome keeps its low parapet and
  open roof there (a tower half saved and half regenerated has part of a dome).
- **No surface spawns in a footprint**: `MobSpawner.TryFindSurfaceSpawn` refuses a ring column inside a tower's footprint
  box (`TowerPlacement.Meets`, a cached site verdict, gated by `TowersEnabled` and skipped for a column a world source
  supplies), since the dark ground floor would otherwise take slimes and goblins; the tower's skeletons own it.
  `TowerFootprintSpawnTests`.
- **Two worldgen-only, unbreakable blocks** (`hasItem: false`, `loot: "none"`), both from the single tower cell function,
  so the point query still equals the stamp. `tower_door` (a solid, cutout, `cullSelf` portcullis `cube_all` that passes
  light) fills the headroom over the two door half-steps of every floor (`TowerSite.DoorCells(k)`, stamped by
  `TowerShell.CorridorAt` through the closed-form `TowerShell.IsDoorStep`): door `k` seals floor `k` from floor `k + 1`, the
  last one the roof, and the ground entrance stays open. `skeleton_pile` (non-solid, a low hitbox, never replaceable) is
  the `S` palette key of the three `tower_floor_*.json` templates, found per floor by `TowerSite.PileCells(k)` (a template
  scan, kept by its consumer). Both tiles are placeholders from `tools/generate-tower-lock-textures.py` (USER_TODO.md).
- **`TowerManager`** (`Simulation/Tower/`, owned by `World` as `World.Towers`, main thread, ticked by `MobDirector.Update`
  right after the skeletons on authoritative worlds only) keeps one `TowerRecord` per tower keyed by its placement cell,
  with a `TowerFloorState` per floor. A record is materialized lazily: every `MobConfig.TowerScanSeconds`, a target whose
  eye chunk is loaded and who stands in no record asks `TowerPlacement.TryFindAt` (the generation workers have usually
  cached the verdict already). `TowerRecord.Build` takes explicit floor data, which is the test and loopback seam.
- **Piles** (`SkeletonPileManager`): on an awake floor (floor 0, or the floor below's door open), a pile whose cell holds
  the pile block raises a wave of `TowerDifficulty.PackSize(k)` skeletons at tier `k` (`SkeletonManager.Spawn`, around the
  pile by `TowerRules.SpawnPoints`) when a **non-immune** target stands within `SkeletonPileTriggerBlocks` on that floor's
  band, every `SkeletonPileWaveSeconds`, for `SkeletonPileWaves` waves, then it is depleted. A pile never raises past `TowerDifficulty.PileNearLimit(k)` living skeletons within
  `SkeletonPileNearRadiusBlocks` of it (`SkeletonManager.LivingCountNear`; `SkeletonPileNearLimitBase` 5 on floor 0, plus
  `SkeletonPileNearLimitPerTier` 2 per floor): a wave is trimmed to the room left and waits for kills when there is none, and
  owed respawns obey the same limit. **Opening a chest alarms the nearby piles** (`TowerManager.AlarmPilesNear`, called where the chest opens: `ChestInput.Open` in singleplayer, `ChestSessions.OnOpen` on the server): every pile with a wave left on the chest's awake floor within `SkeletonChestAlarmBlocks` is `PileState.Alarmed` (never saved) and raises its next wave at once, ignoring its trigger range, its cooldown and the near limit (only the hard cap still holds). A wave counts only if one
  skeleton rose (the near limit and the hard cap keep it for later). A pile in an unloaded chunk freezes; a loaded cell without the pile
  block counts as depleted.
- **Bound skeletons are never immortal.** Each raised skeleton is tagged with its `TowerFloorRef`. It despawns like any
  mob, and past `TowerSkeletonLeashBlocks` from the tower's center the manager releases it (`SkeletonManager.Release`);
  either way `SkeletonManager.TakeReleased` reports it once and the floor's **owed** count grows. Owed skeletons are raised
  again at any of the floor's piles (depleted ones too) the next time a target triggers one. So a half-cleared tower far
  away holds no cap room, and a skeleton that wandered off can never soft-lock a door. A killed one is never owed.
- **Idle skeletons crumble back into their piles.** A bound skeleton that has chased nobody (no target noticed, or its
  sight of one lost: `MobInterest.IdleSeconds`) for `MobConfig.SkeletonPileReturnIdleSeconds` reads
  `Skeleton.ReturnsToPile`, and `SkeletonManager` releases it right after the shared frame: its death clip, no loot,
  reported through `TakeReleased`, so its floor owes it like a leashed one and the piles raise it again, a fresh
  skeleton at full health, for the next target. The door count never drops (it moves from living to owed in one
  frame). An unbound skeleton (a scenario's, a king's summon) has no pile and keeps only the shared despawns. Authoritative
  only: a client's mirror just sees it die. `SkeletonManagerTests` and
  `TowerManagerTests.AnIdleBoundSkeletonCrumblesBackToItsPileAndRisesAgainAtFullHealth`.
- **The door rule** (`TowerRules.DoorMayOpen`): door `k` opens once door `k - 1` is open (always for `k = 0`), every pile of
  floor `k` is depleted and **alive = living + owed + pending** is 0, where living counts the tagged skeletons in the
  manager, and pending the floor's tagged entries still waiting in the save restore
  (`WorldPersistence.PendingTowerSkeletons`); a restore that times out credits its tagged entries to owed
  (`TakeDiscardedTowerSkeletons`), so a reload never opens a door early. Opening is permanent.
- **The writer** (`TowerBlockWriter`) is the one sanctioned path past `unbreakable`: `Chunk.CollapseWriteAt` (`TryClear` to air,
  `TryReplace` to any block and state, a replaced chest spilling through its `spillChest` hook), then
  every edit-path hook (player-placed unmark, `MarkAreaDirty` over the light radius, fluid and leaf-decay schedules, a falls
  block toppling above, the LOD notify, the support pop, the canopy branch), capped at `TowerMaxBlockWritesPerFrame`; the
  raw write journals the cell to clients as `BlockChanges`. Each frame a sweep writes the depleted piles and the open
  doors' remaining cells: a written or gone cell leaves the set, an unloaded one stays for later and never blocks the rest,
  and the cap ends the sweep. BT-8's collapse writes through it too, after the sweep and under the same cap.
- **Saves and the wire**: `stations.json`'s `Towers` (`StationSaveData.TowerData`: the cell, and per floor the waves left
  per pile in `PileCells` order, the door bit and owed), restored before the entities through `TowerPlacement.TryGetSite`
  (an entry whose site, floor count or pile count no longer matches is dropped and counted). Only site-built records are
  saved. Every record rides the end of `StationState` (protocol 26; 27 appends its altar state byte). The client's manager has no writer: it never ticks,
  never raises a skeleton or opens a door, and each snapshot replaces its `TowerRecord.Mirror`s; door and pile cells reach
  it through `BlockChanges`.
- **Look-at line** (`TowerManager.Detail`, `TowerReadout`, on both sides): a pile reads "Floor 2: 3 piles, 4 skeletons", a
  sealed door "Floor 2 sealed: clear floor 1 first", "... 2 piles left" or "... 5 skeletons left" (floors counted from 1).
- **Scenario**: the `towerclear` op (`World.ClearTowerFloor`) clears a floor at once and the `towerdefeat` op
  (`World.DefeatTower`, `TowerManager.DefeatAt`) marks the king killed, so the real collapse runs;
  `visual-examples/battle-tower.json` (`VOXEL_TIME=147`) films the tower, the piles, a sealed and an opened door, floor 1
  dark and then lit by three torches, floor 0 under its lanterns alone, and the ruin from outside, inside and above. Tests: `TowerLockBlockTests`, `TowerShellTests`, `TowerPlacementTests`, `TowerRulesTests`,
  `SkeletonPileManagerTests`, `TowerBlockWriterTests`, `TowerManagerTests`, `TowerManagerSaveTests`,
  `StationNetCodecTests`, the skeleton despawn cases of `SkeletonManagerTests`, the bound skeleton in
  `WorldLifecycleTests` and the loopback `GameServerTests.AClientSeesATowerDoorOpenOnceItsFloorIsCleared`.
- **Known gaps**: only a non-immune target raises piles, so a creative player who is not flying cannot clear a floor (a
  flying one noclips past the doors); a door's support pop on a server runs with the creative server camera's context and
  may drop nothing; a tower whose chunks were saved before BT-4 keeps air in its door cells and has no piles, so the
  manager still tracks it but its doors read gone and open at once; the far LOD shows none of this; a pile whose nine
  spawn columns are all walled in retries forever.
- **Chests (BT-5)**: `tower_chest` (worldgen-only, breakable, spills like a ruin chest; placeholder tiles reused from the
  underground ruin chest, USER_TODO.md) stands 2 per floor in each `tower_floor_*` template and 1 on the roof in
  `tower_altar`, the tower's treasure. `TowerGenerator.TemplateCell` ORs the floor tier `TowerSite.Tier(k)` (the roof's:
  `TowerSite.TreasureTier`, one above any floor) into its state as `ChestProfile.TierState`, so the point query equals the
  stamp. Its `chest.tiers` maps the tiers onto `tower_floor_low`/`_mid`/`_high`/`_top` (two floors each) and
  `tower_treasure`, all nesting `tower_supplies`, richer going up (a gear rarity band one step rarer a table, more gear,
  the ingot ladder tin..wrought iron, scrolls and mana draughts). Like any ruin chest it holds no state until first opened
  or broken. Hooks: `TowerGenerator.ChestCells(s, k)`/`AltarChestCells(s)`. Tests: `TowerChestTests` and the chest cases
  of `TowerPlacementTests`. Known gap: towers in chunks saved before BT-5 have no chests. The collapse (BT-8) keeps floor 0's
  chests and spills every higher one's contents (an unopened chest rolls its loot first), so nothing is lost.
- **The altar (BT-6)**: `tower_altar` (worldgen-only, `unbreakable`, `loot: "none"`, a `cube_bottom_top` of
  `stone_bricks` with the placeholder `tower_altar_top` tile from `tools/generate-tower-lock-textures.py`, light 7 in the
  default color) stands at `TowerSite.AltarCell` (the center, `RoofY + 1`): the `tower_altar` structure template leaves that
  cell free (`StructureLoader`'s rule) and `TowerGenerator.Merge` writes `TowerPalette.AltarBlock` there, so the point query
  still equals the stamp. Like piles and doors it is recognized by its **palette id**, never a JSON flag: a worldgen-only
  tower part means nothing outside its `TowerRecord`, so a generic flag would only move the same id check.
  `TowerRecord.AltarCell` (center, top floor's `TopY + 1`) equals the site's, so test records and mirrors have one too.
- **Altar state** (`TowerRecord.AltarState`, a `TowerAltarState`): Dormant, Ritual (with `RitualLeft`), Awake, Defeated.
  `TowerRules.AltarVerdictOf` is the one rule, first match wins: Defeated, Awake, Ritual reads Stirring, any floor whose
  `DoorOpen` is unset reads Sealed (the dome seals the roof, but a creative flyer noclips past it), else Begin.
  `TowerManager.JudgeAltar(cell)` answers it without changing anything (NoTower when no record holds the cell as its altar;
  the authoritative side tracks the tower on first use, like `ClearFloor`); `BeginRitual(cell)` sets Ritual only with a
  writer and a Begin verdict, and always returns the verdict, so it is idempotent (a second click reads Stirring). Toasts
  and look-at lines: `TowerReadout.AltarToast` ("The ritual begins...", "The seals still hold", "The ritual is under way",
  "The Skeleton King walks", "The Skeleton King has fallen", "The altar is silent") and `TowerReadout.Altar`, which
  `TowerManager.Detail` shows for the altar block.
- **The right-click**: `BlockInteractions.TryUseAltar` claims a click on the altar block (not while sneaking or holding a
  block, which place against it) as `RightClickOutcome.Altar`, right after the waystone claim in
  `RightClickAction.TryUseSelected`, and calls `World.UseAltar(cell)` (`RightClickAction.LastAltarCell` and
  `LastAltarVerdict`): a client only judges from its mirrored record (a prediction; the chain still raises the usual `Use`
  intent), while singleplayer and the server's replay of that `Use` both begin the ritual through the same call, so
  `IntentReplay` has no altar branch and no message is new. `GameplayInput` toasts the verdict on a fresh press only (a
  held button repeats the use harmlessly).
- **The roof chest**: opening the altar's treasure chest (`TowerGenerator.AltarChestCells`, the Skeleton King chest) calls
  `TowerManager.BeginRitualFromChest` where the chest opens (`ChestInput.Open` in singleplayer, `ChestSessions.OnOpen` on the
  server): the same ritual start as a right-click on the altar, so it begins only on a Dormant altar with every door open.
- **The ritual and the rise** (`TowerManager.UpdateAltars`, called by `MobDirector.Update` right after the king manager on
  authoritative worlds): a Ritual record counts `MobConfig.SkeletonKingRitualSeconds` down, then spawns one king through
  `SkeletonKingManager.Spawn` at `TowerRecord.KingHome` (feet on top of the altar, leash `OuterRadius`, summon tier
  `TowerDifficulty.Clamp(Floors.Length - 1)`) with its rise clip, and turns Awake; a full hard cap retries next frame.
  While a record is in Ritual, `AmbientEffects.UpdateKingEffects` (its ritual half) puffs sparks over the altar and pulses
  a small camera shake by distance (singleplayer and a client, from the mirrored state; never a server).
- **Settling** (`TowerManager.SettleAltars(kings, pendingKing)`, after every `UpdateAltars` and right before the stations
  capture): over Ritual and Awake records, the tower's `SkeletonKingManager.StatusFor(key)` (a `KingStatus`) decides. A
  king at `Health <= 0` (Killed, dead or dying) marks the tower **Defeated the same frame**, before its death has played,
  so no frame or save ever records Dormant for a killed king; a Ritual record whose king is Alive turns Awake; an Awake
  record with no living king (None, or Fading: a despawn keeps its health, since `Mob.BeginDying` never touches it) and no
  entry pending in the save restore (`WorldPersistence.PendingKingFor`) returns to **Dormant**, so the ritual is free again.
  So every target leaving (past `SkeletonKingLeaveRadiusBlocks`), an empty server (the manager fades a king when nobody is
  in play, which the base distance despawn skips) and a player dying and respawning far away all reset the tower, while
  only a kill defeats it. `TowerManager.MarkDefeated` raises the `TowerManager.BossDefeated` event once and
  `TowerRecord.BossDefeated` reads true from then on, which BT-8's collapse reads (polled, not through the event).
- **The Skeleton King** (`skeleton_king.json`, hp 600, not difficulty-scaled, `"boss": true`; its boss table
  `loot/skeleton_king.json` (BT-7) rolls exactly one unique first, the bone-breaker blade, the bone charm or the crystal
  staff, then bones, wrought iron, bronze ingots and, half the time, a modifier scroll or mana draughts;
  `SkeletonKingLootTests`) is a `SkeletonKing`
  (`Entities/TowerMobs/`), a `RealmMob` with `IsBoss` and `IsUndead` true (the `undeadDamage` stat applies) and no floating
  healthbar. Its model and skin come from `tools/generate-skeleton-king-model.py` (deterministic, `--check`, on the hell and
  skeleton scripts' machinery): a 3-block crowned humanoid (`EntityContract.RequireHumanoid`) with a bone club under
  `ArmR`, clips idle, walk, `slam` (`impact`), `volley` (`release`), `summon` (`summon`), hurt, death and a `spawn` that
  rises out of the altar. Three attack variants ride one `AttackCount` (slam 0, volley 1, summon 2; a mirror plays the clip
  by remainder, see "A mirror's attack clip"). Its GL-free `SkeletonKingBrain` (an injected roll) observes the health share:
  phase 2 below `SkeletonKingPhase2Share`, phase 3 below `SkeletonKingPhase3Share`, rising only; the first observation is
  silent (so a restore never re-summons) and each later phase change owes one summon. It chooses, first match wins: an owed
  summon, a slam in reach off cooldown, from phase 2 a rolled summon off cooldown, a volley inside its band with a clear
  line, grounded, off cooldown; phase 3 enrages (shorter cooldowns, faster walk). `SkeletonKingManager`
  (`Simulation/Tower/`, on the shared `RealmMobManager` frame and `MobCaps`, forced spawn only, one king per tower: a tower
  that reads Alive or Killed refuses another) lands the slam as a shockwave (`MobTargets.InShockwave`, the cloud golem's
  rule, `SkeletonKingSlamRadius` round its feet for `SkeletonKingSlamDamage`) plus a burst of
  `SkeletonKingSlamShrapnelCount` bone shards all round from `SkeletonKingSlamShrapnelLiftBlocks`
  over its feet, flying like the bone blade's shrapnel (`BoneShrapnel.RollFragment`; `ShrapnelConfig`'s speeds, gravity
  share and life, which `ThrownProjectile` and `MobMissiles.Launch` take per missile) and dealing
  `SkeletonKingSlamShrapnelDamage` to a target they touch; like every mob missile they never touch a mob, so the king and
  the skeletons are never hurt by them. The slam also **heaves the ground** where the club strikes
  (`SkeletonKing.SlamPoint`, `SkeletonKingSlamReachBlocks` in front of its feet), cosmetic only: the impact (the manager's
  on the authoritative world, the mirror's own clip on a client, `SkeletonKing.AnimateMirror`) leaves a cue
  (`TryTakeSlamLanded`) that `AmbientEffects.UpdateKingEffects` turns into a `GroundHeave` (`Particles/Heave/`, never on a
  server) and a camera jolt by distance: every full-cube ground cell with no full cube over it within
  `JuiceConfig.SlamHeaveRadiusBlocks` pops up and settles back, later and lower the farther it stands, so the ring ripples
  out; `ObjectPasses.RenderGroundHeave` draws a raised, biome-tinted copy of each block (`BlockDebrisSystem.TintAt`) and no
  cell, body, save or wire ever sees it (`GroundHeaveTests`). The volley is a **burst** (`BoneBurst`): from the clip's
  release `SkeletonKingBurstBones` `bone` projectiles (more when enraged) leave the hand one after another over
  `SkeletonKingBurstSeconds` (`SkeletonKing.TryTakeBurstBone`, a dying king's burst stops), each the aimed throw strayed
  at random within `SkeletonKingBurstSpreadDegrees`, `SkeletonKingBurstPitchDegrees` and `SkeletonKingBurstSpeedJitter`
  (`SkeletonKing.ScatterVelocity`), through the same `MobMissiles`
  (merged into `ThrowAction`'s projectile list; a hit by either knocks back `SkeletonKingBoneKnockbackSpeed`), and
  the summon as a ring of skeletons at the home's summon tier (`SkeletonManager.Spawn`, standing spots at the feet level,
  a cell below or a cell above, so a king on the altar raises them on the roof), never more living than
  `SkeletonKingMaxMinions`; summoned skeletons have no floor, so the lock recount ignores them. The king is leashed to its
  home: past its leash it walks back (`SkeletonKing.HomeMove`), and knocked far past it or below the roof it is put back on
  the altar (`EnforceLeash`); with no target for `SkeletonKingResetSeconds` it heals to full and its brain resets. Tuning:
  the "Skeleton King" block of `MobConfig` (first guesses).
- **The boss bar** comes from `BossSelection.Nearest` over `World.Mobs` (`MobDirector.RefreshMobs` adds the kings), in
  singleplayer and from a client's mirrors alike, limited by the king's `Mob.BossBarRange`
  (`SkeletonKingBossBarRangeBlocks`; the sandworm's is unlimited).
- **Saves and the wire (BT-6)**: `stations.json`'s `TowerData.Altar` (the state's name, absent = Dormant; a Ritual restores
  with a full timer) and `entities.json`'s king (a `MobData` with `TowerCell` its tower and `Tier` its summon tier; a
  fading king is not saved): a restore re-binds it through `TowerPlacement.TryGetSite` and `Towers.Track(site).KingHome`
  and spawns it with no rise and no ritual, resuming the fight, and the tower stays Awake while the entry is pending.
  Protocol 27: each tower's altar state byte at the end of its `StationState` record, and `NetEntityKind.SkeletonKing` (14),
  its phase in `ItemId` (clamped to 1..3 on read) and its attack count in `Ammo`; a client mirrors it unbound
  (`SkeletonKingManager.SpawnMirror`, no leash, no summons).
- **Scenario and tests (BT-6)**: the `altar` op (`World.UseAltar` plus its toast); `visual-examples/skeleton-king.json`
  (`VOXEL_TIME=147`) clears the default tower's floors with `towerclear`, then films the altar, the ritual, the rise, the
  boss bar, the slam, the volley and a summon. Tests: `TowerAltarTests`, `TowerAltarWorldTests`, `SkeletonKingBrainTests`,
  `SkeletonKingTests` (the burst's scatter and timing), `SkeletonKingManagerTests` (the slam's shrapnel: `ASlamThrowsBoneShrapnel`,
  `ASlamShardFliesThroughTheKingAndASkeletonAndHurtsOnlyTheTarget`), the altar cases of `TowerPlacementTests`, `TowerLockBlockTests`,
  `TowerManagerSaveTests` and `StationNetCodecTests`, `BossSelectionTests.ABossBarRespectsItsRange`, the king in
  `WorldLifecycleTests`, `NetworkEntitySyncTests` and `UndeadStrikeTests`, `VisualScenarioTests.AnAltarStepParsesItsCell`
  and the loopback `GameServerTests.AClientRaisesTheSkeletonKingFromAClearedTowersAltar` and
  `ASealedAltarRaisesNothingForAClient`.
- **Known gaps (BT-6)**: the king's bones and slam shards, like every mob missile, are not on the wire (a client sees only
  the damage); the
  king is not drawn in the water mirror; a ritual's remaining time is not saved (it restarts); tower chunks saved before
  BT-6 keep an air altar cell, so that tower's altar cannot be clicked; the king's numbers are first guesses.
- **Collapse (BT-8)**: once the king is killed the tower falls into a ruin. `TowerManager.Update` polls
  `TowerRules.CollapseDue` (Defeated and not done) every frame after the door/pile sweep, so a live defeat and one restored
  from a save take the same path; a new collapse waits `MobConfig.TowerCollapseDelaySeconds` (the death and loot play
  first), a restored one resumes at once. Only a record built from a world site (`TowerRecord.Site`) collapses. **The rule**
  (`WorldGen/Structures/TowerRuin.RuinAt`, GL-free and pure in cell, seed and site, salt `0x5E80`, no cache): each wall column
  keeps a hashed stump (`TowerRuinStumpMinBlocks..MaxBlocks` over the ground slab), every unbreakable cell at or below its
  stump (the palette bricks, ground slab and foundation included) becomes `stone_bricks` or `cobblestone`
  (`TowerRuinCobbleShare`), doors, piles and the altar become air, everything above the stump becomes air except floor 0's
  breakable dressing on the ground slab (its chests included), and a `TowerRuinRubbleChance` share of the room's air columns
  gets a gravel or cobblestone pile at `GroundY + 1`, always on the solid ground slab so no gravel falls; the apron and
  breakable cells below a stump are kept; no new content id. **The staging** (`Simulation/Tower/TowerCollapse`, built over a box
  and a rule delegate, the test seam): a main pass walks layers from the site's `TopY` down to `BottomY` under
  `TowerCollapseScanCellsPerFrame` (shared by every collapsing tower; every cell costs one, a waiting chunk's too, so a pass
  over an unloaded tower never hitches) and the writer's `TowerMaxBlockWritesPerFrame` (shared with the door/pile sweep), so a
  whole tower takes some tens of seconds. A cell whose chunk is unloaded puts that chunk on a waiting set and the pass moves
  on; once the main pass is done each waiting chunk is swept top-down when it loads. Done is the main pass finished with
  nothing waiting, and permanent. An air-to-solid rubble write into a cell a target's body overlaps is skipped
  (`PlayerContext.BodyOverlapsCell`). A replaced chest spills through `ChestManager.NotifyBlockRemoved` (`World` wires the
  writer's `spillChest`). **Saves**: `stations.json`'s `TowerData.Collapse` (`TowerCollapseData`: the main pass's `NextY`, the
  `Waiting` chunks, `Done`; the in-layer index is not saved, since a rescan is idempotent: a cell no longer holding its
  expected block reads gone); an entry on a tower that is not Defeated is ignored, and a Defeated tower with none (a save from
  before BT-8) starts a fresh collapse. No wire change: clients see the ruin through the `BlockChanges` journal (protocol
  stays 27). **The show** (`Particles/Collapse/TowerCollapseEffects`, cosmetic, tuning in `JuiceConfig.TowerCollapse*` and
  `TowerDebris*`, never built on a server): every cell going from an unbreakable block to anything else
  (`TowerCollapseEffects.ShowsCollapse`: only the tower's writer can do that, so a player's edit never plays it; a
  translucent one, the dome's forcefield, has no rubble and just winks out) inside a
  tracked tower whose king is killed, within `TowerCollapseEffectRangeBlocks` of the camera, crumbles into a few chips of
  itself (coarse `BlockDebrisSystem.Puff` chips, under a chips-per-second cap), and one near the wall (from `TowerDebrisMinRadiusShare`
  of the footprint radius out) that falls away to air, with the cell outside it on its main outward axis open (so the block
  clears the wall instead of landing on the stump behind brick still standing), may throw a whole block off the tower (`CollapseDebris`, under a
  launches-per-second cap and `TowerDebrisMax` alive): launched away from the tower's center with some lift, it tumbles,
  bounces and rolls on the terrain (it only reads cells through `BoxSweep`; no cell, body, damage, save or wire ever sees
  it, drawn by `ObjectPasses.RenderCollapseDebris`) and bursts into rubble chips when its life runs out, jolting a camera
  close by. The camera rumbles by its distance to the nearest falling cell (`ShakeFalloff`), in overlapping pulses every
  `TowerCollapseRumblePulseSeconds` like the sandworm's thump and the ritual's rumble, while cells keep falling (a
  per-frame hold through `SandwormEffectRules.HoldTrauma` would stay invisible: the view shake decays linearly, so adding
  `trauma * dt / 0.7` a frame never builds up). Singleplayer feeds it from the
  writer's `replaced` hook, a client from the cells its journal brings (`ClientMirror.ApplyNetworkBlockChanges`, against
  its mirrored tower records), so the same rule plays the same show with no wire of its own;
  `visual-examples/tower-collapse.json` (`VOXEL_TIME=147`) films it. Tests: `TowerRuinTests`, `TowerCollapseTests` (top-down,
  the per-frame cap, the delay, unloaded cells waiting, the save round trip, done is permanent, no body entombed), the
  `TryReplace` and spill cases of `TowerBlockWriterTests`, `TowerRulesTests.ACollapseIsDueOnlyForADefeatedTowerThatHasNotFinished`,
  the collapse cases of `TowerManagerSaveTests`, `TowerAltarTests.ADefeatedTowerStartsCollapsingAndAnUndefeatedOneNever` and
  `TowerCollapseEffectsTests` (the show's rule, only a cell open on its outer side throws a block, a thrown block always
  ends in a burst and never writes a cell).
  **Known gaps**: desert and frost towers fall to
  stone rubble too; a storeless world (a `VOXEL_SCRIPT` run) forgets a ruined chunk on unload, so it regenerates intact; a
  crash between a chunk's save and `stations.json` saying Done can leave an intact part never redone; a late-loading,
  unmodified chunk shows the intact tower until the waiting sweep reaches it; worldgen never builds the ruin itself; no
  collapse sound yet, and the thrown blocks cast no shadow and are not in the water mirror; mobs and drops standing in a
  rubble cell are not protected like players.

### Sandworm

The desert boss (plans/SANDWORM.md, tuning in `SandwormConfig`): a chain of 14 rigid segments sharing one health pool, summoned
with the **thumper** (`use.action: "summon"`, `use.entity: "sandworm"`, `SummonItem`, crafted from copper, branches, vines and
sandstone), never spawned naturally. Right-clicking desert sand or sandstone (`SandwormManager.CanSummonAt`: loaded sand cell,
desert column, none alive, room under the hard cap; a client judges the same rule to predict) starts a thump and then the hunt.
It burrows **virtually**: it never edits a block, so the edit-path hooks, saves, LOD and the journal never see it; a segment under
the sand surface is simply not exposed.

- **Content.** `assets/data/entities/sandworm.json` (hp 300, loot `sandworm`: fangs and sandstone) and its Blockbench project
  `sandworm.bbmodel`, generated by `tools/generate-sandworm-model.py` (also the skin, the `sandworm_fang` and `thumper` sprites;
  `--check`). It is not a humanoid rig: the groups are `Head` (with `Jaw` nested under it, hinged at the back of the mouth), `Body`
  (one ring) and `Tail`, each authored around its own origin facing -Z, drawn once per chain segment.
- **The chain** (`SandwormChain`, GL-free): `Follow(head)` drags every segment to its fixed link distance behind the one ahead, so
  moving the head tugs the whole body; a link may not bend past `MaxBendDegrees`. `SandwormPose` turns a segment into its matrix
  (scale tapered from `BodyScaleStart` to `TailScale`, facing along the chain) and the jaw into its hinge matrix.
- **The brain** (`SandwormBrain`, GL-free, a roll source injected for the dive-or-slide pick): `Thump` (rumbles in place) ->
  `Dig` (burrowed hunt: tunnels toward the target at `DigSpeed` under `BurrowDepthBlocks` of sand, locks the point it will surface at
  within `LockDistanceBlocks` so a strafing target dodges the bite, then `Breach`) -> `Breach` (erupts upward, the head chomps) ->
  `Air` (steers at the target, and at the apex either dives back under and digs again or slides) -> `Dash` (flat along the sand,
  half in the ground, turning only `DashTurnDegreesPerSecond` so it can be dodged; ends on `DashMaxSeconds` or at the edge of the
  sand) and `Retreat` (no target for `RetreatAfterSeconds`, or all far: sinks away and is gone). A repeat of the last dive-or-slide
  pick is less likely. Its signals are `Telegraph` (how near the surface, 0..1), `Shake`, `Contact` (chomp, slide) and `SurfacePoint`.
- **The mob** (`Sandworm : Mob`): `Position` is the head. `Mob` gained three virtual hooks for multi-part bodies, all defaulting to
  the old single box: `OverlapsBox` (projectiles and spell bolts, `ThrownProjectile.FindMobHit`), `AimPointFrom` (every melee cone
  and line of sight, null while nothing is exposed so a buried worm is untouchable) and `ScaleDamage` (`MobStrikes.StrikeMob`: the
  head takes the blow whole, the body `BodyDamageShare` of it), plus `ShowsHealthbar` (hidden under the sand). A segment is
  *exposed* (can be hit) when a quarter of it is above the surface and *visible* (drawn) when any of it is.
- **The manager** (`SandwormManager`, joins `MobCaps`, not a `RealmMobManager`): picks the nearest target within
  `HuntRadiusBlocks` in any biome (a non-immune one first, an immune one when it is alone, so a hovering camera can film
  it), keeps digging toward where it last saw a target for `TargetMemorySeconds` after losing it, runs the worm,
  bills the contact (the chomp: `ChompDamage` and an upward `ChompLift`, from the head even while it is still under the sand
  beneath its victim; the dash: `DashDamage` from any exposed segment) once per `ContactCooldownSeconds`, rolls the loot on a kill and
  retires a worm nobody is near or whose head strays `DesertLeaveBlocks` from the desert. Registered in `World`, `MobDirector` (update, billing, `RefreshMobs`, the `spawn` op: the worm's
  head starts one burrow depth under the given feet), `WorldPersistence` (a restored worm resumes hunting, never thumps again) and
  `ClientMirror`.
- **Look.** `MobPasses.RenderSandworms` draws the visible segments (and `ShadowPass` casts them) with the jaw swung open while it
  breaches, flies and slides; a dying worm shrinks and sinks. `SandwormEffects` (cosmetic, never on a server) throws sand around the
  surfacing point with chips, speed and radius growing as it nears (`SandwormEffectRules`), a ring at the breach with a boom, a splash
  where it dives, dust along a dash, a pulsed rumble while thumping, and the earthquake: camera trauma by distance to the head, only
  while it digs. `visual-examples/sandworm.json` (`VOXEL_TIME=147`) films the encounter in the deep desert.
- **Multiplayer.** Protocol 23: the head is a mob entry (`NetEntityKind.Sandworm`, phase in the ammo field, velocity for
  dead-reckoning) and, while it digs toward a point, a `SandwormTarget` entry with the same id (the point and the nearness in
  thousandths). The body is not on the wire: the client's mirror (`Sandworm.IsMirror`) drags its own chain after the head, so a late
  joiner sees a straight body until the head has travelled a body length.
- **Tests.** `SandwormChainTests`, `SandwormBrainTests`, `SandwormPoseTests`, `SandwormMeleeTests`, `SandwormManagerTests`,
  `SummonItemTests`, the mirror in `NetworkEntitySyncTests`, the save round trip in `WorldLifecycleTests` and the loopback in
  `GameServerTests`.
- Known gaps: the death is only a shrink and a sink; the worm hurts players only (no mobs, no blocks); the numbers are first
  guesses from the scenario, not a play-tested balance; the item debugger shows the three parts stacked at the origin.

### Icebergs

Drifting icebergs float on the open sea at the pack-ice edge. They are entities, never blocks, and
**not mobs**: an `Iceberg` (`src/Entities/Icebergs/`) has no health, is never a punch, slice or
projectile target, draws no healthbar and counts toward none of the mob caps (`MobCaps`); the
`IcebergManager` (`src/Simulation/Icebergs/`, owned by `World`, main thread only) keeps its own
`IcebergConfig.NaturalCap` (the ring spawner) and `MaxAlive` (every forced spawn: a scenario, a
save restore). All tuning lives in `Config/IcebergConfig.cs`.

- **Content.** `assets/data/entities/iceberg.json` names no `hp` and lists its size classes as
  `variants` (`iceberg_small`, `_medium`, `_large`.bbmodel, generated with their shared skin by
  `tools/generate-iceberg-models.py`, byte-identical re-runs, `--check`). Model y = 0 is the
  **waterline**; every cube is axis-aligned and snapped to 8 px vertically, the one cube crossing
  the waterline is a **shelf** whose top is 8 px up (a swimmer can climb onto it), and every cube
  above it is an 8 px **step** inset from the one below on all four sides, so walking auto-step
  (`PlayerPhysics.WalkStepHeight`, half a block) climbs to the top from any side;
  `BodySolidsTests.ASwimmerCanBoardEveryIcebergVariant` enforces it.
- **Shape and placement.** `IcebergShape` reads a variant's collision boxes from the model's own
  cubes through `EntityModel.CubeBoxes` (the frame the baked mesh uses) and is the one definition
  of a berg's placement: `RotateQuarter` turns a box (each quarter turn maps (x, z) to (z, -x),
  exactly `Matrix4x4.CreateRotationY(pi/2)` on a row vector, so the boxes stay axis-aligned) and
  `ModelMatrix` is the matrix the renderer draws with (`IcebergShapeTests` checks they agree for
  every turn). A berg rolls a variant, a **horizontal-only** scale (`ScaleMin..ScaleMax`, so step
  heights and freeboard stay exactly as authored), a quarter-turn yaw and a seed, all fixed for
  life; its center's Y is fixed at the waterline. Its mass is `Density * Volume * scale^2`.
- **Motion.** `IcebergMotion`: the velocity eases (`WaterDragRate`) toward a drift along the world
  wind (`Wind.Direction`, turned by a seed-hashed angle within `DriftSpreadRadians`, at
  `DriftSpeed`), a push adds `PlayerPushForce` along the summed contact normals over the mass (the
  same push moves a big berg less), capped at `MaxSpeed`. A berg moves along X then Z; a move
  whose leading face enters a cell layer holding a solid or unloaded cell anywhere across the
  berg's bounds (the pack ice, shallows, land), or whose bounds would overlap another berg, or
  whose boxes would push into a standing (not riding, flying or dead) target's body, stops that
  axis. A berg only moves while a target is within `SimulateRadiusBlocks` and every chunk under it
  is loaded.
- **Pushing** is derived from positions, with nothing on the wire: a target in side contact with a
  berg box (`IcebergContact.TrySideContact`, within `ContactSlackBlocks`) pushes it along the
  contact normal, unless it rides the berg, flies or is dead (`IMobTarget.IsFlying` / `IsDead`,
  default members the camera and `ServerPlayer` override).
- **Standing on a berg.** The player body collides with berg boxes through one seam,
  `Player/IBodySolids.cs`: `BodyCollision.BodyCollides` / `MoveOnAxis` / `TryStepUp` /
  `PushOutOfBox` take an optional `IBodySolids`, `PlayerPhysics` passes `Camera.BodySolids` (the
  world's `IcebergManager`), so walking, auto-step, the swim exit and landing all treat the boxes
  as solid. The rider is carried client-side (`IcebergManager.CarryAndPushOut`, after the bergs
  moved): a body riding a berg's boxes where they stood before the move is moved by the berg's
  displacement through the body collision with its own berg ignored (terrain and other bergs still
  stop it), then a body left inside any box is pushed out. Singleplayer runs it after
  `IcebergManager.Update` (`MobDirector.UpdateIcebergs`), a client after its mirrors dead-reckoned
  (`MobDirector.AnimateIcebergMirrors`). A placement into a cell a berg box overlaps is refused
  (`IcebergManager.OverlapsCell`, beside the falling-cube check); the air above a low ledge is free.
- **Wobble** (`IcebergWobble`) is render only: bob, pitch and roll as two sines each of hashed
  periods and phases, a pure function of (seed, lighting clock), smaller for a bigger berg. The
  collision boxes never bob, so a rider's feet may visually bob by its small amplitude.
- **Spawning and life.** Every `SpawnIntervalSeconds`, below the natural cap, the spawner tries
  ring points around a target in turn (`IcebergSpawner.TryFindSite`): the stored column must be
  `Biome.Ocean` at `WorldGenerationConfig.SeaLevel` (no river or lake), its climate in the band
  (`IcebergSpawnRule.InBand`: cold from `SpawnColdMin`, a little below `PackIceColdStart`, up to
  `FreezeColdThreshold`, and the top water not frozen by `SnowGenerator.IsFrozenWater`), the berg's
  whole placed bounds loaded and free of solids (deep and wide enough) and no target within
  `SpawnClearRadiusBlocks`. The climate read is an injectable `IcebergClimate`; the default reads
  worldgen and none for a column a world source supplies. A fresh berg rises out of the sea over
  `EmergeSeconds` (render only); past `DespawnRadiusBlocks` from every target it is removed; when
  its rolled life runs out and nobody rides it or stands within the clear radius it sinks over
  `SinkSeconds` (no longer colliding) and is removed. With nobody online nothing spawns, moves or
  despawns.
- **Saves and the wire.** `entities.json` carries an optional `Icebergs` list (variant, scale,
  turns, seed, waterline center, velocity, life left); a saved berg is restored once every chunk
  under its placed bounds is loaded. On a server each berg rides `EntityList` as
  `NetEntityKind.Iceberg` (see "Multiple players and chat"); a client mirrors it without
  simulation (`Iceberg.Mirror`, fully emerged, dead-reckoned along its velocity for at most
  `MirrorMaxLeadSeconds` past a snapshot, `IcebergManager.ReplaceMirrors` / `AnimateMirrors`).
- **Rendering.** `Rendering/Actors/IcebergPasses.cs` builds one mesh and skin per variant
  (`EntityModel.FromData`) and draws each berg with `IcebergPasses.ModelMatrix` (the wobble's tilt
  about the waterline center, then `IcebergShape.ModelMatrix` at the center raised by the bob and
  lowered by the rise or sink) in the entity pass (before contact AO), the near shadow cascade and
  the water mirror (whose reuse motion signal reads each berg's center). The wobble clock
  (`IcebergPasses.Time`) is set once a frame before the shadows, so the three agree.
- **Debugger.** The item debugger's ENTITIES tab offers one mode per variant for the iceberg
  (`ITEM_DEBUGGER_SELECT=iceberg ITEM_DEBUGGER_ANIM=iceberg_large`).

Tests: `Entities/Icebergs/` (shape, motion, wobble, spawn band, contact, the berg),
`Simulation/Icebergs/` (`IcebergManagerTests`: drift stopped by the ice and by other bergs, frozen
over unloaded chunks, a small berg pushed farther than a large one, carry, push-out, mirrors,
despawn and sinking; `IcebergSpawnerTests`; `IcebergWorldTests`: the spawn op, standing on a berg,
the placement refusal and the save restore), `Player/BodySolidsTests`, the hover rule in
`MovementRulesTests`, the codec and protocol tests and `GameServerTests.AnIcebergReachesTheClientsEntityList`.
`visual-examples/icebergs.json` (`VOXEL_TIME=147`) films the three sizes, the wobble, the drift,
standing on a berg, a push and the reflection.

Known gaps (TODO `M-27`): no natural bergs on a column a world source supplies (no worldgen climate there);
item drops, mobs, projectiles, TNT blasts, the punch and slice cones and the block raycast ignore
bergs; the server's endpoint-collision rule is not judged against bergs (only the hover rule is);
a body merely resting against a berg nudges it a hair until contact breaks; mirrors dead-reckon
without collision; a rider's feet visually bob by the wobble; and a block that lands or hardens
inside a berg's bounds (a falling block, a fluid crust, a felled tree) is not refused and is
carried inside it.
