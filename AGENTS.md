# AGENTS.md

Guidance for AI coding agents working in this repository.

## Project overview

A Minecraft-style voxel engine written in C# on .NET 9, using **Silk.NET** (windowing, input, OpenGL bindings) and StbImageSharp for texture loading. It renders an infinite, procedurally generated world (FastNoiseLite OpenSimplex2 noise) with chunk streaming, an underground of 3D-noise caves, worm-carved tunnels and heightmap ravines, greedy per-face meshing with ambient occlusion, frustum culling, block breaking/placement via raycast, and simple player physics (gravity, jumping, collision). The player spawns standing on the terrain at the origin (`Camera.SpawnPosition`).

There is a single executable project. Historical naming quirks to be aware of:

- The solution is `OpenGLTest.sln`, the project folder is `OpenGLTest/`, the csproj is `OpenGLTest/silkgltriangle.csproj`, and **all code lives in the `silkgltriangle` namespace**. Keep new files in that namespace unless deliberately renaming everything.
- There are no test projects and no CI. Verification is done by building and running the game.

## Build and run

```powershell
dotnet build OpenGLTest.sln -c Release
dotnet run --project OpenGLTest/silkgltriangle.csproj -c Release
```

Always build and run **Release**. Debug is several times slower in the voxel loops (mesh
builds, light propagation, the per-frame chunk walks), so a framerate or hitch observed in Debug
says little about the game. `Directory.Build.props` at the repo root makes Release the default
when no `-c` is given to a project build or `dotnet run`; a solution build still needs the
explicit `-c Release`, and Rider's run configuration follows the solution configuration
dropdown, which must be set to `Release | Any CPU`. Use `-c Debug` only when you need
breakpoints. The startup line `Build: Release | debugger ...` confirms which one is running.

Setting the `VOXEL_SPAWN` environment variable to `x,y,z` (three comma-separated floats, e.g. `-40,-107,-75`) starts the camera eye at exactly that world position instead of on the surface at the origin, which is handy for inspecting a specific cave or feature without flying to it; an unparseable value is ignored with a console warning. `VOXEL_LOOK` takes `yaw,pitch` in degrees (two floats, e.g. `-90,-20`; yaw 0 faces +X so the default `-90,0` faces -Z, and pitch is clamped to ±89 exactly as the mouse look is) and pins the initial facing, so a scripted verification run frames the same thing every time. `VOXEL_TIME` takes a number of seconds and is added to the window clock before the lighting pass reads it (`Program.timeOffset`), which jumps the day/night cycle straight to noon, dusk or night for a screenshot instead of waiting out the orbit. All three parse once at startup and ignore garbage with a console warning. Setting `VOXEL_SCRIPT` to a scenario JSON path (see `OpenGLTest/visual-examples/basic.json`) runs a scripted visual test instead of playing: `teleport`/`look`/`wait`/`walk`/`mine`/`place`/`give`/`screenshot` steps drive the real update functions through a scripted keyboard and save numbered PNGs to `visual/<name>/` (`screenshot` takes an optional `hideHud`), then the window closes. Scenario parsing is covered by `VisualScenarioTests`; prefer a scenario over ad-hoc scripts for screenshot verification. `VOXEL_DEBUG=1` starts with the X debug line already on, so such a run logs FPS, position and the profiler breakdown once a second.

The app opens a window and grabs the mouse. Controls: WASD to move, mouse to look, Space to jump, double-tap Space to toggle flight (Space/Left Ctrl for up/down while flying), `C` to toggle noclip, Left Shift for 10x speed, hold left-click to mine a block (hit-based: one hit per `World.BlockHitSeconds` = 200 ms removes 1 HP of the block's `hardness`, declared in its JSON - 5 for most blocks, so ~1s, 8 for logs, 2 for leaves, and `0` means it pops on first contact like a pebble; damage resets if you release or switch target; instant while noclip; stepped progress overlay via `break-*.glsl`; each hit pops the block out and back via `RenderManager.RenderBlockPop`), right-click to place the selected block or, when the selected slot holds an item with a `use` action, to use it (the bucket scoops a water source and pours it back out — see the item schema below), 1-10 to pick one of the 10 hotbar slots, `E` for the list inventory (blocks/tools/misc tabs with shared caps of 2048/8/2048; click a row to preview, click a hotbar slot to equip, same item again to unequip; the world keeps running and `E`/Escape closes it), `X` to toggle debug logging (FPS/position/water state to console), Escape to quit.

`X` also raises the **frame profiler overlay** under the FPS counter, and prints the same
snapshot to the console once a second so a headless run's log carries it. Each row is a section
of the frame — `camera`, the three `chunk*` rows from `ChunkManager.Update`, `lod`, `entities`,
`fluidWake`, `fluidTick`, `raycast`, `breaking` on the update side, then `lighting`, `shadows`,
`lodPass`, `worldPass`, `entityPass`, `waterPass`, `selectPass`, `uiPass` on the render side —
with its average and worst millisecond cost over the last second; `other` is the wall time no
section claimed (mostly the buffer swap). The header carries the frame time and FPS, the CPU
sum against the GPU's own `TIME_ELAPSED` measurement of the whole frame (`--` until the first
query comes back), GC collections per second and the loaded/visible chunk counts. Timings are
collected every frame whether or not the overlay is up, so the first second after pressing `X`
already has data; the overlay's own glyph draws land in `uiPass`, so compare that row with `X`
on and off before reading it as a baseline. `Program.OnLoad` and the `RenderManager` constructor
each print one startup line — build configuration, debugger, GC mode and CPU count, then the GL
renderer, vendor and version — so a measurement always records which build and which GPU
produced it. Timing lives in `OpenGLTest/src/FrameProfiler.cs`; wrap new work in
`FrameProfiler.Begin`/`End` (main thread only) rather than adding another ad-hoc stopwatch.

Standing in water switches the player to **swimming** (`Camera.IsInWater`, recomputed every frame by `Camera.UpdateWaterState` from the real fluid surface heights under the body): horizontal speed halves, gravity is replaced by an eased sink toward `SinkSpeed` (Space eases up toward `SwimUpSpeed` instead, both through `MathUtils.Damp`), and swimming into a bank while holding Space adds `SwimExitBoost` so the player can climb out without a ramp. Flight and noclip ignore water entirely, and a player standing on the bottom of a shallow puddle still jumps normally. Flowing water also pushes the player and floating item drops along its flow direction (`ChunkManager.GetFluidFlowGlobal`, Minecraft's `FlowingFluid.getFlow`: a horizontal vector summed from how much lower each neighbouring cell's surface sits, zero in a still pool). `Camera.IsEyeInWater` is the separate eye-only test and is the source of truth for the underwater tint.

**Water rendering** lives in the shared world shader plus two extra passes in `RenderManager`. Just before `RenderWorldTranslucent` draws, `CopyScene` snapshots the default framebuffer's colour and depth into two textures on units 3 and 4 (`CopyTexSubImage2D`, no FBO — the window has no MSAA); the water fragment then composes its own look out of them: a refracted lookup offset along the analytic wave normal, Beer-Lambert absorption over the water's measured thickness (`WaterAbsorb`) plus in-scattered `WaterScatter`, and a Schlick fresnel mix into the planar reflection. The reflection pass now also draws the sun disc and the cloud layer into the mirror, and both it and the scene copies are skipped whenever `CullWater` (one frustum pass a frame, feeding the water pass its draw lists) found no water on screen — glass shares the translucent mesh, so `Chunk.HasWaterMesh` is the flag that decides, not `HasTranslucentMesh`. Opaque faces that look into water are flagged by the mesher in bit 3 of `Vertex.Normal` (bits 0-2 stay the face index) and get the animated Voronoi caustic net. While the eye is submerged, `UnderwaterRaysRenderer` adds one additive full-screen ray-march for sun shafts, which is why the shadow map keeps rendering underwater (`_shadowStrengthDry`, the strength before the underwater fade) even though the world diffuse no longer uses it. **Depth convention**: `Camera.GetProjectionMatrix` comes from `System.Numerics`, which is Direct3D-style (clip depth 0..1), so window depth is *not* the GL -1..1 mapping — never write a near/far linearization. Reconstruct a world position by inverting the very matrix that wrote the depth (`uInvViewProj`), and compare raw depth values directly for "which is nearer". `visual-examples/water.json` (run it with `VOXEL_TIME=70`) frames the shallows, the grazing surface, deep water from height and two underwater shots.

Shaders (`assets/shaders/**/*.glsl`), textures (`assets/textures/**/*.png`), fonts (`assets/fonts/**/*.ttf`) and all content JSON (`assets/**/*.json` — blocks, items, loot tables, models) are copied to the output directory via `CopyToOutputDirectory` rules in the csproj — **new asset types/extensions need a matching `<None Update>` entry** or they won't be found at runtime. The JSON rule is a wildcard over the whole `assets` tree, so adding a block, item, loot table or model needs no csproj edit.

Assets are opened by **relative** path (`FileUtils.ReadFile("assets/shaders/...")`, `TextureLoader.LoadFromFile(gl, "assets/textures/atlas.png")` in `RenderManager.LoadResources`), so the app must be launched with a working directory that contains `assets/` — either the build output directory or `OpenGLTest/`. `dotnet run --project OpenGLTest/silkgltriangle.csproj` satisfies this (it runs from the project folder); launching the built executable from an arbitrary directory does not.

## Layout

```
OpenGLTest/
  src/
    Program.cs                 # entry point, wires window callbacks, input polling
    World.cs                   # owns Camera, ChunkManager, ChunkGenerationWorker, LodManager; raycast block edit API
    Chunk.cs                   # 16^3 block storage (byte IDs), block/column fill + cave carving via WorldGen/, mesh data generation + AO
    ChunkGenerationWorker.cs   # background threads creating chunks around the camera
    Camera.cs                  # FPS camera, physics (gravity/jump/collision), DDA raycast
    LodRegion.cs               # far-terrain LOD: region columns meshed at reduced resolution from heightmaps; per-column chunk-Y ranges for burial
    FluidState.cs              # encoding of the per-block state byte for fluid cells (source / falling / level 1-7)
    BlockType.cs               # runtime block record built from JSON, plus the per-id lookup tables
                               #   the mesher and gameplay read (ById, Emission, IsSolid/Occludes/...)
    ItemType.cs                # pure items (vine, branch, flint): ids handed out after the blocks;
                               #   never placed or meshed, only drops and inventory slots
    WorldGenerationConfig.cs   # all tuning constants (view radius, per-frame budgets, thread counts) and every worldgen knob (seed, ravine, cave, carver)
    MathUtils.cs               # lerp/easing/pulse helpers for animation curves + shared constants
    Mesh.cs, Shader.cs, TextureLoader.cs, FileUtils.cs, FastNoiseLite.cs (vendored, don't reformat)
    Data/                      # Vertex struct, cube geometry, frustum/plane math, direction tables
    Content/                   # the data-driven content pipeline (see "Content" below)
      ContentRegistry.cs       # loads assets/data + assets/models, assigns byte ids, fills the tables
      BlockDefinition.cs, ItemDefinition.cs, LootTableDefinition.cs, ModelDefinition.cs  # JSON DTOs
      BlockModel.cs, ModelLoader.cs         # Minecraft model format: parent chains, #texture refs
      ModelBaker.cs, BakedModel.cs, BakedQuad.cs   # elements -> atlas-ready quads + full-cube flags
      LootTable.cs, LootContext.cs          # compiled loot tables and the world cell they roll at
      BlockRenderer.cs, BlockBreakAction.cs # enums: geometry emitter / what breaking does
    Systems/
      WindowManager.cs         # Silk.NET window setup
      InputManager.cs          # keyboard/mouse context
      RenderManager.cs         # GL state, world/selection/UI passes, viewport
                               #   a depth-only directional shadow pass (RenderShadows) into its
                               #   own FBO runs first each frame, and SampleShadow in
                               #   test-fragment.glsl consumes that depth texture on unit 1
                               #   the sun disc (assets/textures/environment/sun.png) is drawn
                               #   additively right after the clear (RenderSun), depth off, so
                               #   the terrain that follows occludes it
                               #   CopyScene snapshots the frame's colour/depth to units 3/4
                               #   for the water pass's refraction (see Water rendering above)
      UnderwaterRaysRenderer.cs # additive full-screen sun shafts while the eye is submerged
      ChunkManager.cs          # chunk lifecycle: load/unload, dirty tracking, mesh scheduling
      LodManager.cs            # LOD region lifecycle: worker thread builds, main thread uploads/unloads, burial flags
      FluidManager.cs          # the fluid tick: scheduled cells, level recompute + spread, batched remeshes
    WorldGen/
      TerrainGenerator.cs      # the 2D half: owns every 2D noise channel; Compute(chunkX, chunkZ) -> ColumnData
      ColumnData.cs            # per-chunk-column arrays: Heights (ravines included), DirtDepth, EntranceWeight, CaveIntensity
      NoiseCaves.cs            # 3D-noise caves (cheese rooms, wide/narrow spaghetti, deep caverns) on a strided lattice
      WormCarver.cs            # worm-carved tunnels: per-XZ-cell deterministic paths, cached, carved sphere by sphere
      CaveNode.cs, Worm.cs     # one tunnel sphere (center + radius); a worm's node array plus its AABB
      SplitMix64.cs            # deterministic integer-hash PRNG so worms don't depend on System.Random internals
  assets/shaders/, assets/textures/   # GLSL shaders; block tiles (stitched into an atlas at
                                      #   startup), item sprites, crosshair
  assets/fonts/                       # TTF for the UI text
  assets/data/blocks/<name>.json      # one file per block; the file name is its string id
  assets/data/items/<name>.json       # vine, branch, flint, bucket, water_bucket
  assets/data/loot/<name>.json        # stone, wood, leaves, pebble
  assets/models/block/<name>.json     # Minecraft Java block models, incl. the built-in parents
                                      #   cube, cube_all, cube_column, cube_bottom_top
```

## Content: blocks, items, models and loot are data

Nothing about a block is hardcoded any more. `ContentRegistry` reads `assets/data` and
`assets/models` at startup, hands out byte ids and publishes read-only tables that the rest of
the engine indexes by id. **Adding a block means adding JSON, not editing C#.**

Ids are `byte` and chunk storage is a `byte[]`: id 0 is air (it has no definition file, and a
block may not be named `air`), then blocks sorted by file name, then items sorted by file name.
Blocks and items therefore share 255 ids and the registry throws if they overflow. Code that has
to name a specific block or item asks for it by file name — `ContentRegistry.RequireBlock("stone")`,
`ContentRegistry.RequireItem("bucket")` — which throws naming the missing content at startup
instead of silently mis-indexing. Hot paths must not do that per voxel: worldgen and the LOD
mesher resolve their palette once in the owning feature's static constructor
(`TerrainGenerator.SurfaceBlock/SoilBlock/RockBlock/SeaBlock`, `TreeGenerator.LeavesBlock`,
`PebbleGenerator.PebbleBlock`) and everything else reads those bytes. Best of all is not naming
a block: when the question is "what does this block do", add a data flag and read it (see the
convention below). Every content error names the file it came from.

### Startup order (`RenderManager.LoadResources`)

1. `ContentRegistry.Load("assets")` - parses items, loot tables, blocks and every model they
   reference, and assigns ids. No GL, no atlas.
2. `TextureAtlas.Build(gl, "assets/textures/blocks")` - stitches the tiles.
3. `ContentRegistry.BakeModels()` - bakes each block's model into atlas-ready quads and settles
   the defaults that need to know whether the result is a full cube (`occludes`, `light.passes`).
4. Shaders, `BlockCubeCache`, `ItemMeshCache`, `UIRenderer`, then the `World`.

The `World` (and with it every worker thread) is created last, so all content tables are
immutable by the time anything but the main thread can see them. Keep it that way.

### Block schema (`assets/data/blocks/<name>.json`)

Every field is optional except `model`. Parsing is case-insensitive and tolerates comments and
trailing commas.

| field | default | meaning |
|---|---|---|
| `model` | required | a `"block/cube_all"`-style path under `assets/models/`, **or** an inline model object with the same schema |
| `hardness` | 5 | hits to break, one per `World.BlockHitSeconds`; `0` means it breaks on first contact (pebble) |
| `solid` | true | collides with bodies (`BlockType.IsSolid`) |
| `occludes` | full cube && !cutout | hides a neighbouring full cube's face (`OccludesNeighbours`) |
| `cullSelf` | false | also cull faces against a neighbour of the same block (glass) |
| `castsAO` | `solid` | darkens neighbouring faces (`CastsAmbientOcclusion`) |
| `cutout` | false | alpha-tested texture; forces `occludes` off whatever the file says |
| `translucent` | false | drawn blended in the sorted translucent pass with the fluids (glass); forces occludes off; never casts a shadow |
| `sway` | false | leaf-wind sway in the vertex shader |
| `light.emission` | 0 | 0..15 (`LightEmission`) |
| `light.passes` | `!fullCube` | whether voxel light travels through the cell (`PassesLight`) |
| `renderer` | `"model"` | `model` (baked quads), `tapered_wood`, `pebbles` or `fluid` (procedural emitters); a fluid's model only supplies its tile |
| `wood.taperLevel` | none | 0 = full-cube log, 1..5 taper steps; across all blocks 0..5 must be covered exactly once |
| `wood.halfWidth` | 0.5 | half edge length of the tapered box, in blocks (`HalfWidth`) |
| `hasItem` | true | enters the blocks pool and can be hotbarred; false for taper wood and pebbles |
| `needsSupport` | false | pops, rolling its loot, when the block below is removed; also makes the block replaceable, so a flow washes it away |
| `falls` | false | gravity-affected (sand, gravel): topples into a `FallingBlock` entity when the cell below stops being solid, sinks through water, slides off lone supports; never combines with `fluid`/`needsSupport` |
| `fluid` | false | the block is a flowing fluid (water): forces `solid`, `castsAO`, `hasItem`, `needsSupport` off, `occludes` off and `light.passes` on, and drops nothing whatever the file says |
| `onBreak` | `"drop"` | `drop` (roll loot) or `prime_tnt` (no drop, spawns a `PrimedTnt`) |
| `loot` | the table sharing this block's name, else drop self | loot table name; `"none"` drops nothing |

Two traps worth repeating: a **model that is not a full cube needs `light.passes: true`** (which
is its default) or its own cell stays dark and the interior quads render black; and a **cutout
block must not occlude**, which the registry enforces rather than trusting the file.

### Item schema (`assets/data/items/<name>.json`)

```json
{ "name": "Flint", "texture": "assets/textures/items/flint.png" }
```

`name` and `texture` are all a plain pickup needs. An optional `use` object gives the item a
right-click behaviour (`World.TryUseSelected` -> `TryUseItem`):

```json
{ "name": "Bucket", "texture": "assets/textures/items/bucket.png",
  "use": { "action": "pickup_fluid", "fluid": "block/water", "result": "item/water_bucket" } }
```

| field | meaning |
|---|---|
| `use.action` | `pickup_fluid` (scoop the targeted fluid source into the item) or `place_fluid` (pour one into the targeted cell) |
| `use.fluid` | the fluid block the action works on; must be a block with `fluid: true` |
| `use.result` | the item this one becomes on success; must be an item, and the two normally point at each other |

Both names resolve like a loot entry (`block/` / `item/` prefixes pin the namespace).
`ContentRegistry.ValidateItemUses` runs after the blocks are loaded and throws, naming the file,
if the fluid is not a fluid block or the result is not an item. A successful use goes through
`Inventory.MorphTool`, which turns the tool instance into its result in place, so every hotbar
slot holding that bucket flips with it and scooping and pouring alternate on the same button.
`ItemType.TargetsFluids` makes the selection
raycast include fluid sources only while a `pickup_fluid` item is held — otherwise water is
aimed straight through, and mining refuses fluid cells regardless.

### Loot schema (`assets/data/loot/<name>.json`)

```jsonc
{
  "pools": [
    {
      "rolls": 1,                    // int, {"min":1,"max":3}, or {"provider":"pebble_count"}
      "chance": 0.15,                // the whole pool is skipped otherwise; default 1
      "entries": [
        { "item": "cobblestone", "weight": 1, "count": 1 },  // count: int or {min,max}; weight default 1
        { "empty": true, "weight": 3 }                       // weighted nothing
      ]
    }
  ]
}
```

An `item` name resolves against blocks first, then items; a `block/` or `item/` prefix pins it to
one of the two. `leaves.json` therefore has to say `"item/branch"`, because a *block* named
`branch` (the thickest taper step) exists as well. The only roll-count provider is `pebble_count`
(`PebbleGenerator.CountAt(x, z)` at the loot context's cell).

Every destruction path rolls through one place: `DropManager.RollLoot` (a block with no table
drops itself, `LootTable.None` drops nothing) and its spawning wrapper `SpawnLoot`. Mining
(`World.RemoveBlock`), blasts (`TntManager.Detonate`) and felled trees (`TopplingTree.TakeDrops`)
all go through it, so a block's yield never depends on how it came down.

### Model schema (`assets/models/block/<name>.json`)

Minecraft Java block-model format, i.e. what BlockBench exports: `parent` (chained, cycles
rejected), `textures` (`#var` refs, child entries overriding the parent's) and `elements[]` with
`from`/`to` in 0..16, optional `rotation {origin, axis, angle, rescale}`, `shade`, and `faces`
carrying `uv`, `texture`, `cullface` and `rotation`. `tintindex`, `display`, `ambientocclusion`
and `gui_light` are ignored. Texture refs are normalized by stripping `minecraft:` and any
leading `block/` or `blocks/`, then looked up as an atlas tile.

Minecraft's face names map onto the engine's face indices (`Chunk.CubeFaces`) as
`north` = -Z = 0, `south` = +Z = 1, `west` = -X = 2, `east` = +X = 3, `up` = +Y = 4,
`down` = -Y = 5.

Four built-in parents mirror Minecraft's byte for byte, so a BlockBench export referencing them
works unchanged: `cube` (six faces, `#north #south #east #west #up #down`), `cube_all` (all six
`#all`), `cube_column` (`#end` on up/down, `#side` around) and `cube_bottom_top` (`#top`,
`#bottom`, `#side`). `ContentRegistry.BakeModels` self-checks at startup that all four still bake
to six full faces.

## Architecture: threading model (the most important thing to preserve)

Chunk work is split across threads, and OpenGL calls are only legal on the main thread:

1. `ChunkGenerationWorker` runs dedicated background threads that scan positions around the camera (closest first) and call `ChunkManager.QueueChunk`, which creates the `Chunk` and generates its block data **on that background thread**.
2. Meshing is scheduled by `ChunkManager.ProcessDirtyChunks` (main thread, per-frame budget `MaxDirtyChunksPerFrame`) and runs on the thread pool via `Task.Run`, throttled by a `SemaphoreSlim` (`MaxConcurrentMeshGenerations`). Mesh generation produces CPU-side `ChunkMeshData` only.
3. Completed mesh data lands in `_completedMeshQueue` and is uploaded to the GPU on the **main thread** in `ApplyCompletedMeshes`, sorted closest-first and capped at `MaxMeshUploadsPerFrame` per frame.

Rules that follow from this:

- **Never call GL functions (buffer uploads, `Mesh` creation/disposal, shader use) off the main thread.** Background work must only produce plain data handed over via the concurrent queues.
- A chunk is only meshed when its block data is ready **and all 6 cardinal neighbors exist with ready data** (`HasAllNeighbors`) — meshing reads neighbor blocks for face culling and cross-chunk AO. Breaking this reintroduces holes/seams at chunk borders (see git history). That scheduling check can go stale (the mesh task waits on the semaphore and an unload may take a neighbor away meanwhile), so `Chunk.GenerateMeshData` re-resolves the whole 3x3x3 neighbourhood once up front and returns `null` when a face neighbor is gone; callers must then re-mark the chunk dirty rather than apply anything. Only the edge/corner neighbors may be absent, and those read as air (they feed light and fluid corner heights only).
- Shared state uses `ConcurrentDictionary`/`ConcurrentQueue`; `Chunk.IsBlockDataReady` is `volatile`. Keep new shared state lock-free in the same style.
- Editing a block on a chunk border must also remesh the affected neighbor chunk(s) — block edits use `ChunkManager.ImmediateRemesh` / `MarkDirty`.
- Performance tuning knobs all live in `WorldGenerationConfig`; change constants there rather than hardcoding budgets elsewhere.
- Animation curves (interpolation, easing, one-shot pulses) belong in `MathUtils`, not inline in gameplay or render code — everything time-driven should be lerped through one of those helpers rather than snapped.

Other invariants:

- World/chunk coordinates: `Chunk.Position` is in chunk units; world pos = chunk pos × `Chunk.Size` (16). Chunks are cubic and stacked vertically too (3D chunk grid, not 2D columns).
- Blocks are `byte` IDs; `0` is air. Everything else about a block comes from `assets/data/blocks/<name>.json` through `ContentRegistry` (see "Content" above), never from a new `switch` on the id. Hot paths read the flat mirrors (`BlockType.IsSolid/Occludes/CastsAO`, `BlockType.Emission`, `BlockType.PassesLightById`) rather than `BlockType.ById[...]`, so keep new per-id flags in that shape.
- **Per-block state byte**: alongside `Chunk._blocks` each chunk has a lazily allocated `volatile byte[]? _state` (null = all zero), read through `Chunk.GetLocalState` / `ChunkManager.GetStateGlobal` / `TryGetCell` and written only by `Chunk.SetBlockRaw` (no remesh, no scheduling — the fluid tick batches those). Today only fluids use it: `FluidState` decodes it as source (`0`), falling (`8`) or flowing level `1..7`. **Every write that is not a fluid write zeroes it** (`RemoveBlockAt`, `PlaceBlockAt` — which also treats a fluid cell as replaceable — and `ClearBlockAt`), so a stale level can never outlive the water that owned it. Meshing gets it as a second padded snapshot from `FillPaddedSnapshot(padded, paddedState, neighbors, neighborStates)`, filled from the neighbourhood `TryResolveNeighbors` resolved for the build; the fluid simulation must read cells through `TryGetCell`/`ChunkAt`, never `GetBlockGlobal`, which reports unloaded chunks as air.
- **Fluid tick**: `Systems/FluidManager.cs` (owned by `World`, main thread only) is a *scheduled-cell* simulation, not a scan. `World.Update` calls it once per frame after `UpdateToppling`; it runs whole ticks every `WorldGenerationConfig.FluidTickSeconds` (0.25 s), at most `MaxFluidTicksPerFrame` of them per frame, and drops the leftover time so a hitch never leaves it spiralling. Each tick takes the set of cells scheduled by the previous one (packed as `long`, 21 bits per axis), recomputes every **non-source** fluid cell from its neighbours (Minecraft's `getNewLiquid`: two adjacent sources over ground that cannot drain become a source themselves, fluid directly above means falling, otherwise one level thinner than the fullest neighbour, and nothing left means the cell drains to air), then spreads it downwards, or sideways toward the nearest hole within `FluidSlopeSearchDistance` (ties spread evenly). Spreading only ever writes into non-fluid `IsReplaceable` cells, popping the fragile block it washes away through `DropManager.SpawnLoot`; a cell that already holds fluid recomputes itself on its own tick. Writes go through `Chunk.SetBlockRaw` and dirty only the cell's own chunk plus the face-adjacent chunk on each axis where the cell sits on a border, batched into `MarkDirty` at the end of the tick — not `ImmediateRemesh`, and never `MarkAreaDirty`, because a fluid passes light and casts no AO. A cell reschedules itself and its neighbourhood exactly when it changed or wrote into a neighbour, so settled water costs nothing.
- **Waking worldgen water**: the sea is generated as static source cells, so a cave or entrance carved into a land column beside it would leave a wall of water standing against worldgen air that nothing ever schedules. `FluidManager.WakeLoadedChunks(chunks)` — called from `World.Update` each frame, immediately before the fluid tick — fixes that by scheduling a chunk's whole water/air frontier when it loads, so the sea pours into whatever breached it exactly like Minecraft water does when you dig into an ocean. It has two halves: the **interior**, collected on the generation worker at the end of `Chunk.GenerateBlockData` into `Chunk.FluidFrontier` (local block indices of every fluid cell with a `FluidManager.IsFlowTarget` neighbour in -X/+X/-Z/+Z/-Y — never +Y, since water does not flow upward and every sea-surface cell has air above it; only its own `_blocks` are read, so worldgen stays a pure per-chunk function), and the **borders**, which need both chunks loaded and are therefore scanned on the main thread over the shared 16x16 plane of each face neighbour, scheduling whichever side actually holds the fluid (the vertical faces only ever schedule the fluid above the hole). Chunks entirely above `SeaLevel` skip the border scan — generated water cannot reach them, and player-placed water is woken by the edit paths. The queue of newly generated chunks is `ChunkManager.TryDequeueLoaded`; a consumer must re-check that the chunk is still the live one at its position, because an unload can race the queue. Once the frontier has settled, it costs nothing again.
- **Every block edit path must call `FluidManager.ScheduleAround(cell)`** (or `ScheduleBox(min, max)` for a batch) or the water beside the edit will never notice it: `World.RemoveBlock`, `World.FellWood` (per cleared cell, and in both single-block branches), `World.TryPlaceBlockFromRaycast`, `TntManager.Detonate` (`ScheduleBox` over the blast box grown by one; the blast also **skips fluid cells**, since a dry crater under water would just refill from every side) and bucket edits through `FluidManager.SetCellImmediate`, which is the only fluid write that remeshes synchronously. Mining never takes a fluid out: `RemoveBlock` returns early on `BlockType.IsFluid`, so only a bucket removes water.
- **Gravity**: `Systems/FallingBlockManager.cs` (owned by `World`, main thread only) topples `falls` blocks into `FallingBlock` entities and writes rested cubes back as blocks. Every edit path that can unsupport one must call `TryTopple` above it (`World.RemoveBlock`, `World.FellWood` per cleared cell, `World.TryPlaceBlockFromRaycast` at the placed cell, `TntManager.Detonate` via `CheckColumn` bottom-up over the blast box); fluids never count as support, so fluid edits need no hook. A landing cube slides with chance (8 - solid neighbours of its support) / 8 toward a random clear side, corners included (a corner needs its two flanking sides and the three cells above it clear), committing to its direction across landings, and drops straight once past the edge. Sand stacks on sand: a falls-block support never slides, so towers rebuild. A calm landing converts at once so a collapsing tower settles bottom-up instead of interpenetrating, and a cube that would rest over air pops out as a drop instead of floating. A live cube owns its cell: `TryPlaceBlockFromRaycast` refuses placements into one, cubes shove the player aside one-way (`Camera.PushOutOf`, never through walls, never in noclip), and a cube motionless-but-unsettled for 5 s breaks into its usual loot instead of hovering. Shorelines generate from sand (`TerrainGenerator.IsBeachHeight`, gravel-capped where `IsGravelPatch`); the far LOD wears sand and gravel there too, the gravel read with one noise eval per cell.
- Worldgen must stay deterministic and thread-safe (`FastNoiseLite.GetNoise` is read-only after setup; noise instances are configured once in static constructors and never re-tuned per call). All noise seeds derive from `WorldGenerationConfig.Seed` plus fixed per-channel offsets (`TerrainGenerator.MakeNoise`). Before player edits, terrain obeys:
  - **Nothing generated is ever solid above `height(x,z)`** — heights come from `Chunk.GetColumnData(...).Heights` (or the `GetColumnHeights` wrapper) and already include ravines, which are cut into the heightmap so the far LOD sees them too. The block at `height` is solid unless a cave or entrance carved it.
  - Both carvers only ever **remove** blocks: `Chunk.GenerateBlockData` fills the columns (grass at depth 0, dirt down to `DirtDepth`, stone below), then `NoiseCaves.Carve` and `WormCarver.Carve` clear air out of the result and `_solidCount` is recounted.
  - **Sea water** fills every air cell at `y <= SeaLevel` in columns whose `height < SeaLevel`, in `Chunk.FillWater` after both carvers and before trees and pebbles, so sea-floor caves flood while land caves stay dry and nothing grows underwater (`TerrainGenerator.IsSubmerged`; submerged columns also top out in dirt instead of grass). Generated water is always a **source** (state `0`), so the sea needs no state array at all. Set `WaterEnabled = false` to turn the whole thing off.
  - Caves fade out within ~`CaveSurfaceFadeEnd` blocks of the surface (`WormSurfaceMargin` for worms), except inside entrance zones — sparse patches where `ColumnData.EntranceWeight` lifts the fade and caves reach the sky.
  - Worldgen is a **pure function of world position**: each chunk generates its own blocks with no cross-chunk writes, so neighbours agree at their borders without shared state. Worm paths are cached per 64-block XZ cell in `WormCarver` exactly the way column data is cached per chunk column in `Chunk.GetColumnData` (compute-then-overwrite, wholesale `Clear` at the size limit — entries are deterministic, so a race yields identical values).
  - 3D cave noise is sampled on a `CaveNoiseStride` lattice (aligned to world coordinates so adjacent chunks share boundary samples) and trilinearly interpolated per block before thresholding.
- **Far-terrain LOD**: full-detail chunks load to `ChunkRadius` (16 chunks = 256 blocks); a cascade of LOD rings defined in `LodRegion.Levels` extends the render distance (level 1: 2-block cells to 512, level 2: 4-block cells to 1024, level 3: 8-block cells to 2048 blocks). Regions are columns meshed at reduced resolution straight from the heightmap cache (no block data). Each level's heights are eroded (min over a widened fine-column window) and biased downward so its mesh sits strictly inside real terrain *and* strictly below the next finer level — finer geometry always buries coarser via the depth buffer, so no masking or camera-driven remeshing exists. Because those shells are stacked, only one level may actually be drawn per area — otherwise they are all visible once the camera is inside the terrain — so `LodManager.UpdateBuriedFlags` marks each region whose footprint is fully covered by *loaded* finer-level regions (region grids nest exactly: one region = ratio×ratio regions of the finer level) and `RenderLod` skips those. Testing loaded regions rather than each level's radius means an unbuilt finer region can never open a hole in the horizon. The finest level (0) has no finer level to hide it, so it is additionally marked buried wherever the full-detail chunks its shell could show through are loaded *and* meshed (`LodManager.IsCoveredByChunks`, using the per-column inclusive chunk-Y ranges recorded at region build in `LodRegion.ColumnCyMin/ColumnCyMax` and `Chunk.IsMeshed`, which unlike `HasMesh` is true for all-air and fully enclosed chunks too). This is needed because caves cut into the band the shell occupies and would otherwise show it from inside; presence is tested per chunk rather than by horizontal distance because the load volume is a sphere. LOD side faces use grass at the top cell, dirt above `NominalDirtDepth` and the stone tile below it, so far cliffs match near ones. The nesting invariants (`SampleMargin(L+1) >= SampleMargin(L) + CellSize(L) - 1`, `HeightBias(L+1) >= HeightBias(L) + CellSize(L)`) must hold when adding levels, and `Camera.NearPlane`/`FarPlane` are tuned to keep those separations from z-fighting at the outermost ring. LOD draws before chunks, coarsest level first (`RenderManager.RenderLod`). Destroying a block would expose the buried LOD, so removals cap that column's LOD height below the removed block (`LodRegion.LowerColumnBelow`) and affected regions on every level rebuild (`LodManager.MarkEditedColumn`); placements need nothing.

## Conventions

- C# with file-scoped namespaces, one type per file, `_camelCase` private fields, PascalCase public members. Primary constructors are used where natural (`ChunkManager(GL gl)`).
- XML doc comments (`/// <summary>`) on classes and non-obvious public methods; brief `//` comments explaining *why* for tricky logic. Match the existing density — the code is moderately commented.
- **New blocks go in JSON, not C#.** When a block has to behave differently, prefer a new data flag on `BlockDefinition`/`BlockType` that every call site reads over another per-id branch in gameplay code — the point of the content registry is that `World`, `TntManager` and the mesher ask "what does this block do?" instead of "which block is this?".
- `FastNoiseLite.cs` is vendored third-party code — do not reformat or refactor it.
- xUnit tests live in `OpenGLTest.Tests/` — run `dotnet test OpenGLTest.Tests/silkgltriangle.Tests.csproj -c Release`. They cover headless logic and simulated gameplay without a window (fixture: `TestContent.cs`); add a test with every feature instead of a throwaway script. Verify feel-in-game by running the game and using the `X` debug overlay (FPS, position, grounded state). Watch the console for chunk generation errors — worker threads catch and log exceptions rather than crashing.
- Branch naming follows feature work off `master` (current work is on `worldgen`). Commit messages are short imperative sentences ("Add block placement", "Fix performance").
