# Performance

> Guide: plans/README.md

## Status

Frame time (F#), worldgen, meshing and streaming (G#). Ids F4, F5 and F13 (and the early C#/R# ids code
comments still cite) exist only in git history or under Ruled out. Every number names its machine and
date; a win not yet measured is labeled *estimated*. Landed items are summarized under Reference;
`git log -p -- plans/PERF.md` has the full write-ups (history was trimmed on 2026-10-28, 2026-10-31
and 2026-11-05). Last updated 2026-11-10.

## In progress

Nothing.

## Open

Ranking: within each group the prerequisite measurement comes first (the landed ones are summarized under Reference). F8 follows the
share of the 2026-10-31 GPU frame its target pass owns (shadows 36%, of which the far cascade ~59%,
before F2 and F3 cut it; worldPass 26%, reflect 16%, clouds 7%, lodPass 6%; F4, F5 (an exact worldPass depth
pre-pass) and F13 (exact reflection cuts) were tried and ruled out; F3, F7 and F9 landed);
F11-F12 are CPU-only and matter less while the frame is GPU-bound (F10, the chunkApply hitch, landed).
G follows the G1 stage split (`WorldgenCostProbe`). Ties and unmeasured items fall back to effort. Each entry has a
bold title, then **Files**, **Win** (measured or estimated), **Verify** and **Invariant**.

### A. Frame time (FPS)

- [ ] **`F8` `lod` CPU spike and `lodPass` worst** (low priority; 0.91 ms, 6%, worst 10.7 ms GPU).
  - **Files**: `Lod/LodManager.cs`, `LodCoverPass.cs`, `LodRegionSet.cs`. The 1306 ms single-frame `lod`
    spike of 2026-10-28 did not reproduce in three 2026-10-31 runs (worst 24-31 ms). If it returns,
    capture a per-region `// TEMP-DIAG` timer (unload storm vs synchronous work vs cover pass) before assuming a cause.
  - **Win**: estimated; only the rare hitch. **Verify**: perf-fly `lod` worst over three runs, far-teleport
    leg. **Invariant**: LOD nesting and burial unchanged; GL on the main thread.

- [ ] **`F9` GPU occlusion culling** (the old Phase 5): the opaque chunk pass's order-preserving Hi-Z cull
  landed 2026-11-01, see Reference. Left open: the mirror and translucent passes are not occlusion-culled, the
  shadow cascades gain nothing, and the frustum test and sort stay on the CPU.

- [ ] **`F11` GC attribution while streaming** (10-45 gen0/s streaming, 3-7 settled, 2026-10-31).
  - **Files**: measure first with `// TEMP-DIAG` `GC.GetAllocatedBytesForCurrentThread` around the main-thread
    sections and around worker `GenerateBlockData`/`GenerateMeshData`, or `dotnet-counters` if installed.
    Legitimate: `Chunk` arrays, `TerrainGenerator.Compute`'s per-column arrays (retained by `ColumnCache`).
    Suspects: `MeshBufferPool` misses, LOH-sized LOD lists.
  - **Win**: estimated; fewer gen0 pauses during streaming. **Verify**: the `gc` token pair.
    **Invariant**: no new per-frame allocation; pools hold no edit state.
  - **Scoped result (PR5 refresh, master `996fdf2`)**: eager translucent rents made a dry full mesh
    consume and discard the returned wet pair; lazy rents keep it available. The matched seed-71234
    fixture measured dry 44,344→8,456 B/build (−35,888 B, 80.9%) with identical geometry/light/fluid
    metadata hashes; mixed-wet stayed 4,296 B/build. The wet→dry→wet regression fails on base and passes
    on the refresh. This fixture result does not establish a whole-game allocation or FPS gain.
    The catch-up's only conflict was deleted `PERF_TODO.md`; this is the scoped note in its current home.

- [ ] **`F12` Shared per-frame chunk walks** (CPU, low priority while GPU-bound).
  - **Files**: `TerrainPasses.RenderWorld`/`CullWater`, `ShadowPass` (two cascades), `ReflectionPass`:
    each walks `Renderable` with its own frustum tests and several sort. Ideas: one shared camera-frustum
    visible list for `RenderWorld` and `CullWater`; column-level skips.
  - **Win**: estimated; CPU only, worth it once the frame is CPU-bound. **Verify**: perf-fly CPU rows of
    the render sections. **Invariant**: zero per-frame allocation; static comparers; per-pass draw order unchanged.

- [ ] **`F14` Tower collapse worst frame** (CPU, low priority: once per conquered tower; over its 1.0 ms ceiling).
  - **Measured** (`TowerCollapseCostProbe`, the largest tower, radius 24, 8 floors, dev machine, 2026-11-09): the
    collapse's main-thread step costs 0.25-0.33 ms mean, p90 0.55-0.61, p99 0.70-1.09, but 7-27 of its 2059 frames
    pass 1 ms and the worst after the first frame is 5.8-8.2 ms (the first frame 10-15 ms, JIT). The step's 32 writes
    each run `TowerBlockWriter`'s hooks (`MarkAreaDirty` over the light radius, the fluid and leaf-decay schedules,
    the LOD notify); the spikes are unattributed (GC, a costly write, tier-up).
  - **Files**: `Simulation/Tower/TowerCollapse.cs`, `TowerBlockWriter.cs`. Capture a `// TEMP-DIAG` per-write timer
    first; ideas: batch the `MarkAreaDirty` per chunk per frame instead of per write, lower the write cap.
  - **Win**: estimated; the worst frame of a collapse. **Verify**: rerun the probe and an in-game `X` capture of the
    `entities` row during a `towerdefeat`. **Invariant**: every edit-path hook still runs for every cell; the ruin is
    unchanged.

### B. Worldgen, meshing and streaming

Ranked by the G1 split (`WorldgenCostProbe`, medians of 3).
After G3 (2026-11-03) a warm fill (every cache hot) costs 0.11-0.65 ms per filled chunk (0.13-0.85 just before
it, 0.38-1.13 before G6), led by `NoiseCaves`: 26-34% of the probe mix, then `Fill` 14-16%, G3's three lattice
stamps about 21% together (42% before G3) and the snow canopy pass about 6%; the 18 feature stamps G6 gated are
noise. On a deep-stone or cave-heavy fill `NoiseCaves` (about 0.19 ms) now costs about as much as the three ore
and pocket stamps together (about 0.18 ms). A cold fill (every cache cleared)
costs 49-145 ms, of which 69% is tree batch growth, 24% worm cells and 6% neighbor columns (G5). No open item
targets the first two: they amortize over a 64x64 cell in game, and only a cold-start measurement in game would
justify one.

- [ ] **`G5` Hoist per-chunk-column work out of `TerrainGenerator.Compute`** (bit-identical).
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

- [ ] **`G8` LOD region build** (bit-identical; after G5; L2 median 80 ms on 2026-10-31).
  - **Files**: `Lod/LodMeshBuilder.cs`, `LodRegionClaims.TryBuildOneRegion`. Candidates: a prefetch order that hits
    already-cached columns; a heights-only column path for levels >= 1 skipping the carver-only fields
    (`EntranceWeight`, `CaveIntensity`), only if a split of `Compute` shows they cost, with no double caching.
  - **Win**: estimated, the cold far sweep. G1 timed whole `Compute` only: at 0.25-0.33 ms a column, a level-2
    region's 324 prefetched columns come to 81-107 ms against the 80 ms L2 median, so `Compute` dominates a cold
    build. Whether `EntranceWeight`/`CaveIntensity` cost anything inside it is still open. **Verify**:
    `LodBuildCostProbe` pairs. **Invariant**: LOD nesting and burial; identical region meshes (`LodBuildPoolTests`);
    no more LodGen threads.

- [ ] **`G7` Worker thread balance** (bit-identical; full ring ~12 s after launch on 2026-10-31).
  - **Files**: `Config/StreamingConfig.cs` (`MaxConcurrentBlockDataGenerations` 4, `MaxConcurrentMeshGenerations` 4),
    `Chunks/Streaming/ChunkGenerationWorker.cs`, `ChunkMeshScheduler` (`MeshRunner`). Measure each pool's busy share
    and the time to a full ring on the perf-fly teleports, then try 5/3 and 3/5 interleaved.
  - **Win**: estimated, ring fill time. **Verify**: perf-fly `wgen` and fps pairs. **Invariant**: never raise
    `MaxConcurrentLodGenerations` (C5); an fps guard; no blind budget moves.

- [ ] **`G9` Saved-world restore IO** (measure only).
  - **Files**: `Saves/ChunkSaveStore.cs` (one Deflate file per modified chunk), `Chunk.RestoreFrom` (on the generation
    worker), `WorldLoadGate`. Time a large save's loading phase before proposing region files (a save-format change).
  - **Win**: unknown until measured. **Verify**: a loading-phase timer on a heavily edited world. **Invariant**: the
    store's write and lookup ordering (`ChunkSaveStore`'s class doc).
  - **Note**: the chunk save is v3 since 2026-11-10 (ids widened to `ushort`; a chunk whose palette passes 255 names
    stores 2-byte indices per cell), so any restore IO number taken before then predates the format. Palette chunk
    storage (also 2026-11-10) kept the v3 bytes identical; a restore now ends in `ChunkBlocks.FromDense` (or a clone
    of an already compact snapshot), a few us a chunk on the worker.

- [ ] **`G10` A/B the per-id tables now that they are not `static readonly`** (measure first).
  - **Files**: `BlockType.cs` (the per-id tables, sized at load by `Content/Blocks/BlockTables.Allocate`), and their
    hot readers: `ChunkMesher`/`ChunkMeshBuilder`, `ChunkLighting`, `ChunkTerrainFill` and the feature stamps,
    `Simulation/Fluids`. Widening ids to `ushort` dropped `readonly` from the tables, so the JIT can no longer fold
    them as constants. The memory side is settled: palette chunk storage (`Chunks/ChunkBlocks`) holds the 17,077-chunk
    rest ring's ids in ~49.6 MB against 136.6 MB dense (RTX 3070 machine, 2026-11-10; see "Palette chunk storage"
    under Landed results); the light flood's dense id snapshot is still ~195 KB per mesh build.
  - **Win**: unknown; the palette-storage capture compared against d29c0735 (already widened), so the table A/B
    itself is still unmeasured. Hoist a table into a local in any hot
    loop that still reads it per cell (`ChunkLighting.SpreadLight` already does). **Verify**: perf-fly fps pairs,
    `MeshBuildCostProbe` and `WorldgenCostProbe` pairs against the pre-widening commit. **Invariant**: tables are
    never reallocated while workers run.

## Reference

### How to measure

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
  Runs in the default suite (~9 s); see the G1 entry under Landed results; cold and warm bracket the in-game rate.

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

### Measured baselines

#### 2026-10-31 (branch biomes, c8e0f3c6)

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

`shadows` is the one shadow row before F1 split it into `shadowNear`/`shadowFar` (landed; see Landed results).
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

#### 2026-10-28 reference (T1000, throttled, perf-fly x3)

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

#### Worldgen and LOD (headless)

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
- Hell chunks (`L-6`, 2026-11-07, `WorldgenCostProbe` `hell` class): a wholly-hell chunk skips the
  overworld pipeline, warm 0.024 ms (the stamp 0.019 ms) against deepStone's 0.92 ms; cold 0.33 ms.
- Sky chunks (`L-12`, 2026-11-07, `WorldgenCostProbe` `skyIsland` and `cloudSea` classes): a chunk in
  the island band or the cloud sea takes the sky short path, warm 0.031 ms (island stamp 0.030 ms) and
  0.05-0.11 ms (sea stamp); cold 3.9 / 7.8 ms, all but the stamp being the column-cache neighborhood
  scan every chunk pays. Open sky in the island band costs a few zone hash rolls (the zone gate).
  With the biomes and skins (`L-7`/`L-8`, same probe and 6 chunks, two runs each against a HEAD
  worktree): the stamp warm 0.15-0.16 ms mean against 0.019-0.022 ms before, cold 0.25-0.30 ms against
  0.03; whole chunk warm 0.16-0.17 ms, still under a fifth of deepStone's ~0.7 ms. The open field now
  covers a one-cell ring and the skin reach (up to 7^3 lattice points, 18x18x21 blends) and every cell
  near open cavern is classified; the biome and pool noises sit on per-layer 2D lattices (4 and 9
  points a chunk), which made no measurable difference, so the halo field and the per-cell classify are
  the cost. Untried: a halo only where a wall test can reach it.
  With the flora (`L-9`, same probe, two runs each): the stamp warm 0.26 ms mean (0.14-0.18 with the
  flora rule stubbed out: the vine scan reads up to 6 mask cells over every air cell and the mask
  grew 3 rows for it), the `HellTrees` stage warm 0.003 ms and cold 0.6-0.75 ms mean (a cold sample
  regrows every tree it crosses; in game a tree is grown once per cell and layer and cached). Untried:
  a per-column vine pass in the stamp instead of the per-cell scan.
- Sky biomes and flora (`L-13`, 2026-11-07, `WorldgenCostProbe` `skyIsland` class, one run each): a
  sky-island chunk warm 0.065 ms against 0.034 before (the island stamp 0.061 ms, now asking the flora
  rule per cell, and the `SkyTrees` stage 0.006 ms); cold 4.1-4.3 ms against 3.9, of which `SkyTrees`
  0.33 ms mean (a cold sample regrows every tree it crosses; in game a tree is grown once per cell and
  cached). Untried: the stamp asking the flora rule only at the top, the plant cell and the vine run.
- Sky islands everywhere (2026-11-07, `IslandOpenSkyWeight` 0.7, 112-block island cells, `TouchesLand`, the
  zone gate gone; `WorldgenCostProbe` `skyIsland` class, one run each, different island samples since the
  layout moved): a sky-island chunk warm 0.14 ms against 0.34 ms at the previous commit (island stamp 0.10
  against 0.22, `RuinsSky` 0.018 against 0.077), cold 5.0 against 12.1 ms (the column scan). Over a grid of
  6400 band chunks at y 352 around the origin the island stamp averages 17 us a chunk (three passes) against
  10-25 us at the previous commit, though the band now holds about 30x as many islands: the old zone and
  landmass gate cost about what the 2 x 2 island rolls cost now.
- Identity recipe: dump `LandHeight`, `WaterLevelAt` and `SurfaceWaterWeight` bits before and after
  on a +-20000 grid at stride 37, plus `Compute` on 1000 random chunk columns; zero changes allowed
  outside the intended footprint.

### Landed results (kept for the numbers and the traps; the designs live in `AGENTS.md`)

Machine for all of them: Quadro T1000 Max-Q 4 GB, 12 cpus, Release, 2026-10-31 to 2026-11-03,
perf-fly `VOXEL_TIME=60` x3 interleaved against a worktree unless noted. Everything below is exact
(no visible change) except G3 (seed-breaking by decision) and F3 (a cached far map freezes leaf sway
and steps the far key light by under half a texel between renders).

- **F1 shadow split.** `shadowNear`/`shadowFar` rows; far was ~59% of shadow GPU (2.56 / 3.75 ms).
- **F2 receiver-volume caster cull** (`ShadowCasterCull`): `shadowNear` 2.60 -> 1.71 ms, `shadowFar`
  3.89 -> 1.50 ms. Every consumer of shadow units 1 and 7 must add its pyramid there or call `KeepAll`.
- **F3 far cascade reuse** (`FarShadowReuse`): `shadowFar` 2.60 -> 1.30 ms, ~79% of frames skipped;
  `FarShadowReuseGeometryMinFrames` 1 lost, 2 a wash, 4 won. **Trap:** `GpuPassTimer`'s 3-slot ring drops
  results not ready three frames later, mostly the expensive frames, so a pass whose cost alternates must
  be measured with a temporary 8-slot ring in both trees.
- **F6 mirror below-plane cull** (`ReflectionCull`): gain is view-dependent, `reflect` within noise on perf-fly.
- **F7 cloud tile runs** (`CloudTiles`): main cloud GPU 0.98 -> 0.53 ms, mirror 2.09 -> 1.55 ms.
- **F9 Hi-Z opaque cull** (`ChunkOcclusionCuller`): worldPass GPU 3.25 -> 2.38 ms, frame 15.71 -> 14.01 ms,
  ~58-63% of worldPass indices culled; two splits (1/8, 1/2) keep a near split for caves at half the
  pyramid builds. Verified bit-identical by a temporary occlusion-query and image diff (0 differences).
- **F10 pre-sized opaque pool** (`ChunkGeometryPool.OpaqueInitialUnits` 524288): the 332-534 ms mid-play
  hitch was the pool doubling (a driver stall with old and new buffers resident); worst frame 458 -> 61 ms.
  The 5-12 ms land-leg `chunkApply` frames are `NamedBufferSubData` stalls landing the GPU-bound
  frame's own wait, not bytes.
- **G1 worldgen cost probe** (`WorldgenCostProbe`, `WorldgenStageTimer`): `Compute` ~0.25-0.33 ms a column
  warm or cold; a warm chunk fill 0.11-0.65 ms (see the G-group intro under Open); a cold fill 49-145 ms.
- **G2 pond verdict cache** (`WaterBodies.PondSiteAt`): `Compute` on a pond column 2.20 -> 0.99 ms.
- **G3 ore and pocket noise on a coarse lattice** (`OreNoiseLattice`, stride 2): the three stamps 0.17 ->
  0.084 ms a chunk, whole warm fill 0.659 -> 0.439 ms. Stride 4 rejected (deposits lock to multiples of 4).
  A Lipschitz-bound skip was rejected (needs a gradient bound for vendored OpenSimplex2, keeps half the cells).
- **G4 light flood** (`ChunkLighting.ComputeLight` seeds only at sky-column boundaries): mesh build
  6250 -> 4275 us (-32%), blue lamps 7299 -> 4772 us. An `EdgeFlags` table in `SpreadLight` gained under 1%.
- **G6 chunk-level stamp gates** (`StampBand`, `StampGateTests`): warm fill -14% to -64% per chunk class
  (above-surface 0.477 -> 0.171 ms); a new feature stamp must add its band and its `StampGateTests` row.
- **Palette chunk storage** (`Chunks/ChunkBlocks`; RTX 3070, 16 cpus, Release, 2026-11-10, interleaved against a
  d29c0735 worktree). Memory: the `VOXEL_SPAWN=0,30,0 VOXEL_TIME=60` rest ring (17,077 chunks) held ~6,050 Uniform /
  ~11,020 Indexed / 0 Direct chunks, ids ~49.6 MB vs 136.6 MB dense (-64%; ~4,250 Indexed chunks already edited
  by the fluid settle); perf-fly ended at 7,659 / 9,418 / 0, 42.4 MB vs 136.6 MB. Frame time within noise: rest
  pairs 4.68/4.57, 4.35/4.21, 4.29/4.18 ms (after/before; every CPU row moved together by ~1%), perf-fly means
  4.30/3.99, 4.02/4.08, 3.93/3.98 ms with the same worst-frame spread, a 25 s pour of `flow-water.json` 6.21/5.84,
  5.85/6.24 ms (`fluidTick` 0.08 ms both). Probes, after/before: `MeshBuildCostProbe` within ~1% in every case,
  `WorldgenCostProbe` warm all-classes mean 0.342-0.348 vs 0.336-0.343 ms (`FromDense` costs ~5-8 us a chunk,
  charged to the `SolidCount` stage), `TowerCollapseCostProbe` step mean 0.194 vs 0.190 ms over six pairs once the
  Indexed `Get`/`CopyRun` read the 256-slot palette without a bounds check (before that it leaned ~0.03 ms worse).
  **Trap:** the tower probe's step mean is bimodal run to run (~0.14 or ~0.20 ms); judge it over five or more pairs.

### Not chasing (recorded so it is not re-proposed)

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

### Ruled out (do not retry without new evidence)

- Early-Z cutout/solid split: regressed spawn 1.72->1.87 and forest 1.83->2.13 ms GPU on an RTX
  3070; stashed, not in the tree.
- Reflection geometry cutoff (capping mirrored chunks/LOD at 192 blocks): leaves a seam the wave
  offset turns into flicker. F6 (landed) differs: it drops only geometry the clip already discards.
- Half-rate NEAR shadow map: the reflection samples it every frame, so mirrored shading steps at
  fps/2. The far cascade is cached instead (F3, landed), which is not a fixed rate.
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
  saved 2.02 ms; the order-preserving segmented cull that landed (F9, landed) got most of it. Shadow cascades:
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
  the depth test cannot cull, and the mirrored clouds (F7). F7 has since landed (landed): the mirror's
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
