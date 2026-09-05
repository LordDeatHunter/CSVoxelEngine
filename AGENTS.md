# AGENTS.md

Guidance for AI coding agents working in this repository.

## Project overview

A Minecraft-style voxel engine written in C# on .NET 9, using **Silk.NET** (windowing, input, OpenGL bindings) and StbImageSharp for texture loading. It renders an infinite, procedurally generated world (FastNoiseLite OpenSimplex2 noise) with chunk streaming, an underground of 3D-noise caves, worm-carved tunnels and heightmap ravines, greedy per-face meshing with ambient occlusion, frustum culling, block breaking/placement via raycast, and simple player physics (gravity, jumping, collision). The player spawns standing on the terrain at the origin (`Camera.SpawnPosition`).

There is a single executable project. Historical naming quirks to be aware of:

- The solution is `OpenGLTest.sln`, the project folder is `OpenGLTest/`, the csproj is `OpenGLTest/silkgltriangle.csproj`, and **all code lives in the `silkgltriangle` namespace**. Keep new files in that namespace unless deliberately renaming everything.
- There are no test projects and no CI. Verification is done by building and running the game.

## Build and run

```powershell
dotnet build OpenGLTest.sln
dotnet run --project OpenGLTest/silkgltriangle.csproj
```

Setting the `VOXEL_SPAWN` environment variable to `x,y,z` (three comma-separated floats, e.g. `-40,-107,-75`) starts the camera eye at exactly that world position instead of on the surface at the origin, which is handy for inspecting a specific cave or feature without flying to it; an unparseable value is ignored with a console warning.

The app opens a window and grabs the mouse. Controls: WASD to move, mouse to look, Space to jump, double-tap Space to toggle flight (Space/Left Ctrl for up/down while flying), `C` to toggle noclip, Left Shift for 10x speed, hold left-click to mine a block (hit-based: one hit per `World.BlockHitSeconds` = 200 ms removes 1 HP of the block's `BlockType.MaxHealth` = 5, so ~1s; damage resets if you release or switch target; instant while noclip; stepped progress overlay via `break-*.glsl`; each hit pops the block out and back via `RenderManager.RenderBlockPop`), right-click to place stone, `X` to toggle debug logging (FPS/position to console), Escape to quit.

Shaders (`assets/shaders/**/*.glsl`) and textures (`assets/textures/**/*.png`) are copied to the output directory via `CopyToOutputDirectory` rules in the csproj — **new asset types/extensions need a matching `<None Update>` entry** or they won't be found at runtime.

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
    BlockType.cs               # block ID -> atlas tiles + mining HP (1=grass, 2=dirt, 3=stone; 0=air)
    WorldGenerationConfig.cs   # all tuning constants (view radius, per-frame budgets, thread counts) and every worldgen knob (seed, ravine, cave, carver)
    MathUtils.cs               # lerp/easing/pulse helpers for animation curves + shared constants
    Mesh.cs, Shader.cs, TextureLoader.cs, FileUtils.cs, FastNoiseLite.cs (vendored, don't reformat)
    Data/                      # Vertex struct, cube geometry, frustum/plane math, direction tables
    Systems/
      WindowManager.cs         # Silk.NET window setup
      InputManager.cs          # keyboard/mouse context
      RenderManager.cs         # GL state, world/selection/UI passes, viewport
      ChunkManager.cs          # chunk lifecycle: load/unload, dirty tracking, mesh scheduling
      LodManager.cs            # LOD region lifecycle: worker thread builds, main thread uploads/unloads, burial flags
    WorldGen/
      TerrainGenerator.cs      # the 2D half: owns every 2D noise channel; Compute(chunkX, chunkZ) -> ColumnData
      ColumnData.cs            # per-chunk-column arrays: Heights (ravines included), DirtDepth, EntranceWeight, CaveIntensity
      NoiseCaves.cs            # 3D-noise caves (cheese rooms, wide/narrow spaghetti, deep caverns) on a strided lattice
      WormCarver.cs            # worm-carved tunnels: per-XZ-cell deterministic paths, cached, carved sphere by sphere
      CaveNode.cs, Worm.cs     # one tunnel sphere (center + radius); a worm's node array plus its AABB
      SplitMix64.cs            # deterministic integer-hash PRNG so worms don't depend on System.Random internals
  assets/shaders/, assets/textures/   # GLSL shaders and texture atlas (atlas.png, crosshair.png)
```

## Architecture: threading model (the most important thing to preserve)

Chunk work is split across threads, and OpenGL calls are only legal on the main thread:

1. `ChunkGenerationWorker` runs dedicated background threads that scan positions around the camera (closest first) and call `ChunkManager.QueueChunk`, which creates the `Chunk` and generates its block data **on that background thread**.
2. Meshing is scheduled by `ChunkManager.ProcessDirtyChunks` (main thread, per-frame budget `MaxDirtyChunksPerFrame`) and runs on the thread pool via `Task.Run`, throttled by a `SemaphoreSlim` (`MaxConcurrentMeshGenerations`). Mesh generation produces CPU-side `ChunkMeshData` only.
3. Completed mesh data lands in `_completedMeshQueue` and is uploaded to the GPU on the **main thread** in `ApplyCompletedMeshes`, sorted closest-first and capped at `MaxMeshUploadsPerFrame` per frame.

Rules that follow from this:

- **Never call GL functions (buffer uploads, `Mesh` creation/disposal, shader use) off the main thread.** Background work must only produce plain data handed over via the concurrent queues.
- A chunk is only meshed when its block data is ready **and all 6 cardinal neighbors exist with ready data** (`HasAllNeighbors`) — meshing reads neighbor blocks for face culling and cross-chunk AO. Breaking this reintroduces holes/seams at chunk borders (see git history).
- Shared state uses `ConcurrentDictionary`/`ConcurrentQueue`; `Chunk.IsBlockDataReady` is `volatile`. Keep new shared state lock-free in the same style.
- Editing a block on a chunk border must also remesh the affected neighbor chunk(s) — block edits use `ChunkManager.ImmediateRemesh` / `MarkDirty`.
- Performance tuning knobs all live in `WorldGenerationConfig`; change constants there rather than hardcoding budgets elsewhere.
- Animation curves (interpolation, easing, one-shot pulses) belong in `MathUtils`, not inline in gameplay or render code — everything time-driven should be lerped through one of those helpers rather than snapped.

Other invariants:

- World/chunk coordinates: `Chunk.Position` is in chunk units; world pos = chunk pos × `Chunk.Size` (16). Chunks are cubic and stacked vertically too (3D chunk grid, not 2D columns).
- Blocks are `byte` IDs; `0` is air. New block types are registered in `BlockType.Types` with atlas tile coordinates into `assets/textures/atlas.png` and an optional `maxHealth` (defaults to `BlockType.DefaultMaxHealth`, currently 5 for every block).
- Worldgen must stay deterministic and thread-safe (`FastNoiseLite.GetNoise` is read-only after setup; noise instances are configured once in static constructors and never re-tuned per call). All noise seeds derive from `WorldGenerationConfig.Seed` plus fixed per-channel offsets (`TerrainGenerator.MakeNoise`). Before player edits, terrain obeys:
  - **Nothing generated is ever solid above `height(x,z)`** — heights come from `Chunk.GetColumnData(...).Heights` (or the `GetColumnHeights` wrapper) and already include ravines, which are cut into the heightmap so the far LOD sees them too. The block at `height` is solid unless a cave or entrance carved it.
  - Both carvers only ever **remove** blocks: `Chunk.GenerateBlockData` fills the columns (grass at depth 0, dirt down to `DirtDepth`, stone below), then `NoiseCaves.Carve` and `WormCarver.Carve` clear air out of the result and `_solidCount` is recounted.
  - Caves fade out within ~`CaveSurfaceFadeEnd` blocks of the surface (`WormSurfaceMargin` for worms), except inside entrance zones — sparse patches where `ColumnData.EntranceWeight` lifts the fade and caves reach the sky.
  - Worldgen is a **pure function of world position**: each chunk generates its own blocks with no cross-chunk writes, so neighbours agree at their borders without shared state. Worm paths are cached per 64-block XZ cell in `WormCarver` exactly the way column data is cached per chunk column in `Chunk.GetColumnData` (compute-then-overwrite, wholesale `Clear` at the size limit — entries are deterministic, so a race yields identical values).
  - 3D cave noise is sampled on a `CaveNoiseStride` lattice (aligned to world coordinates so adjacent chunks share boundary samples) and trilinearly interpolated per block before thresholding.
- **Far-terrain LOD**: full-detail chunks load to `ChunkRadius` (16 chunks = 256 blocks); a cascade of LOD rings defined in `LodRegion.Levels` extends the render distance (level 1: 2-block cells to 512, level 2: 4-block cells to 1024, level 3: 8-block cells to 2048 blocks). Regions are columns meshed at reduced resolution straight from the heightmap cache (no block data). Each level's heights are eroded (min over a widened fine-column window) and biased downward so its mesh sits strictly inside real terrain *and* strictly below the next finer level — finer geometry always buries coarser via the depth buffer, so no masking or camera-driven remeshing exists. Because those shells are stacked, only one level may actually be drawn per area — otherwise they are all visible once the camera is inside the terrain — so `LodManager.UpdateBuriedFlags` marks each region whose footprint is fully covered by *loaded* finer-level regions (region grids nest exactly: one region = ratio×ratio regions of the finer level) and `RenderLod` skips those. Testing loaded regions rather than each level's radius means an unbuilt finer region can never open a hole in the horizon. The finest level (0) has no finer level to hide it, so it is additionally marked buried wherever the full-detail chunks its shell could show through are loaded *and* meshed (`LodManager.IsCoveredByChunks`, using the per-column inclusive chunk-Y ranges recorded at region build in `LodRegion.ColumnCyMin/ColumnCyMax` and `Chunk.IsMeshed`, which unlike `HasMesh` is true for all-air and fully enclosed chunks too). This is needed because caves cut into the band the shell occupies and would otherwise show it from inside; presence is tested per chunk rather than by horizontal distance because the load volume is a sphere. LOD side faces use grass at the top cell, dirt above `NominalDirtDepth` and the stone tile below it, so far cliffs match near ones. The nesting invariants (`SampleMargin(L+1) >= SampleMargin(L) + CellSize(L) - 1`, `HeightBias(L+1) >= HeightBias(L) + CellSize(L)`) must hold when adding levels, and `Camera.NearPlane`/`FarPlane` are tuned to keep those separations from z-fighting at the outermost ring. LOD draws before chunks, coarsest level first (`RenderManager.RenderLod`). Destroying a block would expose the buried LOD, so removals cap that column's LOD height below the removed block (`LodRegion.LowerColumnBelow`) and affected regions on every level rebuild (`LodManager.MarkEditedColumn`); placements need nothing.

## Conventions

- C# with file-scoped namespaces, one type per file, `_camelCase` private fields, PascalCase public members. Primary constructors are used where natural (`ChunkManager(GL gl)`).
- XML doc comments (`/// <summary>`) on classes and non-obvious public methods; brief `//` comments explaining *why* for tricky logic. Match the existing density — the code is moderately commented.
- `FastNoiseLite.cs` is vendored third-party code — do not reformat or refactor it.
- No test framework exists. Verify changes by running the game and using the `X` debug overlay (FPS, position, grounded state). Watch the console for chunk generation errors — worker threads catch and log exceptions rather than crashing.
- Branch naming follows feature work off `master` (current work is on `worldgen`). Commit messages are short imperative sentences ("Add block placement", "Fix performance").
