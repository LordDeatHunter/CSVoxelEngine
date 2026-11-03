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
- `WorldgenCostProbe` (WorldGen): `TerrainGenerator.Compute` per column category (all, land, ocean, lake/river,
  highland, massif, great lake), cold and warm; per-stage `ChunkTerrainFill.GenerateBlockData` through
  `WorldgenStageTimer` on surface, above-surface, sky, deep-stone, cave-heavy and sea-floor chunks, cold and warm.
  Runs in the default suite (~9 s); see G1 under Done for how to read cold and warm.

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
- Worker costs never appear as rows: worldgen shows only in the `wgen` token (`WorldgenStats`), which is
  one whole-chunk figure; `WorldgenCostProbe` splits it by stage headlessly. The mesh build shows only in
  `MeshBuildCostProbe`.
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

Ranking: within each group the prerequisite measurement comes first (G1, G6, F1, F2, F3, F7, F9 and F10 are done). F8 follows the
share of the 2026-10-31 GPU frame its target pass owns (shadows 36%, of which the far cascade ~59%,
before F2 and F3 cut it; worldPass 26%, reflect 16%, clouds 7%, lodPass 6%; F4, F5 (an exact worldPass depth
pre-pass) and F13 (exact reflection cuts) were tried and ruled out; F3, F7 and F9 are done);
F11-F12 are CPU-only and matter less while the frame is GPU-bound (F10, the chunkApply hitch, is done).
G follows the G1 stage split (`WorldgenCostProbe`). Ties and unmeasured items fall back to effort. Each entry has a
bold title, then **Files**, **Win** (measured or estimated), **Verify** and **Invariant**.

### A. Frame time (FPS)

**F8. `lod` CPU spike and `lodPass` worst** (low priority; 0.91 ms, 6%, worst 10.7 ms GPU).
- **Files**: `Lod/LodManager.cs`, `LodCoverPass.cs`, `LodRegionSet.cs`. The 1306 ms single-frame `lod`
  spike of 2026-10-28 did not reproduce in three 2026-10-31 runs (worst 24-31 ms). If it returns,
  capture a per-region `// TEMP-DIAG` timer (unload storm vs synchronous work vs cover pass) before assuming a cause.
- **Win**: estimated; only the rare hitch. **Verify**: perf-fly `lod` worst over three runs, far-teleport
  leg. **Invariant**: LOD nesting and burial unchanged; GL on the main thread.

**F9. GPU occlusion culling** (the old Phase 5): the opaque chunk pass's order-preserving Hi-Z cull
landed 2026-11-01, see Done. Left open: the mirror and translucent passes are not occlusion-culled, the
shadow cascades gain nothing (Done), and the frustum test and sort stay on the CPU.

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

Ranked by the G1 split (`WorldgenCostProbe`, medians of 3; G1, G6 and G3 under Done have the tables).
After G3 (2026-11-03) a warm fill (every cache hot) costs 0.11-0.65 ms per filled chunk (0.13-0.85 just before
it, 0.38-1.13 before G6), led by `NoiseCaves`: 26-34% of the probe mix, then `Fill` 14-16%, G3's three lattice
stamps about 21% together (42% before G3) and the snow canopy pass about 6%; the 18 feature stamps G6 gated are
noise. On a deep-stone or cave-heavy fill `NoiseCaves` (about 0.19 ms) now costs about as much as the three ore
and pocket stamps together (about 0.18 ms). A cold fill (every cache cleared)
costs 49-145 ms, of which 69% is tree batch growth, 24% worm cells and 6% neighbor columns (G5). No open item
targets the first two: they amortize over a 64x64 cell in game, and only a cold-start measurement in game would
justify one.

**G5. Hoist per-chunk-column work out of `TerrainGenerator.Compute`** (bit-identical).
- **Files**: `WorldGen/TerrainGenerator.cs`, `BiomeClassifier.cs`, `TerrainSites.cs`, `TerrainPeaks.cs`,
  `TerrainIslands.cs`, `GreatLakes.cs`. Count noise and site reads per `Compute` first (`// TEMP-DIAG`,
  `WorldgenReads`-style); then gather the sites reaching the 16x16 column once, iterate them in the same order, and
  dedupe same-(x, z) re-reads such as repeated `BiomeWeights` calls. Late, optional: a `Vector256` 2D OpenSimplex2
  in a separate file, only if bit-identical (same operation order, no FMA).
- **Win**: measured (G1): `Compute` costs 0.24-0.33 ms per column warm in every category and about the same cold,
  except a great lake at 0.88 cold / 0.23 warm (its site verdict build; 4 columns). In a chunk fill it is the
  `Columns` stage: 3-6% of a cold fill (13% sea floor), all 6.5 ms of a cold sky chunk (the tree-reach neighbor
  columns), ~0.1% warm. So it matters for cold column fills and every LOD prefetch (the ~41 s cold far sweep), not
  for a warm fill. **Verify**: G1 pairs, identity recipe, `LodBuildCostProbe`. **Invariant**: pure in position and
  seed; summation order kept.

**G8. LOD region build** (bit-identical; after G5; L2 median 80 ms on 2026-10-31).
- **Files**: `Lod/LodMeshBuilder.cs`, `LodRegionClaims.TryBuildOneRegion`. Candidates: a prefetch order that hits
  already-cached columns; a heights-only column path for levels >= 1 skipping the carver-only fields
  (`EntranceWeight`, `CaveIntensity`), only if a split of `Compute` shows they cost, with no double caching.
- **Win**: estimated, the cold far sweep. G1 timed whole `Compute` only: at 0.25-0.33 ms a column, a level-2
  region's 324 prefetched columns come to 81-107 ms against the 80 ms L2 median, so `Compute` dominates a cold
  build. Whether `EntranceWeight`/`CaveIntensity` cost anything inside it is still open. **Verify**:
  `LodBuildCostProbe` pairs. **Invariant**: LOD nesting and burial; identical region meshes (`LodBuildPoolTests`);
  no more LodGen threads.

**G7. Worker thread balance** (bit-identical; full ring ~12 s after launch on 2026-10-31).
- **Files**: `Config/StreamingConfig.cs` (`MaxConcurrentBlockDataGenerations` 4, `MaxConcurrentMeshGenerations` 4),
  `Chunks/Streaming/ChunkGenerationWorker.cs`, `ChunkMeshScheduler` (`MeshRunner`). Measure each pool's busy share
  and the time to a full ring on the perf-fly teleports, then try 5/3 and 3/5 interleaved.
- **Win**: estimated, ring fill time. **Verify**: perf-fly `wgen` and fps pairs. **Invariant**: never raise
  `MaxConcurrentLodGenerations` (C5); an fps guard; no blind budget moves.

**G9. Saved-world restore IO** (measure only).
- **Files**: `Saves/ChunkSaveStore.cs` (one Deflate file per modified chunk), `Chunk.RestoreFrom` (on the generation
  worker), `WorldLoadGate`. Time a large save's loading phase before proposing region files (a save-format change).
- **Win**: unknown until measured. **Verify**: a loading-phase timer on a heavily edited world. **Invariant**: the
  store's write and lookup ordering (`ChunkSaveStore`'s class doc).

## Done

F1, F6, G2 and G4 are all 2026-10-31, Quadro T1000 Max-Q, Release, working tree over c8e0f3c6; perf-fly
pairs interleaved (before = a worktree of c8e0f3c6) at 1530 MHz, 60-73 C; probes are medians of 3, one
per invocation. F2, F9, F7, F10, G1 and G6 (2026-11-01) and G3 and F3 (2026-11-03) carry their own setup. None of
them changes anything a player sees, except G3, which re-lays the ore veins and gravel pockets by decision, and F3,
whose cached far map freezes leaf sway and steps the far key light by under half a texel between renders.

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
no more than two before runs differ from each other. The far half was dropped again by F3 (Done): a
cached far map is drawn from its whole cascade box.

**F9. Hi-Z occlusion cull of the opaque chunk pass** (2026-11-01, pixel-identical by construction; same
hardware, 1280x720, perf-fly `VOXEL_TIME=60` x3 interleaved against a worktree of 6c6eeaa6).
`TerrainPasses.RenderWorld` keeps its frustum cull, sort and fade, adds the whole front-to-back run in
one `BeginPass` and draws it as `ChunkDrawBatch.DrawRange` segments split at 1/8 and 1/2 of the chunks
(`PostProcessConfig.ChunkOcclusionSplits`). At each split `ChunkOcclusionCuller` builds a half-res R32F
max pyramid of the HDR depth the LOD and the earlier segments wrote (`hiz-build-compute.glsl`, twin
`HizPyramid`) and `hiz-cull-compute.glsl` (twin `HizOcclusion`, `HizOcclusionTests`) zeroes, in place in
the ring, the `InstanceCount` of every later command whose chunk box, grown by
`ShadowCasterCull.CasterReachBlocks`, has its nearest corner depth strictly behind the pyramid max over its
outward rect plus one texel and 8/2^24. Exact because nothing is reordered and a culled chunk has no
fragment that passes `LEQUAL` against depth already written, fading or not; no history, no reprojection.
- Gate (same setup, a `// TEMP-DIAG` depth readback every 30 frames with a CPU twin of the test): ~60% of
  worldPass indices occluded by the frame's own depth, about two thirds of them underground and one third
  behind hills; skipping them in alternating 4 s blocks cut worldPass GPU by 2.02 ms median
  (1.28/2.18/2.02), and simulated splits at 1/16, 1/8, 1/4, 1/2 culled 54.9-64.4% of the indices. The
  history-and-reorder design first planned saved 0.01 ms (Ruled out).
- worldPass GPU 3.25 -> 2.38 ms median (before runs 2.77/3.60/3.28, after 2.56/2.29/2.27), CPU 0.52 ->
  0.62 ms (the extra dispatches and uniform sets); frame 15.71 -> 14.01 ms; the worst frame per run
  26.4-29.1 ms before, 23.7-26.1 after. A first interleave with four splits (1/16, 1/8, 1/4, 1/2): worldPass
  GPU 3.59 -> 2.63 ms, frame 15.69 -> 14.10 ms. One, two, four and five splits run back to back landed
  within run-to-run noise of each other (2.26-2.70 ms against 2.93/3.30 with the cull off); two keep a
  near split for caves at half the pyramid builds.
- Culled (a temporary readback every 30 frames, frames with >= 500 opaque chunks): two splits median
  57.9% of the worldPass indices, ~1070 of ~1600 chunks; four splits 63.4%, ~1190 of ~1650.
- Exactness: a temporary check every 10th frame drew the culled chunks again against the frame's final
  opaque depth under `LEQUAL` with an occlusion query, and re-rendered the run uncut from a snapshot of
  the post-LOD color and depth and compared both images bit for bit: 0 samples and 0 differing bits over
  4,615 checked frames and ~1.79M culled-chunk tests, run with the four splits (perf-fly, eye-adaptation, lava, ores, grass-tint,
  water at 360, world-events, animations, terrain-shape at 1090). With the margin forced negative both
  checks fired on every checked frame, so they do see a wrong cull.
- The cost lives inside the `worldPass` row (sibling `TIME_ELAPSED` ranges would split worldPass into
  segments that the overlay averages per sample rather than sums).
- Shadow cascades: not done and not worth it; a light-space estimate on the gate's readback found ~0% of
  their casters occluded (near median 0.0%, max 0.2-0.4%; far 0.0%), the buried-caster skip already
  drops them. A depth-only light pass is order-independent, so the same test would need no ordering rule.

**F7. Cloud pass: frustum-culled tile runs** (2026-11-01, exact by construction; Quadro T1000 Max-Q,
driver 597.06, Release, 1280x720, runs starting at 70 C / 765 MHz and ending at 73-74 C / 1335-1530
MHz; perf-fly `VOXEL_TIME=60` x3 interleaved against a worktree of c045abec, both builds carrying the
same temporary timer patch). `CloudField.BuildMesh` also returns each cell's index-run start (cells
are emitted x-major, so the mesh and its bytes are unchanged: SHA-256 of both arrays equal). The GL-free
`CloudTiles` (`CloudTilesTests`) cuts the 128x128-cell field into 16-cell tiles, bounds each tile by the
vertices its triangles actually reference (fillets and riser patches cross cell borders), and per pass
emits, for every surviving copy and every (column, tile row) run whose tile box grown by
`SkyConfig.CloudTileCullMarginBlocks` (1) still meets that pass's own frustum, one
`DrawElementsIndirectCommand` with `BaseInstance` = the copy's slot, copy-major and in ascending index
order, adjacent runs merged. Both sweeps of `CloudRenderer` (depth pre-pass, color) draw that list as
one `glMultiDrawElementsIndirect`, the main view against `viewProj` and the mirror against
`_reflViewProj`. GL defines the old instanced draw as instance-major sequential draws and MDI as its
commands in order, so the new primitive stream is a strict, order-preserving subsequence of the old one
with the same vertices, instance offsets and shader. The margin must cover GL's -w near clip, which
`Frustum`'s Direct3D z >= 0 near plane does not model (GL draws down to about `Camera.NearPlane` / 2
from the eye); a test pins that relation.
- Metric: the two sweeps' own `TIME_ELAPSED` ranges (temporary rows, so the uniform setup and the
  instance upload are outside both builds' figures); per run, the median over the one-second lines
  (line 1 excluded) of the per-line average, main = depth + color, mirror the same on mirror-render
  frames. Gate A (before any draw change) measured the share of indices a tile cull keeps: main 0.641,
  mirror 0.670 median on perf-fly (cloud-shadows at 147: 0.417 / 0.089), the depth and color sweeps
  costing about the same, i.e. geometry-bound.
- Main cloud GPU 0.980 -> 0.525 ms (before runs 1.190/0.980/0.975, after 0.470/1.390/0.525; per-pair
  median -0.450), mirror 2.090 -> 1.545 ms (2.230/1.535/2.090 vs 1.535/1.545/1.545; per pair -0.545):
  combined -1.00 ms per mirrored frame; a forced-mirror pair -0.975 ms; frame-weighted with the mirror
  rendering 58.7% of frames about -0.78 ms GPU a frame. Frame median 24.27 -> 21.94 ms (direction only,
  the frame is not cloud-bound).
- Commands per call: main 338-344, mirror 286-297 (6 copies median). MDI front end: the same split
  commands drawing the full geometry (a temporary no-cull mode) measured main 1.08 / mirror 2.05 ms
  against the before medians 0.98 / 2.09, i.e. within noise. CPU: `cloudPass` median 0.010 -> 0.05-0.06
  ms (the gather, ~36-47 us a call, plus the temporary bookkeeping), the mirror's gather ~+0.03 ms in
  `reflect`; no stall from the second command upload in a frame.
- Exactness: an in-frame A/B (every 10th call per view: snapshot the target, draw the old instanced
  path, restore, draw it again, restore, draw the culled path, compare every texel) found 0 differing
  depth texels anywhere, but its old-vs-old noise floor is not 0: a handful of color texels per run
  differ between two identical legacy draws (equal-depth fillet pieces both pass `LEQUAL` in the color
  sweep and read and write the same texel inside one draw, which GL leaves undefined), and legacy-vs-new
  differs in the same order. So exactness was proven directly instead: every call, the indices the cull
  dropped were drawn with the pass's program, viewport and cull state (depth test, depth and color writes
  off) inside a `SAMPLES_PASSED` query, with the kept runs as a positive control. 0 samples in every call
  of both views (calls main / mirror): perf-fly 1763 / 1642, clouds-mirror (120) 457 / 301, cloud-shadows
  (147) 336 / 432, cloud-silver-lining at 343 452 / 100, at 400 446 / 93, at 147 455 / 91, clouds.json
  382 / 93, water.json (360) 441 / 582, and a scratch flight through the cloud layer (y 159.85-170,
  looking up 0.15 below a base and down 0.15 above a top) 911 / 209, 574 of those main calls with the eye
  inside a cloud. The positive control was non-zero everywhere; with the margin forced to -2 the query
  fired on 281 main and 8 mirror calls (the detector works), at 0 it stayed 0 (that run never put a tile
  edge in the near band, which the unit test covers). The dropped primitives rasterize no fragment, so
  the fragment stream is the old one; the A/B residue is the old draw's own race.
- Cold start: the old 16-24 ms GPU first-line spike sat outside the draw ranges (program first use,
  uniforms, first upload), and the first cloud `Render` cost 3.3-4.5 ms CPU (line-1 `cloudPass` CPU worst
  5.5-7.3 ms). The MDI path raised that worst to 17.8-19.9 ms. `CloudRenderer.WarmUp`, at the end of
  `RenderManager.LoadResources` (one triangle through both sweeps under both cull faces into a private
  1x1 RGBA16F/Depth24 target, every state restored, 12-27 ms of load CPU), brings it back to 12.5 ms
  median (10.4-12.9; without it 14.0-23.2, median 19.1): still ~5 ms over the old path, driver work on
  the first real-target multi-draw that a 1x1 warm-up does not prepay (a full-field warm-up was no
  better, 14.9/16.1). A second 6-16 ms `cloudPass` CPU spike on lines 4-6 in some runs of every variant
  lines up with the run's `reflect` CPU hitches: a world hitch landing in the bracket, not the clouds.
- Rejected without measuring: a fog-distance cull (at full fog the shader still writes an sRGB round trip
  of the scene and its depth), reordering the index buffer (changes primitive order), a mirror-plane cull
  (the cloud shader has no clip and the clouds lie above the plane), the water-sample-region scissor
  (not exact, F13) and skipping the pre-pass for some tiles (overlap cannot be proven).

**F10. Pre-size the opaque chunk pool: the mid-play chunkApply hitch** (2026-11-01, exact by
construction; Quadro T1000 Max-Q 4 GB, driver 597.06, Release, 1280x720; perf-fly `VOXEL_TIME=60` x3
interleaved against a worktree of d5e6c13d, both builds carrying the same temporary `// TEMP-DIAG`
instrument: per-frame apply splits, one enqueue-to-apply latency sample per applied mesh, the
`ChunkDrawBatch` fence wait, GC deltas, a leg mark per teleport). `ChunkGeometryPool.OpaqueInitialUnits`
262144 -> 524288 units (1024 MB of vertices, 96 MB of indices); nothing else changed. Exact: the pool's
size decides only where a slice lives, never what is drawn or in what order, and the upload order, the
count budget and the bytes on screen are untouched.
- Diagnosis (6 runs before the fix, 3 clean + 3 instrumented, matching within noise): the "bay teleport"
  hitch was the opaque pool doubling 262144 -> 524288 in every run, 7-10 s into the first land leg
  (t 17.5-20.3 s), not the bay leg itself (whose worst frames were 23-63 ms). The frame cost 332-534 ms;
  `Grow()` itself was only 4-18 ms of CPU, the rest the driver stalling on the next GL call into the new
  buffers (the first `NamedBufferSubData`, 332-383 ms, or once the `lighting` row, 445 ms) while the old
  560 MB and the new 1120 MB pair coexisted on a card already holding ~2.8 GB: grow plus stall 97-99% of
  the frame, the next three frames clean, no GC, unload or fence wait on it. VRAM after the grow
  3305-3384 of 4096 MiB, so pre-sizing changes nothing at steady state.
- The sustained 5-12 ms land-leg `chunkApply` frames are `glNamedBufferSubData` stalls (81-95% of
  chunkApply on frames over 5 ms; drain + sort 2-12%, bookkeeping under 0.8 ms), not proportional to
  bytes (corr(ms, MB) 0.15; p50 0.14 ms/MB, 6.6 ms/MB on stall frames). They are mostly the GPU-bound
  frame's own wait landing on the first upload: stall frames ran only 2.4-7 ms longer than calm ones,
  which already wait 8-13.5 ms in `lighting`. Fence wait worst 12.6-16.6 ms, GC ~0.8 ms a frame (F11),
  `chunkUnld` worst 4-14 ms: none on a hitch. Per-chunk latency is set by the 32-mesh count budget
  (backlog 4300-5000 on leg 1, latency p95 2.5-3.9 s on the land legs).
- Result, before -> after median [per run]: mid-play worst frame 458.2 [458.2/420.0/483.1, the grow in
  3/3 runs] -> 60.6 [60.6/49.0/63.0], -87%, 0 pool grows; wall avg 11.64 [11.64/11.08/11.79] -> 11.53
  [13.26/11.21/11.53]; wall p99 26.76 [25.40/26.76/28.71] -> 25.29 [34.05/24.66/25.29]; `chunkApply` avg
  1.90 [1.90/1.65/2.02] -> 1.87 [2.13/1.75/1.87], worst [450.3/25.4/479.2] -> [44.8/45.9/29.0]; land-leg
  chunkApply + lighting p95 20.47 -> 20.13. The first after run is an environment outlier (idle VRAM 2070
  MiB against 1608-1801, the lowest clock at 1278 MHz; its legs 3-4, code-identical by then, ran 2-2.6 ms
  slower too).
- Streaming is not later: per-chunk latency median 45 -> 48 ms, p95 2517 -> 2425 ms (legs 1/2:
  3537/3347 -> 3113/2771); time to 95% applied within 0.1 s on every leg. The one cost is the startup
  frame (before the world plays, `lighting` row): median 199 -> 215 ms (before spread 173-204), the driver
  touching the larger allocation once; leg-0 settle unchanged (8.6-8.7 s to 95% applied).
- Rejected: a persistent-mapped 3-slot (6 MB each) staging ring feeding `CopyNamedBufferSubData` took the
  subData stall out of chunkApply (land "chunkApply minus staging" p95 3.3-7.1 ms) but only moved the
  GPU-bound wait into its per-frame `FenceSync`, now the frame's first GL call: land chunkApply + lighting
  p95 20.27 against 20.13 without it, wall avg 12.31 against 11.53, p99 28.04 against 25.29, leg-1 latency
  p95 4180 against 3113 ms, 16 fallbacks in one run, and a new 348-380 ms hitch at t ~1 s in 3/3 runs
  (the first copy-engine writes into the 1 GB pool; pre-touching it at load with a clear, a staged copy
  plus `glFinish` or a 16-byte upload plus `glFinish` only moved where the CPU blocked). Without the
  pre-size it made the grow hitch worse (603-782 ms). Not run, their rules unmet: a byte upload budget
  (cost does not follow bytes, and it would lengthen the count-bound latency) and a partial K-nearest
  selection instead of the full sort (drain + sort 2-12% of chunkApply). Still open: the translucent and
  LOD pools never grew in any run; a grow past 524288 would hitch the same way (an amortized grow is not
  designed).

**G1. Worldgen cost probe** (2026-11-01, measurement only; Quadro T1000 Max-Q machine, 12 cpus, Release,
working tree over 6eeb9d24; medians of 3 invocations, one per invocation). Built: `WorldgenStageTimer`
(`WorldGen/`, internal), passed through a new optional `timer` parameter of `ChunkTerrainFill.GenerateBlockData`
(null in production: one null check per stage, no allocation); each mark charges the wall time since the
previous one to one of 31 stages in production order, so the stages sum to the call. `WorldgenStageTimerTests`
prove blocks, state bytes, solid count, surface floor, fluid frontier and the ready flag byte-identical with and
without a timer on surface, deep, sea-floor and above-surface chunks, every stage marked once on a filled chunk
and only `Columns` on an early-out one. `WorldgenCostProbe` (`OpenGLTest.Tests/WorldGen`, `[Collection("Worldgen
seed")]`, anti-vacuity asserts only, single-threaded) draws its columns and chunks with a fixed `SplitMix64`.
Cold = `WorldSeed.Apply` plus a GC before each sample, so every worldgen cache is empty; warm = the same work
again with every cache hot. They bracket the in-game rate, never measure it.
- `TerrainGenerator.Compute`, ms per column, median cold / warm: all 0.283 / 0.278 (64 columns), land 0.267 /
  0.243, ocean 0.296 / 0.306, lake/river 0.252 / 0.239, highland 0.325 / 0.310 (32 each), massif 0.314 / 0.326
  (8), great lake 0.884 / 0.227 (4; the cold site verdict build). The rare categories fall short of 32 within the
  4000-draw cap.
- `GenerateBlockData`, whole median ms per chunk cold / warm, and the top warm stages: surface 111.6 / 1.12
  (`NoiseCaves` 33%, `GravelPockets` 12%, `Foliage` 8%); above-surface 75.6 / 0.38 (`Fill`, `Foliage`, `Flowers`
  18% each); sky 6.5 / 0.001 (`Columns` only, the early-out); deep-stone 145.0 / 1.10 (`GravelPockets` 23%,
  `NoiseCaves` 16%, `Malachite` 12%); cave-heavy 122.7 / 1.06 (`GravelPockets` 24%, `NoiseCaves` 19%,
  `Malachite` 11%); sea-floor 48.8 / 0.55 (`Snow` 15%, `Fill` 13%, `Pebbles` 12%). Cold, every land class is
  `Trees` 66-93%, `WormCaves` 22-28% (none above the surface) and `Columns` 3-6%; the sea floor is `WormCaves`
  66%, `Trees` 20%, `Columns` 13%.
- All classes (the probe's mix: 6 chunks a class, sky 4, so not the in-game distribution; it only ranks
  stages), whole median 88.9 cold / 0.81 warm:

  | stage | cold share | warm share | warm median ms |
  |---|---|---|---|
  | `Trees` | 69.2% | 1.2% | 0.001 |
  | `WormCaves` | 24.1% | 0.3% | 0.001 |
  | `Columns` | 5.8% | 0.1% | 0.001 |
  | `NoiseCaves` | 0.1% | 19.6% | 0.124 |
  | `GravelPockets` | 0.1% | 15.9% | 0.083 |
  | `Malachite` | 0.1% | 8.3% | 0.051 |
  | `Coal` | 0.1% | 8.2% | 0.056 |
  | `Fill` (0.25 us a column) | 0.1% | 7.9% | 0.071 |
  | `Foliage` | 0.1% | 7.9% | 0.065 |
  | `Flowers` | 0.1% | 7.7% | 0.066 |
  | `Pebbles` | 0.1% | 7.3% | 0.065 |
  | `Snow` | 0.1% | 5.9% | 0.042 |
  | `FluidFrontier` | 0.0% | 3.9% | 0.006 |
  | `Mud` | 0.1% | 2.0% | 0.003 |

  Every other stage is under 1% both ways.
- Stage sum against the whole: the externally timed call exceeds the stage-sum mean by 0.1-1.6 us in every
  class (the call, `IsBlockDataReady` and `WorldgenStats.AddChunk`), so the stages cover the call.
- Bracket: warm filled chunks run 0.38-1.13 ms, below the in-game `wgen` median of 1.31 ms (no worker contention,
  hot CPU caches); cold samples run 49-145 ms, well above the in-game cold first second of 29-34 ms, because in game
  neighboring chunks share each tree batch and worm cell while every probe sample rebuilds them alone.
- Stability: per-class medians of every stage above a 5% share agree within about +-5% across the runs. The
  pooled all-classes warm `GravelPockets` median swung 0.068-0.095 ms over a fourth run (a median over a two-humped
  class mix) while its share held 15.3-16.4%, so read the pooled rows by share. The small `Compute` categories
  (massif, great lake) and ocean cold spread up to about +-20%. Means carry outliers that medians hide:
  deep-stone `FluidFrontier` median 0.012 ms, mean 0.14 (one chunk's frontier scan); surface `NoiseCaves` median
  0.145 ms, mean 0.45.
- Sizing: trimmed to stay in the default suite (both facts about 9 s): one rep of 1 cold plus 2 warm samples per
  chunk, 6 chunks per class (sky 4), 24 underground candidates, 2 `Compute` reps. No env gate. Steadier figures
  come from the median of several invocations, not from raising the constants.
- Not done, by decision: no stage buckets in `WorldgenStats` or the `wgen` token (they would change
  `TakeWindow`'s signature and add `Interlocked` traffic on four workers for nothing the probe does not show); no
  noise-call counts (`FastNoiseLite.cs` is vendored and `GetNoise` is not virtual), so G3's call count stays an
  estimate; no real mesh share: `MeshBuildCostProbe` (5475 us/build on 2026-10-31) builds a synthetic soup, not
  generated terrain, so no mesh-vs-worldgen ratio was measured.

**G6. Chunk-level gates for the feature stamps** (2026-11-01, bit-identical; Quadro T1000 Max-Q machine, 12
cpus, Release, working tree over ef251bca; `WorldgenCostProbe.ChunkFillCostPerStage`, medians of 3 invocations,
one per invocation, before and after). Built:
- `StampBand` (`WorldGen/Features/`, internal): the inclusive world-y range a feature stamp can write, with
  `Reaches(worldY0)` for one chunk. Every gated generator has a GL-free `WriteBand(...)` derived from its own
  `Stamp` code (the XML doc names the write that proves it): the surface plants (`Pebbles`, `Flowers`, `Foliage`,
  `Cotton`) one cell over the column's height range; `Reeds`, `LilyPads` and the snow ground pass also by the
  column's `MaxWaterLevel` (only ever added to or taken in a `Min`/`Max`, since `NoWater` is `int.MinValue`);
  `Cairns`, `RuinedPillars`, `IceSpikes` and `Meteors` by their own height plus or minus their own reach, since they
  write only into columns inside the chunk; `GlowMushrooms`, `CaveFormations` and `UndergroundPools` capped by
  their burial depth (the pool also by `LavaMaxY` and `SeaLevel`, which replaces its old in-`Stamp` early return);
  `Geodes` a constant band from `TryGetGeode`'s y and radius ranges. `ChunkTerrainFill.GenerateBlockData` skips a
  stamp whose band misses the chunk; every `timer?.Mark` stays unconditional, so the stage layout is unchanged.
- Upper-bound gates for the stamps whose site may sit in a neighbor chunk column (`FallenLogs`, `Boulders`,
  `DesertWells`): `ChunkTerrainFill.NeighborhoodMaxHeight` (the tree-reach neighbor `MaxHeight` scan pulled out
  of the `TreesEnabled` block, radius `max(TreeGenerator.ReachChunkRadius, FeatureReachChunks)`), with
  `BoulderGenerator.HorizontalReach` and `DesertWellGenerator.FootprintRadius` named instead of literal offsets.
- Per-column guard reorders inside the hot stamps, pure permutations of side-effect-free `continue` tests (the
  precedent `Cotton` already had): the ly window and the target and ground cells first, the noise and site reads
  (`IsWaterColumn`, `ColdWeightAt`, the plant rolls, the pool's `PatchNoise`, the mushroom hash) last. This is the
  sea-floor win: the cell over a submerged floor is water, so the plants reject it before any noise read.
- `SnowGenerator.Stamp` split into `StampGround` (gated by `GroundWriteBand`) and `StampCanopy` (never gated: its
  leaves can come from trees rooted in neighbor columns up to `TreeGenerator.ReachUp` higher; reordered only).
- Test seams: `GenerateBlockData`'s optional `stampGates` (false = every stamp runs, the pre-G6 path; production
  never passes it) and `WorldgenStageTimer.AfterMark` (a callback after each stage mark, for snapshots; its time
  is charged to the next stage, so the probe never sets it).

Proof:
- A temporary identity dump written at the unmodified HEAD (blocks, state bytes, solid count, surface floor, ready
  flag and fluid frontier per chunk) matched after every step and at the end: 690 chunks (3.38 MB) and an
  exhaustive 7174 chunks over 1183 chunk columns (35.4 MB), across every biome, ocean, lakes and rivers, highland,
  massif, great lake and island anchors plus each feature's own placement sites (fallen logs, boulders, cairns,
  pillars, wells, meteors, geodes, oases, ice spikes), from below the terrain to over the reach of the trees. Removed
  afterwards; the same sample lives on as `StampGateSample`.
- `StampGateTests` (permanent): the gated fill is byte-identical to the `stampGates: false` fill on every sample
  chunk, and every gate both admits and rejects some filled chunk; each of the 18 stamps, called directly on the
  chunk as it stood before its stage (`AfterMark` snapshots), changes only cells inside its band, and the
  plants, snow, cave formations, fallen logs, boulders, geodes and meteors each change at least one cell; and
  `StampBand.Reaches` equals a brute-force check over the chunk's 16 cells. A deliberately tightened band fails both
  of the first two. Default mode 690 chunks, about 6.7 s alone (the cold worldgen of the sample). Exhaustive:
  `VOXEL_STAMP_GATES_EXHAUSTIVE=1 DOTNET_ROLL_FORWARD=LatestMajor ~/.dotnet/dotnet.exe test
  OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release --filter FullyQualifiedName~StampGateTests`, 7174 chunks at
  the default seed plus a second seed's sample in the identity fact, about 2.5 min.

Result, whole fill per class, ms per chunk (median of 3 invocations of each class's median); "stamps" is the sum of
the 18 gated stages' means and its share of the warm fill:

| class | warm before | warm after | change | cold before | cold after | stamps before | stamps after |
|---|---|---|---|---|---|---|---|
| surface | 1.189 | 0.887 | -25.4% | 121.2 | 117.5 | 0.426 (26.8%) | 0.165 (12.8%) |
| above-surface | 0.477 | 0.171 | **-64.2%** | 80.8 | 76.6 | 0.353 (71.9%) | 0.125 (48.7%) |
| deep-stone | 1.141 | 0.977 | -14.4% | 150.8 | 148.0 | 0.259 (18.8%) | 0.110 (9.4%) |
| cave-heavy | 1.127 | 0.830 | -26.4% | 138.1 | 121.9 | 0.270 (25.2%) | 0.024 (3.0%) |
| sea-floor | 0.562 | 0.331 | **-41.1%** | 51.4 | 48.6 | 0.311 (53.5%) | 0.037 (11.8%) |
| all classes | 0.895 | 0.667 | **-25.5%** | 93.5 | 90.1 | 0.295 (31.8%) | 0.081 (11.9%) |

Sky chunks stay on the existing early-out (warm 0.001 / 0.000 ms). Cold barely moves (-2% to -12%): it is `Trees`
and `WormCaves`. The four stamps G1 named, warm median ms (share) before -> after:

| class | `Pebbles` | `Flowers` | `Foliage` | `Snow` |
|---|---|---|---|---|
| surface | 0.076 (5.6%) -> 0.006 (0.7%) | 0.102 (6.9%) -> 0.008 (1.0%) | 0.118 (7.3%) -> 0.044 (3.6%) | 0.054 (3.7%) -> 0.041 (3.4%) |
| above-surface | 0.075 (17.1%) -> 0 | 0.073 (17.5%) -> 0 | 0.076 (18.0%) -> 0 | 0.067 (12.8%) -> 0.014 |
| deep-stone | 0.061 (4.7%) -> 0 | 0.059 (4.7%) -> 0 | 0.059 (4.7%) -> 0 | 0.041 (3.2%) -> 0.001 (0.1%) |
| cave-heavy | 0.069 (6.4%) -> 0 | 0.066 (6.0%) -> 0 | 0.070 (6.2%) -> 0 | 0.044 (4.4%) -> 0.001 (0.1%) |
| sea-floor | 0.070 (12.1%) -> 0.004 (1.2%) | 0.070 (12.1%) -> 0.004 (1.2%) | 0.070 (12.3%) -> 0.005 (1.6%) | 0.045 (14.2%) -> 0.003 (1.1%) |
| all classes | 0.069 (7.3%) -> 0.000 (0.3%) | 0.070 (7.8%) -> 0.000 (0.4%) | 0.070 (7.9%) -> 0.000 (1.4%) | 0.046 (5.7%) -> 0.003 (4.4%) |

The gate (above-surface and sea-floor warm each -30%, or the probe mix -15%) passed on all three counts. The
above-surface `Snow` mean share after (43%) comes from one outlier sample (median 0.014 ms, mean 0.110): the
ungated canopy pass. What is left of an above-surface fill is mostly `Fill`, now its largest median stage (0.081 ms,
32.7%), plus the canopy snow and `IceSpikes` (whose band reaches `MaxHeight + IceSpikeMaxHeight`, 0.013 ms).

Not gated, by decision: `GravelPockets`, `Malachite` and `Coal` (G3's domain, untouched here; G3 now skips each
of the three on a chunk with no host cell, and gravel on a chunk above its burial band); `NoiseCaves`, re-checked:
0.000 ms on the above-surface class before and after (its existing skip), and inside its band on deep-stone (0.206
-> 0.208 ms) and cave-heavy (0.207 -> 0.189) it does real carving, so a depth-band gate has nothing to win;
`Lava` and `Trees`; the snow canopy pass (above); and the lower bounds of the neighbor-site stamps (a neighborhood
min height would cost extra column lookups for stamps each under 1%).

**G3. Ore and pocket noise on a coarse lattice** (2026-11-03, **seed-breaking by decision**; Quadro T1000 Max-Q
machine, 12 cpus, Release, working tree over 80a1d569; `WorldgenCostProbe.ChunkFillCostPerStage`, medians of 3
invocations before and after). Built:
- `OreNoiseLattice` (`WorldGen/Features/Underground/`, internal): the one sampler `MalachiteGenerator`,
  `CoalGenerator` and `GravelPocketGenerator` now threshold. Its field samples the generator's own noise on a
  world-aligned lattice of `WorldGenerationConfig.OreNoiseStride` (2, a power of two dividing `Chunk.Size`) and
  blends the 8 corners trilinearly at cell centers, in `NoiseCaves`' lerp order. A lattice cell whose 8 corners are
  all at or below the threshold holds nothing: a trilinear blend never exceeds its largest corner, so the skip is
  part of the field's definition, not an approximation of it. `Stamp` and the point query `Exceeds` (behind
  `HasOre` and `HasPocket`) share one `Trilinear` and one corner test on the same int lattice coordinates and the
  same fraction expression, so the stamp equals the point query bit for bit and neighboring chunks agree at every
  seam. Pure in position and seed, thread-safe (read-only noise, stack-only scratch); `FastNoiseLite.cs` untouched.
- `IOreLatticeRule`: a generator's host test and replacement as a `struct` generic argument (no delegate, no
  allocation). `Stamp` flags the lattice cells holding a host block in one pass and returns at once when there are
  none (a chunk of air, dirt or water), samples the lattice points lazily (a NaN-filled stack buffer, each point at
  most once, only for flagged cells), skips a flagged cell under its corner test and interpolates only the host
  cells of the rest. Gravel keeps its exact per-cell burial band inside its rule and returns before any noise
  read on a chunk above `column.MaxHeight - GravelPocketMinBurial`.
- Noise calls per generator per chunk: up to 4096 before (one per host cell), at most 729 (`GridCount`, 9^3) after,
  and usually far fewer, since only the lattice points of flagged cells are read.
- Tuning (a probe sweep of stride {4, 2} x frequency scale {1.00, 0.85, 0.70} x threshold, over x 0..127,
  y -64..31, z 0..127): every frequency kept (malachite 1/18, coal 1/20, gravel 1/24 with 2 octaves); thresholds
  malachite 0.88 -> 0.83, coal 0.88 -> 0.84, gravel 0.30 -> 0.29. Stride 4 rejected: a piecewise-trilinear field
  has its maxima on lattice points, and ~50-cell deposits are smaller than a 4^3 cell, so at the thresholds that
  put stride 4 on the deposit targets the deposit centroids lock to multiples of 4 (phase resultant R mod 4 of
  0.43-0.50 on all three axes, Rayleigh N*R^2 about 60-85; a free field reads 0.02-0.06, and the old 0.88 thresholds
  read 0.71-0.79). Stride 2 reads like the per-cell field.
- Rejected: a bit-identical Lipschitz bound skip (it needs an analytic gradient bound for the vendored
  OpenSimplex2 in three configurations, gravel's 2-octave FBm included, plus a rounding margin; at a useful
  spacing the margin keeps about half the host cells on per-cell noise, about a 2x win against the lattice's 3x);
  and a coarse prefilter before the exact noise (not provably safe, and a missed corner clips a deposit flat).
- Tests: `OreScan` (the deposit statistics through the real `Stamp` path, shared by `MalachiteTests` and
  `CoalTests`, whose bands are unchanged); `OreNoiseLatticeTests`: the stamp equals the point query across every
  seam of a 2x2x2 chunk block at negative coordinates (malachite, coal) and through gravel's burial band under
  hashed surface heights, only host cells change in a half-stone chunk, the field is a pure function of the seed,
  and deposits are not locked to the lattice (R below 0.25 for the ores, 0.5 for the few, percolating gravel
  pockets); `GravelPocketTests.PocketsAreSparseButPresentInDeepStone`. Mutations caught: fractions without the
  +0.5 or a truncating base fail the seam, burial-band and host facts; stride 4 fails the lock fact (malachite
  R = 0.517).

Result, warm medians, ms per chunk (median of 3 invocations of each class's median), before -> after:

| class | `Malachite` | `Coal` | `GravelPockets` | the three | whole fill |
|---|---|---|---|---|---|
| surface | 0.085 -> 0.031 | 0.083 -> 0.030 | 0.164 -> 0.063 | 0.332 -> 0.124 (0.37x) | 0.784 -> 0.651 (0.83x) |
| above-surface | 0.006 -> 0.003 | 0.008 -> 0.002 | 0.007 -> 0.000 | 0.021 -> 0.005 | 0.130 -> 0.114 (0.88x) |
| deep-stone | 0.133 -> 0.045 | 0.130 -> 0.045 | 0.268 -> 0.090 | 0.531 -> 0.181 (0.34x) | 0.847 -> 0.514 (**0.61x**) |
| cave-heavy | 0.121 -> 0.045 | 0.115 -> 0.043 | 0.241 -> 0.086 | 0.479 -> 0.174 (0.36x) | 0.776 -> 0.464 (**0.60x**) |
| sea-floor | 0.045 -> 0.020 | 0.043 -> 0.020 | 0.036 -> 0.019 | 0.124 -> 0.059 (0.48x) | 0.321 -> 0.260 (0.81x) |
| all classes | 0.048 -> 0.022 | 0.052 -> 0.024 | 0.071 -> 0.038 | 0.170 -> 0.084 (0.49x) | 0.659 -> 0.439 (**0.67x**) |

Sky chunks stay on the existing early-out. Per-class stage medians agreed within 0.003 ms across the runs, the
whole fill within about 5%. Cold fills are unchanged in practice (the three stages were under 0.5% of them).

Field before -> after (same volume, 6-connected deposits, R the larger of the three axes mod 2):

| | deposits | mean size | share of stone | R mod 2 |
|---|---|---|---|---|
| malachite | 429 -> 433 | 46.0 -> 45.2 | 1.254% -> 1.243% | 0.063 -> 0.037 |
| coal | 317 -> 319 | 64.0 -> 60.2 | 1.290% -> 1.221% | 0.041 -> 0.056 |
| gravel and sand | 91 -> 67 | 3278 -> 4382 | 18.96% -> 18.67% | 0.187 -> 0.134 |

Slices of the stamped field and cave-wall screenshots show rounded, irregular veins and pockets with no grid or
axis-aligned faceting. Existing saves re-lay their unmodified chunks, so old worlds show ore seams at the edges of
their edited chunks.

**F3. Far shadow cascade reuse** (2026-11-03; Quadro T1000 Max-Q, Release, perf-fly `VOXEL_TIME=60` x3
interleaved against a worktree of 7f560bcd, medians of the three runs). The far map is cached:
`FrameLighting.UpdateShadowCamera` asks the GL-free gate `FarShadowReuse` (`FarShadowReuseTests`) through
`ShadowCascade.UpdateReused`, which holds the far center and `ViewProj` until a refresh cause fires, and
`ShadowPass` re-renders the far cascade only on those frames and confirms with `MarkRendered` (an unconfirmed
refresh is retried as `NoMap`). The decision sits in `SetLighting` because `RenderManager.BeginFrame` uploads
`uLightViewProjFar` before the shadow pass runs, so the far `ViewProj` always means the matrix the map holds and
no GLSL changed. Causes, first wins: no map (also after `Invalidate`, on every frame the shadow pass skips and
on `ForgetWorld`); the `Moonlight` handover (the key light is negated there, so it is an explicit flag); a
key-light turn past `FarShadowReuseMaxTurnTexels` (0.5) of a far texel at the box edge (the sine, a cross-product
length, since that cosine is below float resolution); the look-following target (`ShadowCascade.TargetOf`, still
`ForwardFraction` ahead) leaving an inner square of half-extent `FarShadowReuseInnerFraction` (0.25) x radius in
the cached light's lateral axes (`ShadowCascade.LightAxes`), the hysteresis re-center, which still steps the
center in whole texels (`StepCenter`); the eye moving `FarShadowReuseDepthMarginFraction` (0.25) x radius along
the cached key light; `FarShadowReuseMaxFrames` (30); and a caster geometry change once
`FarShadowReuseGeometryMinFrames` (4) frames have passed. The geometry version is `RenderManager.FarCasterVersion`,
the sum of the opaque and LOD pools' `ChunkGeometryPool.ContentVersion` (`GeometryAllocator.ContentVersion` moves
on every successful allocate and every free, `NoteDrawSetChange` on every `LodRegion.IsBuried` flip); every
upload, free and burial flip happens on the update side, before `SetLighting`.
- Interlock with F2: the far receiver cull (`_farCull`) was deleted. A map exact only for this frame's receiver
  hulls would be stale for the next view, so a re-render draws every caster in the far cascade box, i.e. the
  pre-F2 far cost on those frames; only the near cascade is still receiver-culled.
- Measurement trap: `GpuPassTimer`'s 3-slot query ring drops a `TIME_ELAPSED` result not ready three frames
  later, and those are mostly the expensive re-render frames, so the stock overlay read `shadowFar` 0.1-0.3 ms
  (false). Every figure below came from a temporary 8-slot ring in both trees (0 drops over 708 lines). Measure
  any pass whose cost alternates between frames the same way.
- Tuning `FarShadowReuseGeometryMinFrames` (Geometry was ~97% of renders at 1: the version moves on ~64% of
  frames, 40-60/s while streaming and 9-43/s even hovering still): 1 lost (`shadowFar` GPU whole run 4.09 ms
  against 2.60 before), 2 was a wash (2.27), 4 won and was kept.
- `shadowFar` GPU average: whole run 2.60 -> 1.30 ms (before runs 2.21/2.60/2.66, after 1.26/1.30/1.30), last
  three lines 1.65 -> 0.98 ms. Worst: last three lines 2.22 -> 5.79 ms, whole run 6.25 -> 10.87 ms (a re-render
  draws the full unculled far box). CPU avg~worst: whole run 0.39~4.78 -> 0.08~4.08 ms, last three lines
  0.42~2.51 -> 0.07~0.68 ms. `shadowNear` unchanged (GPU 1.93 -> 1.94 ms whole run). Frame average 16.86 ->
  15.84 ms whole run (within noise); the last-three worst frame 21.47 ms sits inside the before spread
  (17.2-36.2).
- Skip rate about 79% (whole run and last three lines). Renders: Geometry first, then the key-light turn (70-90 a
  run), MaxAge (13-24), Recenter (2-3, only at perf-fly's teleports, where the view cuts anyway), Handover 0: at
  the moonlight switch the shadow strength is 0, the map is invalidated and the first shadowed frame renders as
  `NoMap`, so the flag is a safety net.
- Invariants kept: the near map renders every frame; far-map leaf sway freezes between renders (sub-texel); the
  key light steps by at most half a far texel per render; the near box's fade band stays inside the far core
  while the center is held (`ShadowCascadeTests.TheNearBoxsFadeBandLiesInsideTheFarCoreWhileTheFarCenterIsHeld`,
  every sun, eye and view at 0.98 of the inner region and the depth margin).
- Pop check: `visual-examples/far-shadow.json` (`VOXEL_TIME=400`, so its shots land near 430 after the load
  wait) films the far fade band across a low sun, 12 still shots 0.1 s apart and 40 four-block steps. Consecutive
  pair diffs (mean absolute RGB) after 0.567-0.685 still and 5.537 stepping against before 0.555-0.675 and 5.594;
  same-shot after-vs-before diffs 0.41-0.46 against before-vs-before 0.58; the diff images show only chunk and LOD
  pop-in, no band-shaped step. No Recenter fired there (geometry and turn refreshes re-center first), even with
  geometry refreshes disabled. `linear-lighting.json` at 430 and `moonlight.json` at 455 differ from before no
  more than two before runs differ.
- Known over-invalidation: a water-only remesh (`Chunk.UpdateMesh` frees and re-uploads its opaque slice too) and
  any opaque or LOD upload outside the far box bump the version. A follow-up could filter the version to slices
  inside the held far box; the two-map crossfade at a Recenter was not needed.

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
  fps/2. The far cascade is cached instead (F3, Done), which is not a fixed rate.
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
- F9 phase 1 as first planned, a history-predicted two-phase Hi-Z cull with reordering, gated out
  2026-11-01 before any permanent code (T1000 Max-Q, Release, perf-fly `VOXEL_TIME=60`, 1280x720,
  3 runs). The design: each chunk carries a 3-frame-late occlusion bit; predicted-occluded chunks move
  to a phase B drawn after a Hi-Z of phase A's depth, and to stay pixel-identical a chunk may move only
  if it is not fading, no later fading chunk's screen rect overlaps it (fading chunks blend, so order
  matters) and no later phase-A chunk is among its 26 neighbors (depth ties). Measured with a
  `// TEMP-DIAG` readback (every 30 frames, the exact conservative rect/depth test, skip vs draw blocks
  of 4 s): of ~60% of worldPass indices occluded and ~55% eligible, the fading-overlap rule blocked
  30-40% and the neighbor rule 5-17% while streaming (60-90 fading chunks on screen), leaving median
  0.3-3.9% skippable; worldPass GPU saving 0.01 ms median (0.01/0.20/-0.14). History itself was fine
  (realized share ~95% of predicted, false predictions ~0). The same skip with the two rules ignored
  saved 2.02 ms; the order-preserving segmented cull that landed (F9, Done) got most of it. Shadow cascades:
  ~0% light-space occlusion, nothing to gain.
- F13, exact cuts of the planar water reflection (`ReflectionPass.RenderReflection`), gated out
  2026-11-01 before any cut landed (T1000 Max-Q, Release, 1280x720 with the mirror target also
  1280x720 at reflection quality 1.0, clocks 1380-1530 MHz at 70-72 C; perf-fly `VOXEL_TIME=60`,
  water.json at 360, clouds-mirror.json at 120). The rule was pixel-identical. Two cuts were weighed:
  (A) F9's order-preserving segmented Hi-Z cull on the mirror's chunk run against the mirror's own
  depth, exact on every frame; (B) a scissor of the mirror to the texels the water can sample plus a
  rect cull of draws outside it. B was bounded by a GL-free twin: tight per-chunk fluid-vertex bounds
  and per-LOD-region water-sheet bounds, grown by the Y wave lift, clipped to the camera frustum and
  the fancy-water distance, projected through the mirror, then grown by the 0.08 UV tap offset, the
  clamp, the bilinear pair and a float-error margin. A `// TEMP-DIAG` GPU tap check (an atomic bound
  of every mirror texel the water pass read) found 0 texels outside it on render frames and on reuse
  frames whose region had not escaped. The measurements:
  - **Forced-render split** (every frame renders; 3 runs, 113 one-second windows; median ms GPU):
    sky 0.09, chunks 1.33, LOD 0.50, entities 0.00, clouds 1.56, sum 3.65. The windows rendered
    ~441 chunks (2.0M indices) and ~82 LOD regions (1.1M indices). Plain perf-fly rendered 67.6% of
    frames and reused 32.4% (2946 vs 1410). Its render-frame `reflect` samples have a median of
    3.85 ms, consistent with the forced sum. water.json: chunks 0.57, LOD 0.06, clouds 0.69,
    sum 1.69. clouds-mirror: chunks 1.76, LOD 0.22, clouds 1.45, sum 3.53.
  - **Hi-Z (A)**: dead. The mirror depth was read back every 30th render and tested with the exact
    conservative rect and max-depth test. Occluded chunk indices on perf-fly: median 0%, mean 0.33%,
    max 3.3% over 102 samples, and segmented at 1/8 and 1/2 a mean of 0.19%. Neither masking the
    depth outside the sample region to far nor clipping the boxes at the plane changed this.
    water.json: mean 1.8%. clouds-mirror: 0%. At F9's ~1:1 calibration that saves about 0.004 ms
    (water 0.01). The reason: the mirror looks up from below the plane, the clip discards everything
    under it, and what is left is terrain against the sky. Almost every chunk's rect reaches a
    sky texel (depth 1), so few chunks hide behind others.
  - **Scissor + rect cull (B)**: small, and not exact. The sample region covers a median 94% of the
    target on perf-fly (83% for this frame's water alone, the rest a superset for reuse frames).
    Only 0.15% of chunk indices (mean 1.8%) and 0% of LOD indices fall outside it, so the vertex
    work stays. A timing A/B in 4 s blocks gave 0.31/0.39/0.33 ms per run, but the blocks cover
    different flight legs. Matched by window against the all-off runs, the saving is a median of
    0.11 ms (mean 0.26, ~30 windows a side); water.json 0.14 (too few windows); clouds-mirror -0.02.
    It cannot be exact against HEAD. A reuse frame can sample texels the last render never drew
    when water is meshed or uploaded after it (escapes: perf-fly 0.24%, water 0.72%, clouds 0.37%
    of reuse candidates), and the tap check saw it happen on 3 of 6 escape frames. Refreshing on an
    escape keeps every other frame identical but shows a one-frame-fresher mirror there; that was
    never accepted, so B was dropped.
  - **FULL target**: perf-fly 0% for this frame's water and 0.44% for the superset, clouds-mirror
    0%, water.json 48%. All of water.json's are its low poses near the surface, where nearby water
    has mirror w near 0 and an unbounded mirrored UV; there the full target is correct, not a
    loose bound.
  Rejected without measuring: shrinking the viewport (changes the projection mapping), seeding the
  depth with the LOD first (reorders draws), and hardware clip planes in place of the `uClipY` discard
  (edge coverage not provably identical). The diagnostics, the bound twin and its tests were reverted
  and kept outside the tree. Where the mirror's time actually goes: chunk vertex and fragment work
  the depth test cannot cull, and the mirrored clouds (F7). F7 has since landed (Done): the mirror's
  cloud draw ranges fell 2.09 -> 1.55 ms median on perf-fly (F7's metric, the two sweeps only), and
  its chunk and LOD work is unchanged. Retry only with a cheaper mirror (lower reflection resolution is
  a setting, not a free cut).
- F5, an exact depth pre-pass for the opaque chunk pass (`TerrainPasses.RenderWorld`), gated out
  2026-11-01 before any permanent code (T1000 Max-Q, driver 597.06, Release, 1280x720; clocks
  300-1530 MHz, throttling to 765-915 MHz at 74-75 C late in the runs, which the per-frame interleave
  cancels; perf-fly `VOXEL_TIME=60`, leaf-backlight.json at 1100, grass-tint.json at 147). The rule was
  pixel-identical; the gate was a perf-fly worldPass GPU median saving of >= 0.4 ms or >= 15%, with no
  forest regression. No second GPU was available; the interleaved forest scenes stood in for it.
  - **Baseline** (HEAD cdb21b20, after F9): perf-fly worldPass GPU 2.625/2.535/2.655 ms, median
    2.625 (so the gate is 0.39 ms), CPU 0.58-0.60, frame 12.97-16.00 ms; leaf-backlight 2.79 GPU,
    grass-tint 3.15 GPU. The old 3.90 ms figure predates F9; F9's own record read 2.38.
  - **Overdraw** (`// TEMP-DIAG` `GL_FRAGMENT_SHADER_INVOCATIONS` and `SAMPLES_PASSED` around the
    opaque run, chunk-visible pixels from a depth readback every 30th frame): shaded invocations per
    visible pixel 4.23/4.44 on perf-fly (p10 2.2, p90 6.5; 99% of the frame is chunk-covered), 5.57
    leaf, 6.13 grass; samples passed per visible pixel 2.3-2.5 / 2.0 / 1.8. An atomic-counter build
    (early-Z off, so shares of rasterized fragments) put 13-19 rasterized fragments per visible pixel
    (early-Z plus F9's Hi-Z already reject ~70-78%), sway faces at 66-79% and cutout or partly
    transparent layers at 70-82% of them (23 of 68 atlas layers: 18 cutout tiles plus 5 with
    transparent texels). A pre-pass may write depth only from the other ~20-30%.
  - **Fading chunks** (they blend, so their depth history must stay HEAD's): perf-fly median 42-50 a
    frame (p90 151-167, max ~790), on screen in ~86% of frames, crossing the eye plane in 12%; the
    union of their conservative screen rects covers a median 36-41% of the frame (p90 ~0.7-0.77),
    and merged to at most 4 restore rects 60-63% (p90 ~0.95). leaf: 21 a frame, union 0.32, merged
    0.52, eye plane 9.5%; grass: 6, 0.05, 0.07, 5.7%. Only ~20% of perf-fly frames (30% leaf, 55%
    grass) could take an exact pre-pass with a 0.25 coverage cap, 34% at 0.5.
  - **The exact design evaluated**: a depth-only program built from `test-vertex.glsl` with
    `invariant gl_Position` (beside an equally invariant shaded program, `chunkShader` left alone for
    LOD, translucent and the mirror), a fragment that discards sway faces, water-flagged faces and any
    layer not fully opaque at level 0 or in `CutoutTiles` (an eligible-layer SSBO), the F9 segments
    run depth-only with the post-LOD depth saved and the merged fading rects restored before every cull,
    a final whole-run Hi-Z cull, then one shaded `DrawRange` of the run under the unchanged `LEQUAL`
    and blend; plain HEAD path whenever a fading box crosses the eye plane or the coverage is too high.
  - **G1, a cost-complete `// TEMP-DIAG` prototype** timed by `glQueryCounter` around the opaque run
    (median ms, frames with >= 50 commands). Modes: B HEAD, A the exact design, C its pre-pass alone,
    D HEAD's segments with a trivial fragment (sample, alpha test, albedo), E A without the save and
    restore blits (inexact, timing only). perf-fly in 4 s blocks, 3 runs: B 3.50/4.01/4.18, A
    7.98/14.56/14.63, C 1.94/2.57/2.61, D 1.50/1.78/1.73, E 3.31/3.54/3.60. perf-fly rotated per frame,
    2 runs: B 2.73/4.55, A 11.21/19.57, C 2.21/3.60, D 1.32/2.10, E 4.33/6.40 (E's small win in the
    blocks is scene-phase confounding, the paired frames contradict it). leaf per frame: B 3.48/3.53,
    A 10.40/10.64, C 2.15/2.21, D 1.50/1.53, E 4.67/4.88. grass per frame: B 4.39/4.58, A 9.26/9.97,
    C 2.66/2.64, D 2.04/2.29, E 7.22/6.86. So B - A was -4.5 to -15.0 ms on perf-fly and -4.9 to
    -7.1 ms on the forest scenes: G1 failed on every run, and the exactness proof (an in-frame bit-diff
    against HEAD's path) and the interleaved worktree gate were never reached.
  - **Why**: (1) depth can be primed only from the ~20-30% non-sway, non-cutout fragments, since
    leaves and plants dominate the overdraw; (2) after F9 has culled the hidden chunks, the pre-pass's
    full vertex stage and discarding fragment (C, 1.9-3.6 ms, 60-80% of the whole HEAD run) cost more
    than early-Z saves the shaded pass: even frames with no fading rects lost (perf-fly A 4.44 vs B
    3.84 per frame, grass 8.1-8.6 vs 5.8-6.2), and E lost 1.2-3.7 ms on every paired run; (3) the
    restores exactness needs are blits into the scene depth, after which the shaded pass costs several
    times more (per-frame perf-fly frames with rects: A 13.2 vs B 2.7 ms), apparently losing the
    hardware's coarse depth culling for the rest of the pass.
  - Rejected without building: drawing cutout and leaf draws after solid ones (that is the early-Z
    split above, and equal-depth ties between reordered draws make it unprovable) and an `EQUAL` depth
    func in the shaded pass (fading blends and depth ties break it).
  - Not a result, an untested lead: the fragment headroom B - D (1.4-2.4 ms perf-fly, 2.0-2.3 leaf and
    grass) does not rule out bit-exact fragment early-outs. Most terms in `test-fragment.glsl` are
    already branch-gated; the one exact zero gate found unused is skipping `SampleShadow` when `fSun`
    is 0 (every consumer of `shadow` is multiplied by `fSun` or a gate containing it). It helps only
    sun-sealed fragments, which F9 mostly culls, and FMA contraction could move bits on the lit path,
    so it needs its own bit-diff proof and gate before anyone acts on it (and "Not chasing" lists
    shader ALU cuts).
  The diagnostics and the prototype were reverted and kept outside the tree. Retry only with new
  evidence (a GPU without the blit penalty, or a far larger opaque, non-sway share of the overdraw).
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
