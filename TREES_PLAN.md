# TREES_PLAN.md — tree generation & wood shape rework

## Status

All phases (1-8) done (content + codecs; headless growth engine; cutover to engine-grown trees;
tree-vs-tree cell batches + LOD canopy proxy + perf; leaf decay; reactive growth; wide trunks +
baobab/stone pine; ItemDebugger TREES tab + docs). Each phase landed as its own commit on
`tree-rework`; finished phases are collapsed into short summaries here. The residue left open is
tracked in `TODO.md` (`M-10`, `M-13`, `M-14`).

## Context
Trees are hardcoded C# today: `TreeGenerator.BuildShape` (broadleaf) and `BuildSpruceShape`,
plus a parallel copy of the same pattern in `CactusGenerator`. Wood thickness is one block id
per taper step (log/branch/limb/sprig/switch/twig, 4 cactus ids, `wood.taperLevel`), and
`WoodShape` welds neighbours by the heuristic "levels differ by exactly 1". Goal:

- one generic, data-driven, **step-based** tree generator;
- species defined in JSON;
- wood thickness from sapling-thin (1 px) up to multi-block baobab trunks;
- leaves that grow with the tree and decay;
- an explicit tree graph stored in the world, so felling, collision and future sapling growth
  are exact.

Implementation is tracked here, one plan-and-implement run per phase,
like `MESHER_PLAN.md`. Old saves are allowed to break.

## Design (decisions from the grilling session)

### Representation: plain blocks + state byte (no per-tree registry)
One wood block and one leaf block per species. Everything else lives in the per-block state
byte, which is a new user of `Chunk._state` (meaning decided by block id, as with fluids, stacks
and multiblocks).

**Wood state** (`WoodState`, GL-free codec):
```
bits 0-3  thickness n -> diameter (n+1) px; 15 = full cube
bits 4-6  parent dir: 0-5 = CubeFaces order (-Z +Z -X +X +Y -Y), 6 = root, 7 = detached
bit  7    shell (girth cell of a wide trunk, see below)
```
- **Connection rule:** a cell connects to its parent and to any neighbour whose parent is it.
  This replaces the level-diff welding in `WoodShape`, ends sibling and neighbour-tree welds,
  and lets in-world growth rebuild the graph from the blocks.
- **Placed wood:** a wood item placed by the player is a full detached cube (parent 7). It
  never welds and is never felled with a tree.

**Leaf state** (`LeafState`):
- bits 0-2: hydration — `leafCluster.hydration` minus the Manhattan distance to the nearest
  **live tip** (a childless wood cell at or under the species' `leafCluster.tipMaxPx`) whose
  cluster shape holds the cell; a leaf exists only while that is >= 1, and 0 decays. A thick
  stub left by a cut is not a tip, so its leaves decay.
- bit 3: persistent (player-placed leaves never decay).

**Wide trunks (core + shell):** when a node's diameter passes 16 px it becomes:
- a core cell (the graph node, parent = down);
- shell cells around it (bit 7 set, thickness 15, parent = one step toward the core, so a
  diagonal shell chains through an edge shell), laid out per species as a block-resolution
  voxel circle or an NxN square.

A species with `rim: "pixel"` gets its outer shells bevelled by the mesher from their 8
horizontal neighbours (marching-squares, pixel-stepped quarter-rounds). This is stateless and
uses the existing 18³ padded snapshot. Buttresses and flared roots are a post-growth feature
stage that writes ordinary wood cells along the ground.

### Species data — `assets/data/trees/<species>.json`
```jsonc
{
  "wood": "block/oak_log", "leaves": "block/oak_leaves",
  "growth":     { "logic": "oak", "signalEnergy": 14, "upProbability": 2,
                  "lowestBranchHeight": 4, "maturitySteps": [60, 90] },
  "thickness":  { "tipPx": 1, "pipeExponent": 2.0, "maxPx": 16,
                  "girthProfile": [[0, 1.0]], "trunkShape": "circle", "rim": "block" },
  "leafCluster":{ "shape": "blob", "radius": 3, "hydration": 4, "tipMaxPx": 3 },
  "features":   [ { "type": "roots", "depth": 4 } ],
  "lod":        { "crownHeight": [6, 9], "crownRadius": 3, "profile": "dome" },
  "worldgen":   { "biomes": ["forest"], "density": 0.02, "spacing": 3,
                  "maxSlope": 1, "minSoil": 2, "minGround": 0, "maxGround": 999 }
}
```
- **`growth.logic`** names a registered C# direction chooser (`IGrowthLogic`): `oak`,
  `conifer`, `cactus`, `umbrella` (stone pine) and `baobab`.
- **`features[]`** are post-growth stages (`ITreeFeature`): `roots` and `buttress`.
- **`lod`** is the far-ring canopy proxy.
- **`worldgen`** is optional; leaving it out keeps the species out of worldgen (baobab and stone
  pine for now).

Loading: `ContentRegistry.Load` parses the species after the blocks, and every error names the
file. A wood block JSON declares `renderer: "tree_wood"` plus `wood: { hardness curve, loot
bands }`:
- **Hardness:** a species base × a thickness curve, replacing the per-id 15% steps.
- **Loot bands:** loot is chosen by thickness band. Full thickness drops the species log item,
  and thinner wood rolls the current `wood` table.

`Mining` reads hardness through `(id, state)`.

### Growth engine (GL-free, `WorldGen/Trees/`)
- **`TreeGraph`:** virtual nodes with position, parent, child count and a float radius.
- **`ITreeSpace`:** the obstacle view. It has two implementations:
  - `VirtualTreeSpace`: the terrain heightmap plus occupied cells. Used by worldgen and the
    debugger.
  - `WorldTreeSpace`: live chunks via `TryGetCell`, used for reactive in-world growth.
- **`TreeGrower.Pulse(graph, species, space, rng)`:** one Dynamic-Trees signal pulse. It
  starts at the root, picks a path with `IGrowthLogic`, extends or branches one tip by one
  face-adjacent cell, then thickens the path on the way back down. Thickness follows the pipe
  model (`pipeExponent`) multiplied by `girthProfile`.
- **Maturity:** worldgen and `grow` run `maturitySteps` pulses with a `SplitMix64` seeded from
  the root position and the world seed.
- **Leaves follow the wood:** they are a pure function of the graph plus obstacles and never
  steer growth. Worldgen computes them once at maturity; in-world growth regrows the clusters
  around changed tips and prunes the leaves that lost hydration.
- **`TreeRasterizer`:** turns the graph into cells + state (the graph nodes in index
  order — growth nodes, then feature nodes — then the trunk chain's core/shell girth, then
  leaves). It is shared by worldgen stamping, the far LOD and the debugger.
- **Reactive mode:** `TreeGraph.FromWorld(root)` rebuilds the graph from the wood states.
  Cut branches stay cut, obstacles are real blocks, and growth writes only into air or the
  species' own leaves.

### Worldgen (keeps every existing invariant: pure function of position, thread-safe)
- **Placement** keeps today's rules (density hash, lowest-hash spacing, biome, slope, soil,
  water, spawn clear radius), with the per-species `worldgen` block driving them.
- **Cell batches:** every tree of a 64×64 XZ cell grows together, in placement-hash order, in
  one `VirtualTreeSpace` (terrain plus the trees already grown). Trees in the 8 neighbouring
  cells count as obstacles only as their cached **solo** shapes, which keeps the result
  deterministic and independent of chunk order. Both caches are `GenerationalCache` sites
  (pure functions of their keys, static factory delegates), with capacity/generation knobs in
  `WorldGenerationConfig`. Stamping writes wood first, then leaves, into air only (and wood
  over leaves), as today; the stamp order settles any leftover overlap at a border.
- **The far LOD:** levels 0-1 (≤ 512 blocks) read the real cell-batch canopy, while levels
  2-3 read the per-species `lod` proxy rolled by the same placement hash. `CanopyDisturbed`
  and `LodEditState` are unchanged.

### Mesh, collision, felling, decay
- **`tree_wood` renderer** (per-voxel path, not greedy): a node box sized by thickness and a
  tube toward every connection at min(this, neighbour). It crops UVs through
  `Chunk.TiledUv`, keeps smooth light and AO through `FaceLight`, and bevels pixel-rim shells.
  The biome tint and sway rules stay as they are. Full-thickness cells emit culled cube faces
  like today's log.
- **Hitboxes match the mesh** (a `TreeWoodShape` replaces `WoodShape`): node box, tube boxes
  and stair-stepped rim boxes, used by the raycast, the selection outline and
  `Camera.IsColliding`.
- **Felling:** the cells that lose every path to a root fall. The paths run over parent
  edges plus shell-to-shell vertical adjacency, so a cut limb drops only the limb, a cut
  1-wide trunk drops the crown, and a wide trunk falls only once a whole slice is cut. Leaves
  come along, and `TopplingTree` keeps its mesher-based bake.
- **Leaf decay:** a `LeafDecayManager` owned by `World`, main thread only, a scheduled-cell
  simulation like `FluidManager`. A wood or leaf edit schedules nearby leaves; a tick
  re-derives hydration only for scheduled cells, and a leaf left with no hydration decays
  through `DropManager.SpawnLoot`. Every edit path calls `ScheduleAround`: `RemoveBlock`,
  `FellWood`, placement, `TntManager.Detonate` and in-world growth. The rule is the exact one
  above, re-derived on world cells (`LeafCluster.HydrationFromTip` over the live tips in the
  cluster box), and the same manager crumbles orphaned wood: a non-root, non-detached,
  non-shell wood cell whose loaded parent no longer connects to it drops as loot.

### Naming and art
- **Oak (the default broadleaf):** `oak_log`, `oak_leaves`, the tiles `oak_log_side`,
  `oak_log_top`, `oak_leaves` and `oak_leaves_textureless`, and the matching loot/tag
  entries. `tools/generate-leaf-textureless.ps1` is updated to the new names.
- **Spruce and cactus:** `spruce_log` / `spruce_leaves` stay. The four cactus blocks become one
  `cactus` block.
- **New species:** `baobab_log`, `baobab_leaves`, `stone_pine_log` and `stone_pine_leaves`,
  with placeholder art from a new deterministic `tools/generate-tree-textures.ps1` (tinted from
  the existing tiles).

## Phases
1. **Done — content + codecs.** `src/WorldGen/Trees/`: `WoodState` (bits 0-3 thickness, 4-6
   parent dir in `Chunk.CubeFaces` order with 6 root / 7 detached, bit 7 shell; `TryParentOffset`
   reads the face table) and `LeafState` (bits 0-2 hydration, bit 3 persistent);
   `IGrowthLogic`/`ITreeFeature` with immutable name registries (oak, conifer, cactus, umbrella,
   baobab; roots, buttress — placeholders until phase 2); `TreeSpeciesDefinition` ->
   `TreeSpeciesCompiler.Compile` (pure, every error names the file) -> `TreeSpeciesRegistry`,
   loaded by `ContentRegistry.Load` after the blocks (a missing `assets/data/trees` folder is
   tolerated). oak/spruce/cactus ship in `assets/data/trees` pointing at today's log/leaves,
   spruce_log/spruce_leaves and cactus blocks, mirroring `WorldGenerationConfig`'s placement
   numbers; `leaves` and `leafCluster` are optional together (cactus). Nothing reads the species
   yet. Tests: `WoodStateTests`, `LeafStateTests`, `TreeSpeciesCompilerTests`,
   `TreeSpeciesContentTests`.
2. **Done — growth engine (headless).** `TreeGraph` (int-indexed nodes: cell, parent + parent
   dir in `Chunk.CubeFaces` order via `TreeDirections`, child list, girth-free `PipePx` and
   displayed `ThicknessPx`, both diameters in px, feature flag), `ITreeSpace.IsFree` with
   `VirtualTreeSpace` (height delegate, optional carved-cell delegate, occupied set;
   `FromTerrain` reads `Chunk.GetColumnData`), `TreeGrower` (`Pulse`: base weights sides 1 / up
   `upProbability` / travel reinforced / thick children followed by `PipePx × FollowScale` / no
   forks under `lowestBranchHeight`, then `IGrowthLogic.AdjustWeights`, blocked dirs and Down
   masked, one face-adjacent node per pulse; the grower clamps `EnergyAfterTurn` to 0..energy,
   so a turn never adds energy and a growth node stays within `signalEnergy` of the root; pipe
   model on `PipePx`, `ThicknessPx = min(maxPx, max(tipPx, PipePx × girth))` raised to the
   thickest child, so girth is applied once and a parent is never thinner than a child;
   `GrowToMaturity` seeded by `SeedFor(x,y,z,seed,species)` = position + world seed + FNV-1a
   species name (`WorldSeedParser.Fnv1a`), steps and per-tree energy from that stream, final
   `Thicken`, then `ITreeFeature.Apply`), `OakGrowthLogic`/`ConiferGrowthLogic`/
   `CactusGrowthLogic` (umbrella/baobab/buttress still placeholders), `RootsFeature` (feature
   nodes down through free cells), `LeafCluster` (live tips = childless non-feature nodes <=
   `tipMaxPx`; blob/cone shapes; stored hydration = `leafCluster.hydration` − Manhattan
   distance to the nearest tip whose shape holds the cell, >= 1; `HydrationAt` is the one
   predicate) and `TreeRasterizer` (`TreeCell` list: wood, features, leaves; thickness code =
   ceil(px) − 1 clamped at the full cube; `ReachOf` for phase 3's border margin). Nothing in the
   game reads it. oak/spruce/cactus JSON retuned (oak 9-12 tall with a dome crown, spruce 9-14
   conical, cactus 5-6 with 2-3 upward arms). Tests: `TreeGraphTests`, `VirtualTreeSpaceTests`,
   `TreeGrowerTests`, `TreeRasterizerTests`.
3. **Done — cutover.** One `tree_wood` block per species (`oak_log`, `spruce_log`, `cactus`;
   broadleaf content renamed to oak: `oak_log`/`oak_leaves` and the `oak_log_*`/`oak_leaves*`
   tiles; the six taper blocks, the three cactus taper blocks, the branch-connectable tag,
   `wood.taperLevel/halfWidth/family`, every wood family/level table, `WoodShape`,
   `CactusGenerator`, `BuildShape` and `BuildSpruceShape` deleted). The registry forces tree wood
   non-occluding, non-AO and light-passing, never greedy. `TreeWoodShape` is the one connection
   rule (same id, neither side detached, my parent points at you or yours at me) plus node and
   tube boxes (`HalfWidth = (t+1)/32`); the `Chunk` per-voxel `EmitTreeWood` emitter draws a thin
   cell as a node box plus one capless tube per connection at min(this, neighbour) width through
   `EmitBoxFace`/`CropUv` and `EmitterFaceLight`, while a full-thickness or detached cell takes the
   model path and culls against a same-id full cube. Raycast, selection outline and
   `Camera.IsColliding` read `TreeWoodShape.GetBoxesAt`/`OverlapsShape`. `Mining.HardnessOf(id,
   state)` scales by thickness (`wood.tipHardness`), and loot rolls `wood.loot` thickness bands
   (`WoodLootBand.Select`) through a new `LootContext.State` that mining, TNT, flows and felled
   trees pass; placing tree wood writes `WoodState.Detached`. `TreeGenerator` keeps the placement
   gates, now per species (`TryGetTree`: one hash stream, biome -> species in ordinal name order,
   optional `worldgen.openness`/`spawnClear`), grows each tree solo (`ShapeOf`: a
   `GenerationalCache` of rasterised `TreeCell[]` over the terrain heightmap raised to flooded
   water) and `Stamp`s wood with state into air or leaves, then leaves into air, in a fixed scan
   order; the root walk extends the trunk down through cave air but only inside the chunk holding
   the base and never past its floor. The far LOD canopy reads the rasterised leaf cells;
   `PoisonCanopy` takes a radius (1 for a single edit, the species reach on a fell; the disturbed
   set's cap is `CanopyDisturbedLimit`). `TopplingTree.Collect` fells the cut cell's subtree over
   child edges (root: the whole tree; limb: its descendants; detached never follows) plus leaves
   within Chebyshev 2, meshed in a scratch chunk centred on the pivot (`ScratchOrigin`) with the
   state bytes. Save formats bumped (`ChunkSaveCodec.Version`, `WorldStore.CurrentVersion` 2;
   PLAY refuses another format with a notice). Instrumentation: `WorldgenStats` and the
   profiler's `wgen` console token; scenarios `visual-examples/tree-perf.json` (perf fly-through)
   and `visual-examples/trees.json` (oak, cut limb, felled trunk, spruce, cactus). Tests:
   `WorldgenStatsTests`, `WoodLootBandTests`, `WoodLootTests`, `TreeWoodShapeGeometryTests`,
   `TreeWoodShapeTests`, `TreeWorldgenTests`, `CanopyDisturbedTests`, `TreeFellingTests`, plus the
   rewritten mining, meshing, biome, snow, LOD, save-codec and main-menu rules.
4. **Done — tree vs tree + LOD proxy + perf.** Every tree of a 64x64 XZ cell
   (`TreeCellShift`/`TreeCellBlocks`, `TreeGenerator.CellOf` = arithmetic shift) grows together
   in one `VirtualTreeSpace(GrowthGround)`: `TreeBatch` (immutable `Trees` in placement-hash
   order + `MinY`/`MaxY`), `BatchOf(cellX, cellZ)` over a second `GenerationalCache` site
   (`TreeBatchCacheCapacity` 336 / `Generations` 4, static `BuildBatchFor`), and
   `BatchShapeOf(x, z)` for tests and probes. Trunks come from `TryGetTree` over the cell and are
   sorted by (`PlacementHash`, x, z), a total order (`TreeGrower.SeedFor` keeps each tree's RNG
   independent of it). Obstacles are the SOLO shapes (`ShapeOf`, unchanged, still the solo cache)
   of the neighbour trunks in the 2R ring around the cell that pass a Chebyshev filter (distance
   <= own reach + neighbour reach), never another batch, so a batch is a pure function of (cell,
   seed); the static ctor throws if 2R exceeds the cell. Each grown tree's wood and leaf cells
   are occupied (`VirtualTreeSpace.OccupyAll`) before the next grows; the one residual overlap is a
   later tree's unchecked base cell landing in an earlier tree's leaves (or rarely wood).
   `Stamp` and `BuildCanopyTops` walk the cells whose reach-expanded box meets the chunk in
   (cellX, cellZ) order, then tree order, with the unchanged two passes (wood into air/leaves +
   roots, then leaves into air), so border residue (a neighbour stamps its batch shapes, which
   grew against this cell's solo shapes) settles deterministically by stamp order; the first LOD
   ring therefore shows exactly the stamped canopy. Farther rings read a proxy:
   `LodLevelSpec.ProxyCanopy` (true on the cell-4 and cell-8 rings), `TryGetCanopy(x, z, proxy,
   ...)` (CanopyDisturbed first, as before; the 4-argument overload forwards `proxy: false`),
   `ProxyCanopyCache` (`TreeProxyCanopyCache*` knobs) built by `BuildProxyCanopyTops` from the
   same `TryGetTree` placement, `CrownHeightOf` (rolled from the placement hash bits with
   `CrownSalt`) and the GL-free `LodCrownProxy.TopOffset` dome/cone/column silhouette (full height
   at the trunk column before any arithmetic, non-increasing outward) - no growth on the far sweep.
   `[ThreadStatic] GrowsOnThisThread` is the test seam for that. The profiler's `wgen` token gained
   `batch`/`dup`/`brot` (`BatchCacheMisses`, the lock-free `BatchDuplicateBuilds` diagnostic,
   `BatchCacheRotations`). `lod` blocks retuned from a throwaway probe over grown batches (oak dome
   [9,9] r4, spruce cone [7,7] r4; cactus is leafless and unused). `trees.json` re-probed through
   `BatchShapeOf`: oak, limb and cactus unchanged, the spruce shot moved to an isolated spruce at
   (-291, 13, -252). The single-flight on the batch miss path was not added (dup share 6.5-7.6 %,
   under the 10 % gate; see "Phase 4 result"). Known residue for later phases: in a dense taiga
   batch a few late trees have no room left and grow a bare post (8 of 708 sampled, none of them a
   stump when grown solo). Tests: `TreeBatchTests` (order independence, disjoint grown cells, no entry into earlier
   or neighbour-solo cells, trunks = placement, reach, whole trees across cell borders, first ring
   = stamped canopy), `LodCanopyProxyTests` (proxy placement = real placement, proxy within reach,
   profiles non-increasing, deterministic roll, far rings never grow), `VirtualTreeSpaceTests.
   OccupyAllMatchesPerCellOccupy`, and the shared `TreeWorldgenFixture`.
5. **Done — leaf decay.** `LeafDecayManager` (`src/Systems/`, owned by `World`, main thread
   only) is a `FluidManager`-style scheduled-cell simulation: 21-bit packed cell keys, a `_next`
   set snapshotted per tick and walked in per-frame slices, a 16-slot `ChunkAt` cache, leftover
   time dropped, knobs `LeafDecayTickSeconds`/`MaxLeafDecayTicksPerFrame`/
   `MaxLeafDecayCellsPerFrame`/`LeafDecayMaxRemovalsPerTick`/`MaxLeafDecayBacklogCells`; it ticks
   after the fluid tick under the new `leafDecay` profiler row, never while loading. The exact
   rule: a scheduled non-persistent leaf re-derives its hydration as the max of
   `LeafCluster.HydrationFromTip` (the one per-tip formula, now shared with `HydrationAt` and
   `Collect`) over the live tips in its species' cluster box — a non-detached cell of the
   species' wood with `DiameterPx <= tipMaxPx` and no face neighbour whose parent points at it —
   so a settled canopy reads back bit for bit and costs nothing. The adjacency chain rule
   ("a neighbouring leaf minus one") was rejected: leaves reachable from their tip only through
   the branch's own wood (behind a bend) would re-derive to 0 on an intact tree. Hydration
   rewrites never remesh (the mesher never reads `LeafState`); a 0 decays to air under
   `LeafDecayMaxRemovalsPerTick`, the tick's cells taken in a `SplitMix64`-scattered order so a
   crown dissolves speckled over a couple of seconds, rolling `DropManager.SpawnLoot`, bursting
   `LeafParticleBurstDecay` leaf particles, poisoning its column with radius 0 plus
   `LodManager.MarkEditedColumn` (once per column per frame) and scheduling nothing (a leaf feeds
   no other cell; a reach box per decayed leaf flooded the backlog while a crown dissolved);
   a tick start drops the air and terrain of its scheduled boxes with one read each, so the
   per-frame budget and the scatter sort only see leaves and wood, and a leaf scans its tip
   offsets best first, stopping at the first live tip; the backlog cap holds a whole tree's box
   (a fell's box ran past the old 16384); removals remesh only the cell's chunk and the border-adjacent ones (both block kinds pass
   light, cast no AO and occlude nothing). The orphan rule: a scheduled non-root, non-detached,
   non-shell `tree_wood` cell whose loaded parent no longer `TreeWoodShape.Connects` crumbles
   under the same budget with its thickness-band loot and schedules its reach box, so a blasted subtree crumbles outward one
   layer per tick (mining still fells through `TopplingTree`). Freezing: any read that lands in an
   unloaded chunk drops the cell from the schedule, never decays it. `ScheduleReach` = max leafy
   `leafRadius` + 1 (the +1 for a parent that turns into a tip); `ScheduleAround`/`ScheduleBox`
   grow the edited extent by it, so callers never know the radius. Hooks: `World.RemoveBlock`,
   all three `FellWood` branches (one box over the batch clear, never per cell),
   `PlaceBlockAtFace` (placed leaves get `LeafState.Encode(0, persistent: true)` beside the wood
   `Detached` line), the bucket/prime `IItemUseWorld` wrappers (interface unchanged) and
   `TntManager.Detonate`/`Update` through a new defaulted `leafDecay` parameter. Fluids never
   wash leaves (they are not replaceable), so the fluid tick has no hook. Chunk loads schedule
   nothing: worldgen leaves are consistent, and a restored chunk's stale leaves (wood cut before
   saving) wait for the next nearby edit, since a per-restore chunk walk would tax every load.
   `trees.json` gained `limb_cut_before_decay` / `limb_cut_decayed` around the oak limb cut;
   `grass-tint.json`'s `forest_mountain_ground` was re-probed to (113.5, 30.5, -194.5) on ground
   27 with a 2-cell eye clearance (the old y 48.5 teleport was a fatal fall that framed a respawn
   among the batch-grown crowns). The dead `TreeGenerator.ReachDown` was deleted (`Stamp`
   already guards Y through the batch and shape `MinY`/`MaxY`). Tests: `LeafDecayTests` (intact
   canopy re-derives bit for bit and then schedules nothing, a cut tip's leaves decay within the
   budget bound, a second live tip holds a leaf, persistent leaves never decay, unloaded
   neighbours freeze, decay rolls the leaf loot under the per-tick budget, decayed leaves schedule
   nothing further, a whole tree's box is never truncated by the backlog, orphaned thin wood
   crumbles while the connected rest stays), the shared `StampedTree` harness. Known residue: the
   world cannot tell a root-walk feature node from a branch, so a thin childless root counts as a
   tip there (it can only raise a leaf's hydration, never decay one).
6. **Done — reactive growth.** `WorldTreeSpace : ITreeSpace` (`src/WorldGen/Trees/`) reads only
   through `ChunkManager.ChunkAt` + `GetLocalBlock`/`GetLocalState` behind a single-slot cache of
   the last loaded chunk (one main-thread growth call, no unloads, not thread-safe): a cell is free
   when it is loaded, not `reserved` (World passes `FallingBlockManager.OverlapsAny`), air or a
   non-persistent leaf of the species, and not the missing parent of an orphan (no non-detached
   face neighbour of the species' wood names it as its parent — the rule that keeps a cut branch
   cut); any unloaded cell or neighbour read refuses. `IsPlantable` also accepts a non-solid
   placement-replaceable cover (a partial snow layer) for the sapling root. `TreeCellReader` (a
   delegate `ChunkManager.TryGetCell` binds to, so the engine stays storage-agnostic) feeds
   `TreeGraph.FromWorld`: from a root cell (parent code 6) a breadth-first walk with the node index
   as queue follows only child edges (same wood id, not detached, parent offset landing on the
   current cell), so detached and orphaned wood is never reached; every node is seeded and floored
   (`Node.FloorPx`/`SetFloor`) at its stored `DiameterPx`, nodes below the root's Y load as feature
   nodes (exact, since `Pulse` never descends — this resolves phase 5's root-walk residue inside
   the graph; the world-side decay tip rule still counts thin childless roots), and it returns null
   on an unloaded read or past `TreeGrowNodeCap`, so a partial tree never grows. `TreeGrower`:
   `Recompute` became feature-aware (a feature node keeps its thickness, a parent ignores feature
   children) and floor-aware (`max(thick, FloorPx)`), both no-ops for worldgen (features apply after
   the final `Thicken`, grown floors are 0), so a rebuilt tree never thins and graph codes always
   equal world codes; `RollMaturity` (steps, then the clamped `TreeEnergy`, same stream order) backs
   `GrowToMaturity` and `TreeEnergyAt`, which replays a worldgen tree's exact energy from
   `SeedFor`; `GrowFurther` = `Thicken`, N pulses under a node cap, `Thicken`, no features. Knobs:
   `TreeGrowNodeCap` (= `TreeFellWoodCap`, so a grown tree's wood fells whole; leaves past
   `TreeFellTotalCap` are left standing by a fell and cleaned up by decay) and
   `TreeGrowMaxPulsesPerCall` (256). `TreeGrowthWriter` (`src/Systems/`, GL-free, main thread,
   with `TreeGrowthResult`/`TreeGrowthStatus` Grew/Planted/NoGround/NotGrowable) scans the target
   column top-down over the reach window `[yHint - Reach.Down - 1, yHint + Reach.Up]` for the
   species' root (any unloaded read = NotGrowable); with none it plants a sapling on the first
   ground candidate (not air, leaves, attached tree wood or cover — cover is looked through) that
   is solid, not a fluid and, for a `minSoil > 0` species, `SurfaceBlock`/`SoilBlock`, with a
   plantable root cell above (a cover there is cleared to air, dropping nothing); biome and
   ground-height rules are not applied. Planting and an existing tree share one `GrowAndWrite`
   tail: RNG `GrowSeed(SeedFor(root, worldSeed, species), nodeCount, call counter)` so a repeated
   grow never replays the same pulses, `GrowFurther`, rasterise once, then wood (existing nodes
   only re-coded for thickness, new nodes only into air or own leaves that are free, never thinning,
   a skipped parent skips its subtree) and then leaves only around changed tips (new tips or
   re-coded live tips): a new leaf only into free air, an existing own non-persistent leaf only
   ever has its hydration raised, so a mined hole in an untouched part of the crown stays mined.
   All writes are `Chunk.SetBlockRaw` (never player-placed). Pruning is left to decay through one
   `LeafDecayManager.ScheduleBox` over the written box (the hydration rule stays single-sourced);
   `FluidManager.ScheduleAround` per id change; `ChunkManager.MarkDirty` on each touched chunk and
   its border-adjacent ones (never `RemeshAfterBatchEdit`, which dirties the whole radius-15 light
   area for light-neutral blocks); `TreeGenerator.PoisonCanopy(x, z, 0)` + `LodManager.
   MarkEditedColumn` per deduplicated mesh-affecting column; no `TryTopple` and no
   `NotifyLodEdit`, since growth only fills air, leaves or cover and never writes surface solids.
   `World.TryGrowTree` (refuses while loading or for an unknown species) wires the managers and
   does one `ImmediateRemeshWithBorders` on the root chunk, only when mesh cells were written. The
   `grow` scenario op (`{ "op": "grow", "species", "pos": [x, z], "steps" }`) parses registry-free
   in `VisualScenario` (species checked by `VisualDirector` at run time, pos snapped to
   `TryGroundHeight + 1`, a refused grow throws). `trees.json` gained `oak_grown` (the cut oak
   grown 40 steps from the limb overview: +30 nodes, `base_aim` unaffected) and `sapling_1..3` on
   the felled column (Planted +4, Grew +16, Grew +59). `FluidManager.PendingCells` is a new test
   seam. Tests: `TreeGraphFromWorldTests` (round trip of every species, Thicken reproduces the
   stamped states, a cut tree never thins, cut and detached subtrees stay out, below-root nodes are
   features, an unloaded neighbour and a non-root cell refuse), `WorldTreeSpaceTests` (air and own
   leaves free, persistent/foreign/terrain/fluid/unloaded/reserved not, the orphan-parent rule,
   cover plantable but not free), `TreeGrowthTests` (an intact tree and a mined crown rewrite
   nothing, growth only appends and never thins, foreign blocks survive, a cut branch stays cut,
   grown leaves are what decay re-derives, determinism and per-call streams, the edit hooks fire,
   sapling rooting, soil rule, plantable root and cover takeover), `TreeGrowerTests` additions
   (feature-aware and floored Thicken, `GrowFurther` append/cap/determinism, `TreeEnergyAt`), the
   `grow` cases in `VisualScenarioTests`, and the shared `StampedTree` loaded-area helpers
   (`LoadAround`, `Fill`, `Snapshot`, `Min`/`Max`). Known residue: the far-LOD hook only removes
   stale canopy bumps, so a grown column shows no far canopy until reload (no add path, `TODO.md`
   `M-13`); the call counter is not persisted; growth does not avoid the player or mob bodies; a
   target column that crosses only a branch, not the root, plants a sapling; unmodified neighbour
   chunks re-stamp the original worldgen tree on load (the same residue as cutting); the World's
   immediate remesh duplicates one async rebuild.
7. **Done — wide trunks + new species.** `TreeRasterizer` is the only place a wide node becomes
   a core plus shells: `MaxShellRadius` (`RawBlockRadius(TreeSpeciesCompiler.MaxTrunkPx)`, now
   `internal`, assigned before the offset tables are built in the static ctor), `BlockRadius(d)`
   (`floor((d/16 - 1)/2)` clamped to it, so the `2R+1` footprint never exceeds the diameter and
   shells first appear at 48 px), `MinDiameterPx(r)`, `InFootprint` (square = Chebyshev `<= r`,
   circle = `dx²+dz² <= r²+r`), `ShellParentDir` (one step toward the core, along X when
   `|dx| >= |dz|`), `ShellOffsets` (static `ShellOffset(Dx, Dz, ParentDir)` tables sorted
   parent-first) and `TrunkChain` (the root, then repeated `ChildIn(Up)`). Only the trunk chain
   grows shells, so every core is alone at its Y and emission is a function of the node set,
   never of index order. The new `Rasterize(..., out int woodCount)` overload fixes the order as
   nodes in index order (growth, then feature nodes), then shells (thickness 15, shell bit,
   parent toward the core, emitted only when that parent is the core, an emitted shell or a
   full-cube feature node at the core's Y), then leaves; `TreeGenerator`
   (`TreeShape.WoodCount`), `TreeGrowthWriter` and `StampedTree` split wood from leaves at
   `woodCount`. `ITreeSpace.AcceptsShell` (default `IsFree`) is the shell placement test,
   `LeafCluster.Collect` takes an `exclude` set so no leaf lands on a shell, and `ReachOf` widens
   Horizontal by `BlockRadius(MaxPx)` (plus `TreeButtressMaxReach` for a buttress species). A
   species that never passes 16 px rasterises byte-identically, so oak, spruce and cactus
   worldgen is unchanged. `girthProfile` multiplies the grower's thickness by height fraction.
   `TreeGraph.FromWorld` skips shells, recovers a trunk core's floor as `MinDiameterPx(R)` from
   the longest shell run along the eight lines `(±k,0)`, `(0,±k)`, `(±k,±1)` (only when
   `BlockRadius(MaxPx) > 0`), and loads non-root nodes at the root's Y as feature nodes for a
   buttress species; the compiler requires `lowestBranchHeight >= 1` for those, which makes the
   rule exact. `WorldTreeSpace.AcceptsShell` also accepts the species' own attached shells (they
   stay non-free, so growth never grows into them), `TreeGrower.GrowFurther(applyFeatures)` runs
   the features only for a freshly planted sapling, and `TreeGrowthWriter` gained a parent-first
   shell pass with the **new-ring-only** rule: inside the footprint each original core had before
   the call it only re-codes existing shells, and it fills free cells only outside that ring or
   around a core new this call, so a mined shell or a partial slice cut stays mined. Felling and
   decay share one GL-free bounded search, `TreeSupport` (`TreeSupportResult` Supported /
   Unsupported / Unknown): links are parent edges (`TreeWoodShape.Connects`) plus vertical
   shell-to-shell adjacency, anchors are root cells and shells standing on real ground (solid,
   not tree wood, not leaves), and cells that lose every path to an anchor fall.
   `TopplingTree.Collect` fells the components that lose every anchor once the cut cell is gone
   (unloaded reads are no link; identical to the old subtree walk on thin trees; the canopy sweep
   skips shells), so a wide trunk falls only once a whole slice is cut;
   `LeafDecayManager.TickWood` crumbles by the same rule through a memoised parent-chain fast
   path (`WalkParentChain`) and a full search on a broken chain (unloaded = freeze), with
   per-frame `_supported`/`_unsupported` memos, and shells are never live tips. The caps
   decouple: `TreeGrowNodeCap` 512 (FromWorld and grown nodes), `TreeFellWoodCap` 2048 (nodes plus
   shells, the support search cap), `TreeFellTotalCap` 4096. `ButtressFeature` (replacing the
   placeholder; `TreeButtressFinChance`, `TreeButtressMaxReach`) walks full-cube feature fins
   along the four cardinal axes at the root's Y out to `R + len`, stopping at an occupied or
   non-free cell or one over a free cell, always drawing both random numbers per side; on-axis
   fins replace the on-axis shells and the off-axis shells chain through them.
   `BaobabGrowthLogic` (forks boosted at the crown base, trunk stops at `LowestBranchHeight +
   CrownLift`, limbs bounded by `LimbReach` Chebyshev from the root column, a climbing limb never
   turns back out) and `UmbrellaGrowthLogic` (trunk stops at `LowestBranchHeight + CrownLift`,
   limbs run straight and rarely climb) replace `PlaceholderGrowthLogic`; both only lower, zero
   or multiply weights. The pixel rim: `BlockType.PixelRimById`/`HasPixelRim`, filled from each
   species' `rim` in `ContentRegistry.LoadTreeSpecies` (two species sharing a wood must agree, or
   load throws naming both files); `TreeWoodShape.CanBevel`, `IsRimMass`, `RimCornerMask` (a
   corner is carved when neither of its two edge neighbours is same-id attached full-cube wood —
   the marching-squares outcome, and diagonals never matter because a cell only removes its own
   material), `RimInsetPx`, `RimBoxes` (stair-stepped full-height boxes, at most `MaxRimBoxes`,
   `TreeRimBevelPx` 8) and `RimSideFull` (the culling predicate) feed `GetBoxes`/`GetBoxesAt`/
   `OverlapsShape` (allocation-free; non-rim cells keep the zero-read path), and through them the
   raycast, outline and collision. `Chunk.EmitRimWood` meshes a bevelled shell from the 18³
   snapshot (`RimMaskInSnapshot`, `RimFaceFull`; exact caps that subtract a bevelled cell above or
   below), and the plain cube path culls a same-wood face only when the neighbour's facing side
   is full. Content: `assets/data/trees/baobab.json` (logic baobab, maxPx 80, circle, pixel rim,
   buttress) and `stone_pine.json` (logic umbrella, maxPx 24, circle, block rim, roots), both
   **without** a `worldgen` block, the blocks `baobab_log`, `baobab_leaves`, `stone_pine_log`,
   `stone_pine_leaves` mirroring oak's, and placeholder art from the deterministic
   `tools/generate-tree-textures.ps1` (oak tiles times a per-species tint, then
   `generate-leaf-textureless.ps1 -Sources baobab_leaves,stone_pine_leaves`, which leaves the oak
   and spruce tiles untouched); `TreeGenerator.LeavesBlock` is now taken only from worldgen
   species, so the far LOD's fallback canopy stays oak. `trees.json` gained `baobab_grown`,
   `baobab_rim`, `stone_pine_grown`, `baobab_cut_aim`, `baobab_partial_cut`,
   `baobab_felled_falling` and `baobab_felled_down` (baobab grown 120 steps at (-121, 40), stone
   pine 90 steps at (-90, 43), slice y 12 cut middle rows first). Tests: `TreeRasterizerTests`
   (shells only around wide trunk nodes, every shell chains to its core inside the footprint,
   parents step toward the core, circle inside square and rotation-symmetric, footprint never
   exceeds the diameter, shells never land on nodes/leaves/blocked cells, emission ignores node
   order; the order and parent tests run over wide species), `TreeGrowerTests` (a larger girth
   never thins a node or drops a shell, buttress fins are connected wood on the ground, a thin
   root grows no buttress, buttress never lands on blocked cells, the reach bound restated per
   node kind), `TreeGraphFromWorldTests` (a wide trunk round-trips, shells are never nodes),
   `TreeSpeciesCompilerTests` (buttress needs `lowestBranchHeight >= 1`), `TreeGrowthTests`
   (growth keeps a wide trunk and never thins it, a mined shell stays mined, planting applies
   features only once), `TreeFellingTests` (a partial slice fells nothing, a whole slice fells
   everything above, cutting the root core leaves the trunk standing, only real ground anchors
   a shell), `LeafDecayTests` (the orphan crumble agrees with felling for a thin limb and partial
   and whole slices, a standing wide trunk never crumbles), `TreeGrowthLogicTests` (umbrella
   crowns are wider than tall, umbrella and baobab trunks stop at the crown, baobab limbs stay in
   reach and never turn back out, every wide species grows shells), `TreeWoodShapeGeometryTests`
   (rim boxes tile the carved footprint, carving never adds material, insets shrink from the
   corner, mirroring, box bound, `RimSideFull` matches coverage), `TreeWoodShapeTests` (the rim
   mesh covers exactly the rim hitboxes), `HitboxCollisionTests` (pixel-rim shells collide with
   their rim boxes), `TreeSpeciesContentTests` (the pixel-rim flag follows the species), and the
   shared `WideTreeSpecies` harness. Known residue (`TODO.md` `M-14` for the first three): the
   felled mesh is drawn only inside its 16³ scratch chunk (the drops are intact); a wide core cut
   while its slice's shells stand leaves the crown unreachable for reactive growth, because
   `FromWorld` stops at the missing core, which cannot be refilled since the core above names it;
   a bevelled shell's vertical cull reads the edge neighbours of the cell above, which can lie in
   an edge-diagonal chunk that face-adjacent dirtying does not remesh, so a rare cap can go stale;
   cutting a wide trunk's root core drops only its root walk and fins (plus a couple of leaves the
   canopy sweep finds); buttress fins apply only at maturity or planting; mined shells never
   regrow inside the old ring; the selection outline shows the rim box seams; leaf particles for
   the new leaves use oak tiles; limbs over 16 px render as 16 px cubes.
8. **Done — ItemDebugger TREES tab + docs.** `TreeGrower` gained a deterministic scrub seam:
   `GrowSteps(species, x, y, z, space, worldSeed, steps, applyFeatures)` grows exactly N pulses
   (negative = 0) and `MaturityStepsAt` replays the rolled step count; both share one private core
   with `GrowToMaturity`, which still draws `RollMaturity` first and so stays byte-identical
   (`GrowSteps(MaturityStepsAt(...), true)` is exactly `GrowToMaturity`; without features a
   shorter grow's nodes are a prefix of a longer one's). `CellBatchMesher.Build` (`src/`, GL-free)
   meshes any `TreeCell` list with the real chunk mesher across scratch chunks at their real
   positions, padded by an empty ring with the edge and corner diagonals, output in world cell
   coordinates; `TopplingTree` is deliberately untouched (it is the ready fix for `M-14`(1)).
   `OpenGLTest.ItemDebugger/TreePreviewState.cs` is the tab's GL-free state: the
   `TreeSpeciesRegistry` species sorted by name, an explicit seed (`WorldSeed.Default`, REGROW
   adds one, never `WorldSeed.Apply`), a step clamped to 0..`MaturityStepsMax`, a per-step
   snapshot cache regrown in a fresh flat `VirtualTreeSpace`, features from the rolled maturity
   step on (selecting a species lands there, so it shows the worldgen tree; roots never grow on
   flat ground), grass ground cells, framing over the union of the current and the max-step
   bounds (`FitDistance`) and a `Version` the renderer rebuilds on. `DebugSceneRenderer.RenderTree`
   uploads the `CellBatchMesher` output on a `Version` change; the plain program's `uBiomeTint` is
   now set white (unset, tinted grass and leaves drew black), and `ToDisplaySpaceTint` rebakes the
   linear biome tint for the debugger's undecoded atlas. The UI has the third tab, the species
   grid, STEP -/STEP +/MATURE/REGROW (NEW SEED), the Step slider and wheel zoom;
   `ITEM_DEBUGGER_SELECT` now tries items, entities, then species (`tree/` forces the species,
   e.g. `tree/cactus`), and `ITEM_DEBUGGER_TREE_STEP` takes an integer or `mature`. Captures of
   oak, baobab and `tree/cactus` at step 10 rendered whole trees with no seam clipping, tinted
   grass and leaves, the oak's thin tubes and the baobab's bevelled core/shell trunk with its
   buttress fins. `visual-examples/trees.json` already covered the whole Verification list, so
   it needed no change. AGENTS.md was rewritten for the tree rework: the `grow` op, the TREES
   tab and its env vars, tree-wood hardness, the mesher and biome-tint mentions, the Layout tree,
   the `tree_wood`/`wood{}` block schema rows, a `### Tree species schema` and a `### Trees`
   engine section, the `WoodState`/`LeafState` state-byte users, the
   `LeafDecayManager.ScheduleAround` edit-path rule, the six `GenerationalCache` sites, the LOD
   canopy proxy and the Biomes vegetation text. Tests: `TreeGrowerTests` (the seam),
   `CellBatchMesherTests` (world-space output, no exposed cell dropped, every touched chunk
   meshed, determinism), `TreePreviewStateTests` (determinism, scrubbing, clamping, maturity,
   seed, bounds, ground, framing, name lookup, version).

## Perf baseline

Measured with `visual-examples/tree-perf.json` (a fixed flight-sprint fly-through: forest, taiga,
desert, 12 s each after an 8 s settle) in Release:
`cd OpenGLTest && VOXEL_DEBUG=1 VOXEL_TIME=147 VOXEL_SCRIPT=visual-examples/tree-perf.json dotnet run -c Release`,
reading the console line's `wgen` token (`WorldgenStats`, published by `FrameProfiler`).

Machine: `Build: Release | debugger False | serverGC False | latency SustainedLowLatency | cpus 12`,
`GPU: Quadro T1000 with Max-Q Design/PCIe/SSE2 | NVIDIA Corporation | GL 4.6.0 NVIDIA 597.06`.

Columns: *chunks* is the lifetime `GenerateBlockData` count at exit (every chunk, the many
above-terrain early exits included, which is why ms/chunk is low); *ms/chunk* and *tree ms/chunk*
are lifetime totals over that count (tree = the tree + cactus stamp); *tree ms/s*, *grow ms/s* and
*misses/s* are medians of the once-a-second windows that generated any chunk (summed over the
generation workers, so ms/s can exceed 1000); *grows* is every shape build (tree + cactus cache
misses, on the generation workers and the LOD builder's canopy sweep alike; from phase 4 one entry
per tree grown, solo or in a cell batch); *misses/s* counts the solo tree shape cache only. From
phase 4 the token also carries `batch` (cell-batch cache misses), `dup` (batch builds started
while another thread was already building the same cell) and `brot` (batch cache generation
rotations), each per window.

| run | chunks | ms/chunk | tree ms/chunk | tree ms/s | grows | grow ms/tree | grow ms/s | misses/s |
|---|---|---|---|---|---|---|---|---|
| old 1 | 189247 | 0.66 | 0.124 | 435.8 | 133242 | 0.004 | 7.4 | 1604 |
| old 2 | 187378 | 0.70 | 0.131 | 464.7 | 123278 | 0.004 | 7.1 | 1602 |
| old 3 | 186988 | 0.71 | 0.135 | 484.2 | 117306 | 0.004 | 7.3 | 1542 |

Phase 3 result (same machine and command, after the cutover):

| run | chunks | ms/chunk | tree ms/chunk | tree ms/s | grows | grow ms/tree | grow ms/s | misses/s |
|---|---|---|---|---|---|---|---|---|
| new 1 | 189306 | 0.65 | 0.083 | 323.6 | 144773 | 0.288 | 721.2 | 2448 |
| new 2 | 189101 | 0.67 | 0.080 | 309.6 | 138187 | 0.296 | 704.7 | 2281 |
| new 3 | 188373 | 0.69 | 0.086 | 317.9 | 132462 | 0.313 | 767.5 | 2304 |

Overall ms/chunk is unchanged (0.65-0.69 vs 0.66-0.71) and the stamp itself got cheaper (tree
ms/s ~310-325 vs ~435-485), but growing a tree now costs ~0.3 ms instead of ~0.004 ms, so grow
time rose about 100x to ~700-770 ms/s with ~1.5x the shape-cache misses. The stamp's tree time
includes every grow the generation workers trigger, so at least ~400 ms/s of grow time (over half)
runs on the single LOD builder thread's far-ring canopy sweep: that is phase 4's LOD proxy target.

Phase 4 result (same machine and command, cell batches + the far-ring canopy proxy):

| run | chunks | ms/chunk | tree ms/chunk | tree ms/s | grows | grow ms/tree | grow ms/s | misses/s |
|---|---|---|---|---|---|---|---|---|
| p4 1 | 188917 | 0.61 | 0.048 | 11.0 | 115252 | 0.274 | 369.9 | 694 |
| p4 2 | 188338 | 0.68 | 0.060 | 12.1 | 113392 | 0.312 | 438.1 | 757 |
| p4 3 | 187786 | 0.70 | 0.061 | 12.8 | 111460 | 0.325 | 496.5 | 750 |

Batch cache per run: 1685 / 1673 / 1656 misses (median 24-26/s), `dup` 109 / 127 / 117
(6.5 / 7.6 / 7.1 % of the misses), `brot` 21 / 20 / 20. A measurement run before these three gave
the same picture (0.62 ms/chunk, 1694 misses, dup 6.9 %, brot 22).

Overall ms/chunk is unchanged (median 0.68 vs 0.67, range 0.61-0.70 vs 0.65-0.69), so the 25 %
rule did not trip. The expected doubling of grows did not happen. Every tree is still grown twice
on the near side, solo as a neighbour obstacle and again in its batch, but the far rings no longer
grow anything, and that more than pays for it: grows fell from 132-145k to 111-115k, solo
misses/s to about a third (694-757 vs 2281-2448) and grow ms/s from 705-768 to 370-497, while
grow ms/tree stays at ~0.3 ms (growing against more obstacles costs no more). The stamp itself is
now nearly free on the generation workers (tree ms/s 11-13 vs 310-325, tree ms/chunk 0.05-0.06 vs
0.08). The first LOD ring reads batches out to 512 blocks, so the LOD builder has usually grown a
cell's batch before a generation worker stamps it. Total grow time is 31.6-36.2 s per run against
only 9.1-11.5 s of total stamp time (which includes every grow the workers trigger), so at least
two thirds of the growing now runs on that one LOD thread. Duplicate batch builds stay
under the 10 % gate (they cluster in the cold-start windows, where all four workers open on the
same cells: the first window of each run reads 35-56 batch misses with 20-30 dup), so the per-key single-flight was **not**
added and the miss path stays lock-free. The rotations equal the misses divided by the 84-insert
generation budget (capacity 336 / G 4), which is ordinary turnover as the flight streams new
cells; nothing here separates re-misses from first misses, but the steady ~25 misses/s while
flying gives no sign of thrash. Measured batch size: forest ~21 trees and ~5250 cells (~82 KB) on
average, max ~250 KB; taiga ~79 trees and ~18400 cells (~288 KB), max ~350 KB. Capacity 336 bounds
an all-taiga cache at ~95 MB.

## Critical files
- `OpenGLTest/src/WorldGen/TreeGenerator.cs`: becomes the placement and stamping front-end
  over the new engine.
- `OpenGLTest/src/WorldGen/CactusGenerator.cs`: deleted.
- `OpenGLTest/src/Content/WoodShape.cs`: replaced by `TreeWoodShape`.
- Content plumbing: `src/Content/ContentRegistry.cs`, `BlockDefinition.cs`, `BlockRenderer.cs`
  and `src/BlockType.cs` (wood family/level tables removed).
- Meshing: `src/Chunk.cs` (the tapered-wood emitter becomes the `tree_wood` emitter).
- Gameplay: `src/TopplingTree.cs`, `src/World.cs` (`FellWood`, `RemoveBlock`, placement,
  decay hooks) and `src/Mining.cs`.
- LOD: `src/LodRegion.cs` (canopy reads).
- Saves: `src/Saves/ChunkSaveCodec.cs` / `WorldInfo` (format version).
- `OpenGLTest/assets/data/blocks/*`: the wood, leaves and cactus JSON.
- New: `assets/data/trees/*.json` and `src/WorldGen/Trees/*`.
- Tests: `OpenGLTest.Tests/WoodShapeTests.cs` and `TopplingTreeTintTests.cs` get rewritten
  against the new rules.

Reuse: `GenerationalCache`, `SplitMix64`, `Chunk.TiledUv`/`FaceLight`, the `FluidManager`
scheduled-cell pattern, `DropManager.SpawnLoot`, `TopplingTree.BuildMeshData`'s scratch-chunk
meshing and `Chunk.RemeshAfterBatchEdit`.

## Verification
Every phase:
- `dotnet build OpenGLTest.sln -c Release`;
- `dotnet test OpenGLTest.Tests/silkgltriangle.Tests.csproj -c Release`. Rule tests only, with
  no pinned constants (the functional-tests skill).

From phase 3 on:
- run the game in Release and check the `X` profiler. Phase 4 has the explicit perf
  comparison against the phase-3 baseline.
- `visual-examples/grass-tint.json` (for felled-tree and canopy regressions) and a new
  `visual-examples/trees.json` (`VOXEL_TIME=147`): oak, spruce, cactus, a cut limb, a felled
  trunk, leaf decay over time, and from phase 7 a `grow`n baobab and stone pine.

Phase 8: `ITEM_DEBUGGER_SELECT=<species>` + `ITEM_DEBUGGER_SHOT` captures of the TREES tab were
run (oak, baobab, and `tree/cactus` with `ITEM_DEBUGGER_TREE_STEP=10`) and inspected.
