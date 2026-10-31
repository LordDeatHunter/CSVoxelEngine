# Performance TODO

Closed history (every landed item, the per-round measurement write-ups, the gate machinery) was
removed on 2026-10-28; it lives in git (`git log -p -- PERF_TODO.md`). What stays: the measurement
protocol, the current profile, the open items and the do-not-retry **Ruled out** list.

Machine for every number below: Release, Quadro T1000 Max-Q (GL 4.6, driver 597.06), 12 CPUs,
`SustainedLowLatency`, `VOXEL_TIME=60`, `VOXEL_DEBUG=1`.

## How to measure

Repeatable fly (checked in): `OpenGLTest/visual-examples/perf-fly.json` (10 s settle at spawn,
teleport + sprint-walk legs over land, water bay (228,-138), a far teleport (-120,100)). Run from
`OpenGLTest/` so `assets/` resolves:

```powershell
$env:VOXEL_DEBUG="1"; $env:VOXEL_TIME="60"; $env:VOXEL_SCRIPT="visual-examples/perf-fly.json"
dotnet run --project VoxelEngine.csproj -c Release --no-build
```

Read the `perf` console line (`avg~max` per section, `/gpuAvg~gpuMax` on render passes). Traps:

- **`lighting` CPU is the draw-ring fence wait, not lighting.** It is the frame's first GL work
  (`BeginFrame` -> `ChunkDrawBatch.BeginFrame` waits on the fence of the slot 3 frames back). Measured
  with temporary sub-timers: `SetLighting` 0.03 ms, audio 0 ms, clear + sky 0.03 ms, the fence wait
  0.5 -> 20 ms. A fat `lighting` row therefore means **GPU-bound**; read the GPU suffixes.
- `gpu` in the header is a sum of the per-pass timers and is `0.1`/partial on frames where GPU timing
  is gated off (idle interval). Only average the lines whose `worldPass`/`shadows` GPU suffix is non-zero.
- **The T1000 throttles.** `nvidia-smi` during the fly: 100 % utilization, SM clock 1530 MHz at the start
  falling to 1080 MHz by the end (throttle reason `0x20`, SW thermal slowdown, 74 C). Late-fly lines
  are therefore ~30 % slower than early ones for the same scene (fps mean 76 early vs 55 late).
  Cool the machine / idle it between runs, and compare only interleaved pairs.
- Worker-thread costs (meshing, worldgen) never show in the per-frame profiler.

## Current profile (2026-10-28, `perf-fly`, 3 runs, 113 `perf` lines)

The game is **GPU-bound**, not CPU-bound: at every point the CPU sections sum to a few ms and the rest
of the frame is the fence wait in `lighting`. Over the 34 full-GPU-timing lines (mean frame 18.1 ms,
GPU sum 15.9 ms):

| Pass | GPU avg ms | Share | Worst |
|---|---|---|---|
| `shadows` | 6.65 | 42 % | 19.7 |
| `worldPass` | 3.64 | 23 % | 7.1 |
| `reflect` (water on screen only) | 2.54 | 16 % | 10.0 |
| `cloudPass` | 1.20 | 8 % | 2.7 |
| `lodPass` | 0.72 | 5 % | 1.9 |
| `postPass` | 0.46 | 3 % | 1.4 |
| `waterPass` | 0.35 | 2 % | 1.6 |
| `uiPass` (X overlay) | 0.35 | 2 % | 1.0 |
| `aoPass` | 0.25 | 2 % | 0.8 |

Frame time: early fly 3-10 ms (100-337 fps, small world loaded), streaming 15-30 ms (33-68 fps),
settled-but-throttled far leg 21-25 ms (~40 fps, `chunkApply`/`lod`/`fluidTick` all ~0).

This is ~2x worse than the 2026-09-11 snapshot (settled 9-10 ms). Part is the thermal throttle above, but the
shadow pass is the outlier and may be a real regression since then; that is open item 1.

CPU spikes (streaming only, worst single frames): `chunkApply` avg 7 ms with 46 ms worst, `lod` 1306 ms
worst on a teleport (one frame, seen once in three runs), `fluidTick` up to 18 ms, `entities` 30 ms and
`camera` 17 ms on the very first frame (startup), `cloudPass` 16 ms CPU / 33 ms GPU on the first frame.
GC: streaming 20-45 gen0/s, settled 4-11 gen0/s.

## Worldgen column cost: relief phase B (headless Compute, 2026-10-29)

`TerrainGenerator.Compute` per chunk column, before vs after the hashed massifs and lone peaks
(BIOMEGEN 13 phase B), measured with a temporary xUnit probe: one `Compute` per listed column after a
64-column warm-up, median ms per category, 5 interleaved before/after pairs in fresh processes, the
median of the five medians. The column lists (fixed SplitMix64 seeds, default world seed) were
`all` (2048 random columns 1000..12000 blocks out), `lowland` (1024 columns whose center carries no
highland, massif or lone-peak term), `massif` (1024 columns within 0.8 radius of the 9 accepted
massifs inside +-12000) and `peak` (1024 within 0.8 radius of the 234 accepted lone peaks). The
baseline was a robocopy of the working tree taken before phase B rather than a `git worktree` of
HEAD, because the phase A relief layer was still uncommitted and HEAD would have measured the wrong
baseline.

| Columns | Before ms | After ms | Change |
|---|---|---|---|
| `all` | 0.2221 | 0.2372 | +6.8 % |
| `lowland` | 0.1869 | 0.1996 | +6.8 % |
| `massif` | 0.2323 | 0.2700 | +16.2 % |
| `peak` | 0.2023 | 0.2252 | +11.3 % |

The lowland rise is small but systematic (every after run above every before run, run-to-run spread
about 3 %): it is the two `CellRange` scans every `LandHeight` call now pays (a `SpawnClear`, then a
`TryRoll` hash per reaching cell for massifs and lone peaks), also inside each lake candidate's nine
rim samples. No cache probe or noise read happens before the distance cull, so the cost does not
grow with the site caches. Inside a disc the extra is the cached verdict, the wobble noise, the
spur noise and the `Pow` profile; the first column of a site also pays its verdict (massif: ten
`ReliefLandHeight` samples; lone peak: ten `PreFeatureLandHeight` samples, each with its own massif
scan). Not chased: about 0.013 ms per chunk column (50 ns per block column), far below the mesh build; the cache capacities and
generation counts were not touched.

## Worldgen column cost: islands phase C (headless Compute, 2026-10-30)

`TerrainGenerator.Compute` per chunk column, before vs after the ocean-gated island term and the
hashed archipelagos (BIOMEGEN 13 phase C, `TerrainIslands`), measured with the same temporary xUnit
probe as phase B: one `Compute` per listed column after a 64-column warm-up, median ms per category,
5 interleaved before/after pairs in fresh processes, the median of the five medians. The column lists
(fixed SplitMix64 seeds, default world seed, every center 1000..12000 blocks out) were `all` (2048
random columns), `land` (1024 columns whose center has a pre-island ocean weight of exactly 0, the
control), `ocean` (1024 past the shore, `pre > OceanShoreWeight`, with an island weight of 0),
`island` (1024 whose center is island land: past the shore before the fold, inside it after) and
`archipelago` (1024 inside an accepted archipelago footprint, past the shore). The baseline was again
a robocopy of the working tree taken before phase C rather than a `git worktree` of HEAD, because
phases A and B and the icebergs were still uncommitted and HEAD would have measured the wrong
baseline. The first baseline run's `all` median (0.6448 ms) was a cold start; the median of five
discards it.

| Columns | Before ms | After ms | Change |
|---|---|---|---|
| `all` | 0.2425 | 0.2823 | +16.4 % |
| `land` | 0.2311 | 0.2272 | -1.7 % |
| `ocean` | 0.2042 | 0.2500 | +22.4 % |
| `island` | 0.2019 | 0.2822 | +39.8 % |
| `archipelago` | 0.1789 | 0.2463 | +37.7 % |

`land` is flat inside its run-to-run spread (before 0.211..0.245, after 0.226..0.237): continental
land pays one compare (`pre <= OceanShoreWeight`) per `BiomeWeights` call and nothing else, and
continental massif and lone-peak sites skip the island scaling entirely. Ocean past the shore pays,
per `BiomeWeights` call (several per column: the column itself, the shore clamp, each lake
candidate's rim samples), one `SpawnClear`, the archipelago `CellRange` with a `TryRoll` hash per
reaching cell, and the two 2-octave island noises: about 180 ns per block column. Island and
archipelago columns pay the same, plus the land work an Ocean-biome column skips once it has become
land (the lake and river tests `WaterBodies` gates on the biome, the ravine cut the ocean weight fades
out; the height profiles themselves are computed for every column either way); island massif and lone-peak sites add one
more continent read and the island noises, but only inside their discs (3 island lone peaks in
+-20000, no island massif). Not chased: about 0.05 ms per ocean chunk column, far below the mesh
build; a later saving would be to skip the small-island noise where the large channel already
saturates. No cache was added (the archipelago verdict is a stateless hash), and no cache capacity
or generation count was touched.

Identity against the baseline (a dump of both trees with APIs present in both, the pre-island weight
taken from the baseline's own `BiomeWeights`, which is exactly the pre-island weight there):

- +-20000 grid at stride 37 (1,170,724 points): 105,692 changed in `LandHeight`, the folded ocean
  weight or the biome; **0** of them where the baseline ocean weight is at or below
  `OceanShoreWeight` (624,405 points have a baseline weight of exactly 0, none changed).
- Massif and lone-peak cell verdicts over the box (8,884 cells, 707 sites before, 710 after):
  **0** changed at a continental candidate center; 3 new island lone peaks (cells (-42,-2), (-11,38)
  and (-9,30)), each at a baseline center weight of 1.
- `Compute` on 1000 random chunk columns: 120 changed `Heights` or `WaterLevel`; of the 529 columns
  whose every cell has a baseline ocean weight of 0, **0** changed. The lake-rim and shared-tree-batch
  cross-effects BIOMEGEN 11 lists did not show in this sample (tree batches do not enter `Compute`).

## Worldgen column cost: great lakes phase D (headless Compute, 2026-10-30)

`TerrainGenerator.Compute` per chunk column, before vs after the great-lake tier (BIOMEGEN §6
phase D: `GreatLakes`, wired through `WaterBodies.SampleSurfaceWater`, the pond yield in
`TryLakeSite` and the ravine guard), measured with the same temporary xUnit probe as phases B and C:
one `Compute` per listed column after a 64-column warm-up, median ms per category, 5 interleaved
before/after pairs in fresh processes, the median of the five medians. The column lists (fixed
SplitMix64 seeds, default world seed) were `all` (2048 random columns 1000..12000 blocks out), `land`
(1024 random columns in the same ring whose center has a great-lake weight and guard of exactly 0,
the control), `lake` (1024 columns within 0.8 of a lobe radius of the 13 lobes of the 6 accepted
sites inside +-16000) and `shore` (1024 columns in the guard band: weight 0, guard above 0). The
baseline was again a robocopy of the working tree taken before phase D, because the icebergs were
still uncommitted. Absolute times run higher than the phase C table on this session's machine state;
only the pairs measured together compare.

| Columns | Before ms | After ms | Change |
|---|---|---|---|
| `all` | 0.3861 | 0.3996 | +3.5 % |
| `land` | 0.3231 | 0.3398 | +5.2 % |
| `lake` | 0.2622 | 0.3237 | +23.5 % |
| `shore` | 0.2606 | 0.3173 | +21.8 % |

The `land` rise is small but systematic (before 0.320..0.329, after 0.339..0.364): every block column
now pays `GreatLakes.Sample`'s spawn test, `CellRange` and a `TryRoll` hash per reaching great-lake
cell, and every pond candidate in reach pays the same scan again in `NearAnyLobe` before its rim
samples; no cache probe or noise read happens before the exact distance cull. Inside a footprint
the extra is the cached verdict, the 2-octave shape noise read once per block column and the
smoothsteps, about 240 ns per block column. Verdicts are cheap and rare:

- `BuildSite` timed directly (uncached) over the 199 rolled candidates of the cells covering
  +-16000, two passes in each of two fresh processes. Cold pass: accepted (6) median 1.83..1.87 ms,
  worst 2.26 ms; rejected (193) median 0.031 ms, worst 12.2..16.2 ms, which is the very first call
  (JIT and the first massif and lone-peak cache fills). Warm pass: accepted median 1.28..1.32 ms,
  worst 2.12 ms (cell (11, -7)); rejected median 0.005 ms, worst 0.78 ms. The whole box costs about
  45 ms cold. The 20-60 ms per accepted verdict the plan feared did not show (the radius skew keeps
  most lakes small, and most candidates fall to the spacing, spawn, relief or first rim sample), so
  none of the spacing or ordering fallbacks was applied.
- Cold sweep, single thread, fresh process: all 203 coarsest-ring regions (8-block cells) within
  2048 blocks of a point 64 blocks past the east shore of the rim-fed lake of cell (-8, -8): before
  39.9 / 39.8 s, after 41.5 / 41.0 s (+3.4 %); **5** verdicts built, 7.0 / 7.1 ms in total (a
  temporary counter in the cache factory, removed afterward). The rest of the rise is the
  per-column tax above.
- One finest-ring region (2-block cells, (-146, -153), over that lake's main lobe), first build in
  a fresh process: before 1525 ms, after 1091 ms (-28 %), second build 6.4 vs 7.0 ms (noise). The
  cold build is dominated by the column fills and the first ring's tree batches, and the lake now
  covers what was wooded lowland, so fewer trees grow there; the likely reason it got cheaper.

Not chased: about 0.017 ms per land chunk column and 0.06 ms per lake chunk column, far below the
mesh build. The cache capacity (512) and generation count (4) were not moved.

Identity against the baseline (a dump of both trees with APIs present in both; the footprint is
each accepted lobe disc, over a +-23000 cell scan (27 lobes), grown by the guard plus twice
`LakeMaxRadius` plus `GreatLakePondClearance`, 132 blocks past the edge, since a pond that yields
reaches its own radius past its center):

- +-20000 grid at stride 37 (1,170,724 points), `WaterLevelAt` and `SurfaceWaterWeight`: 717 changed
  (651 levels, 717 weights); 714 inside a lobe disc grown by the guard and 3 in the pond-yield ring
  (at most 64.3 blocks past a lobe edge, so inside the plan's tighter 86-block reach too); **0** of
  the 1,168,181 points beyond the footprint changed.
- `Compute` on 1000 random chunk columns (`Heights`, `WaterLevel`, `Bed`): 1 changed (all 256
  cells), the one column that touches a lobe; of the 999 columns whose every cell lies beyond the
  footprint, **0** changed.

## Worldgen column cost: great-lake rivers phase E (headless Compute, 2026-10-31)

`TerrainGenerator.Compute` per chunk column, before vs after the great-lake river ramps and outflows
(BIOMEGEN §6 phase E: `GreatLakes.RiverLevelAt` read through `WaterBodies.SampleSurfaceWater` by the
carve, `WaterLevelFor` and `WaterLevelAt`, the wider river cull, the outflow channel and the
bank-raise rule with its `RampRiverBeside` re-scan), measured with the same kind of temporary xUnit
probe as phases B-D: one `Compute` per listed column after a 64-column warm-up, median ms per
category, 5 interleaved before/after pairs in fresh processes, the median of the five medians. The
baseline was a `git worktree` of `d2b5788f` (HEAD before phase E; this time everything earlier was
committed). The column lists were drawn once in the after tree (fixed SplitMix64 seeds, default
world seed) and read by both trees, each category defined by the sampled point inside the listed
column: `all` (2048 random columns 1000..12000 blocks out), `land` (1024 in the same ring whose point
has a great-lake weight, guard and outflow of exactly 0, the sea river level and no bank, the
control), `lake` (1024 within 0.8 of a lobe radius of the 14 lobes of the 7 accepted sites inside
+-16000), `ramp` (1024 whose point lies past a ramped site's lobes by less than its `RampLength`) and
`outflow` (1024 inside the 3 outflow channels; only 31 distinct columns, since the channels are
small). Absolute times differ from the phase D table on this session's machine state; only the
pairs measured together compare.

| Columns | Before ms | After ms | Change |
|---|---|---|---|
| `all` | 0.3778 | 0.3977 | +5.3 % |
| `land` | 0.2819 | 0.2844 | +0.9 % |
| `lake` | 0.2804 | 0.2876 | +2.6 % |
| `ramp` | 0.2624 | 0.2660 | +1.4 % |
| `outflow` | 0.2464 | 0.2649 | +7.5 % |

`land` is flat inside its run-to-run spread (before 0.278..0.306, after 0.280..0.291), so it stays
well under the 3 % bar over phase D: the wider river cull (`RiverCull`, a candidate's reach plus
`RiverReach`, capped at the old `MaxReach + Guard`) reads a cached verdict for a few more columns, and
`NearAnyFootprint` a few more for pond candidates, but no noise is read before the cull. `all` is the
first category each process times, so it absorbs the cold verdict and relief-cache fills (before
0.363..0.578, after 0.369..0.508); its rise is inside that spread. `lake` and `ramp` rise within
their spreads (lake before 0.276..0.287, after 0.275..0.294; ramp before 0.251..0.273, after
0.259..0.284): a ramp column pays `Beyond` (one sqrt per lobe), `RiverLevelAt`, the ramp guard's
smoothstep and `OutflowWeight`, and the rare dry column under its bank pays the `RampRiverBeside`
re-scan (four `RiverWeight` reads per ramped candidate). `outflow` is the one systematic rise (before
0.241..0.254, after 0.253..0.270): those columns now carve a channel and run the bank rule along its
sides. About 0.02 ms per outflow chunk column, far below the mesh build; not chased.

Verdicts: `BuildSite` timed directly (uncached) over the 234 rolled candidates of the cells covering
+-16000 (grown by `MaxReach` plus the guard), two passes in each of two fresh processes. Every
accepted site in the box is ramped now (7, none accepted without a ramp, so that bucket is empty).
Cold pass: accepted median 2.08..2.22 ms, worst 18.9..19.7 ms (cell (13, -7), whose first verdict
also fills the massif and lone-peak caches it reads); rejected (227) median 0.026..0.028 ms, worst
12.8..12.9 ms, the very first call (JIT). Warm pass: accepted median 2.09..2.22 ms, worst 4.2..4.9 ms
(cell (4, 3), the largest ramp with an outflow); rejected median 0.005..0.007 ms, worst 0.93 ms. The
whole box costs about 61 ms cold and 23 ms warm. Against phase D's warm accepted median (1.28..1.32 ms)
the ramp's checks (`RiversHoldOnRamp`'s ring walk with five `LandHeight` reads per river sample,
`TryOutflow`'s ring and corridor) add about 0.8 ms per accepted verdict, once per cell on a worker
thread; the 10+ ms the plan feared did not show (these ramps are short, 12..48 blocks). The cache
capacity (512) and generation count (4) were not moved.

Identity against the baseline (a dump of both trees with APIs present in both; the footprint is each
accepted lobe disc over the cells covering +-23000 (14 sites, 13 ramped, 29 lobes), grown by the guard,
or by `RiverReach` for a ramped site, plus twice `LakeMaxRadius` plus `GreatLakePondClearance` for the
ponds that yield to it):

- +-20000 grid at stride 37 (1,170,724 points), `WaterLevelAt`, `SurfaceWaterWeight` bits and
  `LandHeight` bits: 490 changed (486 levels, 36 weights, **0** land heights); 482 inside a lobe disc
  grown by its guard or `RiverReach`, 8 in the pond-yield ring (at most 251.9 blocks past a lobe edge);
  **0** of the 1,164,135 points beyond the footprint changed.
- `Compute` on 1000 random chunk columns within +-20000 (`Heights`, `WaterLevel`, `Bed`): 1 changed;
  of the 992 columns whose every cell lies beyond the footprint, **0** changed.

## Open

1. **Shadow pass GPU cost (6.65 ms avg, 42 % of the GPU frame).** Largest single item and not yet
   explained. Check first: how much of it is the far cascade (352 blocks + non-buried LOD regions)
   versus the near one (90 blocks); chunk count and vertex volume each cascade draws; whether the far
   cascade needs to render every frame (a half-rate map is ruled out for the *near* map because the
   reflection samples it; the far map has not been priced separately); shadow map sizes
   (`LightingConfig`); and whether it grew since 2026-09-11 (bisect with `perf-fly` on an older commit,
   interleaved, at matched GPU temperature). Add per-cascade `FrameProfiler`/GPU timer rows before
   changing anything.
2. **`worldPass` GPU 3.6 ms** and **`reflect` 2.5 ms (up to 10 ms with the bay in view).** After item 1,
   the next levers. `reflect` already has temporal reuse; re-check that it is actually skipping frames
   in a settled bay capture (`reflect` GPU suffix on alternate frames) before pricing more.
3. **`chunkApply` on the main thread during streaming (avg 7 ms, worst 46 ms).** Upload budget is
   `MeshUploadBudget`; it competes with the GPU-bound frame, so a lower budget may trade streaming speed
   for frame time. Capture before moving the budget.
4. **Single-frame `lod` spike of 1.3 s on a far teleport** (one run of three). Not reproduced; capture
   with a per-region timer before assuming a cause (region unload storm vs a synchronous build).
5. **GPU time under the thermal throttle is not stable run to run.** Any before/after claim needs
   interleaved pairs and a cool-down; consider logging `clocks.sm` next to the `perf` line.

### Not chasing (recorded so it is not re-proposed)

- Hierarchical / compute GPU cull: post-MDI cull+sort+fill is ~2 % of the frame.
- Shader ALU cuts (caustic Voronoi net, 8 point lights, 4-tap PCF); half-res is the allowed lever.
- Worldgen noise reduction in `TerrainGenerator` / `NoiseCaves` (changes visuals).
- `FluidManager`, `Shader` uniform caching, `MeshBufferPool`, `ChunkDrawBatch`, `BlockCubeCache`,
  `ModelBaker`, `Vertex`, the station/drop/TNT managers, `Camera`: audited, already cached, pooled or
  allocation-free. (`ChunkDrawBatch`'s fence wait is the *symptom* of a GPU-bound frame, not a bug:
  3 slots, signaled-at-rest.)
- `LodColumnCaps` unbounded growth: a correctness item, `TODO.md` `M-11`. It makes two LOD tests
  intermittent in full-suite runs (`LodBuildPoolTests.RegionBuildsAreIdenticalWhicheverThreadRunsThem`,
  `LodHeightSamplingTests.SeparableMinMatchesTheNestedWindowAtEveryLevel`); never "fix" them by touching
  `ColumnCaps`.

## Ruled out (do not retry without new evidence)

- Early-Z cutout/solid split: regressed spawn 1.72 -> 1.87 ms and forest
  1.83 -> 2.13 ms GPU on RTX 3070. Driver keeps the early depth *test*; only the
  write defers. Change stashed, not in tree.
- Reflection geometry cutoff (drop LOD / cap mirrored chunks at 192 blocks):
  leaves a hard terrain/sky seam that the animated wave-normal offset turns into
  flicker. A planar mirror needs continuous far terrain. (The shipped reflection
  temporal reuse kept full mirrored geometry and only reuses the finished texture
  — a different lever, so this entry still stands.)
- Half-rate shadow map: reflection samples the map every frame, so a 2-frame
  cadence steps mirrored shading at fps/2. Pair only with a flicker-free design.
  (Reflection temporal reuse takes the analogous risk on the mirror itself and
  caps it at one skipped frame behind a camera-delta gate, so mirrored shading
  never steps at fps/2 for more than a single frame; the shadow map itself stays
  every-frame.)
- Compute frustum cull (Phase 5): post-MDI cull+sort+fill is ~2 % of the T1000
  frame (`worldPass` 0.50, `shadows` 0.26, `waterPass` 0.09, `reflect` 0.83 ms).
  Retry only if a CPU-bound profile shows cull+sort above ~1 ms.
- Fluid chunk-cache (16-slot direct-mapped): no spike reduction alone, 16 vs 256
  slots identical — per-cell cost was the whole-set sort, not dictionary lookups.
  The sort is gone (per-frame spread); no per-tick cap either (a per-tick cap
  freezes fluid because wakes schedule 60/s while ticks run 4/s).
- Mud 6-face gate as specified: not outcome-equivalent (diagonal dirt can be wet
  with no face-adjacent dirt). Only valid under an unproven scheduling invariant.
- VSync/limiters, GL debug context, per-frame `GetError`, worker priorities
  (thread-priority tuning — the landed chunk-scan frontier is the algorithmic
  change aimed at that scan, not this),
  shader ALU (4-tap PCF, 8 point lights), uniform location lookups, indexed mesh
  upload — all audited, not the problem.

- **R3 · Half-res scene copies for water — resolved, PARKED, and closed rather than unparked** —
  `RenderTargets.CopyScene`, called with color+depth in `TerrainPasses.RenderWorldTranslucent` and
  depth-only in `ScreenPasses.RenderUnderwaterRays`. The condition R2 left on this bullet was
  resolved against the code rather than implemented, and it settles three ways.
  **(a) The depth copy cannot move.** `CopyScene(bool color)` copies depth
  *unconditionally* — only the color half is gated by the parameter — and both call sites
  write the same `_sceneDepthTex`, which R2's depth-aware upsample pins at full res. So the
  color+depth call keeps paying a full-res `DepthComponent24` `CopyTexSubImage2D` whatever R3
  does, and a second, half-res depth used only by the water pass is the same *additive* cost
  (an extra texture plus an extra reduction, on top of a full-res copy that still has to
  happen) that R2 already rejected on the rays side. Only the RGBA8 color copy is left.
  **(b) The shader claim holds.** `test-fragment.glsl`'s refraction really is
  resolution-independent: `uv = gl_FragCoord.xy * uInvViewport` (`:267`) against
  `uInvViewport = 1/_viewportSize` (`RenderManager.ApplyFrameUniformsTo`), bilinear `texture()` fetches
  (`:270-272`), a `uInvViewProj` unprojection (`:276`) — no `texelFetch` and no pixel indices
  anywhere in the file. The shader would need zero changes.
  **(c) But the ceiling is tiny and the change is not "sample less".** The color copy is
  gated behind `if (_nearWaterVisible)` and sits inside a `waterPass` measured at **0.09 ms
  GPU total** (the compute-frustum-cull capture in this same section, above) — a figure that
  also contains every translucent chunk draw and every LOD water sheet. That capture
  demonstrably *included* the color copy: the same line reports `reflect 0.83 ms`, and
  `ReflectionPass.RenderReflection` early-returns unless `_nearWaterVisible`
  (`TerrainPasses.NearWaterVisible`) — the very flag that gates the copy — so near water was on
  screen when it was taken. 0.09 ms is therefore a real ceiling for the whole pass, not a
  figure that might have excluded the work. And `CopyTexSubImage2D` cannot downscale, so this
  is not a sampling change but new plumbing: an FBO, a texture, sizing and teardown across
  `CreateSceneCopy` / `DeleteSceneCopy` / `SetViewport` / `Dispose`, and a
  `glBlitFramebuffer` that still *reads* the full-res framebuffer and saves only the write
  side (arithmetic off the format, no timing claim: 3.69 MB -> 0.92 MB of RGBA8 writes per
  frame at 1280x720). Its cost is blurring everything seen through water — a perceptual
  change, which drags R2's full perceptual gate along with it.
  **Parked, not disproven:** effort M *plus* a perceptual gate, for an unmeasurable slice of
  an already-gated 0.09 ms pass. Revisit only if a capture ever isolates the color copy as
  its own row. **Impact low · risk low-med · effort M + perceptual gate.**
  **Closed on 2026-09-20, and moved here rather than unparked.** It was parked against the code and
  never disproven; what is left is not a measurement but a perceptual judgment — whether blurring
  everything seen through water is acceptable in this game — and no capture can answer that. The human
  closing this round chose to stop carrying it rather than unpark it. It lives here because this
  section's job is to stop items being re-proposed, so its reasoning travels with it. Retry only on
  new evidence, which here means a capture that isolates the color copy as its own row.
- **C5's pool-size lever — "a bigger LodGen pool".** Measured at two sizes and declined at both, on a
  quantity that was never the limiter. Three `BelowNormal` workers reach a fixed region count **1.31x**
  sooner and two reach it **1.26x** sooner (disjoint, counterbalanced, p = 0.0143 at each of three
  region counts) — but three also slow chunk generation **disjointly** (seconds to the full
  17077-chunk ring 5.0 -> 6.1 median) and cost **30 %** of burst frame rate (median 108.0 -> 75.3 fps),
  while two, which do clear the chunk-gen and GC guards, still cost **18 %** (110.3 -> 90.7 fps) — paid
  on every run, in the exact phase this item targets, for 1.26x on a quantity with **7.8-8.2x of drain
  headroom** (53-55 regions/s measured against a ~433 regions/s drain ceiling). Why it is so far below
  linear is the reusable half: a LOD build is **terrain generation in disguise** (36 / 100 / 324 column
  prefetches at levels 0 / 1 / 2, each miss generated on the spot through `Chunk.ColumnCache`), so
  LodGen workers do not add throughput to an idle resource — they contend with the four chunk-gen
  threads for the one the burst is already saturating. `StreamingConfig.MaxConcurrentLodGenerations`
  ships at **1**. The capture is under **"Measurement: C5"** and the argument under **"The C5
  concurrency argument"**; **everything except the extra threads landed and stays** (see **What
  landed**). Only the pool size is ruled out, and re-proposing it needs a capture that answers this one.
