# Performance TODO

Merged 2026-09-11 from `PERF_TODO.md` + `FLUID_PERF.md` (the latter deleted).
All old items 1-12, 15, 16, 17 and fluid items 1, 2, 4, 5, 6 are **done**; what matters
about them is under "Ruled out" and "Done (one line each)". Work the Open items
top to bottom. Full per-item history lives in git (`git log --oneline -- PERF_TODO.md FLUID_PERF.md`).

Machine for every number below: Release, Quadro T1000 (GL 4.6, driver 597.06),
12 CPUs, `SustainedLowLatency`, `VOXEL_TIME=60`, `VOXEL_DEBUG=1`.

## How to measure

Repeatable fly (checked in): `OpenGLTest/visual-examples/perf-fly.json` — 10 s
settle at spawn, teleport + sprint-walk legs over land, water bay (228,-138) and
a far teleport (-120,100). Shots land in `OpenGLTest/visual/perf-fly/` (gitignored).
Run from `OpenGLTest/` so `assets/` resolves:

```powershell
$env:VOXEL_DEBUG="1"; $env:VOXEL_TIME="60"; $env:VOXEL_SCRIPT="visual-examples/perf-fly.json"
dotnet run --project silkgltriangle.csproj -c Release --no-build
```

Two profiler traps, read before trusting a number:

- In scripted runs `progUpdate` **contains** `World.Update` (`VisualDirector.Drive`
  calls it inside the `ProgramUpdate` span). A 15-25 ms `progUpdate` during a script
  is chunk/LOD/fluid work, not input overhead. In real play `progUpdate` is ~0.2-0.5 ms.
  CPU-section sums therefore exceed the frame in scripted runs by design.
- The `perf` console line shows `avg~max` per section (`~` = worst single frame in
  the 1 s window) with a `/gpuAvg~gpuMax` suffix on render passes (item 18) — read
  the `~` half for spikes, the avg half for steady cost. `lighting` CPU absorbs the
  GPU stall (it is the frame's first GL call), so a fat `lighting` row with a
  matching GPU sum means GPU-bound, not lighting-bound. The `X` overlay keeps CPU
  avg+max and GPU avg only (fixed-width rows); the console line is the spike record.

## Current profile (2026-09-11 fly, 58 `perf` lines)

| Phase | Frame | FPS | Notes |
|---|---|---|---|
| Settled, land | 8.8-10.5 ms | 95-114 | cpu ~9-10, gpu ~9-10; balanced |
| Settled, over water | 9.4-11.4 ms | 88-106 | `reflect` GPU 0 when no water on screen, up to ~8 ms when the bay is in view; `worldPass` GPU 3.5-5.6 ms is the largest steady cost |
| Streaming (first 10 s) | 18-39 ms | 25-54 | worst 1 s window 32.9 ms avg (true single-frame max higher, not logged) |
| Teleport | one ~30 ms window | ~34 | unload storm 17077 -> 4991 chunks, `lod` 2-3 ms |
| Underwater | ~10-14 ms | 72-100 | `raysPass` GPU 1.5-2.3 ms while submerged |
| Screenshot step | +7-18 ms | 1 fps that frame | PNG encode on the render thread (scripted runs only) |

Streaming-second breakdown (avg over the 1 s window, so peaks are higher):
`lod` up to 10.7, `chunkDirty` up to 6.3, `chunkApply` up to 5.8, `fluidTick`
up to 4.6, `fluidWake` up to 0.96, `lighting` CPU 3-7 (stall absorber, see traps).
Settled: `chunkApply`/`chunkDirty`/`lod`/`fluidTick` all <= 0.2 ms.

GC: streaming 17-25 gen0/s, 8-11 gen1/s, 0-2 gen2/s; settled 1-3 gen0/s, ~0 gen1.
Pool growth during first stream-in only: opaque 65536 -> 131072 -> 262144 units
(**512 MB**), translucent to 65536 (128 MB). No growth after settle.
`fluidTick` avg is 0-0.6 ms settled — the old ~100 ms max was not reproduced in
avg data (max is not logged; see item 18). No `TEMP-DIAG` strings in tree.

### Hitch ranking (what actually drops frames)

1. **Chunk streaming burst** — gen + mesh schedule + upload + LOD rebuild.
   100+ fps settled -> 25-55 fps streaming. `lod`/`chunkDirty`/`chunkApply` rows.
2. **Reflection + world pass when water is on screen** — `reflect` GPU 0 -> 8 ms,
   `worldPass` GPU up to 5.6 ms. Looking at the bay costs ~2x dry land.
3. **Geometry pool doubling mid-stream** (opaque to 512 MB) — one-time copy hitch.
4. **Underwater `raysPass`** (1.5-2.3 ms GPU) and **`cloudPass`** (0.7-1.5 ms when
   clouds in view). Clouds were already cut 63 % (item 15); remainder is steady, not spiky.
5. **Fluid wake storm** at spawn over the sea — now bounded (queue 100-196k -> 8-12k),
   secondary to item 1.
6. **Screenshot encode** 7-18 ms — scripted runs only, but fix anyway (item 20).
7. **`uiPass`** 0.5-1 ms settled — the `X` overlay itself; subtract before comparing.

## Ruled out (do not retry without new evidence)

- Early-Z cutout/solid split: regressed spawn 1.72 -> 1.87 ms and forest
  1.83 -> 2.13 ms GPU on RTX 3070. Driver keeps the early depth *test*; only the
  write defers. Change stashed, not in tree.
- Reflection geometry cutoff (drop LOD / cap mirrored chunks at 192 blocks):
  leaves a hard terrain/sky seam that the animated wave-normal offset turns into
  flicker. A planar mirror needs continuous far terrain.
- Half-rate shadow map: reflection samples the map every frame, so a 2-frame
  cadence steps mirrored shading at fps/2. Pair only with a flicker-free design.
- Compute frustum cull (Phase 5): post-MDI cull+sort+fill is ~2 % of the T1000
  frame (`worldPass` 0.50, `shadows` 0.26, `waterPass` 0.09, `reflect` 0.83 ms).
  Retry only if a CPU-bound profile shows cull+sort above ~1 ms.
- Fluid chunk-cache (16-slot direct-mapped): no spike reduction alone, 16 vs 256
  slots identical — per-cell cost was the whole-set sort, not dictionary lookups.
  The sort is gone (per-frame spread); no per-tick cap either (a per-tick cap
  freezes fluid because wakes schedule 60/s while ticks run 4/s).
- Mud 6-face gate as specified: not outcome-equivalent (diagonal dirt can be wet
  with no face-adjacent dirt). Only valid under an unproven scheduling invariant.
- VSync/limiters, GL debug context, per-frame `GetError`, worker priorities,
  shader ALU (4-tap PCF, 8 point lights), uniform location lookups, indexed mesh
  upload — all audited, not the problem.

## Done (one line each, detail in git history)

1 FrameProfiler + `X` overlay + per-second `perf` line + startup build/GPU line.
2 Release by default (`Directory.Build.props`), `-c Release` everywhere.
3 Flat `Renderable`/`RegionList` lists, unload scan on camera-chunk change only,
  partial dirty selection. 4 Per-pass `ApplyFrameUniforms`, 3 calls/draw.
6 Buried flags on change only (`MeshVersion`/`SearchVersion`/camera chunk).
7 Enclosed-chunk fast path, `MeshBufferPool` (cap 128), `MaxWakeChunksPerFrame = 16`.
8 Batch block-edit remesh via dirty queue (single-block mine stays synchronous).
9 `Vertex` 48 -> 24 -> 32 B (`Layer` for array textures), packed attrs.
11 Greedy mesher (-14..-20 % verts on this terrain; AO fragmentation is the ceiling),
  GL 4.6 core + `TEXTURE_2D_ARRAY` everywhere, pooled `ChunkGeometryPool` /
  `GeometryAllocator`, one `MultiDrawElementsIndirect` per chunk pass
  (four chunk passes 11.5 -> 1.7 ms). 10 `SustainedLowLatency` + concurrent GC +
  TieredPGO, no Server GC; chunk fade on real delta time. 12 Per-pass GPU timers
  in overlay + console (`cpu/gpu` pairs). 15 Cloud cell 16, max 2 layers
  (field mesh -63 %). 17 Translucent/neighbour/apply/LOD scratch pooled.
Fluid: early-outs kept MC semantics, per-frame spread (`MaxFluidCellsPerFrame`
2048, backlog cap 8192), `Inventory.Update` out of the `FluidTick` span,
`HashSet<long>` dirty tracking. Total fluid writes rose 4x (51k -> 202k) as the
queue started draining instead of ballooning.

## Open items

- [x] **16. GPU quality knobs, exposed and documented.** (done: the settings menu
  covers every live knob — Display: VSync/resolution/FPS cap; Graphics: shadow
  toggle, leaf sway, sun size/glow, cloud opacity; Water: reflection toggle +
  Low/Med/High quality (0.25/0.5/1.0 scale), waves, refraction, caustics, sun-shaft
  strength — all read live via `GameSettings`, persisted to settings.json. Checked
  2026-09-11 against the original lever list. Deliberately **not** exposed: render
  distance (`ChunkRadius`), shadow map resolution (`ShadowMapSize`), LOD cascade and
  cloud mesh density (`CloudCellSize`/`CloudMaxLevels`) — all structural (chunk
  pipeline couplings, FBO realloc, field rebuild), not slider material. If a low
  preset is ever wanted, that is a feature, not perf work.)
- [x] **18. Log per-section max, not just avg.** (done 2026-09-11: `JoinSections`
  emits `label avg~max` plus `/gpuAvg~gpuMax` on render passes; overlay untouched —
  its 29-char rows cannot fit a GPU max inside the 32-glyph budget. Build clean,
  351/353 tests with the 2 failures pre-existing on HEAD. First verification run
  (`basic.json`, streaming) immediately showed what avg hid: `chunkApply ~149`,
  `lod ~57`, `fluidTick ~47`, `reflect` CPU ~44 ms worst-frame in the window.)
- [ ] **19. Slice the streaming burst.** `lod` 10.7 / `chunkDirty` 6.3 /
  `chunkApply` 5.8 ms peaks during stream-in. Candidates: bound LOD cover-test work
  per frame (level-0 slice), adaptive `MaxDirtyChunksPerFrame` /
  `MaxMeshUploadsPerFrame` from last frame's time instead of fixed 64/32, spread
  `ApplyCompletedMeshes` uploads across two frames when the frame is already long.
  Measure with the perf-fly scenario, settled-vs-streaming table.
- [ ] **20. Screenshot off the render thread.** 7-18 ms `screenshot` spike per shot.
  Encode the PNG on a worker (or skip the readback stall with a PBO) — scripted
  visual runs become hitch-free and manual F2-style shots stop dropping a frame.
- [ ] **21. Pre-size the geometry pools.** Opaque doubles twice mid-stream to
  262144 units = 512 MB before settling at ~93 % occupancy. Raise
  `OpaqueInitialUnits` (upfront 512 MB) or stream the growth (grow earlier in
  smaller steps / async copy) so the first fly-over does not hitch on
  `CopyBufferSubData`. Confirm steady-state occupancy first.
- [ ] **22. Fix the scripted-run profiler nesting.** `progUpdate` wrapping
  `VisualDirector.Drive` (which calls `World.Update`) double-counts CPU vs frame
  and confuses every log reading. Time `Drive` overhead and `World.Update`
  separately (or exclude the inner sections from the outer span).
- [ ] **23. Underwater + cloud steady costs behind flags.** `raysPass` 1.5-2.3 ms
  GPU submerged (`WaterRaySteps` 20, `WaterRayMaxDistance` 40), `cloudPass` up to
  1.5 ms, `lodPass` GPU ~1.5 ms steady. These belong in item 16's quality ladder,
  but verify each halves cleanly with its knob before wiring it in.
- [ ] **24. Re-check translucent sort + `CullWater` per frame.** Post-MDI the CPU
  side is small at rest, but streaming numbers were not isolated per pass. If a
  CPU-bound profile ever shows cull+sort above ~1 ms, revisit — otherwise leave alone.
- [ ] **25. Worldgen allocation during streaming.** Settled GC is clean (1-3 gen0/s)
  but streaming still does 17-25/s with 8-11 gen1/s. Suspects: `TerrainGenerator`
  column/compute temps, worm-path cache churn, LOD scratch (partially pooled in
  17). Temporary `GC.GetTotalAllocatedBytes` counters per subsystem like item 7 did,
  then pool the winner. Do not guess — item 7 showed the guess (mesh lists) was
  only half the allocations.

## Measurements

| Item | Build | View / scenario | Result |
|---|---|---|---|
| 11 (phase 4) | Release, T1000 | spawn still, scripted 40 s | four chunk passes 11.5 -> 1.7 ms; frame 28.2/32.1 -> 26.6/30.7 ms (GPU-bound session, stall relocated into `lighting`) |
| fluid 1+2+4 | Release, T1000 | bay, `VOXEL_TIME=70`, ~35 s | `fluidTick` avg 0.55-0.66 ms, typical max 11-16 ms, one ~41-48 ms loading outlier; queue 100-196k -> 8-12k; writes 51k -> 202k |
| **fly 2026-09-11** | Release, T1000, this merge | `perf-fly.json` (settle + land/water/far legs) | settled 8.8-10.5 ms (95-114 fps); streaming 18-39 ms (25-54 fps); `reflect` GPU 0 -> 8 ms on water; `worldPass` GPU to 5.6 ms; `raysPass` 1.5-2.3 ms submerged; pools to 512/128 MB; GC 17-25 gen0/s streaming, 1-3 settled |
