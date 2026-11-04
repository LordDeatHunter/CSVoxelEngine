# Codebase cleanup campaign

Handoff notes for the ongoing cleanup, so a new session can pick it up. Last updated 2026-10-21.

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
  - Baseline: 0 warnings, 2441 tests (last full run: 2440 passed, 1 failed: known flaky `LodBuildPoolTests.TheInFlightCapHoldsUnderThreeProducers`, which passed on class re-run 4/4).
- **Known flaky tests.** Re-run a failure alone, by class filter, before treating it as real
  (the workflow's verifier reads this list):
  - `GenerationalCacheTests.EveryKeyAlwaysReturnsItsOwnValueUnderFourThreads`
  - `LodManagerTests.StopJoinsTheLodGenThreads`
  - `GameServerTests.TntOnePlayerPrimesDetonatesForEveryClient`
  - the `LodBuildPoolTests` class (fails under CPU contention in a full run)
- **Resuming an interrupted run.** `resumeFromRunId` only works in the same session and only
  with byte-identical args. In a new session, relaunch the workflow: with no args it picks the
  "In progress" task up again.

## Done

Every finished task is removed from this file; `git log -- CLEANUP_PLAN.md` and the commit
messages are the record (god-class splits of World, RenderManager, UIRenderer, Program, Chunk,
Camera, PlayerModel, GameServer, ChunkManager, ChunkMesher, LodRegion, LodManager, FluidManager,
Inventory, TerrainGenerator, TreeGenerator, LeafDecayManager, PlayerPhysics and
WorldGenerationConfig; the dead-code, duplication and first early-return passes).

## In progress

Nothing is in progress. The next task is the first unchecked entry of "Still to do".

## Still to do, in order

### 1. God classes (one workflow run each)

- [ ] `src/World.cs` (~1,000 lines, has a stated reason): check whether anything is still worth
  extracting; AGENTS.md names no next candidate.
- [ ] ItemDebugger `ItemDebuggerUi.cs` / `ItemDebuggerProgram.cs` (~665 lines each): borderline.
- [ ] Test files over 700 lines (GameServerTests, LodManagerTests, TreeGrowthTests,
  TreeGrowerTests, GeometryAllocatorTests, MeshingTests, PlayerModelTests, LeafDecayTests): split
  by rule area. Lower priority.
- [ ] `ChunkMeshScheduler.cs` (566) is over the ~550 target; if it grows, move the immediate-remesh
  paths out next.

`RenderManager.cs` and `UIRenderer.cs` have stated reasons; `Core/FastNoiseLite.cs` is vendored:
never touch it.

### 2. Dead code

- [ ] Triage the public zero-reference members (delete or give a use): `Vector2Utils.Ceil/Floor/Round`;
  `Vector3Utils.Ceil/Round/IsNearZero`; `MathUtils.SinDeg`;
  `Vector3Directions.EdgeDirections/DiagonalDirections/AllDirections`; `Rgba.Black/Transparent`;
  `ChunkManager.TryGetLightGlobal`; `ImportedMapSource.ColumnCount`;
  `NetworkConfig.NetRemotePlayerReachBlocks`; `WaterRenderConfig.WaterReflectionScale`;
  `WorldFeatureConfig.OasisPalmMinGround`; `FrameProfiler.ConsoleLine`; `PostEffects.Neutral`;
  `ItemFactory.RegisterCustom`; `BreakProgress.IsActive`; `PlayerRig.TotalHeight/SwatchUv`;
  `ChunkGeometryPool.CapacityUnits`; `CampfireManager.LitCount`; `FluidState.MaxLevel`;
  `Inventory.ClearSlot`; `BranchPileGenerator.PileTop`; `GenerationalCache.Generations`;
  `ItemDisplayEditor.CanSave`; `AnimationClip.Tracks`.
- [ ] `NetMessage.PlayerJoined` / `PlayerLeft` are unused wire ids: never delete, document as reserved.
- [ ] Members used only by tests (`Vector2Utils.Lerp/Damp`, `Vector3Utils.FlattenY/WithY/LerpUnclamped`):
  production code or a test helper?
- [ ] `FluidWakeRules.IsCarvedFlowTarget` has no production caller: keep the rule or assert through
  `IsWakeTarget`.
- [ ] Two dead stores in tests: `CampfireCookingTests.cs:48`, `PoolCairnTests.cs:87/88`.
- Deliberate, leave: `ChunkTerrainFill`'s `var hasWater = false;` (const-true `WaterEnabled`).

### 3. Duplication

- [ ] Search for more near-identical methods and repeated constants or rules.
- Deliberately unfolded: `LightFlicker.CellPhase` (own axis order, would move the phases),
  `LeafParticle`/`BubbleParticle` damping, `PlayerPhysics.Gravity` (34, the player's own).

### 4. Early returns / nesting

- [ ] Hot-path depth-4 blocks (`ChunkMeshBuilder` corner loops, `ChunkMesher`'s `while (true)` key probe
  at ~line 187, `LodMeshBuilder.FillCell`): extract only against a before/after capture. Needs a
  workflow run, not a quick edit: measure mesh-build cost before and after with
  `MeshBuildCostProbe` / `LodBuildCostProbe` (OpenGLTest.Tests) plus the `perf-capture` skill's interleaved
  Release runs in a git worktree, one site per commit, and revert any extraction the JIT does not inline
  to the same speed (the loops are per-voxel and per-corner, so a call that is not inlined shows up).

### 5. Small doc and code follow-ups

Docs and build:
- [ ] `.claude/workflows/plan-and-implement.js` has a stale REPO preamble (`~/.dotnet/dotnet.exe`,
  `OpenGLTest.sln`); it is the user's file.
- [ ] Stale comments still unchecked: `ChunkManager.cs` prose naming the scheduler's and
  `PlayerPlacedIndex`'s private fields, `LodManager.cs` (~73, 211-212).
- [ ] `LodFaceEmitter`'s constructor takes 27 parameters (a grid bundle if it grows); measure the LodGen
  build cost of the promoted fields with `LodBuildCostProbe`.
- [ ] Spot-check `InventoryPanelInput.cs` for mojibake from its cp1252 -> UTF-8 re-encode.

Code:
- [ ] `PunchPose.TorsoRotation` calls `SecondaryMotion.ComputeMods` once per arm solve and
  `GetPartMatrix` recomputes `GetPose` per part (perf candidates).
- [ ] `TerrainNoise` builds every channel twice on the first `WorldSeed.Apply` (static ctor, then
  `Reseed`); `TreeBatches` vs `TreeBatch` are near-namesakes (rename to `TreeShapeCaches`?).
- [ ] `Camera.IsCreative`/`IsFlying` are `internal set`; `Inventory` exposes six internal accessors:
  narrow if possible.
- [ ] `OpenGLTest.Tests/Simulation` holds 23 files (target ~22).

## Not verified in game

The `RenderManager` and `UIRenderer` splits and the GLSL local renames in the spelling sweep
(`contact-ao`, `post`, `vignette`) were gated by build and unit tests only.

- [ ] Run `VOXEL_SCRIPT=OpenGLTest/visual-examples/basic.json` or `linear-lighting.json` with
  `VOXEL_TIME=147` from `OpenGLTest/` (see the `visual-verify` skill).
