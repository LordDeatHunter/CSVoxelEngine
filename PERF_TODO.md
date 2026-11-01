# Performance TODO

Closed history (every landed item, the round write-ups, the worldgen phase B-E measurements in full)
was removed on 2026-10-28 and again on 2026-10-31; it lives in git (`git log -p -- PERF_TODO.md`).
Code comments still cite old ids (C2, C5, D2, R5, R7, R10, R11, items 19 and 26) that exist only in
that history. New items are **F#** (frame time) and **G#** (worldgen, meshing, streaming).

Every number names its machine and date. A win not yet measured is labeled *estimated*.

Dangling references (fix in a follow-up, not here): `MESHER_PLAN.md` was deleted, but the AGENTS.md
project overview (lines 7 and 15) and `.claude/skills/perf-capture/SKILL.md` (line 8) still cite it,
and the skill asks for a "measurements table" and a box to tick that this file no longer has.
`StreamingConfig.cs`'s `MaxConcurrentLodGenerations` comment points at a removed "Measurement: D2".
The old Phase 5 compute cull now lives here as F9.

## How to measure

Follow `.claude/skills/perf-capture/SKILL.md`: Release only, interleaved before/after pairs in a git
worktree, `// TEMP-DIAG` counters stripped before commit. The fly, run from `OpenGLTest/`:

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'; $env:VOXEL_DEBUG='1'; $env:VOXEL_TIME='60'; $env:VOXEL_SCRIPT='visual-examples/perf-fly.json'; & "$HOME\.dotnet\dotnet.exe" run --project VoxelEngine.csproj -c Release --no-build
```

Scenarios: `perf-fly` (10 s settle at spawn, sprint legs over land, the bay near (228,-138), a far
teleport to (-120,100)), `tree-perf`, `lod-horizon`. Headless probes, one per invocation (two in one
filter run in parallel collections and inflate the mesh probe by ~25%), from the repo root:

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'; & "$HOME\.dotnet\dotnet.exe" test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release --filter "FullyQualifiedName~<Probe>" --logger "console;verbosity=detailed"
```

The probes assert no timing and print through `ITestOutputHelper`:

- `MeshBuildCostProbe` (Chunks/Meshing): warm single-thread `GenerateMeshData` over a cube-and-lamp soup, seed 26.
- `LodBuildCostProbe` (Lod): regions per level; the first builds are biased by the cold column prefetch.
- `ColumnCacheReplayProbe`, `ChunkRekeyReplayProbe`, `PlayerPlacedUnloadReplayProbe` (Chunks).
- `GeometryLayoutReplayProbe` (Rendering/Geometry).
- No worldgen `Compute` or chunk-fill probe exists; see G1.

Traps:

- The `lighting` CPU row is the `ChunkDrawBatch` fence wait (`RenderManager.BeginFrame` ->
  `ChunkDrawBatch.BeginFrame`), so a fat row means GPU-bound. Old sub-timers: `SetLighting` 0.03 ms,
  clear + sky 0.03 ms, the wait 0.5-20 ms.
- The `gpu` header is partial on gated frames (`GpuTimingGate`), and once GPU-bound most late lines
  carry no per-pass suffix. Average only lines with non-zero `shadowNear`/`shadowFar`/`worldPass` GPU
  suffixes, and remember those lean toward early, lighter frames. The two shadow rows drop their GPU
  sample independently (`shadowFar` more often, ~40 of 58 lines against every line for `shadowNear`), so
  sum them only over lines where both report.
- The T1000 throttles its SM clock from 1530 to ~1080 MHz at ~74 C, so late lines run ~30% slower.
  Log `nvidia-smi` clocks, cool down between runs, compare interleaved pairs only.
- Worker costs never appear as rows: worldgen shows only in the `wgen` token (`WorldgenStats`), the
  mesh build only in `MeshBuildCostProbe`.
- `uiPass` includes the X overlay's own glyphs.

## Measured baselines

### 2026-10-31 (branch biomes, c8e0f3c6)

`Build: Release | debugger False | serverGC False | latency SustainedLowLatency | cpus 12`;
`GPU: Quadro T1000 with Max-Q Design/PCIe/SSE2 | NVIDIA Corporation | GL 4.6.0 NVIDIA 597.06`;
`LodGen: 1 workers`. `perf-fly` x3 (58 lines each), `VOXEL_TIME=60`; SM clock 1530 MHz at start,
last-10-s average 1086-1292 MHz (min 765) at 74-75 C. GPU table over the 68 full-timing lines
(frame mean 16.7 ms, pass sum 15.0 ms):

| Pass | GPU avg ms | share | worst |
|---|---|---|---|
| shadows | 5.35 | 36% | 15.9 |
| worldPass | 3.90 | 26% | 11.8 |
| reflect | 2.36 | 16% | 14.1 |
| cloudPass | 1.08 | 7% | 1.9 |
| lodPass | 0.91 | 6% | 10.7 |
| postPass | 0.43 | 3% | 0.95 |
| uiPass | 0.36 | 2% | 1.0 |
| waterPass | 0.34 | 2% | 1.5 |
| aoPass | 0.26 | 2% | 1.9 |

`shadows` is the one shadow row before F1 split it into `shadowNear`/`shadowFar` (see Done).
Averaging every line that carries a pass instead: shadows 5.85 ms (n=137; settle 3.2, land legs 7.7,
bay 6.7, far 4.3), worldPass 3.79, reflect 2.26, lodPass 0.99. `godPass`/`raysPass` were 0 at this time.

- Frame (avg ms): settle 10.3 (60-264 fps), land legs 24.2, bay 22.1, far 17.1; settled with 17077
  loaded / 1214-2101 visible chunks 21.6 ms (~46 fps), of which the fence wait is 15-19 ms. GPU-bound throughout.
- CPU (avg~worst): `chunkApply` 4.1-5.1~530 (5-12 sustained on the land legs), `lod` 0.8~30, `fluidTick`
  0.25~15, `entities` 0.32~11.5. One 450-740 ms frame per run on the bay teleport (`chunkApply` 530/454
  ms, or the fence wait 737 ms). The old 1306 ms `lod` spike did not reproduce.
- GC gen0/s: 10-45 streaming (phase means 16-25), 3-7 settled.
- `wgen`: 143-5430 chunks/s (~1700 avg), median 1.31 ms/chunk; cold first second 29-34 ms/chunk (tree
  batches 17.7); full 17077-chunk ring ~12 s after launch.
- Probes (alone): mesh 5475 us/build, 6223 with blue lamps (+14%; 25188 opaque + 7052 translucent
  vertices). LOD median/min/mean ms: L0 28.2/17.8/48.1 (cold firsts ~145), L1 22.3/10.0/20.4, L2
  80.3/3.9/78.6 (max 168); medians swing between invocations with the cold prefetch.

### 2026-10-28 reference (T1000, throttled, perf-fly x3)

| Pass | GPU avg ms | share | worst |
|---|---|---|---|
| shadows | 6.65 | 42% | 19.7 |
| worldPass | 3.64 | 23% | 7.1 |
| reflect | 2.54 | 16% | 10.0 |
| cloudPass | 1.20 | 8% | 2.7 |
| lodPass | 0.72 | 5% | 1.9 |
| postPass | 0.46 | 3% | 1.4 |
| waterPass | 0.35 | 2% | 1.6 |
| uiPass | 0.35 | 2% | 1.0 |
| aoPass | 0.25 | 2% | 0.8 |

Mean frame 18.1 ms, GPU sum 15.9. GPU-bound throughout; `chunkApply` avg 7 / worst 46 ms; one `lod`
spike of 1306 ms on a far teleport (once in three runs); `fluidTick` up to 18 ms; first-frame
`cloudPass` 16 ms CPU / 33 ms GPU. GC 20-45 gen0/s streaming, 4-11 settled; ~2x slower than the
2026-09-11 snapshot (settled 9-10 ms), partly thermal.

### Worldgen and LOD (headless)

Method: a temporary `Compute` probe, 64-column warm-up, median of 5 interleaved pairs in fresh
processes. Absolute cost drifts between sessions (0.18-0.40 ms per chunk column); only pairs compare.

| Phase | feature | `all` | land control | worst category |
|---|---|---|---|---|
| B (2026-10-29) | massifs and peaks | +6.8% | +6.8% | massif +16.2% |
| C (2026-10-30) | islands | +16.4% | -1.7% | island +39.8% |
| D (2026-10-30) | great lakes | +3.5% | +5.2% | lake +23.5% |
| E (2026-10-31) | great-lake rivers | +5.3% | +0.9% | outflow +7.5% |

- The tax is the site scans (`TerrainSites.CellRange` + `TryRoll`), paid before any distance cull.
- `GreatLakes.BuildSite` (cached per cell): warm accepted ~2.1 ms, rejected ~0.005 ms; the +-16000
  box ~61 ms cold / 23 ms warm.
- LOD build medians 17.7 / 17.6 / 65.3 ms at levels 0/1/2, tracking the 36/100/324 prefetched
  columns; cold sweep of 203 coarsest regions ~41 s; first finest region cold 1.1-1.5 s, warm ~7 ms;
  full 17,077-chunk ring ~5.0 s with 4 generation threads (headless).
- Identity recipe: dump `LandHeight`, `WaterLevelAt` and `SurfaceWaterWeight` bits before and after
  on a +-20000 grid at stride 37, plus `Compute` on 1000 random chunk columns; zero changes allowed
  outside the intended footprint.

## Open

Ranking: within each group the prerequisite measurement comes first (G1; F1 and F2 are done). F3-F8 follow the
share of the 2026-10-31 GPU frame their target pass owns (shadows 36%, of which the far cascade ~59%,
before F2 cut both cascades; worldPass 26%, reflect 16%, clouds 7%, lodPass 6%; F4 was tried and ruled out); F9 builds on F2;
F10-F12 are CPU-only and matter less while the frame is GPU-bound.
G follows the `wgen` and probe split. Ties and unmeasured items fall back to effort. Each entry has a
bold title, then **Files**, **Win** (measured or estimated), **Verify** and **Invariant**.

### A. Frame time (FPS)

**F3. Far cascade reuse** (`shadowFar` 3.75 ms GPU, ~59% of the shadow pass on 2026-10-31; 1.50 ms
after F2).
- **Files**: `ShadowPass.cs`; `ShadowCascade.cs` (`Update`, `StepCenter`); `FrameLighting` (calls
  `FarShadow.Update(camera.RenderEye, camera.ViewDirection, _sunDir)`, owns the key light);
  `RenderManager` (the far ViewProj uniform); a new GL-free gate like `ReflectionReuse`, with tests.
- **Prerequisite**: the far center follows the look (`Update` targets `eye + viewDir * RadiusBlocks *
  ShadowCascade.ForwardFraction`, ~123 blocks ahead), so any mouse-look moves it by many ~0.34-block
  texels. While caching, hold the center and re-center only when the target leaves an inner region of
  the box (hysteresis), or use `ForwardFraction` 0 for the far cascade, so only translation drives it.
- Re-render when: the re-center fires; the camera's depth along the sun leaves a margin; the key
  light turns by more than ~half a far texel at the box edge; the `Moonlight` handover; a geometry
  counter moves (`ChunkManager.MeshVersion` skips remeshes of meshed chunks in `ChunkMeshScheduler`,
  so add one bumped on every opaque chunk or LOD upload and free); or N frames passed. The cached map
  is sampled with its own ViewProj.
- **Interlock with F2** (done): `ShadowPass` now drops every far caster outside this frame's receiver
  hulls (`ShadowCasterCull`), which is exact only for the frame it was built for. A cached far map must
  therefore either be rendered from the full cascade-box caster set (bypass `_farCull` for that render)
  or be invalidated whenever the receiver hulls it was rendered for stop containing this frame's
  (camera or mirror pyramid moved or turned, the mirror gate flipped on). The first gives back F2's
  far gain on re-render frames; the second makes re-renders frequent under mouse-look.
- **Win**: estimated as the far cascade's 1.50 ms after F2 (3.75 ms before) times the measured skip
  rate (a `// TEMP-DIAG` hit counter on perf-fly), not far x (1-1/N), less whatever the interlock costs.
- **Verify**: perf-fly `shadowFar` pair plus the hit rate; low sun at `VOXEL_TIME` 430; `moonlight.json` at 455.
- **Invariant**: the near map stays every frame (half-rate near is ruled out); far-map leaf sway
  freezes (sub-texel); no visible pop at re-render; the hysteresis keeps the view's far-shadow region inside the box.

**F5. worldPass fragment cost and overdraw** (3.90 ms, 26%).
- **Files**: `TerrainPasses.RenderWorld`, `test-fragment.glsl`. Measure overdraw with a `// TEMP-DIAG`
  additive count view; cutout leaves defeat early-Z. A solid-only depth pre-pass is the candidate, but
  it is close to the ruled-out early-Z split, so measure it interleaved on two GPUs.
- **Win**: estimated; unknown until the overdraw count exists. **Verify**: perf-fly worldPass pair on
  the T1000 and a second GPU; forest screenshots identical.
- **Invariant**: pixel-identical output; the cutout alpha test and `CutoutMipmap` coverage unchanged.

**F7. Cloud pass** (1.08 ms, 7%; cold first line 16-24 ms GPU worst, 3.9-5.9 ms CPU worst).
- **Files**: `Rendering/Atmosphere/CloudRenderer.cs` `Render` (`CloudCopies.Gather` culls whole
  copies, then an instanced draw of the full field mesh after a depth pre-pass), `CloudCopies.cs`,
  `SkyPasses.RenderClouds`, `cloud-fragment.glsl` (the silver lining reads unit 8). Check first: split
  pre-pass from color pass with `// TEMP-DIAG` GPU timers and measure how much of a visible copy is off
  screen. Candidates: tile the field mesh and cull tiles; move the first-frame cost into load if it is lazy init.
- **Win**: estimated; the off-screen share of each drawn copy. **Verify**: perf-fly `cloudPass` pair;
  `clouds-mirror.json` and `cloud-shadows.json` screenshots.
- **Invariant**: the `CloudCopies`/`CloudCover` twins and their tests; the `SkySample` copies
  (shader-sync); unit 8 stays reserved for the cloud shadow.

**F8. `lod` CPU spike and `lodPass` worst** (low priority; 0.91 ms, 6%, worst 10.7 ms GPU).
- **Files**: `Lod/LodManager.cs`, `LodCoverPass.cs`, `LodRegionSet.cs`. The 1306 ms single-frame `lod`
  spike of 2026-10-28 did not reproduce in three 2026-10-31 runs (worst 24-31 ms). If it returns,
  capture a per-region `// TEMP-DIAG` timer (unload storm vs synchronous work vs cover pass) before assuming a cause.
- **Win**: estimated; only the rare hitch. **Verify**: perf-fly `lod` worst over three runs, far-teleport
  leg. **Invariant**: LOD nesting and burial unchanged; GL on the main thread.

**F9. GPU-driven culling: compute cull + `glMultiDrawElementsIndirectCount` + Hi-Z** (the old Phase 5, with occlusion; shadows 36% + worldPass 26%).
- **Files**: `Rendering/Geometry/ChunkDrawBatch.cs`, a new compute shader under `assets/shaders`,
  `TerrainPasses`, `ShadowPass`, `RenderTargets`. A persistent SSBO of chunk AABBs and `GeometrySlice`s
  updated on upload and free; per pass a compute shader tests the frustum plus a Hi-Z pyramid of the
  previous frame's HDR depth (raw D3D 0..1 depth reprojected through the previous view-proj, two-phase
  to stay conservative) and appends `DrawElementsIndirectCommand` plus `ChunkInfo` through an atomic
  count; the draw is `MultiDrawElementsIndirectCount` (GL 4.6 core). After F2 (F4, the CPU chunk
  visibility graph, is ruled out: Hi-Z occlusion is the remaining lever for underground chunks); the
  occlusion-less version is ruled out.
- **Win**: estimated; the GPU work of occluded chunks. **Verify**: perf-fly pair (worldPass, shadowNear, shadowFar);
  cave, forest and water screenshots identical.
- **Invariant**: the 3-slot fenced ring and per-draw fade alpha; opaque front-to-back order is lost
  (early-Z) while translucent stays CPU-sorted; `gl_DrawID`/`TO_WORLD` and the sway blocks stay
  byte-identical (shader-sync, `ChunkDrawShaderTests`).

**F10. chunkApply streaming spikes** (avg 4.1-5.1 ms, 5-12 sustained on land legs, one 454-530 ms frame on the bay teleport).
- **Files**: `Chunks/Meshing/ChunkMeshScheduler.ApplyCompletedMeshes`, `Rendering/Geometry/ChunkGeometryPool`
  (its `NamedBufferSubData` upload and grow-by-copy), `Chunk.UpdateMesh`, `StreamingConfig.MaxMeshUploadsPerFrame`
  (32; `MeshUploadBudget` is raised while loading). Check first: `// TEMP-DIAG` sub-timers (allocate vs
  upload vs bookkeeping) and whether a pool grow fires mid-play on the teleport (the other run's 737 ms
  hitch was the fence wait). Candidates: an upload budget in bytes instead of a count; a persistent-mapped
  staging ring plus `CopyNamedBufferSubData`; pre-growing the pools at load.
- **Win**: estimated; the teleport hitch is the top CPU spike of 2026-10-31. **Verify**: perf-fly `chunkApply` avg and worst pair.
- **Invariant**: GL on the main thread only; closest-first order; budgets move only against a capture.

**F11. GC attribution while streaming** (10-45 gen0/s streaming, 3-7 settled, 2026-10-31).
- **Files**: measure first with `// TEMP-DIAG` `GC.GetAllocatedBytesForCurrentThread` around the main-thread
  sections and around worker `GenerateBlockData`/`GenerateMeshData`, or `dotnet-counters` if installed.
  Legitimate: `Chunk` arrays, `TerrainGenerator.Compute`'s per-column arrays (retained by `ColumnCache`).
  Suspects: `MeshBufferPool` misses, LOH-sized LOD lists.
- **Win**: estimated; fewer gen0 pauses during streaming. **Verify**: the `gc` token pair.
  **Invariant**: no new per-frame allocation; pools hold no edit state.

**F12. Shared per-frame chunk walks** (CPU, low priority while GPU-bound).
- **Files**: `TerrainPasses.RenderWorld`/`CullWater`, `ShadowPass` (two cascades), `ReflectionPass`:
  each walks `Renderable` with its own frustum tests and several sort. Ideas: one shared camera-frustum
  visible list for `RenderWorld` and `CullWater`; column-level skips.
- **Win**: estimated; CPU only, worth it once the frame is CPU-bound. **Verify**: perf-fly CPU rows of
  the render sections. **Invariant**: zero per-frame allocation; static comparers; per-pass draw order unchanged.

### B. Worldgen, meshing and streaming

**G1. Worldgen cost probe** (prerequisite; `wgen` median 1.31 ms/chunk on 2026-10-31, cold 29-34).
- **Files**: a new `OpenGLTest.Tests/WorldGen/WorldgenCostProbe.cs` in the `MeshBuildCostProbe` style (`[Fact]`,
  `ITestOutputHelper`, only an anti-vacuity assert, `[Collection("Worldgen seed")]`, `WorldSeed.Default`). Time
  `TerrainGenerator.Compute` over fixed `SplitMix64` column lists (all, land, ocean, lake), then the
  `ChunkTerrainFill.GenerateBlockData` stages in order on fixed surface, deep-stone and sea-floor chunks: the
  `ColumnCache.GenerateColumn` loop, `NoiseCaves.Carve`, `WormCarver.Carve`, `MalachiteGenerator`/`CoalGenerator.Stamp`,
  `FillWater`/`StampMud`, `LavaGenerator.Stamp`, `TreeGenerator.Stamp`, the scenery stamps, `ScanFluidFrontier`, the
  whole call. Optionally add stage buckets to `WorldgenStats` and the `wgen` token (`FrameProfiler.FormatWorldgen`).
- **Win**: none; it ranks G3, G5 and G6. **Verify**: stable medians over three runs. **Invariant**: no seed or cache state leaks.

**G3. Ore and pocket noise skip** (bit-identical only with a proven bound, else seed-breaking).
- **Files**: `WorldGen/Features/Underground/MalachiteGenerator.cs`, `CoalGenerator.cs`, `GravelPocketGenerator.cs`.
  Each `Stamp` evaluates a 3D OpenSimplex2 noise per host cell, up to ~12k calls per deep chunk against ~1000 for
  the `NoiseCaves` lattice. Bit-identical: sample a coarse lattice (spacing s) and skip a block when every corner
  plus L*s*sqrt(3)/2 is at or below the threshold, L an analytic gradient bound for that exact FastNoiseLite
  configuration (type, frequency, octaves, gain) times a documented float-rounding safety factor; brute-force
  equality is a sanity check, never the proof. Without the bound: lattice plus trilinear interpolation, seed-breaking.
- **Win**: estimated, deep chunks. **Verify**: G1 stage pairs; byte-equal block and state dumps of N chunks.
  **Invariant**: `FastNoiseLite.cs` stays vendored and unedited.

**G5. Hoist per-chunk-column work out of `TerrainGenerator.Compute`** (bit-identical).
- **Files**: `WorldGen/TerrainGenerator.cs`, `BiomeClassifier.cs`, `TerrainSites.cs`, `TerrainPeaks.cs`,
  `TerrainIslands.cs`, `GreatLakes.cs`. Count noise and site reads per `Compute` first (`// TEMP-DIAG`,
  `WorldgenReads`-style); then gather the sites reaching the 16x16 column once, iterate them in the same order, and
  dedupe same-(x, z) re-reads such as repeated `BiomeWeights` calls. Late, optional: a `Vector256` 2D OpenSimplex2
  in a separate file, only if bit-identical (same operation order, no FMA).
- **Win**: estimated; `Compute` costs 0.2-0.4 ms per column and feeds every LOD prefetch (the ~41 s cold far sweep).
  **Verify**: G1 pairs, identity recipe, `LodBuildCostProbe`. **Invariant**: pure in position and seed; summation order kept.

**G6. Chunk-level gates for the feature stamps** (bit-identical).
- **Files**: `WorldGen/ChunkTerrainFill.cs`, the generators under `WorldGen/Features/`. About 20 `Stamp` calls in
  `GenerateBlockData` each walk 256 columns even when the chunk's Y range cannot hold the feature; add early-outs
  from the `ColumnData` height and water ranges plus each feature's vertical reach, including
  `NoiseCaves.SampleLattice` channels whose depth band excludes the chunk, only where G1 shows a cost.
- **Win**: estimated, sky and deep chunks. **Verify**: G1 pairs; byte-equal dumps. **Invariant**: gates provably conservative.

**G7. Worker thread balance** (bit-identical; full ring ~12 s after launch on 2026-10-31).
- **Files**: `Config/StreamingConfig.cs` (`MaxConcurrentBlockDataGenerations` 4, `MaxConcurrentMeshGenerations` 4),
  `Chunks/Streaming/ChunkGenerationWorker.cs`, `ChunkMeshScheduler` (`MeshRunner`). Measure each pool's busy share
  and the time to a full ring on the perf-fly teleports, then try 5/3 and 3/5 interleaved.
- **Win**: estimated, ring fill time. **Verify**: perf-fly `wgen` and fps pairs. **Invariant**: never raise
  `MaxConcurrentLodGenerations` (C5); an fps guard; no blind budget moves.

**G8. LOD region build** (bit-identical; after G5; L2 median 80 ms on 2026-10-31).
- **Files**: `Lod/LodMeshBuilder.cs`, `LodRegionClaims.TryBuildOneRegion`. Candidates: a prefetch order that hits
  already-cached columns; a heights-only column path for levels >= 1 skipping the carver-only fields
  (`EntranceWeight`, `CaveIntensity`), only if G1 shows they cost, with no double caching.
- **Win**: estimated, the cold far sweep. **Verify**: `LodBuildCostProbe` pairs. **Invariant**: LOD nesting and
  burial; identical region meshes (`LodBuildPoolTests`); no more LodGen threads.

**G9. Saved-world restore IO** (measure only).
- **Files**: `Saves/ChunkSaveStore.cs` (one Deflate file per modified chunk), `Chunk.RestoreFrom` (on the generation
  worker), `WorldLoadGate`. Time a large save's loading phase before proposing region files (a save-format change).
- **Win**: unknown until measured. **Verify**: a loading-phase timer on a heavily edited world. **Invariant**: the
  store's write and lookup ordering (`ChunkSaveStore`'s class doc).

## Done

F1, F6, G2 and G4 are all 2026-10-31, Quadro T1000 Max-Q, Release, working tree over c8e0f3c6; perf-fly
pairs interleaved (before = a worktree of c8e0f3c6) at 1530 MHz, 60-73 C; probes are medians of 3, one
per invocation. F2 (2026-11-01) carries its own setup. None of them changes anything a player sees.

**F1. Shadow pass split** (diagnostics only). `ProfileSection.Shadows` is now `ShadowNear` and
`ShadowFar` (rows `shadowNear`/`shadowFar`); `ShadowPass.RenderShadows` times itself, CPU and GPU,
split at the `FarShadow.Fbo` bind since `TIME_ELAPSED` ranges cannot nest (both open after its one early
return, so an early-out frame leaves no range open). perf-fly x3, medians over lines where both report:
near 2.56 ms, far 3.75 ms GPU (far ~59%); their sum 6.31 ms against the old `shadows` row's 5.87 ms in
the interleaved before runs (spread 5.86-5.99 before, 6.12-6.91 after: the extra query boundary on timed
frames and thermal drift; F1 changes no shadow work). No caster counts or commit bisect were taken.

**F6. Mirror skips geometry wholly below the water plane** (pixel-identical by construction).
`ReflectionPass.RenderReflection` drops an opaque chunk or LOD region when `ReflectionCull.MayReachPlane`
(GL-free, `ReflectionCullTests`) is false: top (`Chunk.WorldMax.Y`, `LodRegion.YMax`) plus
`TopMarginBlocks` below `RenderManager.WaterPlaneY`, where the mirror's first fragment statement
discards `fWorldPos.y < uClipY`. The margin is derived: `MeshOverhangBlocks` 1 (a multiblock anchor on a
chunk's top layer bakes a 2-tall model; a test checks every baked and rotated model) plus
`VertexLiftBlocks` 0.1 from `test-vertex.glsl` (leaf sway lifts at most 0.072 at `LeafSwayMax`; water and
lava waves at most 0.085/0.088, only on water-flagged vertices that never sway; the `uWind` lean is XZ
only). Translucent, entities, sky and clouds untouched. `reflect` GPU on perf-fly: median 2.70 ms before,
2.82 after, within noise (it reports only on non-reuse frames, ~20 samples a run); the gain is
view-dependent (near-horizontal views over deep terrain). `water.json` shots match to the degree two
baseline runs match each other; `clouds-mirror.json` is not a pixel baseline (a death and respawn in its
wait happens in some runs only), and a cull-off control differed from cull-on runs no more than cull-on
runs differ among themselves.

**G2. Pond verdict cache** (bit-identical). `WaterBodies.PondSiteAt`, the tenth `GenerationalCache` site
(`PondSiteCacheCapacity`/`Generations`, reset in `WorldSeed.Apply`), holds `TryLakeSite`'s (accepted,
surface, floor) per lake cell through the static factory `BuildPondSiteFor`; `LakeSample` keeps its roll,
spawn-clear and radius culls before the lookup. A temporary probe over 64 pond chunk columns:
`TerrainGenerator.Compute` 2.197 -> 0.985 ms per column (-55%), `WaterLevelAt` 5.92 -> 0.29 us per call
(steady-state hits; the cold first visit is unchanged). The identity recipe dump (19,696,688 bytes) was
byte-equal after the change and at the end. `PondSiteCacheTests` compares cached and uncached verdicts
and `LakeSample` against an uncached reimplementation.

**G4. Light flood** (byte-identical). `ChunkLighting.ComputeLight` still sets every passable sky cell to
15 but queues only seeds next to a passable non-seed cell (the cell below a column's first seed, or a
lateral cell under a neighbor column's seed start); exact because every sun seed enters at the one max
level and `SpreadLight` is a monotone max BFS. `FillLightSnapshot` copies z runs. `MeshBuildCostProbe`
(us/build, plain / blue lamps): before 6249.9 / 7299.3; seed pruning 4441.6 / 4902.2; plus run copies
4292.9 / 4802.7 (kept); final 4275.0 / 4771.7 (-32% / -35%), vertex counts unchanged. An `EdgeFlags`
table replacing `SpreadLight`'s per-dequeue div/mod gained under 1% (the JIT already turns division by a
constant into multiply-shift) and was reverted. `LightFloodTests` compares the flood against a naive
reference (per-slot BFS for tinted light) on eight generated scenes; `SpreadBlockLight` is untouched.

**F2. Cull shadow casters to the receiver volume** (2026-11-01, pixel-identical by construction; same
hardware, perf-fly x3 interleaved against a worktree of ba63a233, medians over lines where both rows
report). `ShadowPass` keeps a chunk or LOD caster only when `ShadowCasterCull` (GL-free,
`ShadowCasterCullTests`) finds its light-space footprint overlapping a receiver hull of that cascade:
`shadowNear` GPU 2.60 -> 1.71 ms (-34%), `shadowFar` 3.89 -> 1.50 ms (-61%), about 3.3 ms of GPU a
frame; CPU 0.33 -> 0.42 and 0.30 -> 0.44 ms (the after runs still carried the diagnostic counters).
Casters per frame: near chunks ~240 -> ~150 (-37%), far chunks ~627 -> ~266 (-58%), far LOD ~80 -> ~48
(-40%). Exactness: a depth-only LEQUAL pass stores an order-independent minimum, so dropping a caster
that rasterizes no texel any sample's bilinear footprint touches leaves every sampled texel bit-identical;
the hulls are the light-space convex hull of every point any consumer samples, grown by the receiver
reach (bilinear 1 + normal offset + the near PCF's diagonal half-kernel + 1 texel of float slack) and
the caster footprint grown by the multiblock overhang plus the shadow-vertex sway bound. Consumers
(only `test-fragment.glsl`, `rays-fragment.glsl` and `godrays-fragment.glsl` read units 1 and 7): the
world program (chunks, LOD, entities, water, the held item, leaf backlight whose sun-ward step keeps
light-space XY, caustics) through the camera pyramid; the mirror through `ReflectionCull.MirrorView`'s
pyramid, unioned whenever `ReflectionCull.MirrorMayRender` holds, since its refresh and visibility are
decided after the shadows; the underwater rays and godrays marches through `RenderEye` as an extra hull
point plus far corners at twice `Camera.FarPlane` (headroom for the sky target's unprojection slide).
No consumer needed the keep-all fallback. Both cascades use the whole pyramid: the far map is sampled
wherever the near fade is below 1, inside the near box too, so the old plan's "from the near fade
start" was wrong. A temporary runtime check drew only the dropped casters into a cleared map, read it
back and tested every written texel against the hulls: 0 violations over ~475M texels (perf-fly twice,
`water.json` at 360, `godrays.json` at 395). Screenshots at `VOXEL_TIME` 430 (`linear-lighting.json`),
`water.json` at 360, `leaf-backlight.json` at 1100 and `godrays.json` at 395 and 147 differ from before
no more than two before runs differ from each other. Interlock: see F3.

## Not chasing (recorded so it is not re-proposed)

- Shader ALU cuts (caustic Voronoi, 8 point lights, 4-tap PCF): culling and half-res are the allowed levers.
- Worldgen changes that alter output (coarser 2D terrain lattices, fewer octaves): only bit-identical
  work qualifies, or a change explicitly accepted as seed-breaking (old saves show seams).
- Small passes (2026-10-31): postPass 0.43, uiPass 0.36, waterPass 0.34, aoPass 0.26 ms; godrays
  ~0.35 ms (older figure, not re-measured). Revisit after shadows, world, reflect and clouds.
- Audited clean: `FluidManager`, `Shader` uniform caching, `MeshBufferPool`, `ChunkDrawBatch` (its
  fence wait is the GPU-bound symptom), `BlockCubeCache`, `ModelBaker`, `Vertex`, the station, drop
  and TNT managers, `Camera`.
- Save and net compression already use `CompressionLevel.Fastest` (`ChunkSaveCodec`,
  `ColumnFileCodec`, `LodEditState`, `ChunkNetCodec`, `LodNetCodec`).
- `LodColumnCaps` growth is a correctness item (TODO.md `M-11`); it makes
  `LodBuildPoolTests.RegionBuildsAreIdenticalWhicheverThreadRunsThem` and
  `LodHeightSamplingTests.SeparableMinMatchesTheNestedWindowAtEveryLevel` intermittent. Never "fix" it here.

## Ruled out (do not retry without new evidence)

- Early-Z cutout/solid split: regressed spawn 1.72->1.87 and forest 1.83->2.13 ms GPU on an RTX
  3070; stashed, not in the tree.
- Reflection geometry cutoff (capping mirrored chunks/LOD at 192 blocks): leaves a seam the wave
  offset turns into flicker. F6 (done) differs: it drops only geometry the clip already discards.
- Half-rate NEAR shadow map: the reflection samples it every frame, so mirrored shading steps at
  fps/2. The far cascade was never priced (F3).
- GPU compute frustum cull without occlusion (old Phase 5): removes only CPU cull+sort+fill, ~2% of
  the T1000 frame (worldPass 0.50, shadows 0.26, waterPass 0.09, reflect 0.83 ms). Pays only with GPU
  occlusion (F9) or in a CPU-bound profile above ~1 ms.
- F4, chunk visibility graph (cave culling) for the camera passes, tried and dropped 2026-11-01
  (T1000 Max-Q, Release, perf-fly `VOXEL_TIME=60`, 3+3 interleaved against a worktree of bba2ca95).
  The exact form: a 26-portal sight mask per chunk from the padded snapshot on the mesh worker (26-
  connected non-sealing cells, so edge and corner crossings count; a cell seals only as an opaque
  full-cube `Model` occluder whose tiles never discard), a main-thread portal walk from the chunk of
  the true view eye (bob, shake and shoulder offset included) stamping a one-chunk dilation, eye,
  unmeshed, untracked and fading chunks open, the graph off when an occluding cell lies inside the
  near-clip box or a leak cell (an occluder that does not seal, the kiln) is reachable; it filtered
  only `RenderWorld` and `CullWater`'s draw list, the fade lifetime still advancing. Result: of 1218.6
  frustum-visible chunks it drew 1213.6 (median 2 opaque chunks culled, ~174 triangles); translucent
  list 399 -> 397.8. worldPass GPU 3.940 -> 3.885 ms (per run 4.19/3.99/3.67 vs 3.92/3.71/3.815,
  inside the before spread), CPU 0.550 -> 0.570; frame 18.23 -> 19.42 ms, every after run slower: the
  `chunkVis` row cost 0.905 ms avg (worst 12.75 ms), the walk rerunning 28 times a second at 1.57 ms
  mean, almost all opening events while streaming (newly fading chunks, exit growth). A headless probe
  put a rerun at 1.32-1.40 ms after a bit-parallel rewrite of the walk (5.0 ms scalar). Why it culls
  nothing here: `Renderable` holds only chunks with geometry, an underground chunk has geometry only
  where it touches caves, the noise caves and worms percolate at chunk level through single edge and
  corner cells, and the exact one-chunk dilation draws every closed neighbor of reached space. It was
  not pixel-identical either: a temporary occlusion-query check (the culled chunks redrawn against the
  frame's depth, under both Lequal and Less) passed 2-3 samples in perf-fly (4 frames), world-events
  (3 frames) and terrain-shape (975 frames of one static shot, chunk (-91,0,304) at ~175 blocks),
  unattributed pixel-pair leaks at 130-175 blocks consistent with rasterization or T-junction cracks;
  0 in eye-adaptation, lava, ores, water, animations and a mining and flight-into-rock run. The whole
  attempt was reverted (patch with the diagnostics kept outside the tree). Retry only with GPU
  occlusion (F9's Hi-Z), not a CPU connectivity graph.
- Fluid 16-slot chunk cache: no spike reduction (16 vs 256 slots identical); a per-tick cap freezes fluid.
- Mud 6-face gate: not outcome-equivalent (diagonal dirt can be wet).
- VSync and limiters, GL debug context, per-frame `GetError`, worker priorities, uniform location
  lookups, indexed mesh upload: audited, not the problem.
- R3, half-res water scene copy: `RenderTargets.CopyScene` copies full-res depth unconditionally and
  that depth is needed downstream; only the color copy could shrink, inside a 0.09 ms waterPass, and
  it would blur everything seen through water. Closed 2026-09-20.
- C5, a bigger LodGen pool: 2-3 workers built LOD 1.26-1.31x faster but cost 18-30% of burst fps and
  slowed the full ring 5.0->6.1 s; a LOD build is column generation in disguise.
  `MaxConcurrentLodGenerations` stays 1; the lever is cheaper columns (G5, G8).
