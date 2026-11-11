# Battle tower

> Guide: plans/README.md

## Status

Done: BT-1 to BT-10. BT-1 added the `unbreakable` block flag and the three worldgen-only tower bricks, BT-2
the tower generator (see "Tower generator (BT-2)" under Reference), BT-3 the undead skeleton with its melee rake, bow
and arrows, floor-tier difficulty and forced-spawn-only manager (see "Skeleton (BT-3)" and AGENTS.md "Skeleton"), BT-4
the skeleton piles, the floor doors and `TowerManager` (see "Locks (BT-4)" and AGENTS.md "Battle tower"), BT-5 the
tower chest and per-floor loot (see "Chests (BT-5)"), BT-6 the roof altar and the Skeleton King boss (see "Skeleton King
(BT-6)"), BT-8 the staged collapse of a defeated tower into a ruin (see "Collapse (BT-8)").
BT-9 verified and documented it: the `towerdefeat` op, the dark, torch-lit and ruined shots in
`visual-examples/battle-tower.json`, the cost probes (see "Costs (BT-9)"), AGENTS.md's "Battle tower" section and the
wiki's battle tower page. BT-10 made the floors dark: the tower bricks cover the sky in the light flood, and surface
ring spawns keep out of a tower's footprint (see "Dark interiors (BT-10)").
Tracks the battle tower: a rare, huge (radius ~20+) cylindrical landmark of 4-8 floors with skeleton piles, chests, floor locks, a roof
altar that summons the Skeleton King, unique boss loot and a collapse into a ruin.
Last updated 2026-11-10.

## In progress

Nothing.

## Open

Nothing.

## Reference

- **Content the tower added**: BT-3 the `arrow` and `bone` items, BT-7 `bone_breaker_blade` and `bone_charm`, BT-4
  `skeleton_pile` and `tower_door`, BT-5 `tower_chest`, BT-6 `tower_altar`; BT-8's rubble reuses `stone_bricks`,
  `cobblestone` and `gravel`. Content ids are `ushort` now, so they are no longer a budget.
- **How `unbreakable` is enforced** (BT-1): load rules (`BlockFieldRules.ValidateUnbreakable` refuses fluid, falls,
  needsSupport, replaceableByPlacement, flammable, meltsTo, the tree-wood and multiblock renderers, upperHalf and
  prime_tnt, and `BlockLinkResolver` refuses an unbreakable multiblock source) keep the block away from every raw-write
  path, plus guards in `Chunk.RemoveBlockAt`/`ClearBlockAt`, `Placement.CanOccupy` and the gameplay entry points
  (`Mining`, `LeftClickAction`'s creative branch, `BreakProgress`, `BlockEditor`, `TntManager.Detonate`,
  `FluidManager.SetCellImmediate`, `StatusEffectBlockRule`, `LookLabel`). `Chunk.SetBlockRaw` stays raw;
  `Chunk.CollapseWriteAt` is the only bypass, reached only through BT-4's `TowerBlockWriter`, which calls every edit-path hook.
- Tower brick tiles are stone_bricks.png with its five colors swapped
  one-to-one onto a palette per variant; a new biome variant is one more palette and one more block JSON.
- **Tower generator (BT-2)**, 0 content ids used (the tower is the three BT-1 bricks plus existing blocks).
  - **Files** (`OpenGLTest/src/WorldGen/Structures/`): `TowerPlacement` (cells, rolls, the cached site verdict, the band,
    the stamp, the finders), `TowerSite` (one accepted tower and the hooks below), `TowerShell` (pure shell geometry),
    `TowerGenerator` (shell plus dressing merge, the point query), `TowerPalette`, `TowerRing`. Templates
    `assets/data/structures/tower_floor_{hall,library,barracks}.json` and `tower_altar.json`. Tests
    `TowerShellTests`, `TowerPlacementTests`, the `Towers` row of `StampGateTests`, the tower cases of `StructureTests`.
  - **Placement**: one candidate per 1536-block cell (floor division), `SplitMix64.Hash(cell, Seed ^ 0x5E7F)` (no noise,
    so nothing re-lays; salt recorded in `BIOMEGEN.md`). The hash rolls the chance, the jittered center (a margin keeps
    the footprint in its cell), radius, floors, turn, the altar template and a floor template plus turn per floor. The
    site verdict is the 13th `GenerationalCache` site (`TowerSiteCache*`, cleared by `WorldSeed.Apply`): outside the spawn
    clear (center at least `TowerSpawnClearRadius + Reach` out), center not Ocean and dry, top under the cloud slab, and a
    sparse grid over the disc plus a 3-block ring all dry and within `TowerMaxStep` of the center ground. A chunk off every
    footprint pays only hashes and box rejects; `TopReaching` lifts the early-out over a tower chunk.
  - **Shell rings** (integer `d2 <= r*r + r`): apron (an entrance walkway cut through higher ground on a cobblestone stoop),
    outer wall 2 thick with a crenellated parapet 2 high and a ground entrance, stair corridor 3 wide (solid brick but for a
    helical tube), inner wall 1 thick with one doorway per floor at its landing, the room with a slab every 7 blocks and
    the roof slab. A foundation 6 deep fills the disc under the ground slab, and every disc cell to `TopY` is written.
  - **Stair**: half-steps, 60 per turn, alternating `cobblestone_slab` (even) and full brick (odd), `StepY(t) = GroundY + 1 +
    t/2`, headroom 3; landings at `LandingStep(k) = 14k - 1` are level with `FloorY(k)`; the last step reaches the roof.
  - **Hooks** for BT-4/6/8: `TowerSite.FloorY(k)`, `RoofY`, `Tier(k)` (= k), `DoorCells(k)` (with `LandingStep`/`DoorStep`),
    `AltarCell` (center, `RoofY + 1`, left empty by the altar template), `BottomY`/`TopY`, `MinX..MaxZ`, the
    `MinChunkX..MaxChunkZ` and `MinChunkY..MaxChunkY` ranges; `TowerPlacement.TryGetSite(cellX, cellZ)` and `TryFindAt(x, z)`;
    `TowerGenerator.CellAt(x, y, z)` (-1 or `id | state << 8`). A main-thread or server consumer reads a cached site at load,
    never per frame and never on a client.
  - **Templates**: kinds `towerFloor` (at most `TowerFloorHeight - 1` = 6 layers) and `towerAltar` (at most 8), odd size,
    `groundLayer` 0, no satellite or waystone, an empty altar template center at layer 0; a `tower_bricks` cell becomes the
    site's palette brick; lanterns hang only under a solid cell (a floor's top layer sits under the next slab); dressing
    blocks are breakable decoratives; each floor template and the altar also stand tower chests (see "Chests (BT-5)").
  - **Default seed**: the tower nearest the origin is cell (1, -1), center (2920, -1411), ground 10, radius 21, 5 floors,
    turns 2, plain bricks, slabs y 10/17/24/31/38, roof 45, altar (2920, 46, -1411), top 51
    (`visual-examples/battle-tower.json`, `VOXEL_TIME=147`).
  - **Tuning** (unchanged first guesses, `WorldGenerationConfig` "Battle tower" block): cell 1536, chance 0.6, radius
    20-24, floors 4-8, floor height 7, wall 2, stair 3, inner wall 1, 60 steps a turn, headroom 3, entrance 3 steps,
    doorway half-width 2, door offset 6, apron 3, parapet 2, altar height 8, foundation 6, max step 4, sample step 4,
    water ring 3, cache 256 x 4 generations.
  - **Cost** (`WorldgenCostProbe` `tower` class, default seed, ms a chunk): cold whole chunk 200-222 mean, of which tree
    growth about 180 and the Towers stage 0.28-0.30 (the stamp; the site build is paid in Columns by `TopReaching`); warm
    whole chunk median 0.44-0.50, warm Towers median 0.08 (the p90 of 1.6-2.7 is JIT tier 0 on unwarmed chunks; a steady
    full tower chunk stamps in 0.13-0.25). Surface chunks: Towers 0.001 cold, ~0 warm; Columns unchanged.
  - **Known gaps**: far-LOD canopy bumps over the footprint; tree crowns and limbs cut at the disc edge leave floating leaves and orphan wood until a nearby edit schedules
    decay; the stair's `cobblestone_slab` half-steps are breakable (a mined one leaves a pit over brick, never a bypass, as
    the flood-fill test proves).

- **Skeleton (BT-3)**, 2 content ids (`arrow`, `bone`). See AGENTS.md "Skeleton" for the whole design.
  - **Hooks for BT-4**: `SkeletonManager.Spawn(position, tier)` (a body center; null at the hard cap, the only way a
    skeleton appears, since it has no ring spawn), `TowerDifficulty.PackSize(tier)` (the wave size, no caller yet),
    `TowerSite.Tier(k)` as the tier, `World.Skeletons` for the floor's living count.
  - **Despawn** (resolved by BT-4): skeletons keep the shared realm-mob distance and idle despawn past
    `MobConfig.SkeletonDespawnRadiusBlocks` (96); a pile's bound skeleton that despawns or strays past the leash is owed
    back to its floor and re-raised there, so it never needs to stay (see "Locks (BT-4)").
  - **Tier on the wire and in saves**: `NetEntity.ItemId` (protocol 25) and `EntitySaveData.MobData.Tier`, clamped by
    `TowerDifficulty.Clamp`; the attack count's parity names the clip (melee even, shot odd).
  - **Known gaps**: arrows are not on the wire, skeletons are not drawn in the water mirror.

- **Locks (BT-4)**, 2 content ids (`skeleton_pile`, `tower_door`). See AGENTS.md "Battle tower" for the whole design.
  - **Files**: `src/Simulation/Tower/` `TowerManager`, `TowerRecord`, `TowerFloorState`, `PileState`,
    `SkeletonPileManager`, `TowerRules`, `TowerBlockWriter` (+ `TowerWriteResult`), `TowerReadout`;
    `src/Entities/TowerMobs/TowerFloorRef.cs`; `TowerShell.IsDoorStep`, `TowerGenerator.PileCells`/`TowerSite.PileCells`,
    `TowerPalette.DoorBlock`/`PileBlock`; the `S` piles in the three floor templates; placeholder tiles from
    `tools/generate-tower-lock-textures.py` (USER_TODO.md). Tests `TowerLockBlockTests`, `TowerRulesTests`,
    `SkeletonPileManagerTests`, `TowerBlockWriterTests`, `TowerManagerTests`, `TowerManagerSaveTests`, the tower cases
    of `StationNetCodecTests`, `SkeletonManagerTests` and `WorldLifecycleTests`, the loopback
    `GameServerTests.AClientSeesATowerDoorOpenOnceItsFloorIsCleared`, `VisualScenarioTests.ATowerClearStepParsesItsFloor`;
    shared fixture `Support/TowerSiteFixture`.
  - **Rule**: door k (seals floor k from k + 1, the last the roof) opens when door k - 1 is open, every pile of floor k
    is depleted and alive = living + owed + pending is 0; floor k's piles sleep until door k - 1 opens. Opening is
    permanent and saved (`stations.json` `Towers`, protocol 26 on the wire).
  - **Hooks**: BT-6 refuses the altar until every `TowerFloorState.DoorOpen` of the tower's record is set
    (`TowerRules.AltarVerdictOf`; the client mirror carries the door bits too). BT-8's collapse writes through
    `TowerBlockWriter.TryReplace` (a target block and state; `TryClear` is its air case) under the same cap; a collapsed
    tower keeps its `TowerRecord` (it carries the saved Done flag), whose cleared piles and open doors judge nothing.
  - **Default seed**: tower (2920, -1411) has floor-0 piles at feet y 11 at (2920, -1417), (2916, -1411), (2924, -1411),
    (2920, -1405) and door 0 at x 2906..2909, y 15..18, z -1426..-1423 (`visual-examples/battle-tower.json`).
  - **Tuning** (first guesses, `MobConfig` "Battle tower locks"): 4 piles a floor, 2 waves a pile of
    `TowerDifficulty.PackSize(k)`, trigger 8 blocks, a wave every 6 s, 32 writes a frame, a tower scan every 0.5 s, leash
    48 blocks.
  - **Known gaps**: a creative player who is not flying cannot clear a floor (only non-immune targets raise piles); a
    server door pop runs with the creative server camera's context and may drop nothing; towers saved before BT-4 have
    air in their door cells and no piles, so they open at once; the far LOD shows none of it; a pile with all nine spawn
    columns walled in retries forever.

- **Chests (BT-5)**, 1 content id (`tower_chest`). See AGENTS.md "Battle tower" and "Chests (I-4)".
  - **Files**: `ChestProfile` (the tier helpers `TierShift`/`TierState`/`TierOf`, `TierTables`, `TableFor`, `Tables`),
    `BlockDefinition.ChestDefinition.Tiers`, `BlockSchemaParser.ParseChest`, `ChestLoot.Materialize` (takes the cell's
    state), `ChestManager` (`Open` and `NotifyBlockRemoved` read or take the state), `BlockEditor.RemoveBlock` (passes
    `brokenState`); `TowerPalette.ChestBlock`, `TowerSite.TreasureTier`, `TowerGenerator.TemplateCell`/`ChestCells`/
    `AltarChestCells`; `blocks/tower_chest.json`; `loot/tower_supplies`, `tower_floor_low/mid/high/top`,
    `tower_treasure`; two chests in each `tower_floor_*` template and one on the roof in `tower_altar`.
  - **Tier encoding**: a chest is a facing block, so bits 0-1 of its state are the facing and bits 2-7 the tier
    (`ChestProfile.TierState(tier)`, up to 64). `TowerGenerator.TemplateCell` ORs `TierState(TowerSite.Tier(k))` onto
    the template's facing for a floor's chest and `TierState(TowerSite.TreasureTier)` (= `TowerMaxFloors`, 8) for the
    roof's, so the point query equals the stamp. `chest.tiers` maps tier to table, the last entry for any higher tier:
    floors 0-1 `tower_floor_low`, 2-3 `_mid`, 4-5 `_high`, 6-7 `_top`, the roof `tower_treasure`; all nest
    `tower_supplies`. Gear rarity bands climb one step a table (common..uncommon up to legendary only) and the gear per
    chest never falls; the ingots climb tin..wrought iron.
  - **Tests**: `TowerChestTests` (every stamped tier has a table, the map never steps back and each table is richer by
    least rarity and gear count, a chest rolls its tier's table whatever its facing, determinism, open equals break,
    no state until used, the `tiers` load rules) and the chest cases of `TowerPlacementTests` (chests per floor at their
    tier, the treasure tier on the roof, chest states in the point-query-equals-stamp check).
  - **Known gaps**: towers in chunks saved before BT-5 have no chests; an unopened chest destroyed by a blast rolls nothing, like a ruin
    chest (BT-8's collapse instead spills it through `ChestManager.NotifyBlockRemoved`, rolling an unopened one first); the chest reuses the underground ruin chest's tiles (USER_TODO.md).

- **Skeleton King (BT-6)**, 1 content id (`tower_altar`). See AGENTS.md "Battle tower" for the whole design.
  - **Files**: `src/Entities/TowerMobs/` `SkeletonKing`, `SkeletonKingBrain`, `SkeletonKingAttack`, `SkeletonKingHome`;
    `src/Simulation/Tower/` `SkeletonKingManager`, `KingStatus`, `TowerAltarState`, `AltarVerdict`, plus the altar parts
    of `TowerManager` (`JudgeAltar`, `BeginRitual`, `UpdateAltars`, `SettleAltars`, `MarkDefeated`, `RitualAltars`),
    `TowerRecord` (`AltarState`, `RitualLeft`, `AltarCell`, `KingHome`, `BossDefeated`), `TowerRules.AltarVerdictOf` and
    `TowerReadout.AltarToast`/`Altar`; `BlockInteractions.TryUseAltar`, `RightClickOutcome.Altar`, `World.UseAltar`;
    `MobTargets.InShockwave` (shared with the cloud golem) and `Mob.BossBarRange` (read by `BossSelection`);
    `blocks/tower_altar.json`, `entities/skeleton_king.json`, `loot/skeleton_king.json`; the model from
    `tools/generate-skeleton-king-model.py` and the `tower_altar_top` tile from `tools/generate-tower-lock-textures.py`
    (both placeholders, USER_TODO.md); `visual-examples/skeleton-king.json` and the `altar` op.
  - **Rules**: the altar stands at `TowerSite.AltarCell`, written by `TowerGenerator.Merge` (the template leaves the cell
    free). A click is Sealed until every floor door is open, then begins a ritual once (idempotent: later clicks read
    Stirring); after `SkeletonKingRitualSeconds` one king rises on the altar. A king at health 0 defeats the tower the same
    frame; a king that fades (every target gone, an empty server, a far respawn) returns the tower to Dormant unless its
    save entry is pending, so only a kill defeats it. The brain's phases come from the health share (the first observation
    silent, each phase change owes a summon, phase 3 enrages); it is leashed to the altar and heals after a long lull.
  - **Saves and wire**: `stations.json` `TowerData.Altar` (absent = Dormant); the king in `entities.json` with
    `TowerCell` and `Tier`; protocol 27 (the altar byte, `NetEntityKind.SkeletonKing` 14, phase in `ItemId`).
  - **Hook for BT-7**: the king rolls the placeholder `loot/skeleton_king.json` (bones, wrought iron, bronze ingots);
    done in BT-7: its first pool rolls exactly one unique (blade, charm or crystal staff), then bones, metal and a scroll or draught (`SkeletonKingLootTests`). No new content ids; the staff-or-scroll unique reuses `crystal_staff`.
  - **Hook for BT-8**: `TowerManager.BossDefeated` (an event, raised once per tower) and `TowerRecord.BossDefeated` (true
    from then on, saved); done in BT-8: `TowerManager`'s collapse polls `TowerRules.CollapseDue` over the record's altar
    state each frame (the event stays a public hook).
  - **Tuning**: the "Skeleton King" block of `MobConfig` (all first guesses: hp 400 in the JSON, phase shares 0.66/0.33,
    slam, volley and summon numbers, minion cap 6, leash margin, reset 20 s, boss bar range 48, ritual 3.5 s).
  - **Known gaps**: the bones are not on the wire; the king is not drawn in the water mirror; a ritual's remaining time is
    not saved; tower chunks saved before BT-6 keep an air altar cell, so their altar cannot be clicked; the numbers are not
    play-tested.

- **Collapse (BT-8)**, 0 content ids. See AGENTS.md "Battle tower".
  - **Files**: `src/WorldGen/Structures/TowerRuin.cs` (`RuinAt`, `StumpTop`; salt `0x5E80`), `TowerGenerator.CellIn`;
    `src/Simulation/Tower/TowerCollapse.cs`; `TowerBlockWriter.TryReplace` and its `spillChest` hook (wired by `World` to
    `ChestManager.NotifyBlockRemoved`); `TowerRules.CollapseDue`; `TowerRecord.Site`/`Collapse`; `TowerManager`'s
    `Collapse`, `Capture` and `Restore`; `StationSaveData.TowerCollapseData`; the `TowerRuin*` constants in
    `WorldGenerationConfig` and `TowerCollapseDelaySeconds`/`TowerCollapseScanCellsPerFrame` in `MobConfig`.
  - **Rules**: a Defeated tower whose collapse is not done collapses (polled each frame after the door/pile sweep, so a
    restored defeat takes the live path); a new collapse waits 4 s, a restored one resumes at once. Each wall column keeps a
    hashed stump of 2-9 blocks over the ground slab; unbreakable cells at or below it become stone bricks or cobblestone,
    doors, piles and the altar air, everything above air except floor 0's breakable dressing (chests included), and 15 % of
    the room's air columns get a gravel or cobblestone pile on the ground slab. The apron and breakable cells under a stump
    stay. Layers go top-down under a 4096-cell scan budget and the writer's shared 32-write cap; an unloaded cell's chunk
    waits and is swept when it loads; done is permanent. Rubble never fills a cell a target's body overlaps. A replaced chest
    spills what it holds.
  - **Saves and wire**: `stations.json` `TowerData.Collapse` (`NextY`, `Waiting` chunks, `Done`; absent = not started, a
    Defeated tower with none starts fresh, an entry on a tower that is not Defeated is ignored). Collapsed chunks are
    `ModifiedSinceSave`, so their saves win over worldgen. No wire change (the cell journal carries the ruin; protocol 27).
  - **Tests**: `TowerRuinTests`, `TowerCollapseTests`, the `TryReplace`/spill cases of `TowerBlockWriterTests`,
    `TowerRulesTests.ACollapseIsDueOnlyForADefeatedTowerThatHasNotFinished`, the collapse cases of `TowerManagerSaveTests`
    and `TowerAltarTests.ADefeatedTowerStartsCollapsingAndAnUndefeatedOneNever`.
  - **Known gaps**: desert and frost towers fall to stone rubble; a storeless world regenerates a ruined chunk intact after
    unload; a crash between a chunk's save and `stations.json` saying Done can leave an intact part that is never redone; a
    late, unmodified chunk shows the intact tower until the waiting sweep reaches it (worldgen never builds the ruin); no
    collapse sound (the chips, thrown blocks and rumble are `TowerCollapseEffects`, see docs/agents/tower-and-bosses.md
    "Collapse (BT-8)"); a whole tower takes some tens of seconds at 32 writes a frame (measured under "Costs
    (BT-9)"); mobs and drops in a rubble cell are not protected like players.

- **Dark interiors (BT-10)**, 0 content ids. See AGENTS.md "Sky cover (BT-10)" and "Battle tower".
  - **Files**: the `coversSky` block flag (`BlockDefinition.CoversSky`, `BlockType.CoversSky`/`CoversSkyById`/
    `SkyCoverIds`/`ContainsSkyCover`, `BlockFieldRules.ValidateCoversSky`, `BlockLoader`, `ContentRegistry.Reset`), set on
    the three tower bricks; `Chunk.MayHaveSkyCover` (set by `SetBlockRaw`, by `ChunkTerrainFill.GenerateBlockData` right
    after the tower stamp and by `ChunkRestore` for every restore); `ChunkLighting.AnySkyCover` and the `skyCover`
    argument of `ComputeLight` (`RaiseSeedsUnderSkyCover`), passed by `ChunkMeshBuilder`; `MobSpawner`'s
    `InTowerFootprint` and the internal `TryFindSurfaceSpawn` overload.
  - **Rule**: only when one of a mesh build's 27 resolved neighbors may hold a covering block, each light-volume column's
    sun seeds start one above the highest covering cell at or above the heightmap start, so every floor under a slab
    gets no sun and is lit only by its lanterns and the torches a player brings. Every seed still enters at 15, so the
    pruned-seed flood stays exact. It reads only the volume's blocks, so singleplayer, a headless server and a client
    (streamed chunks, restored through `ChunkRestore`) agree byte for byte with no wire change; the collapse's
    light-radius remesh relights the ruin, whose rubble does not cover. A chunk with no flagged neighbor pays 27 field
    reads. Surface ring spawns refuse a column inside a tower footprint box (`TowerPlacement.Meets`, gated by
    `TowersEnabled`, skipped for a column a world source supplies), since the ground floor is dark ground with two clear
    cells over it.
  - **Tests**: `SkyCoverFlagTests`, `ChunkSkyCoverTests`, `LightFloodTests` (the `TowerSlabAboveTerrain` scene and the
    cover-aware reference flood), `TowerInteriorLightTests` (no room cell between slabs above its terrain height gets sun
    15, and a streamed copy lights byte-identically), `TowerFootprintSpawnTests`,
    `TowerPlacementTests.EveryPaletteBrickIsUnbreakableAndCoversTheSky`.
  - **Known gaps**: a room cell in the volume above a chunk whose slab lies past the volume's top still seeds sun, which
    can reach the chunk only down the helical stair, more than 10 cells away (at worst a faint glow at a chunk's top in
    the corridor); an edge or corner neighbor missing at mesh time reads as air, so a little sun may enter until the next
    remesh; the chunk flag is sticky, so a ruin's chunks keep paying one extra top-down pass over the light volume.

- **Costs (BT-9)**, dev machine, 2026-11-09, Release, numbers from one sitting.
  - **Mesh build with sky cover** (`MeshBuildCostProbe`, the synthetic soup, 300 warm builds): 4885 us a build without
    cover (case B), 4853 us with the flag raised and nothing covered (C1, every column scanned: noise), 5093 us under a
    brick roof (C2, +4 %). Ceiling: within +15 % of the same scene without cover; met. A tower chunk's rebuild during the
    collapse costs about one C2 build and is bounded by the mesh scheduler's per-frame budgets.
  - **Tower worldgen** (`WorldgenCostProbe`, the `tower` class, 4 chunks): warm whole chunk median 0.51 ms (p90 2.8,
    unwarmed JIT), the Towers stage median 0.10; cold whole chunk 207 median, 223 mean, mostly tree growth, the Towers
    stage 0.27. Surface chunks: warm whole median 0.11-0.67 ms, the Towers stage about 0.002. Ceiling: tower chunk warm
    median at most 0.6 ms; met.
  - **Collapse** (`TowerCollapseCostProbe`, the largest tower: radius 24, 8 floors, 100 chunks): 2059 frames to finish
    (about 34 s at 60 fps), 65803 cells changed; the main-thread step costs 0.25-0.33 ms mean, p90 0.55-0.61, p99
    0.70-1.09; 7-27 frames pass 1 ms, the worst after the first frame 5.8-8.2 ms (the first 10-15 ms, JIT). Ceiling:
    at most 1.0 ms on the worst frame; **not met**, tracked as `PERF.md` `F14`.

- **Bone shrapnel (BT-7's first two uniques)**, 2 content ids (`bone_breaker_blade`, `bone_charm`); see AGENTS.md
  "Bone shrapnel". The blade carries `shrapnel: { count: 6, damage: 3 }`; the charm grants `bone_shrapnel`
  (`ShrapnelConfig.Charm*`, weaker) and sharpens the blade's by `ShrapnelConfig.TogetherFactor` (~20 %). Fragments reuse
  the `bone` item and fly as ordinary `ThrownProjectile`s, so the draw and the water mirror needed nothing new. The
  effect runs on the authoritative side only, so another player's client sees the damage but not the fragments (the
  existing projectile gap, `M-12`); both items are crafted until the boss table exists (an `iron_sword` and bones; bones
  and vines).

Decisions made while planning (2026-11-08):

- **Hybrid generation**, because templates cap at 17x17x24 and the shell is far bigger; the shell stays a pure
  function so a point query equals the stamp across chunk seams.
- **Unbreakable by data flag**, not by a check on a block name: the tower holds until the boss dies, so no player can
  dig around the fight or the locks.
- **Conquered state is the saved ruin itself.** The tower spans about 3x3 to 5x5 chunk columns, all inside the load
  radius when the player stands at the altar; saved chunks win over regeneration. The conquered record only covers a
  footprint chunk that was not loaded.
- **Locked until cleared** per floor, with a barrier door that opens by manager state (not mineable).
- **Palette swap only** per biome; the same skeleton, boss and layout everywhere.
- **Full scope from the start**: server-authoritative, saved, networked.
- **Order of the three efforts** (2026-11-08): accessories (done) first, this plan second, cave biomes
  (`CAVE_BIOMES.md`) third. `BT-7`'s accessory (slight mana regen, +10% damage against undead) needed the `undeadDamage` gear stat, which the
  accessories added; it is now the shipped `bone_charm`.
- Tuning left open: the stair and door layout at radius 20+, the floor height rhythm, bone-burst and accessory
  numbers, skeleton variants per tier and pile wave sizes. These are first guesses to retune after a play-test.
