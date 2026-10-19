# Codebase cleanup campaign

Handoff notes for the ongoing cleanup, so a new session can pick it up. Last updated 2026-10-19.

## The rules (what "clean" means)

The standing rules live in `AGENTS.md`, section **"Code organization (standing rules)"**. The
procedure for applying them lives in the **`code-cleanup` skill**
(`.claude/skills/code-cleanup/SKILL.md`). In short:

1. **No god files/classes**: past ~700 lines a class is a smell, and no file goes past ~1000
   lines without a stated reason. Split by composition, never `partial`.
2. **No god directories**: keep every folder under ~30 files (the target in use is ~22, which
   leaves headroom) and group by domain.
3. **Namespaces mirror folders**: root `VoxelEngine`, one sub-namespace per folder. Never name
   a folder after a type or an existing namespace segment, because it shadows it.
4. **American English** in identifiers, comments and docs.
5. **No dead code**: unused fields, methods, parameters, locals and usings get deleted.
6. **No duplication**: a rule that exists twice becomes one shared helper.
7. **Early returns and guard clauses** over nesting.
8. **No deep nesting**: a body that nests past ~3 levels gets an extracted method.

## How each task is run (the process)

- **Every task runs through the `cleanup-campaign` workflow** (`.claude/workflows/cleanup-campaign.js`),
  one run at a time: tasks share the working tree. Never spawn ad-hoc general-purpose agents.
- **Launch with `scriptPath`**, not `name`. A named workflow is cached when the session starts,
  so an edit to the script would be ignored. The path is:
  `scriptPath: C:\Users\DH\Documents\CodeProjects\OpenGL\CSVoxelEngine\.claude\workflows\cleanup-campaign.js`
- **No args** runs the next task of this file: the "In progress" task when there is one, else
  the first worthwhile entry of "Still to do". A task string runs that task instead. `tasks: n`
  runs n tasks back to back (stopping at the first one not accepted), `dryRun: true` stops after
  the plan, `commit: false` leaves the accepted work uncommitted, `maxFixRounds` defaults to 1 and
  `implementer: 'sonnet' | 'opus'` forces one implementer for every step.
- **Roles.** Opus calls the shots: it picks the task and briefs the scouts, writes the plan,
  assigns each step to Sonnet or to itself, and accepts or rejects the result. Sonnet surveys
  this file, scouts the code, answers Opus' questions, fact-checks the plan, implements the
  steps, builds, tests, audits the diff and commits. Opus implements the steps it reserved and
  takes over any step Sonnet leaves blocked, partial or red.
- **The user's preference:** no new tests and no visual scenarios or in-game verification. The
  only gate is a zero-warning Release build plus the existing xUnit suite. The workflow bakes this
  in, together with the toolchain and the cleanup rules, so no `extraContext` is needed.
- **The review** reads the move-diff residue, not the whole diff: the Sonnet auditor runs
  `python -I .claude/skills/code-cleanup/move_residue.py` (every removed line paired with an added
  one; only what does not pair is printed, plus line-ending, BOM and final-newline changes) and
  lists each item with an assessment; Opus decides from that and reads only the hunks it doubts.
- **After an accepted task** the workflow stages the task's paths explicitly (never
  `.claude/worktrees/`, `.sisyphus/`, `.run/` or `.gitattributes`), commits with a one-line
  subject plus the `Co-Authored-By` trailer, records the commit here (Done row, queue, line counts,
  follow-ups) and ends the run with one `Update cleanup plan` commit. A task that is not accepted
  stays uncommitted in the tree, its gaps are written into "In progress", and the next run refuses
  to start until the tree is clean. Never change git config.
- **Toolchain.** The code uses C# 14, so it needs the .NET 10 SDK, which is the system `dotnet`.
  `~/.dotnet/dotnet.exe` is a 9.0.100 SDK and fails with CS0079/CS0131/CS0411.
  - Build: `DOTNET_ROLL_FORWARD=LatestMajor dotnet build VoxelEngine.sln -c Release --no-incremental`
  - Test: `DOTNET_ROLL_FORWARD=LatestMajor dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release --no-build`
  - Baseline: 0 warnings, 2441 tests (last full run: 2440 passed, 1 known flaky `StopJoinsTheLodGenThreads` that passed on class re-run).
- **Known flaky tests.** Re-run a failure alone, by class filter, before treating it as real
  (the workflow's verifier reads this list):
  - `GenerationalCacheTests.EveryKeyAlwaysReturnsItsOwnValueUnderFourThreads`
  - `LodManagerTests.StopJoinsTheLodGenThreads`
  - `GameServerTests.TntOnePlayerPrimesDetonatesForEveryClient`
  - the `LodBuildPoolTests` class (fails under CPU contention in a full run)
  - `MeshingTests.MissingFaceNeighborAbandonsTheBuild` (depends on run order; caused by the
    known bug listed under "Bugs found" below, which a cleanup run must not fix)
- **Resuming an interrupted run.** `resumeFromRunId` only works in the same session and only
  with byte-identical args. In a new session, relaunch the workflow: with no args it picks the
  "In progress" task up again.

## Done (committed, each with a green build and the full suite)

| Commit | What |
|---|---|
| `1951116` | Rename the projects and the solution to `VoxelEngine` (`VoxelEngine.sln`, `VoxelEngine*.csproj`) |
| `8da6055` | Fix the build commands in the docs and skills (SDK 10, `VoxelEngine.sln`) |
| `d66f685` | Split flat folders into domain subfolders (the 372 flat test files into mirrored folders, `Effects/` split, `src/` root emptied of strays) |
| `6340628` | Move all code from `silkgltriangle` to `VoxelEngine.*` namespaces, one per folder, plus one `GlobalUsings.cs` per project |
| `87c5ddd` | Use Opus for the workflow architect (`ARCHITECT_MODEL` in the workflow script) |
| `590f372` | Convert British spellings to American English (identifiers, comments, docs, tools, GLSL locals, about 70 test names) |
| `0660ac3` | Split `World` (4,688 lines to 1,137) into 14 components (BlockEditor, BlockPlacer, LeftClickAction, RightClickAction, ThrowAction, BlockInteractions, WorldItemUse, MobStrikes, AmbientEffects, LocalPlayer, ClientMirror, WorldPersistence, DropPickup, WorldRenderer) |
| `3289edc` | Split crowded folders into domain subfolders (`Networking/{Client,ServerSide}`, `Rendering/{Atmosphere,Geometry,Overlays,ScreenSpace}`, `Chunks/{Meshing,Sources,Streaming}`, `Entities/{Goblins,Slimes}`, `WorldGen/Features/Underground`); every src folder is now at or under 22 files |
| `294c006` | Split `RenderManager` (4,588 lines to 1,032) into 16 pass components (FrameLighting, RenderTargets, ShadowPass, ReflectionPass, TerrainPasses, TextureUnits, GpuPassTimer, Atmosphere/SkyPasses, ScreenSpace/ScreenPasses, Overlays/{Billboard,Selection,Ui}Passes, Actors/{Mob,Object,Player,Station}Passes) |
| `f01c883` | Split `UIRenderer` (2,504 lines to 802) into 7 page renderers (Hud, Menu, InventoryPanel, CraftCard, TopStrip, LoadingScreen, Chat) |
| `3d45be4` | Split Program.cs (2,297 -> 628 lines) into App/ (AppScreen, DevDiagnostics, InventoryPanelInput, ChatInput, MenuInput, GameplayInput, InputEvents) and Networking/Client/ClientSession; Program keeps Main, OnLoad wiring, the AppScreen flow, the world lifecycle and the OnUpdate/OnRender orchestration; docs and crefs retargeted |
| `d79f434` | Split WorldGenerationConfig (2,401 lines, 921 members) into 17 per-domain static classes under src/Config/ (VoxelEngine.Config, globally imported in all four projects); WorldGenerationConfig keeps its 199 worldgen knobs (480 lines), every call site, cref, shader/tool comment and doc mention retargeted |

## In progress

Nothing is in progress. The next task is the first entry of "Still to do".

## Still to do, in order

### 1. God classes (one workflow run each)

Line counts as of `f01c883` (Program.cs is done: 628 lines after `3d45be4`; App/ files: AppScreen 4, ChatInput 149, DevDiagnostics 91, GameplayInput 297, InputEvents 227, InventoryPanelInput 485, MenuInput 175; Networking/Client/ClientSession 558). Every task text follows the Program one above: map the jobs, move
verbatim, use a composition root, list the GL, order and threading invariants that apply, and
update the docs. Folders now have headroom: `Chunks/` holds 9 files plus `Meshing/` 6.

| File | Lines | Notes |
|---|---|---|
| `src/Content/ContentRegistry.cs` | 2,383 | Loading, id assignment, baking, validation, recipes, tree species. The `Content/` subfolders exist. |
| `src/Chunks/Chunk.cs` | 1,944 | Storage, state, snapshots, generation fill, mesh upload. Respect the threading model and the state-byte invariants. |
| `src/Player/PlayerModel.cs` | 1,468 | Pose, arm frames, slice, swim, float, mods. Use the ItemDebugger modes as a mental check. |
| `OpenGLTest.Server/GameServer.cs` | 1,393 | Accept loop, Pump, intent handling, streaming answers, status. The Server project is flat and namespaced `VoxelEngine.Server`. |
| `src/Chunks/ChunkManager.cs` | 1,349 | Lifecycle, dirty scheduling, mesh scheduling, foci, unload. Threading rules apply. |
| `src/Chunks/Meshing/ChunkMesher.cs` | 1,276 | Greedy pass vs per-voxel emitters. Perf-hot: no new allocations, and keep `GreedyMeshingTests` equality. |
| `src/Lod/LodRegion.cs` | 1,236 | LOD mesh build, levels, caps. Keep the nesting invariants. |
| `src/Player/Camera.cs` | 1,175 | Pose, view cache, head motion, FOV/roll terms, water state. |
| `src/World.cs` | 1,144 | Has a stated reason. Next extraction: `Update`'s mob target/damage block and `PushFromBlast`/`PushTargetFromBlast`. |
| `src/Simulation/FluidManager.cs` | 1,066 | Tick, wake scans, hardening. |
| `src/Rendering/RenderManager.cs` | 1,032 | Has a stated reason (composition root). |
| `src/Lod/LodManager.cs` | 932 | |
| `src/UI/InventoryPanel/Inventory.cs` | 896 | |
| `src/WorldGen/TerrainGenerator.cs` | 877 | |
| `src/WorldGen/TreeGenerator.cs` | 845 | |
| `src/Rendering/Overlays/UIRenderer.cs` | 802 | Has a stated reason (shared primitives the ItemDebugger and tests bind to). |
| `src/Simulation/LeafDecayManager.cs` | 760 | |
| `src/Player/PlayerPhysics.cs` | 708 | |
| ItemDebugger `ItemDebuggerUi.cs` / `ItemDebuggerProgram.cs` | 669 / 665 | Borderline. |
| Test files over 700 (GameServerTests 1,266, LodManagerTests, TreeGrowthTests, TreeGrowerTests, GeometryAllocatorTests, MeshingTests, PlayerModelTests, LeafDecayTests) | | Lower priority. The rule targets classes; split by rule area if done. |

`Core/FastNoiseLite.cs` (2,507) is vendored: never touch it.

### 2. Dead code pass

- 67 pre-existing unused `using System…` lines, plus 2 hidden behind others
  (`SnowCanopyTests.cs`, `RecipeDefinition.cs` line 1 `using System.Text.Json.Serialization;`).
  A doc+style build finds them:
  `dotnet build VoxelEngine.sln -c Release -p:GenerateDocumentationFile=true -p:EnforceCodeStyleInBuild=true`,
  then look for IDE0005.
- Unused private members, parameters and locals, repo-wide. Use the compiler and analyzers plus a
  grep for zero references.
- Pre-existing unresolved XML crefs (about 16 CS1574 in the doc build), for example
  `ItemTooltip` → `UIRenderer`, and two game-file crefs to `GameServer`. Fix or qualify them.

### 3. Duplication pass

Known duplicates, recorded by the earlier runs:
- `AssetRoot` is declared in both `OpenGLTest.Server` and `OpenGLTest.ItemDebugger`. Move one
  shared copy into the game project.
- The inline sprite-or-cube icon blocks in `HudRenderer.DrawHotbar` and
  `InventoryPanelRenderer.DrawEntryRow` duplicate `UIRenderer.DrawIdIcon`.
- `MenuRenderer` has duplicated confirm-delete and list pages (worlds vs servers).
- `World.Update` and `World.UpdateClient` both gather the fire cells.
- Search for more: near-identical methods, and repeated constants or rules that should be one
  helper.

### 4. Early returns / nesting pass

Do it slice by slice, per folder, as separate workflow runs. Use guard clauses, `continue` and
extracted methods for bodies that nest past ~3 levels, and do not rewrite logic.

### 5. Small doc and code follow-ups found along the way

- `MenuInput`'s constructor also takes `inventoryView` (`SetPauseMenu` reads `inventoryView.IsOpen`), which the Program split plan's signature omitted. Correctly handled; noted for anyone describing the wiring.
- `InventoryPanelInput.cs` was re-encoded from cp1252 to UTF-8 during the Program split. Spot-check its non-ASCII comment characters (em dashes etc.) against HEAD's old `Program.cs` (`git show 3d45be4^:OpenGLTest/src/Program.cs`) for mojibake.
- The doc-generation build (`-p:GenerateDocumentationFile=true`) was not run after the cref retargets in `Chat.cs` and `SettingsMenu.cs`. A future run should confirm no new cref warnings.
- The `// TODO: move this to player class` `placeTimer` still relies on the static-initializer ordering quirk: it starts at 0.0, not 0.2. This affects only the storeless dev-override path. A later behavior task could make the intent explicit; it was out of scope for the cleanup.
- Next extractions named in `AGENTS.md` remain: `World.Update`'s mob target/damage block and `PushFromBlast`; `Chunk.cs`, `Camera.cs`, `ChunkManager.cs` and `ContentRegistry.cs` are still on the offender list.
- Section headers attached to the first member below them kept their old titles in their new config classes: `// --- Lighting ---` above `DayLengthSeconds` in `SkyConfig`, `// --- Ambience ---` in `AudioConfig` and `// --- GPU timer queries ---` in `PostProcessConfig`. Retitle them to match the class they now head.
- Judgment-call config placements could be revisited (a pure call-site rewrite): `TonemapExposure` and `EyeAdapt*` in `LightingConfig` rather than `PostProcessConfig`; `SelectionOutline`/`SelectionPulse` in `HudConfig`; the `Steam*` lava-hardening knobs in `JuiceConfig` while `SpringSteam*` is in `ParticleConfig`.
- git sees `WorldGenerationConfig.cs` as a rename (now `src/Config/`) plus a heavy rewrite; blame for the moved members needs `git blame -C -C`.
- `AGENTS.md` still cites the deleted `MESHER_PLAN.md` (project overview, around lines 7 and 15).
- `AGENTS.md` names tools scripts that do not exist: `generate-ore-materials.ps1`,
  `generate-mold-models.ps1` and `generate-biome-textures.ps1`. `PERF_TODO.md` (around line 1292)
  names `WorldgenShapeCacheReplayProbe.cs`, which never existed.
- `World.RefreshMobs`' doc claims indexed loops, but the code uses LINQ (it allocates per
  frame), and `ThrowAction.RefreshProjectiles` cites the same claim. Fix the doc or the loop.
- The per-frame sapling lambda in `World.Update` allocates every frame.
- `HudRenderer` never disposes the five crosshair meshes (a pre-existing leak).
- `OpenGLTest.Tests/Simulation` holds 23 files, one over the ~22 target.
- The stale REPO preamble in `.claude/workflows/plan-and-implement.js` (`~/.dotnet/dotnet.exe`,
  `OpenGLTest.sln`) is the user's file. They may want it fixed. (`cleanup-campaign.js` carries the
  correct commands, so the cleanup no longer depends on it.)

## Bugs found (not cleanup: fix as separate, normal tasks)

- **`Chunk.GenerateMeshData` stale neighbor slots.** The all-air or fully-enclosed early return
  (around `return new ChunkMeshData([], [], [], [], false);`) never calls
  `scratch.ClearNeighbors()`, and `TryResolveNeighbors` never nulls a missing slot. A later mesh
  build on the same thread (the `[ThreadStatic]` `MeshScratch.Current`) can therefore reuse the
  previous build's neighbor chunks. It may then fail to abandon when a face neighbor is missing,
  or read a stale chunk where an edge or corner neighbor should read as air.
  `MeshingTests.MissingFaceNeighborAbandonsTheBuild` fails on an unlucky test order.
  **Fix:** clear the neighbors before that early return, or null the unresolved slots. Add a rule
  test: an empty build followed by a build with a missing face neighbor on the same thread
  returns null. Record it in `TODO.md`.

## Not verified in game

Following the user's instructions, every step was gated by the build and the unit tests only.
The `RenderManager` and `UIRenderer` splits (GL draw code) and the GLSL local renames in the
spelling sweep (`contact-ao`, `post` and `vignette` fragment shaders) were never run in game. The
reviewers proved the GL call shapes and counts identical to HEAD, but a quick scenario run would
confirm it: `VOXEL_SCRIPT=OpenGLTest/visual-examples/basic.json` or `linear-lighting.json` with
`VOXEL_TIME=147`, run from `OpenGLTest/` (see the `visual-verify` skill).
