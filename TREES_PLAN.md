# TREES_PLAN.md — tree generation & wood shape rework

## Status

Phase 1 done (content + codecs, headless). Phases 2-8 pending. Each phase lands as its own
commit on `tree-rework`; finished phases are collapsed into short summaries here.

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
- bits 0-2: hydration, the distance to a **live tip** (a childless wood cell at or under the
  species' `leafCluster.tipMaxPx`). A thick stub left by a cut is not a tip, so its leaves
  decay.
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
- **`TreeRasterizer`:** turns the graph into cells + state (wood, then core/shell girth, then
  features, then leaves). It is shared by worldgen stamping, the far LOD and the debugger.
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
  `FellWood`, placement, `TntManager.Detonate` and in-world growth.

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
2. **Growth engine (headless).** `TreeGraph`, `ITreeSpace` (virtual), `TreeGrower.Pulse` with
   the pipe model and girth, the `oak`/`conifer`/`cactus` logics, the leaf-cluster rule,
   `TreeRasterizer` (no wide trunks yet) and the `roots` feature. Tests: determinism, a parent
   is never thinner than its child, every cell reaches the root, obstacles respected, leaves
   only within hydration of a tip.
3. **Cutover.** Record the worldgen baseline first: a worker-side tree-time counter plus
   chunk-gen throughput on a fixed fly-through scenario, written into `TREES_PLAN.md`. Then:
   - the new oak/spruce/cactus wood + leaf blocks and the renames;
   - the `tree_wood` renderer and `TreeWoodShape` hitboxes;
   - mining hardness and loot by `(id, state)`, and detached placement;
   - worldgen stamping solo species shapes;
   - graph-based felling in `TopplingTree.Collect`;
   - the far LOD reading rasterised canopies;
   - a world save format version bump.

   Delete the six taper blocks, the cactus taper blocks, `WoodShape`'s level rule,
   `wood.taperLevel`/`family`, `CactusGenerator`, `BuildShape` and `BuildSpruceShape`.
4. **Tree vs tree + LOD proxy + perf.** The cell-batch cache with solo-neighbour obstacles, and
   the LOD level-2/3 proxy. Measure against the phase-3 baseline, record the result in
   `TREES_PLAN.md` and **stop for the user's call** if it looks bad.
5. **Leaf decay.** `LeafDecayManager`, the edit-path hooks, persistent placed leaves.
6. **Reactive growth.** `WorldTreeSpace`, `TreeGraph.FromWorld`, a growth write path that
   remeshes and schedules decay and fluids, and the `grow` scenario op
   (`{ "op": "grow", "species", "pos": [x, z], "steps" }`) with `VisualScenarioTests`
   parsing.
7. **Wide trunks + new species.** Core/shell rasterisation (circle and square), the pixel-rim
   bevel in the mesher and the hitboxes, shell-aware felling, `girthProfile`, the `buttress`
   feature, the `baobab` and `umbrella` logics, the baobab and stone_pine species without a
   `worldgen` block, and the placeholder art script.
8. **ItemDebugger TREES tab + docs.** A third tab that grows a species pulse by pulse with a
   step slider, meshed through the scratch-chunk path `TopplingTree.BuildMeshData` already
   uses, plus a GL-free tested state class. Add `visual-examples/trees.json`, and update
   AGENTS.md (Content schema, Layout, invariants).

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

Phase 8: an `ITEM_DEBUGGER_SELECT=<species>` + `ITEM_DEBUGGER_SHOT` capture of the TREES tab.
