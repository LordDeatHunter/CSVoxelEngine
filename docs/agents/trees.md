# Trees

The tree species schema and the tree growth engine, rasterizer, worldgen, leaf decay, felling and reactive growth.

### Tree species schema (`assets/data/trees/<species>.json`)

One file per species; the file name is the species name (`TreeSpeciesRegistry.ByName`, which
`ITEM_DEBUGGER_SELECT` and the `grow` op match). `ContentRegistry.Load` reads the folder **after
the blocks** (`TreeSpeciesLoader.LoadTreeSpecies`; a missing folder is an empty registry), compiles each file through
the pure `TreeSpeciesCompiler.Compile` (DTO `TreeSpeciesDefinition.cs`) into an immutable
`TreeSpecies`, and every error starts `Tree species '<path>'`. Block references take an optional
`block/` prefix and never resolve to an item. Eleven species ship: `oak` (the forest), `spruce` (the
taiga), the leafless `cactus` (the desert), the leafless `dead_tree` (the desert, the frozen
plains and the steppe, which borrows the oak's wood — `TreeSpeciesRegistry.ForBlock` resolves a
shared wood to the leafy owner, so the growth item never turns an oak into a snag) and the sparse
`stone_pine` (the savanna, its umbrella crown an acacia stand-in) generate in the world; the `palm`
generates only through its `worldgen.oasis` disc; `baobab` has **no `worldgen` block** and is
only ever grown (`TreeGrowthWriter`, the `grow` op, the item debugger); and the hell's `fungal_tree`
and leafless `charred_tree` have no `worldgen` block either: `HellTrees` plants them from the hell
biome palettes (see "Hell flora"); nor do the sky's broad `sky_oak` and glowing `crystal_tree`, which
`SkyTrees` plants from the sky biome palettes (see "Sky biomes"). See
`### Trees` for what the fields drive.

| field | default | meaning |
|---|---|---|
| `wood` | required | the wood block; must use `renderer: "tree_wood"` and must not be a fluid. Two species may share one wood only if they agree on `thickness.rim` |
| `leaves` | none | the leaf block (not the wood, not a fluid). Omitted means a leafless species (cactus), and then `leafCluster` must be omitted too; present means `leafCluster` is required. A leaves block is what `TreeGenerator.IsLeaf` answers for, and so the block whose state byte is a `LeafState` |
| `growth.logic` | required | the registered `IGrowthLogic` direction chooser (`GrowthLogicRegistry`): `oak`, `conifer`, `cactus` (a saguaro pitchfork: each arm leaves the stem level for `CactusGrowthLogic.ArmRun` cells — a pure hash of position, height and side, no RNG draw — then climbs straight up as a prong parallel to the stem and below its top, on distinct sides at least `ArmSpacing` apart), `umbrella` (stone pine: inside `UmbrellaGrowthLogic.BareReach` of the trunk column a limb never forks and climbs as a bare staircase, at most `LimbsPerTrunkNode` limbs a trunk node on distinct sides, so the `pad` leaves sit only at the outer limb ends) or `baobab` |
| `growth.signalEnergy` | required, >= 1 | the energy a growth signal starts with: one per step out from the root, so it bounds the tree's reach. Each tree also rolls its own energy, clamped to 1..this (`IGrowthLogic.TreeEnergy`) |
| `growth.upProbability` | required, >= 0 | weight of growing straight up against the four sideways choices (weight 1 each) |
| `growth.lowestBranchHeight` | required, >= 0 | height above the root below which nothing forks; a `buttress` species needs >= 1 |
| `growth.maturitySteps` | required | `[min, max]`, `1 <= min <= max`: the pulse count a tree grows to maturity, rolled per tree |
| `thickness.tipPx` | required, 1..16 | diameter of a branch tip in pixels |
| `thickness.pipeExponent` | required, finite > 0 | exponent of the pipe model that sums child diameters into the parent's |
| `thickness.maxPx` | required, `tipPx`..128 | largest diameter in pixels; past 16 a node is a full cube, and from 48 a trunk node grows shells (wide trunk) |
| `thickness.girthProfile` | `[[0, 1]]` | `[[height01, multiplier], ...]` along the tree's height: heights start at exactly 0, strictly increase within 0..1, multipliers > 0 (`TreeGrower.Girth`) |
| `thickness.trunkShape` | `circle` | `circle` or `square` (`TrunkShape`): a wide trunk's block footprint |
| `thickness.rim` | `block` | `block` or `pixel` (`TrunkRim`): `pixel` bevels the outer shells of a wide trunk with pixel-stepped quarter-rounds |
| `leafCluster.shape` | `blob` | `blob`, `cone` or `pad` (`LeafClusterShape`; a pad is a flat cap on the tip: a disc of radius `max(1, r - 1)` at the tip's level and one of radius `r` above it, nothing below, so the limb under it stays bare) |
| `leafCluster.radius` | required, 1..8 | the full cluster radius in blocks around each live tip, reached once the trunk is thick enough (see `minRadius`) |
| `leafCluster.minRadius` | `radius` | 1..`radius`: the cluster radius of a tree whose root is 1 px. The tree's one radius (`LeafCluster.ClusterRadius`) runs from this up to `radius` with the root cell's diameter, so a young tree wears a small crown; the default means no scaling |
| `leafCluster.fullRadiusPx` | 16 | 1..`min(maxPx, 16)`: the root diameter in pixels at which the cluster reaches `radius`. Capped at one block because that is the most the wood state byte stores, so a wide-trunk species (baobab) only scales as a seedling |
| `leafCluster.hydration` | required, 1..7 | hydration a leaf right at a tip gets (`LeafState.MaxHydration` is 7); it falls by one per Manhattan step, and a leaf needs at least 1 to exist |
| `leafCluster.tipMaxPx` | required, 1..`maxPx` | the thickest childless wood (in pixels) that still counts as a live tip and grows a cluster |
| `features[].type` | none | a registered post-growth `ITreeFeature` (`TreeFeatureRegistry`): `roots` (feature nodes walked down through free cells) or `buttress` (full-cube fins along the four axes at the root's level); each at most once per species |
| `features[].depth` | 0 | >= 0; `roots` needs >= 1 (its depth in blocks), `buttress` takes exactly 0 |
| `lod.crownHeight` | required | `[min, max]`, `0 <= min <= max`: the far rings' proxy crown height, rolled per tree from its placement hash |
| `lod.crownRadius` | required, >= 0 | the proxy crown radius in blocks |
| `lod.profile` | `dome` | `dome`, `cone` or `column` (`LodCrownProfile`, the silhouette `LodCrownProxy.TopOffset` draws) |
| `worldgen.biomes` | required | non-empty, duplicate-free list of `Biome` names (`forest`, `desert`, `ocean`, `taiga`, `frozen`) the species is planted in |
| `worldgen.density` | required, 0..1 | chance per candidate column |
| `worldgen.spacing` | required, >= 0 | minimum gap to another trunk in blocks (the lowest placement hash wins) |
| `worldgen.maxSlope` | required, >= 0 | largest ground height step across the footprint |
| `worldgen.minSoil` | 0 | soil depth the column needs, >= 0 (oak and spruce 2, so they never root on bare rock) |
| `worldgen.minGround` / `maxGround` | required | the ground height band the species grows in, `minGround <= maxGround` |
| `worldgen.openness` | 1 | 0..1, the share of the forest-openness thinning (`TerrainGenerator.ForestOpenness`) applied to this species (spruce 0.5, cactus 0) |
| `worldgen.spawnClear` | true | whether the species keeps out of the spawn clearing (`TreeSpawnClearRadius`) |
| `worldgen.fallenLog` | none | the block worldgen lays as this species' fallen hollow log on the floor of the biomes it is the tree of (`TreeWorldgenRules.FallenLogId`, read by `FallenLogGenerator.HollowLogFor`); it must be a `facing: "horizontal"` model block, not tree wood or a fluid, or the species is refused at load, since worldgen writes its facing into the state byte. Omitted means that biome gets no fallen logs (oak `hollow_oak_log`, spruce `hollow_spruce_log`, stone pine `hollow_stone_pine_log`) |

`worldgen` as a whole is optional: leaving it out keeps the species out of worldgen entirely
(`TreeSpecies.Worldgen` is null and `TreeGenerator.TryGetTree` never picks it).

### Trees

Trees are grown, not drawn from templates: one generic, deterministic, step-based engine under
`src/WorldGen/Trees/` grows every species from its JSON (`### Tree species schema` above), and the
world stores the result as **plain blocks plus the state byte** — one `tree_wood` block and one
leaves block per species, no per-tree registry or entity.

- **Wood state** (`WoodState`): bits 0-3 the thickness code `n` (diameter `n + 1` px, 15 =
  `FullCube`), bits 4-6 the parent direction in `Chunk.CubeFaces` order (0-5, 6 = `ParentRoot`,
  7 = `ParentDetached`) and bit 7 `ShellBit` (a girth cell of a wide trunk). The one connection
  rule is `TreeWoodShape.Connects`: same id, neither side detached, and one cell's parent points at
  the other — so siblings and two neighboring trees never weld, and the tree graph can be read back
  out of the blocks (`TreeGraph.FromWorld`). Placed wood is `WoodState.Detached`, a full cube that
  never welds or fells.
- **Leaf state** (`LeafState`): bits 0-2 hydration, bit 3 `PersistentBit` (player-placed leaves,
  which never decay). Hydration is `leafCluster.hydration` minus the Manhattan distance to the
  nearest **live tip** whose cluster shape holds the cell (`LeafCluster.HydrationFromTip`, the one
  per-tip formula, behind `HydrationAt`, `Collect` and decay alike). The cluster radius is one per
  tree and follows the trunk: `LeafCluster.ClusterRadius` scales `minRadius..radius` by the
  tree's **support**, the root cell's stored diameter (`LeafCluster.SupportPx`, the quantized
  root `ThicknessPx` = `WoodState.DiameterPx` of the root cell, capped at 16 px), up to
  `fullRadiusPx`. It changes shape membership only (hydration stays `hydration` minus the
  distance), and every shape is nested — a smaller radius is a subset of a larger one — which is
  what keeps decay's full-radius, best-first tip table exact; a live tip is a non-detached,
  non-shell cell of the species' wood at or under `tipMaxPx` with no child, so a thick stub left by
  a cut is not a tip and its leaves decay. The mesher never reads `LeafState`.

**Growth engine** (GL-free, headless). `TreeGraph` holds int-indexed nodes (cell, parent and
parent direction, children, the girth-free `PipePx`, the displayed `ThicknessPx`, `IsFeature`, a
`FloorPx` that a tree rebuilt from the world never thins below). `ITreeSpace.IsFree` is the obstacle
view, implemented by `VirtualTreeSpace` (a ground-height delegate, optional carved cells and an
occupied set; `FromTerrain` reads `ColumnCache.Get`) for worldgen and the item debugger, and by
`WorldTreeSpace` for live chunks. `TreeGrower.Pulse` sends one Dynamic-Trees style signal up from
the root: each step it weighs the six directions (sideways 1, up `upProbability`, existing limbs
followed by their `PipePx`, no forks under `lowestBranchHeight`, never down, blocked cells masked)
and lets the species' `IGrowthLogic.AdjustWeights` lower, zero or scale them, then either follows a
child or grows exactly one new face-adjacent node and stops, thickening the path on the way back
down. Thickness follows the pipe model on `PipePx` (`pipeExponent`), and the displayed
`ThicknessPx = min(maxPx, max(tipPx, PipePx * Girth))` is raised to the thickest child, so the
girth profile applies once and a parent is never thinner than a child. Everything is deterministic
from `TreeGrower.SeedFor(x, y, z, worldSeed, species)` (position, world seed and an FNV-1a hash of
the species name) through one `SplitMix64` stream:
- `GrowToMaturity` draws the maturity step count and the per-tree energy first, runs that many
  pulses, one final bottom-up `Thicken` (so the girth profile sees the final height), then the
  species' `features` on the same stream;
- `GrowSteps(..., steps, applyFeatures)` replays exactly that stream for any pulse count (the item
  debugger's scrub path); `GrowSteps(MaturityStepsAt(...), applyFeatures: true)` *is*
  `GrowToMaturity`, and without features a shorter grow's nodes are a prefix of a longer one's.
  `MaturityStepsAt` and `TreeEnergyAt` replay the two rolls on their own;
- `GrowFurther` is the reactive path: `Thicken`, N pulses under a node cap, `Thicken`, and the
  features only when asked (a freshly planted sapling), since a feature is not idempotent.

**Rasterizer.** `TreeRasterizer.Rasterize(graph, species, space, out woodCount)` turns a graph into
`TreeCell`s (cell, block, state) in a load-bearing order: the graph nodes in index order (growth
nodes, then feature nodes), then the shells, which end the wood at `woodCount`, then the leaves
(`LeafCluster.Collect`, which never lands a leaf on a shell). The thickness code is
`ThicknessCode(px)` = `ceil(px) - 1` clamped at the full cube. Worldgen stamping, the first far-LOD
ring, reactive growth, the felling and decay tests and the item debugger all share it.
`ReachOf(species)` is the tree's (horizontal, up, down) bound from its root — the energy plus the
leaf radius, widened by the shell radius and the buttress reach — which sizes chunk-border margins
and the decay schedule.

**Wide trunks** are a core plus shells, and only the rasterizer makes them. A node on the
**trunk chain** (`TrunkChain`: the root, then repeated straight-up children) whose diameter `d`
gives `BlockRadius(d)` = `floor((d/16 - 1) / 2)` (capped at `MaxShellRadius`) above 0 stays the
graph node as the **core**, and shell cells fill its footprint at the same Y — `InFootprint`:
Chebyshev `<= r` for a `square` trunk, `dx² + dz² <= r² + r` for a `circle` — so the `2r + 1`
footprint never exceeds the diameter and shells first appear at 48 px. A shell is a full cube with
the shell bit and its parent one step toward the core (`ShellParentDir`, along X when
`|dx| >= |dz|`), so a diagonal shell chains through an edge shell; the `ShellOffsets` tables are
sorted parent-first, and a shell is only emitted when its parent was (or is a full-cube feature
node at the core's Y, a buttress fin) and the space `AcceptsShell`. Because only the trunk chain
grows shells, every core is alone at its Y and the shells are a function of the node set, never of
index order; a wide limb elsewhere renders as a 16 px full cube, and a species that never passes
16 px rasterizes exactly as before shells existed.

**Mesh, collision and the pixel rim.** `tree_wood` meshes on the per-voxel path, never greedy:
`TreeWoodEmitter.EmitTreeWood` draws a thin cell as a node box sized `TreeWoodShape.HalfWidth(t)` plus one
capless tube toward every connection at the thinner of the two widths (`TubeHalf`) and a same-width
capless plug toward the solid occluding non-wood block below a root cell (`TreeWoodShape.PlugMask`: the
ground only, never the sides or top, and never a limb or trunk cell lying over a ledge), so a thin trunk or root meets the ground it rests on instead of floating the node
clear of it, with cropped
UVs and smooth light; a full-thickness or detached cell takes the model path and culls against a
same-id full cube. A wood block whose species sets `rim: "pixel"` (`BlockType.PixelRimById`, filled
by `TreeSpeciesLoader.LoadTreeSpecies`) gets its outer shells beveled from their eight horizontal
neighbors: `TreeWoodShape.RimCornerMask` carves a corner when neither of its two edge neighbors
is same-id attached full-cube wood (a cell only ever removes its own material, so diagonals never
matter), `RimBoxes` turns the mask into stair-stepped full-height boxes (`RimInsetPx`, at most
`MaxRimBoxes`, `TreeRimBevelPx` deep), and `TreeWoodEmitter.EmitRimWood` meshes exactly those boxes from the
18³ snapshot with caps that subtract a beveled cell above or below. The hitboxes match the mesh:
`TreeWoodShape.GetBoxes`/`GetBoxesAt`/`OverlapsShape` (node, tubes and rim boxes, allocation-free)
feed the raycast, the selection outline and `BodyCollision.BodyCollides`.

**Worldgen** (`TreeGenerator`, a pure function of position like the rest of worldgen).
`TreeGenerator.TryGetTree(x, z, ...)` keeps the placement gates per species from its `worldgen`
block (one hash stream, biome -> species in ordinal name order, density, lowest-hash spacing,
slope, soil, ground band, water, openness, the spawn clearing). Every tree of a 64x64 XZ cell
(`TreeCellShift`/`TreeCellBlocks`, `TreeGenerator.CellOf`) then grows together as one `TreeBatch`
in placement-hash order inside one `VirtualTreeSpace`, each grown tree's cells occupied before the
next grows, so neighboring crowns grow around each other; the trees of the eight neighboring
cells count as obstacles only as their cached **solo** shapes (`TreeBatches.ShapeOf`), which keeps a batch a
pure function of (cell, seed) and independent of chunk order. `Stamp` writes wood with its state
into air or leaves, then leaves into air, in a fixed scan order (the root walk extends the trunk
down through cave air only inside the chunk holding the base). The far LOD's first rings read the
stamped canopy (`TreeGenerator.TryGetCanopy`), while the coarse rings (`LodLevelSpec.ProxyCanopy`)
read a proxy rolled from the same placement: the species' `lod` crown height and radius through
`LodCrownProxy.TopOffset`'s dome/cone/column silhouette, so the far sweep never grows a tree. The
solo shapes, the batches and both canopy tables are `GenerationalCache` sites (`TreeShapeCache*`,
`TreeBatchCache*`, `TreeCanopyCache*`, `TreeProxyCanopyCache*` in `WorldGenerationConfig`).

**Leaf decay** (`Simulation/LeafDecay/LeafDecayManager.cs`, owned by `World`, main thread only, never while
loading, the `leafDecay` profiler row) is a `FluidManager`-style scheduled-cell simulation. An edit
schedules the cells around it (`ScheduleAround`/`ScheduleBox`, which grow the box by
`ScheduleReach` so callers never know a radius — see the edit-path invariant below); a tick
re-derives each scheduled non-persistent leaf's hydration as the best `HydrationFromTip` over the
live tips in its species' cluster box (a full-radius offset table, best first, with each candidate
tip's own tree radius re-checked: `TipHydration.TryTipSupport` walks the tip's parent chain under the
`OrphanWoodCheck.WalkParentChain` link rules to the root cell and reads its diameter, memoized per `Update` in
its `_tipSupport`; an unloaded read freezes the leaf), so a settled canopy reads back bit for bit and costs nothing,
and a leaf left at 0 decays to air (at most `LeafDecayMaxRemovalsPerTick` a tick, in a scattered
order, so a crown dissolves speckled), rolling its loot through `DropManager.SpawnLoot` with a few
leaf particles. The same manager crumbles **orphaned wood**: a scheduled non-root, non-detached
`tree_wood` cell that `TreeSupport` finds no longer anchored (`TickWood` through `OrphanWoodCheck`, a memoized parent-chain
fast path plus the full search on a broken chain) drops with its thickness-band loot and schedules
its reach box, so a blasted subtree crumbles outward a layer per tick. Any read in an unloaded
chunk drops the cell from the schedule (it freezes, never decays), and a chunk load schedules
nothing. Tuning: `LeafDecayTickSeconds`, `MaxLeafDecayTicksPerFrame`, `MaxLeafDecayCellsPerFrame`,
`MaxLeafDecayBacklogCells`.

**Stumps** (`TreeStump.TryFind`, `BlockEditor.LeaveStump`). Breaking the lowest log of a natural trunk (its root, never a shell of a wide trunk) fells everything it held up and keeps the root cell, overwritten in place by the block its log names in `wood.stump` (`BlockType.StumpId`, a block with `stump: true`); the root does not fall with the tree (the fall pivots on the cell above it), so the stump keeps that log. A cut higher up leaves the root standing as the log it is, and a wood with no `wood.stump` (a cactus) leaves no stump. Breaking a stump drops its log (`loot/<species>_stump.json`).

**The log on a stump** (`Simulation/Stumps/`, `StumpManager`, `StumpInteraction`, `StumpConfig`). A right-click on a stump with a log item in hand (any `tree_wood` block whose `StumpId` is set, so a cactus is not one, or a block with `stumpLog` such as a hollow log, which stands upright) stands one log on it, where the aim meets the stump's top (clamped to stay on it, turned by the aim's yaw), spending one from the hand unless creative; a right-click on a stump already holding a log lifts it back into the inventory (or drops it when the pool is full); sneaking leaves the stump alone so a block can still be placed against it. An **axe** blow (`Mining.IsAxe`) splits it instead, over `BlockType.SplitHits` blows (4 by default through `wood.splitHits`, 2 for a hollow log): each blow plays the slow overhead `SwordSwing.AxeSplit`, chips the log and opens it a little more (`StumpLog.SplitHits`, saved as `hits` and sent in the snapshot; `StationPasses.RenderStumpLogs` draws two half-width halves that move and lean apart, `StumpConfig.SplitGapPerHit`/`SplitLeanPerHit`), and the last one splits it. Each blow: `LeftClickAction.UpdateStumpHit` starts the slow overhead `SwordSwing.AxeSplit` (`StumpConfig.SplitSwingSeconds`) and `UpdateSplit` lands it at the swing's impact, bursting wood chips off the stump (`ParticleConfig.StumpSplitDebris`, the log's own block as the debris) and sending the blow home; the authoritative world (`StumpInteraction.TryHit` with `split`, the server judging the held axe itself) removes the log and throws out the lumber rolled from the log's `wood.split` table (`StumpConfig.SplitScatter`/`SplitLift`) as drops, and the button must be released after the last blow before it can mine the stump (`_awaitRelease`). Any other left-click blow wobbles it (`StumpManager.Wobble`, a ring-down drawn by `StationPasses.RenderStumpLogs`, `MathUtils.DampedWobble`) instead of mining the stump; the stump can only be mined bare. The state is a block-entity registry like the querns: authoritative in singleplayer and on the server, saved in `stations.json` (`stumps`), sent in the station snapshot, and swept (its log dropped) when the block stops being a stump. A connected client claims the click but changes nothing until the server's snapshot arrives; `NetIntentKind.StumpHit` carries the blow. `StumpManagerTests`; `visual-examples/stump.json`. The stump model stays inside its cell, so the flare is a 16 px base under a 14 px body, 5 px high in all.

**Felling** (`BlockEditor.FellWood` -> `TopplingTree.Collect`). Mining tree wood fells the cells that
lose every path to an anchor once the cut cell is gone, through the GL-free bounded search
`TreeSupport` (`TreeSupportResult` Supported / Unsupported / Unknown): links are parent edges
(`TreeSupport.Links`) plus vertical shell-to-shell adjacency, anchors are root cells and shells
standing on real ground (`TreeSupport.IsAnchor`: solid, neither tree wood nor leaves), and an
unloaded read is no link. So a cut limb drops only the limb, a cut 1-wide trunk drops the crown,
and a wide trunk falls only once a whole horizontal slice of core and shells is cut. Canopy leaves
within Chebyshev 2 of the falling wood come along. The search is capped at `TreeFellWoodCap` wood cells and the whole fell at
`TreeFellTotalCap` (leaves past it are left standing and decay). The falling tree is meshed by the
real chunk mesher through `CellBatchMesher.Build` (`TopplingTree.BuildMeshData`: one mesh per chunk
the felled cells span, so a crown of any size is drawn whole) and drops through
`TopplingTree.TakeDrops`.
It falls as a rigid body (`FellBody`, GL-free, fixed 1/120 s substeps): the blocks are point masses with
the shape's inertia, `BodyMotion.Gravity` pulls, and each block touches the terrain through its real
extremities (`FellContacts`: a thin wood block reaches only half its diameter from the cell center, except
along its tubes) with impulse contacts (restitution, friction) and a depenetration push capped at
`TreeFellDepenetrateSpeed`, so no contact can fling the tree. The terrain is a `FellTerrain` (`FellTerrain.Of`
over the chunks, the felled cells already gone; `FellTerrain.Ground` for plain ground in tests): `Stops` is
what the wood rests on, and standing trees' leaves and thin wood (branches, a thin trunk, never player-placed
wood) are not in it, so a falling crown crashes through its neighbors' crowns instead of being lifted onto
them (wedged in that tangle it used to climb out at 0.45 block per substep and launch); `Snags` is that thin
wood, which snaps off a branch moving through it faster than `TreeFellDetachSpeed`; `HoldsPin` is any
solid but leaves, so a cut on a thin trunk still tips over the stub below it.
`ForestFellTests` fells every species among copies of itself and holds that no piece rises and the tree comes down.
A tree standing on its stump (ground under the cut, wood mostly above it) starts pinned to the cut by
a ball joint and tips over it from a small kick, so "fall forward" is the physics of a pinned body; the pin
lets go when it is nearly flat or has been stopped short. A limb or branch set (no ground under the cut or
wood leaning out) is free from the start, so it drops and tumbles to the ground. The first hard impact is
the thud (`TryTakeLanding`, shake, leaf burst, dust at the cells the blocks are in then); once the body has
been still for `TreeFellRestHoldSeconds` (or `TreeFellSettleCapSeconds` passed since the first hard impact) it freezes, and `TreeFellRestSeconds`
later the drops pop out at the blocks' resting cells. Tuning: the `TreeFell*` and `TreeHinge*` constants.
Wood is heavier toward the root (`TreeFellRootWeightBoost`) so the base outweighs the crown and a canopy
cannot prop the tree up on its branches. A side branch (`FellBranches`: the wood walked from the cut over
`TreeSupport.Links`, the main stem going up at each fork) whose block hits the ground, runs into a wall or
snags on a standing tree's branch faster than `TreeFellDetachSpeed` snaps off whole, leaves included, into its
own `FellPart` that carries on from the parent's pose and speed, free of the pin; the rest is rebuilt and
remeshed without it, keeping the pin if it still had it (`TopplingTree.Parts`, one model matrix
and mesh set per part). Only the original part sheds branches. Falling wood hurts: each update `BlockEditor.StrikeWithTree` (singleplayer and server, never a client)
asks `TopplingTree.TryStrike` per player (`IMobTarget.TakeHit`, skipped when immune or dead) and per mob
(`MobStrikes.StrikeMob`) whether moving wood overlaps its body box faster than `TreeHitMinSpeed`; damage is
`TopplingTree.StrikeDamage` (`TreeHitDamagePerSpeed` per block/s, capped at `TreeHitMaxDamage`), the victim
is shoved along the wood's motion and not hit again for `TreeHitCooldownSeconds`. Leaves and pieces at rest
hurt nobody. Walls are not collided with, only ground: a contact point more than a block below the top of
its solid column (`FellBody.MaxGroundDepth`) is inside a wall, such as a neighboring trunk, and gives no
contact, so the tree is never lifted up the wall (which used to launch it in a dense forest). Likewise a
contact point already inside a solid cell in the standing pose (the hillside beside the base, a neighbor's
branch beside the crown; a block's side-face points sit on the neighboring cell's boundary) is marked
embedded and touches nothing until it has left the solid; a split-off piece keeps its parent's marks.

**Reactive growth** (`World.TryGrowTree(species, x, yHint, z, steps)`, refused while loading or
for an unknown species; the `grow` scenario op calls it). `TreeGrowthWriter` (`Simulation/`, GL-free,
main thread; `TreeGrowthResult`/`TreeGrowthStatus` Grew / Planted / NoGround / NotGrowable) scans
the target column for the species' root and rebuilds the tree with `TreeGraph.FromWorld` (a
breadth-first walk over child edges only, so detached and cut-off wood is never reached; every node
floored at its stored diameter, a wide core's floor recovered from its shell run, nodes below the
root loaded as feature nodes; null on an unloaded read or past `TreeGrowNodeCap`), or plants a
sapling on solid ground when there is none. It then runs `GrowFurther` (at most
`TreeGrowMaxPulsesPerCall` pulses) in a `WorldTreeSpace` — a cell is free when it is loaded, not
reserved by a falling block, air or a non-persistent leaf of the species, and not the missing parent
of an orphan, which is what keeps a cut branch cut — rasterizes once and writes back only the
difference: existing nodes are only re-coded thicker, new wood goes only into free cells, leaves
only around changed tips — or, when the thicker trunk changed the tree's `ClusterRadius`, around
every original tip still in the world (never one whose wood the write skipped, whose leaves would
hang off wood that never landed) — and shells follow the **new-ring-only** rule (inside the footprint a core
already had, existing shells are only re-coded, so a mined shell or a partial slice cut stays
mined). All writes are `Chunk.SetBlockRaw`, followed by `FluidManager.ScheduleAround` per changed
id, one `LeafDecayManager.ScheduleBox` over the written box (pruning is left to decay), a
`ChunkManager.MarkDirty` per touched chunk and `TreeGenerator.PoisonCanopy(x, z, 0)` plus
`LodManager.MarkEditedColumn` per changed column.

**The sapling clock** (`Simulation/SaplingManager.cs`, owned by `World`, main thread only, skipped on
a client): a `plant_tree` use (an acorn on dirt or grass, `WorldItemUse.PlantTree`, which maps the hit
cell and face to the ground cell) plants through the writer's Planted branch and schedules the root
cell; every `SaplingPulseSeconds` (jittered by `SaplingPulseJitter`) the manager grows it one pulse
through the same `TryGrowTree` path, up to the species' `MaturityStepsAt` for its own root cell and
seed, then forgets it — so a sapling becomes a tree over a few minutes. A cell in an unloaded chunk
waits, and a cell whose wood is gone (mined, or grown over) is forgotten. In memory only: a reload
leaves a half-grown sapling standing until it is grown again by hand (see the known gaps).

**The item debugger's TREES tab** (`OpenGLTest.ItemDebugger/TreePreviewState.cs`, see "Build and
run") grows any species pulse by pulse over flat grass at a fixed seed through `GrowSteps`, and
meshes it with `CellBatchMesher` (`src/Chunks/Meshing/CellBatchMesher.cs`): the real chunk mesher over as many
scratch chunks as the cells span, each at its real position inside an empty one-chunk ring, so a
wide or tall tree is never clipped at a chunk seam and wears the real biome tint.

A grown tree shows in the far LOD at once: after poisoning each written column, `TreeGrowthWriter`
scans the loaded column for its highest leaf and records it through `TreeGenerator.SetCanopyAdded`
(`TreeCanopy.CanopyAdded`: packed top and leaf block per fine column, read before the disturbed
marker and the generated canopy on every ring, cleared by any later `PoisonCanopy`, saved in
`lod-edits.bin` (format version 3) with the leaf as a block name, reset by `WorldSeed.Apply`; never
in a `GenerationalCache`). `CanopyAddedTests`, `AGrownCrownGoesBackToTheFarLod`.

Known gap, open: the wide-trunk residue of `M-14` — a wide core cut while its shells stand leaves
the crown unreachable for reactive growth.
