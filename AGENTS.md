# AGENTS.md

Guidance for AI coding agents working in this repository.

## Project overview

A Minecraft-style voxel engine written in C# on .NET 9, using **Silk.NET** (windowing, input, OpenGL bindings) and StbImageSharp for texture loading. It renders an infinite, procedurally generated world (FastNoiseLite OpenSimplex2 noise) with chunk streaming, an underground of 3D-noise caves, worm-carved tunnels and heightmap ravines, a hell of endless cavern layers below a barrier band and floating sky islands above the clouds (see "Layers: hell below, sky above"), binary greedy chunk meshing with ambient occlusion (plain cubes are culled by bitmask and merged into larger quads wherever the result is pixel-identical; procedural blocks and fluids keep per-quad emitters — see "Chunk meshing"), frustum culling, block breaking/placement via raycast, and simple player physics (gravity, jumping, collision). The player spawns standing on the terrain at the origin (`SpawnOverrides.SpawnPosition`), or where they left a saved world; worlds are created, listed and opened from a main menu and saved to disk (see "Launch flow" and "Saves").

The renderer requires an **OpenGL 4.6 core** context: `WindowManager` requests `APIVersion(4, 6)`
with `ContextProfile.Core`, and every shader under `assets/shaders/` declares `#version 460 core`.
The chunk draw path uses those 4.6 core features directly — `gl_DrawID`, `BufferStorage`,
persistent buffer mapping and `glMultiDrawElementsIndirect` (see "Chunk drawing" below) — so this
is a hard requirement rather than a preference; any 2015+ desktop driver on Windows or Linux
provides it, but **macOS (stuck at GL 4.1) is no longer supported**. The opaque chunk pass also runs compute shaders
(the Hi-Z occlusion cull, which zeroes culled commands in the indirect buffer in place; see "Chunk
drawing"), another 4.3+ core feature.

The game is one project under `OpenGLTest/`; the item debugger (`OpenGLTest.ItemDebugger/`) and the dedicated server (`OpenGLTest.Server/`) are separate executables that reference it, as is the wiki data exporter (`OpenGLTest.WikiExport/`, see the `layout-*` docs). Historical naming quirks to be aware of:

- The solution is `VoxelEngine.sln`, the project folder is `OpenGLTest/`, the csproj is `OpenGLTest/VoxelEngine.csproj`, and all code lives under the `VoxelEngine` root namespace with one sub-namespace per folder (see "Code organization"). Keep a new file in the namespace its folder maps to.
- Verification is by building and running the game **and** by the xUnit tests in `OpenGLTest.Tests/` (headless, no window: `dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release`) plus the scripted visual scenarios under `OpenGLTest/visual-examples/` (see `VOXEL_SCRIPT` below). There is no CI.

## Build and run

```powershell
dotnet build VoxelEngine.sln -c Release
dotnet run --project OpenGLTest/VoxelEngine.csproj -c Release
```

Always build and run **Release**. Debug is several times slower in the voxel loops (mesh
builds, light propagation, the per-frame chunk walks), so a framerate or hitch observed in Debug
says little about the game. `Directory.Build.props` at the repo root makes Release the default
when no `-c` is given to a project build or `dotnet run`; a solution build still needs the
explicit `-c Release`, and Rider's run configuration follows the solution configuration
dropdown, which must be set to `Release | Any CPU`. Use `-c Debug` only when you need
breakpoints. The startup line `Build: Release | debugger ...` confirms which one is running.

All csprojs target **net9.0**, but the code uses C# 14 (null-conditional assignment such as
`mouse?.MouseUp += ...`, first-class span conversions), so building needs the **.NET 10 SDK**: an
older SDK (e.g. the 9.0.100 in `~/.dotnet`) fails with CS0079/CS0131/CS0411. Use the SDK 10 launcher
and, on a machine that only has the 10 runtime, prefix every `dotnet` command with
`DOTNET_ROLL_FORWARD=LatestMajor` or the build and the tests refuse to run at all.

## Where things are documented

This file is deliberately small: it is loaded into every agent session. The detailed reference lives in
`docs/agents/`, one topic per file. **Never read a whole doc.** Find the section with
`grep -n "^#\|^\*\*" docs/agents/<file>.md`, then Read just that range (offset/limit). To find where a type, file or
constant is described, `grep -rn "<name>" docs/agents/` (the layout files list every source file with a one-line
purpose). When you change code, edit the doc section that owns it (and nothing else), in the same change.

| Topic | File | Related skill |
|---|---|---|
| Launch flow, main menu, save folder, persistence rules, autosave, teardown | `launch-and-saves.md` | |
| `VOXEL_*` env vars, scenario ops, item debugger, `X` profiler overlay | `dev-tools.md` | `visual-verify`, `item-debugger`, `perf-capture` |
| HUD, swimming, drowning, lava, regen, mana, status effects (player), gear, chests, waystones | `player-systems.md` | |
| Water rendering, linear lighting, fog, sky, godrays, AO, per-tile shader terms | `rendering.md` | `shader-sync` |
| Vertex layout, texture arrays, material atlas, greedy mesher, biome tint, chunk drawing, Hi-Z cull | `meshing-and-chunks.md` | `shader-sync` |
| Block, item, loot, model schemas, multiblocks, building blocks | `content-blocks-items.md` | `add-content` |
| Tree species, growth engine, felling, leaf decay | `trees.md` | |
| Recipes, kiln, casting, bloomery, anvil, iron, the barrier, hell generation | `recipes-and-metalworks.md` | `add-content` |
| Spell parts, casts, status effects | `spells-and-effects.md` | `add-content` |
| Threading model, state byte, edit-path hooks, fluid/gravity/TNT, worldgen invariants, far LOD | `architecture-and-worldgen.md` | `edit-path-checklist` |
| Dedicated server, world sources, wire protocol, intents, client, chat | `multiplayer.md` | |
| Biomes, terrain shape, water bodies, snow, scenery, cave biomes | `biomes.md` | `biome-worldgen` |
| Realm layers (hell, sky), structures, ruins, waystone satellites | `layers-and-structures.md` | `biome-worldgen` |
| Mob base and shared rules, spawn pacing, slime, goblin, wildlife (deer, boar, crow, dragonfly) | `entities.md` | |
| Hell, sky, cave mobs, flying rules, spiders | `realm-mobs.md` | |
| Skeleton, battle tower, Skeleton King, sandworm, icebergs | `tower-and-bosses.md` | |
| Clips, `EntityAnimator`, player and camera juice | `animation.md` | `item-debugger` |
| File tree (every source file, one line each) | `layout-1-src-core.md` (src root, App, Audio, Chunks, Config, Content, Core, Data, Effects, Entities), `layout-2-src-graphics-rendering.md` (Graphics ... Rendering), `layout-3-src-saves-worldgen.md` (Saves ... WorldGen), `layout-4-projects-tests-tools.md` (other projects, tests, tools) | `code-cleanup` |

Cross-references inside the docs such as `see "Chunk drawing" below` name a section; use the table to find its file.

## Non-negotiables (details in the docs above)

- **GL only on the main thread.** Worker threads produce plain data handed over through concurrent queues. A chunk meshes only
  when its block data and all 6 face neighbors (and the 3x3 chunk columns) are ready.
- **Worldgen is a pure, deterministic, thread-safe function of position and seed.** No cross-chunk writes; a `GenerationalCache`
  holds only values that are a pure function of the key (never player edits).
- **Content is data.** Blocks, items, loot, models, entities, trees, recipes, spell parts and effects are JSON; add a data flag
  instead of a per-id branch. Ids are ushort (up to 65,535) shared by blocks and items; the per-block state byte stays a byte.
- **The per-block state byte's meaning is owned by the block id**; every write that is not that meaning zeroes it.
- **Every block-edit path** calls the full hook set (fluid, leaf decay, gravity, LOD, canopy, saves, networking): use the
  `edit-path-checklist` skill when adding or reviewing one.
- **Shader and GL-free twin changes** go together: use the `shader-sync` skill.
- **Authoritative simulation**: singleplayer and the server tick; a client only mirrors and predicts and never writes a cell.
  A wire change bumps `NetFraming.ProtocolVersion`.
- **Tuning constants** live in `src/Config/` classes; animation curves in `MathUtils`.
- **Release builds only** (SDK 10 / C# 14, see above). The working tree is **CRLF**: use the `crlf-safe-edit` skill for any
  scripted or bulk edit.
- **Tests assert rules, not constants** (`functional-tests` skill). Verify visible changes with a `VOXEL_SCRIPT` scenario
  (`visual-verify` skill).
- Placeholder art is recorded in `USER_TODO.md` in the same change; plans live in `plans/` (`plan-docs` skill).

## Code organization (standing rules)

These are standing rules for every change to this codebase, not a one-off task list. When you
touch a file that violates one of them, fixing it is in scope.

- **No god classes.** One class, one coherent job. Past roughly 700 lines a class is a smell:
  move the stray responsibilities out into a well-named class (composition, not `partial`).
  No source file goes past ~1000 lines without a stated reason. `World.cs`,
  `Program.cs`
  are the current offenders — keep shrinking them, never
  grow them. `World.cs` (about 1,000 lines, down from 4,700) has a stated reason: it is the
  composition root that builds and owns the per-job components (`BlockEditor`, `BlockPlacer`,
  `LeftClickAction`, `RightClickAction`, `ThrowAction`, `BlockInteractions`, `WorldItemUse`,
  `MobStrikes`, `AmbientEffects`, `LocalPlayer`, `ClientMirror`, `WorldPersistence`,
  `DropPickup`, `MobDirector`, `WorldRenderer`) and keeps the order-sensitive `Update`/`UpdateClient` frame
  orchestration. `RenderManager.cs` (1,032 lines, down from 4,588) has a stated reason
  too: it is the GL resource owner whose `LoadResources`/`Dispose` fix the creation and
  disposal order, plus the world programs' one-time and per-frame uniforms
  (`InitWorldProgram`, `ApplyFrameUniforms`, `SetDrawUniforms`); its passes live in the
  components it builds and exposes as internal properties (`GpuPassTimer`, `TextureUnits`,
  `RenderTargets`, `FrameLighting`, `SkyPasses`, `ScreenPasses`, `BillboardPasses`,
  `SelectionPasses`, `StationPasses`, `ObjectPasses`, `PlayerPasses`, `MobPasses`,
  `ShadowPass`, `TerrainPasses`, `ReflectionPass`, `UiPasses`). `UIRenderer.cs` (801
  lines, down from 2,504) has a stated reason too: it owns the UI's GL resources and their
  creation and disposal order (the ui program, the count font, the text batch buffers, the
  item-icon textures and frame meshes) and the shared draw primitives every page and the item
  debugger use; its pages live in the components it builds and exposes as internal properties
  (`HudRenderer`, `TopStripRenderer`, `InventoryPanelRenderer`, `CraftCardRenderer`,
  `MenuRenderer`, `LoadingScreenRenderer`, `ChatRenderer`). `Chunk.cs` (690 lines, down from 1,944) is under the smell line now: its ids live in `Chunks/ChunkBlocks` (palette storage) and it owns the volatile reference to them, the volatile state byte and light snapshot, the raw writers and edit API, the save/network snapshots and its GPU slices, with its worldgen fill in `WorldGen/ChunkTerrainFill`, its restore from a snapshot in `Chunks/ChunkRestore`, its mesh build in `Chunks/Meshing/ChunkMeshBuilder` and `ChunkLighting`, and the face and AO tables in `ChunkFaceTables`. `Camera.cs` (684 lines, down from 1,175) is under the smell line now: it is the player's public facade, the one type `PlayerPhysics`, `World` and its components, the renderer passes, the server and the tests compile against, and it owns the pose, the mouse look, the eye fluid state, the mode setters and the `IMobTarget` side; its view juice, F5 rig, mode toggles, view-projection cache, spawn overrides and falling-cube shove live in `HeadMotion`, `ShoulderRig`, `ModeToggles`, `ViewProjectionCache`, `SpawnOverrides` and `BodyCollision`, and about 290 of its lines are the XML docs of the members it forwards to them. `Program.cs` (628 lines, down from 2,297) has a
  stated reason too: it is the process entry point and the app's composition root: `Main` registers the
  window callbacks, `OnLoad` builds the platform managers and the components (`DevDiagnostics`,
  `ClientSession`, `InventoryPanelInput`, `ChatInput`, `MenuInput`, `GameplayInput`, `InputEvents`), it
  owns the app-screen flow and the world lifecycle (the deferred PLAY/CREATE/SAVE & QUIT/CONNECT work,
  opening and saving a world, the save on close), which the components read through `Program.ActiveWorld`,
  `Program.CurrentWorld` and `Program.Screen`, and it keeps the frame-order-sensitive
  `OnUpdate`/`OnRender` orchestration.
- **No god directories.** Keep any one folder under ~30 files and group by domain. The `src/`
  root holds the app entry and core types only; a new file goes into the folder of its area
  (a new feature area gets a new folder), never into a catch-all. `Networking/` is split into
  its shared protocol (the folder root), `Client/` and `ServerSide/` (not `Server/`, which would read
  as the dedicated server's `VoxelEngine.Server`); every src folder stays near ~30 files (`WorldGen/` is at
  31, its worldgen core types; its generators already sit in `Features/`, `Hell/` and `Trees/`), so the god-class
  split-outs land in their area's folder without crowding it.
- **Namespaces mirror the folder tree.** Root namespace is `VoxelEngine`; a file in
  `OpenGLTest/src/WorldGen/Trees/` declares `namespace VoxelEngine.WorldGen.Trees;`, and only
  the six files directly in `src/` (Program, World, WorldPhase, WorldLoadGate, WorldSeed,
  BlockType) are in `VoxelEngine` itself. The tests mirror their own tree the same way:
  `OpenGLTest.Tests/Chunks/` is `VoxelEngine.Tests.Chunks` and the shared fixtures in
  `Support/` are `VoxelEngine.Tests.Support` (no test sits at the project root); the server and
  the item debugger are flat (`VoxelEngine.Server`, `VoxelEngine.ItemDebugger`). Moving a file
  means changing its namespace line and letting the compiler name the usings to add. Never
  park a new file in the root namespace out of convenience; never add an unrelated type to
  whichever namespace a file happened to have. Each project keeps a short, deliberate
  `GlobalUsings.cs` at its root beside the csproj (`OpenGLTest/GlobalUsings.cs`,
  `OpenGLTest.Tests/GlobalUsings.cs`, `OpenGLTest.Server/GlobalUsings.cs`,
  `OpenGLTest.ItemDebugger/GlobalUsings.cs`) listing only the namespaces most of that
  project's files need: `VoxelEngine.Core` everywhere (the `Vector2Utils`/`Vector3Utils`
  extension methods, `MathUtils`, `Log`) and `VoxelEngine.Config` everywhere (the per-domain tuning
  constants, read by the game, the server, the item debugger and the tests alike), plus `VoxelEngine.Chunks`,
  `VoxelEngine.Content` and `VoxelEngine.Tests.Support` in the test project. Everything else is
  an explicit `using` in the files that use it, including a using that only an XML `cref`
  needs (a doc build, `-p:GenerateDocumentationFile=true`, counts it as used; the plain build
  checks neither crefs nor unused usings). The game and the item debugger also carry
  `global using Shader = VoxelEngine.Graphics.Shader;`, because `Silk.NET.OpenGL` has a
  `Shader` struct too; `Plane` likewise clashes with `System.Numerics.Plane`, so a cref to the
  engine's is written `VoxelEngine.Data.Plane`. The vendored `Core/FastNoiseLite.cs` carries
  `namespace VoxelEngine.Core;` as its only local edit.
- **American English** in identifiers, comments and docs — `color` (not colour), `behavior`,
  `initializer`, `center`, `gray`, `normalize`, `canceled`, `traveled`, `catalog`, `license`,
  `meter`. Applies to renames of existing symbols too.
- **No dead code.** Delete unused private fields, methods, parameters, locals and `using`
  directives on sight; git keeps the history. Do not keep anything "for later". Build warnings
  must stay at zero. The plain build does not report dead code; measure it with the doc+style
  build in the code-cleanup skill.
- **No duplication.** A rule that exists twice becomes one shared (and, where it is a rule,
  tested) helper. The GL-free-twin pattern (a `Foo` next to a `FooRenderer`) is the same idea
  for shader/renderer pairs: one definition, two consumers.
- **Early returns over nesting.** Guard clauses at the top of a method, `continue`/`break` over
  a wrapped loop body. A loop body that still nests past ~3 levels gets an extracted method.
- **Plans live in `plans/`.** The roadmap, perf, biome, cleanup and tree plans share one layout
  (Status / In progress / Open / Reference, `[ ]` checkboxes, finished entries deleted); the guide is
  `plans/README.md` and the procedure is the `plan-docs` skill.
- **Keep the docs honest.** When a file moves or splits, update its line in the `layout-*.md` doc under
  `docs/agents/` and any path reference to it; when behavior changes, edit the doc section that owns it. This file
  changes only for the index, the non-negotiables, the standing rules and the conventions. Stale docs send the next
  agent to a file that no longer exists.

## Workflows

Three tiers, cheapest that fits (details in `.claude/workflows/README.md`): **direct** for small or well-scoped changes; `quick` for most 1-6 file changes with a testable or visible result (2-3 agents: one Opus does and one Sonnet builds, tests and films it; args `{task, extraContext?, fullTests?}`); `plan-and-implement-lite` for mid-sized work (roughly 2-6 files, design mostly clear: 4 Sonnet scouts, one Opus planner, 1-2 Opus implementers run sequentially, the last one builds, tests and self-reviews; args `{task, extraContext?, effort?: "medium"|"high", dryRun?}`); `plan-and-implement` for large, cross-cutting or design-unclear work (adds plan red-team, a verifier and an acceptance review, ~20 min / ~1M tokens). **Codebase scouting is always Sonnet** in every tier, and any agent you spawn by hand must pass `model` explicitly.

## Conventions

- C# with file-scoped namespaces (one per folder, see "Code organization"), one type per file, `_camelCase` private fields, PascalCase public members. Primary constructors are used where natural (`ChunkGenerationWorker(ChunkManager chunkManager, StreamingFocus focus)`).
- XML doc comments (`/// <summary>`) on classes and non-obvious public methods; brief `//` comments explaining *why* for tricky logic. Match the existing density — the code is moderately commented.
- **New blocks go in JSON, not C#.** When a block has to behave differently, prefer a new data flag on `BlockDefinition`/`BlockType` that every call site reads over another per-id branch in gameplay code — the point of the content registry is that `World`, `TntManager` and the mesher ask "what does this block do?" instead of "which block is this?".
- `FastNoiseLite.cs` is vendored third-party code — do not reformat or refactor it.
- xUnit tests live in `OpenGLTest.Tests/` — run `dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release`. They cover headless logic and simulated gameplay without a window (fixture: `Support/TestContent.cs`); add a test with every feature instead of a throwaway script — but only when there is a *rule* to lock down. A test must assert behavior (a gate, a formula, a state transition, an invariant that survives content tuning), never a constant, a layout number, a content literal or a starting-stock/seed count (`Assert.Equal(1, inventory holds one converter)` is exactly as pointless as pinning a block's hardness) — see the `functional-tests` skill's ban list for the full rule and rewrite table. Verify feel-in-game by running the game and using the `X` debug overlay (FPS, position, grounded state). Watch the console for chunk generation errors — worker threads catch and log exceptions rather than crashing.
- Branch naming follows feature work off `master` (current work is on `worldgen`). Commit messages are short imperative sentences ("Add block placement", "Fix performance").
