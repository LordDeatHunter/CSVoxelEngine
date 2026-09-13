# Performance TODO

The **Open (next round)** section is the live backlog. Everything from earlier
revisions of this file is **done** — per-item history lives in git
(`git log --oneline -- PERF_TODO.md FLUID_PERF.md`). The measurement protocol, the
last profile snapshot and the do-not-retry **Ruled out** list below are unchanged.

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

- In scripted runs `progUpdate` is just the log/close shell (~0.01 ms): `Drive`'s
  world calls time themselves since item 22, so the CPU sections sum to the frame
  instead of doubling it. (Pre-22 logs show `progUpdate` 5-25 ms containing all of
  `World.Update` — do not compare across that boundary.) In real play `progUpdate`
  is the input shell (~0.2-0.5 ms).
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
| Underwater | ~10-14 ms | 72-100 | `raysPass` GPU 1.5-2.3 ms while submerged (pre-half-res, see below) |
| Screenshot step | +7-18 ms | 1 fps that frame | PNG encode on the render thread (scripted runs only) |

The `reflect` row above predates reflection temporal reuse: the mirror is now
re-rendered at most every other frame, gated by
`WorldGenerationConfig.ReflectionReuseMaxDegrees` (1.0 °/frame),
`ReflectionReuseMaxMoveBlocks` (0.35), `ReflectionReuseMaxFovDegrees` (0.1) and
`ReflectionReuseMaxAspect` (0.001), plus a forced refresh on target recreation, on
the first eligible frame and after any skipped frame (never two skips in a row).
The thresholds are per-frame, so the gate refreshes more often as frame time grows —
read the halving claim off a settled 90-110 fps water-bay capture (stationary and
slow-pan), not a streaming window or the sprint-heavy fly average. Re-capture with
the fly in **How to measure** and compare the `reflect` `/gpuAvg~gpuMax` suffix
before pricing any further reflection work; the acceptance bar is visual — no
ghosting or swimming below the thresholds, and no flash on a drag-resize.

The `raysPass` row likewise predates the half-res underwater march and is pending
re-capture with the fly, as is the `cloudPass` figure in hitch #4 below (pre-R4). **No
`perf-fly` capture has been re-run since either change, so every number above this line is
still the old snapshot.** The post-change figures that do exist were taken with scripted
scenarios rather than the fly — R2's `rays-halfres.json` `raysPass` pair, and the R4 round's
`clouds-mirror.json` `cloudPass` recapture, which found no measurable change — and both
live in the git history of this file (`git log -p -- PERF_TODO.md`). They are different
scenarios: do not mix them with a `perf-fly` number. The R5/R7/R11 round *did* run `perf-fly`
under `VOXEL_DEBUG=1` on both trees, but quoted nothing from it (every row overlapped; that
round's write-up is in `git log -p -- PERF_TODO.md`), so it is not a recapture and the table
above is untouched by it. Nor did the C3/R8 round or this one run the fly, so the table has
now stood through three rounds of scenario-based captures.

Streaming-second breakdown (avg over the 1 s window, so peaks are higher):
`lod` up to 10.7, `chunkDirty` up to 6.3, `chunkApply` up to 5.8, `fluidTick`
up to 4.6, `fluidWake` up to 0.96, `lighting` CPU 3-7 (stall absorber, see traps).
Settled: `chunkApply`/`chunkDirty`/`lod`/`fluidTick` all <= 0.2 ms.

GC: streaming 17-25 gen0/s, 8-11 gen1/s, 0-2 gen2/s; settled 1-3 gen0/s, ~0 gen1.
Pool growth during first stream-in only: opaque 65536 -> 131072 -> 262144 units
(**512 MB**), translucent to 65536 (128 MB). No growth after settle.
`fluidTick` avg is 0-0.6 ms settled, with worst frames in the tens of ms at
stream-in (visible in the `~max` half since the console line grew max columns).

### Hitch ranking (what actually drops frames)

1. **Chunk streaming burst** — gen + mesh schedule + upload + LOD rebuild.
   100+ fps settled -> 25-55 fps streaming. `lod`/`chunkDirty`/`chunkApply` rows.
2. **Reflection + world pass when water is on screen** — `reflect` GPU 0 -> 8 ms,
   `worldPass` GPU up to 5.6 ms. Looking at the bay costs ~2x dry land; the
   `reflect` figure predates reflection temporal reuse and is pending re-capture.
3. **Geometry pool doubling mid-stream** (opaque to 512 MB) — one-time copy hitch.
4. **Underwater `raysPass`** (1.5-2.3 ms GPU, pre-half-res — pending re-capture) and
   **`cloudPass`** (0.7-1.5 ms when clouds in view — pre-R4, also pending a `perf-fly`
   re-capture). Clouds were already cut 63 % (item 15); remainder is steady, not spiky.
   R4 has since removed the nine-draws-per-sweep structure — each sweep is one instanced
   draw — but that change is *submission-side only* (the same nine copies are still
   rasterised twice per pass), and the recapture found **`cloudPass` unchanged within
   run-to-run spread in both halves**; the win is up to 34 fewer draw calls and up to 34
   fewer uniform updates a frame. That recapture is a different scenario
   (`clouds-mirror.json`'s shore hold; the R4 write-up is in `git log -p -- PERF_TODO.md`), so it
   is *not* a like-for-like correction of the 0.7-1.5 ms figure above.
5. **Fluid wake storm** at spawn over the sea — now bounded (queue 100-196k -> 8-12k),
   secondary to item 1.
6. **Screenshot encode** 7-18 ms — scripted runs only, but fix anyway (item 20).
7. **`uiPass`** 0.5-1 ms settled — the `X` overlay itself; subtract before comparing.

## Open (next round)

Not implemented yet, with one exception called out in its own bullet: **C5's code did land**
but its measurement did not support the *pool*, so the pool was declined (it ships at one worker)
and C5 stays here pointing at the different shape the mechanism calls for. Impact / risk / effort are estimates; validate each with the `perf-fly`
protocol above before trusting it. The entries below the passes repeat nothing from
**Ruled out** — overlaps are called out inline.

### GPU / render passes

- [ ] **R3 · Half-res scene copies for water — resolved and PARKED, not implemented** —
  `CopyScene` (`RenderManager.cs:1033-1043`), called with colour+depth at `:1718` and
  depth-only at `:1533`. The condition R2 left on this bullet was resolved against the code
  rather than implemented, and it settles three ways.
  **(a) The depth copy cannot move.** `CopyScene(bool colour)` copies depth
  *unconditionally* — only the colour half is gated by the parameter — and both call sites
  write the same `_sceneDepthTex`, which R2's depth-aware upsample pins at full res. So the
  `:1718` call keeps paying a full-res `DepthComponent24` `CopyTexSubImage2D` whatever R3
  does, and a second, half-res depth used only by the water pass is the same *additive* cost
  (an extra texture plus an extra reduction, on top of a full-res copy that still has to
  happen) that R2 already rejected on the rays side. Only the RGBA8 colour copy is left.
  **(b) The shader claim holds.** `test-fragment.glsl`'s refraction really is
  resolution-independent: `uv = gl_FragCoord.xy * uInvViewport` (`:267`) against
  `uInvViewport = 1/_viewportSize` (`RenderManager.cs:1466-1467`), bilinear `texture()` fetches
  (`:270-272`), a `uInvViewProj` unprojection (`:276`) — no `texelFetch` and no pixel indices
  anywhere in the file. The shader would need zero changes.
  **(c) But the ceiling is tiny and the change is not "sample less".** The colour copy is
  gated behind `if (_nearWaterVisible)` and sits inside a `waterPass` measured at **0.09 ms
  GPU total** (the compute-frustum-cull capture under **Ruled out** below) — a figure that
  also contains every translucent chunk draw and every LOD water sheet. That capture
  demonstrably *included* the colour copy: the same line reports `reflect 0.83 ms`, and
  `RenderReflection` early-returns unless `_nearWaterVisible`
  (`RenderManager.cs:1069-1070`) — the very flag that gates the copy — so near water was on
  screen when it was taken. 0.09 ms is therefore a real ceiling for the whole pass, not a
  figure that might have excluded the work. And `CopyTexSubImage2D` cannot downscale, so this
  is not a sampling change but new plumbing: an FBO, a texture, sizing and teardown across
  `CreateSceneCopy` / `DeleteSceneCopy` / `SetViewport` / `Dispose`, and a
  `glBlitFramebuffer` that still *reads* the full-res framebuffer and saves only the write
  side (arithmetic off the format, no timing claim: 3.69 MB -> 0.92 MB of RGBA8 writes per
  frame at 1280x720). Its cost is blurring everything seen through water — a perceptual
  change, which drags R2's full perceptual gate along with it.
  **Parked, not disproven:** effort M *plus* a perceptual gate, for an unmeasurable slice of
  an already-gated 0.09 ms pass. Revisit only if a capture ever isolates the colour copy as
  its own row. **Impact low · risk low-med · effort M + perceptual gate.**
- [ ] **R6 · Gate GPU timing queries behind the debug flag** — `BeginGpuPass` /
  `EndGpuPass` (`RenderManager.cs:274-290`) and `BeginGpuFrame` (`:247-265`) issue
  ~40-70 GL query calls a frame regardless of the overlay. Gating on `X` gives up
  "the first second after X already has data" (a deliberate property) — decide the
  trade. Different from the ruled-out *GL debug context*.
  **Impact low-med · risk low · effort S.**
- [ ] **R10 · Every LOD region owns a `Mesh`** — `LodRegion` creates a VAO + VBO +
  EBO per region and another set for its water sheet (`LodRegion.cs:110-128`), and
  each draw is a `BindVertexArray` + `DrawElements` behind three uniform sets:
  `RenderManager.RenderLod` (`RenderManager.cs:1805-1814`), the mirror's region loop
  (`:1171-1178`) and the water sheets (`:1745-1752`). Three radius-8 rings
  (`LodRegion.cs:39-44`) are several hundred regions, so a water frame walks them
  three times paying a bind, three uniforms and a draw per survivor, plus GL object
  create/delete churn as regions stream in and out (`LodManager.cs:219-243`). Give
  LOD its own `ChunkGeometryPool` + `GeometryAllocator` and push regions through
  `ChunkDrawBatch.Draw` (`ChunkDrawBatch.cs:202-216`) exactly as chunks already are,
  so each pass becomes one `glMultiDrawElementsIndirect`. The CPU cull stays where it
  is, so this is *not* the ruled-out *compute frustum cull*. **The R5 overlap is gone:**
  R5 has landed, so the texture binds the LOD passes issue already cost nothing when the
  bindings are unchanged: `BindWorldTextures()` at `RenderManager.cs:1723`
  (`RenderWorldTranslucent`, ahead of the water sheets) and `:1771` (`RenderLod`), plus the
  mirror LOD walk's single atlas `BindUnit` at `:1118`. R10 can no longer be credited with
  removing any of them, so what remains is R10's own case and only that: a VAO + VBO + EBO
  per region and another set per water sheet, a `BindVertexArray` + three uniform sets + a
  `DrawElements` per *surviving* region across three ring walks, and GL object create/delete
  churn as regions stream. Price it on those terms alone. **The hold on C5 is discharged** — C5 has
  been measured (see **Measurement** below) — **and the honest re-pricing is that nothing moved.**
  C5's pool *would* have re-priced R10 upward: at two LodGen workers the main-thread `lod`
  **average** rose (median 1.2 ms baseline -> 1.8 ms, disjoint, p = 0.0143; -> 2.4 ms at three, not
  disjoint) while the `lod` **worst** frame did not move in any direction (medians 32.3 / 31.7 /
  23.8 ms), and regions arriving 1.26-1.31x sooner would have exercised the per-region bind + three
  uniforms + draw and the GL create/delete churn harder per unit time. **But the pool was declined
  and ships at one worker**, so the shipped tree's arrival rate and `lod` row are the baseline ones
  and none of those figures describes it. Read them as what a future pool would cost, never as R10's
  case. They are in any case **descriptive, not a C5 result** — `lod` measures `LodManager.Update`,
  not the builders. R10 therefore stands exactly on its own pre-C5 terms and its impact rating is
  unchanged in both directions. The one durable number the capture handed R10 is the size of the set
  it walks: every tree converges to **591 live regions** (197 offsets x 3 levels, `LodRegion.cs`
  `RegionRadius = 8`), the same at every pool size, confirmed on three raised-wait controls in the
  visual gate below — so price the per-frame walk against 591 and not against the arrival rate.
  **Impact med-high · risk med · effort L.**

### CPU / streaming

- [ ] **C5 · Parallelise / cap LOD region builds — MEASURED, and it NEEDS A DIFFERENT SHAPE** —
  the R3 precedent: the work was built, pinned and priced, and the price does not support it as
  specified. The concurrency design landed and is sound (see **The C5 concurrency argument**
  below); what did not survive the capture is the premise that a *thread pool* is the right lever.
  **Gate 1 (is the builder even the limiter?) cleared decisively.** Arrivals are hard-capped by the
  main thread at `MaxLodUploadsPerFrame` x fps in `ApplyCompletedMeshes`; measured baseline
  throughput is **53-55 regions/s** against a drain ceiling of **~433 regions/s** (4 x the measured
  mean baseline fps of 108.2), i.e. **7.8-8.2x headroom**. The upload cap is *not* what holds LOD
  back, so more builder throughput was genuinely available to buy.
  **Gate 2 (does buying it cost more than it is worth?) failed at both pool sizes tried.** Three
  BelowNormal workers reach a fixed region count **1.31x** sooner, two reach it **1.26x** sooner
  (disjoint, counterbalanced, p = 0.0143 at each of three region counts) — but three workers slow
  chunk generation *disjointly* (chunks loaded at the 4th `perf` line, 11156-12485 baseline against
  8427-10253; seconds to the full 17077-chunk ring 5.0 -> 6.1 median) and cost **30 %** of burst
  frame rate (median 108.0 -> 75.3 fps); two workers clear the chunk-gen and GC guards but still
  cost **18 %** (110.3 -> 90.7 fps). Full tables under **Measurement**.
  **Why it is so far below linear, and this is the reusable finding:** a LOD build is *terrain
  generation in disguise*. `LodRegion.BuildMeshData` opens by prefetching **36 / 100 / 324** chunk
  columns at levels 0 / 1 / 2 through `Chunk.GetColumnHeights`, and a column that misses
  `Chunk.ColumnCache` is **generated on the spot** — measured medians 17.7 / 17.6 / 65.3 ms per
  build, with the cost tracking the column count, not the mesh (all three levels mesh the same
  32x32 cell grid). The four chunk-gen threads are doing the same work on the same cache at the
  same moment, so LodGen workers do not add throughput to an idle resource; they contend for the
  one the burst is already saturating. `ThreadPriority.BelowNormal` bounded *preemption* and did
  its job; it does not bound shared last-level cache, memory bandwidth or GC.
  **The decision, taken rather than deferred: the pool ships at one worker.**
  `WorldGenerationConfig.MaxConcurrentLodGenerations = 1`. Gate 2's branch 4 is the letter of the
  capture — neither 3 nor 2 clears it — and three in particular is the **one configuration that
  fails on every reading**, the only one that disjointly slows chunk generation, costing 30 % of
  burst fps for 4 % more LOD throughput than two workers buy. Two workers were the only defensible
  pool (they clear the chunk-gen and GC guards) and were declined too: 18 % of burst frame rate is a
  real cost to the exact phase this item targets, paid on every run, for 1.26x on a quantity that
  was never the limiter — the drain has 7.8-8.2x headroom, so regions were not arriving slowly
  because the builder was saturated. Trading frames for a faster horizon fill-in during the one
  phase that already drops frames is the wrong direction. **What stays in the tree is everything
  except the extra threads**: the TryAdd-gated `_building` claim with both rollbacks, the in-flight
  cap, the Idle/Throttled split, the `CompletedBuildCount` instrument, the raised retention ceilings
  and all of their facts. All are correct at any N and pinned at 3 by `LodBuildPoolTests` (whose
  stress width is deliberately fixed at 3 and NOT read from the shipped constant, or lowering the
  constant would have silently defused them), so raising the constant is a one-token experiment —
  but the capture is already done, so **do not re-propose "a bigger pool"**. The shape the mechanism
  points at instead is cutting the *work*: **D2** (`Chunk.ColumnCache`'s wholesale `Clear()` and its
  16384-entry cap against a level-2 ring of roughly 51k distinct columns), which is why this round
  promoted it.
  **Impact: measured, small and paid for · risk med · effort already spent; the remaining decision
  is S.**
- [ ] **C6 · `_playerPlaced.RemoveWhere` scans the whole set per unload** —
  `ChunkManager.UnloadDistantChunks` (`ChunkManager.cs:164-167`): O(unloads x
  placements). Per-chunk buckets or a spatial index.
  **Impact low-med · risk low · effort M.**

### Data structures / caching

- [ ] **D1 · Packed chunk keys** — `ChunkManager.Chunks` / `_dirtyChunks` /
  `_queuedChunkPositions` / `_generatingMeshes` are keyed by `Vector3`
  (`ChunkManager.cs:23,82-83,92`), paying a 3-float hash on every probe (neighbour
  checks, the dirty scan, `TryResolveNeighbors`, entity lookups). `FluidManager`
  already packs a `long` to avoid exactly this (`FluidManager.cs:32-35,898-899`).
  **Impact med · risk med · effort L** (broad but mechanical). `LodManager` is
  already keyed by integer tuples, so this is chunk-side only.
- [ ] **D2 · Cache eviction is a wholesale `Clear()`** — `Chunk.ColumnCache`
  (`Chunk.cs:646-648`) and `WormCarver.CellCache` (`WormCarver.cs:111-112`) drop
  everything at their cap, so a far teleport recomputes (part of the teleport
  hitch). LRU / generation-stamped eviction would keep reuse across a move.
  `LodRegion.ColumnCaps` (`LodRegion.cs:105`) also grows unbounded over a long
  editing session: since C9 it holds one dense `Chunk.Size * Chunk.Size` cap bucket
  per *edited chunk column*, never evicted — cheaper than the old per-fine-column
  entries once a column is heavily mined, more expensive for a single isolated
  block. **C5 raised the pressure here and made it measurable rather than hypothetical:** every
  LOD region build prefetches 36 / 100 / 324 chunk columns at levels 0 / 1 / 2 and generates on
  the spot any column missing from `ColumnCache`, so a level-2 ring (197 regions,
  `RegionRadius = 8`, 16 chunk columns across a 256-block region — a disc of radius 128 columns,
  on the order of **51k distinct columns**) sweeps roughly three times the cache's 16384-entry cap
  in one pass, and N LodGen builders multiply both the aggregate working set and the `Clear()`
  rate. C5's capture measured chunk generation slowing *disjointly* at three builders, which is
  the same channel seen from the chunk side. **Impact med · risk low-med · effort M.**
- [ ] **D5 · The geometry free list is a flat linearly-scanned list** —
  `GeometryAllocator` keeps free blocks in one offset-sorted `List<(int, int)>`
  (`GeometryAllocator.cs:24`); `TryAllocate` first-fits by walking it from the front
  (`:61-80`) and `Free` walks it again for the insertion point (`:95-107`) before an
  O(n) `List.Insert` (`:109`) and the coalesce (`:113-130`). Both run on the render
  thread from `ChunkGeometryPool.Upload` / `Free` (`ChunkGeometryPool.cs:102-128`,
  `:131`) — up to 32 uploads a frame (`WorldGenerationConfig.cs:16`) and one free per
  disposed chunk, which is what the teleport unload storm (17077 -> 4991 chunks)
  turns into a burst. A size-bucketed free list, or an offset-keyed sorted structure
  beside a largest-block index, makes both O(log n). **Measure first:**
  `FreeBlockCount` exists (`GeometryAllocator.cs:42`) but is never logged, so add it
  to the pool-growth log line and capture it mid-stream and just after a teleport
  before committing — the pool's own comment reports 93 % steady-state utilisation
  (`ChunkGeometryPool.cs:57-61`), which implies a short list and possibly no win at
  all. The class is GL-free with exhaustive headless coverage
  (`OpenGLTest.Tests/GeometryAllocatorTests.cs`), so the invariants are already
  pinned. Distinct from hitch #3 (*geometry pool doubling mid-stream*): that is the
  GL buffer reallocation and copy on the rare grow, this is the CPU-side
  first-fit/coalesce scan that runs on every upload and free whether or not the pool
  grows. **Impact unknown (measure) · risk low · effort M.**

### Not chasing (recorded so it is not re-proposed)

- Hierarchical / compute GPU cull — the ruled-out entry measures post-MDI
  cull+sort+fill at ~2%; revisit only if CPU cull+sort exceeds ~1 ms.
- Shader ALU cuts (the caustic Voronoi net, 8 point lights, 4-tap PCF) — ruled out;
  the half-res route is the allowed lever. R2 has landed on that route (the underwater
  rays march). R3 was the other half of it and has since been **repriced down to parked** —
  read its bullet above rather than treating it as a live lever: the rays composite pins the
  scene *depth* copy at full res, leaving only a colour copy inside a pass whose entire
  measured GPU cost is 0.09 ms, and downsampling that copy would cost a perceptual gate.
  The ruling itself is unchanged, and R2 respected it: the march's step count, sample
  count and per-sample formula are
  byte-identical and are now pinned by
  `WaterTests.RayMarchStaysUnchangedByTheHalfResRound`, so a future tweak cannot
  quietly re-open shader ALU.
- Worldgen noise reduction in `TerrainGenerator` / `NoiseCaves` — pure-function
  worldgen, changes visuals; not pursued.
- `FluidManager` — audited this round and nothing new found: cells are packed `long`
  keys, the per-tick chunk lookup is already cached, and the tick path allocates
  nothing. Adjacent to the ruled-out *fluid chunk-cache* entry but checked
  independently; `fluidTick` (4.6 ms streaming) has no structural lever left.
- `Shader` uniform caching (`Shader.cs:129-141`), `MeshBufferPool`, `ChunkDrawBatch`,
  `BlockCubeCache`, `ModelBaker`, `Vertex` — all already cached, pooled, batched or
  packed; re-read this round, nothing new. (`Shader` is also in the ruled-out
  omnibus line; recorded here so it is not re-opened from the other direction.)
- Dropping the **mirror's cloud depth pre-pass** — considered during R4 and deliberately
  declined, recorded here so it is not re-opened blind. The cloud mesh is closed and
  watertight (`CloudTests.RoundedMeshIsWatertight`), so a view ray crosses at least two
  surfaces, and the mirror's `CullFace(Front)` removes the far side of a *single* mass but
  not overlapping fillet pieces and not two separate masses stacked along one ray. Dropping
  it would darken clouds in the water but not in the sky — a mirror/sky disagreement. The
  main pass's pre-pass is the once-per-pixel blend guard and is not a candidate either. With
  R4's nine draws already collapsed to one instanced draw, what remains to win there is a
  single depth-only instanced draw on a sub-full-res target. The full R4 write-up is in
  `git log -p -- PERF_TODO.md`.
- `MalachiteGenerator.Stamp` (`MalachiteGenerator.cs:41-47`) — the 4096-cell loop
  looks like a noise hot spot but the `GetNoise` call is already gated on
  `blocks[i] != RockBlock` (`:46`), so noise only runs on stone. The remaining lever
  is loop order for cache locality; too thin to be worth a bit-identical-output risk.
- `ChunkGeometryPool.Grow` copying full capacity rather than a high-water mark —
  essentially retired now that `OpaqueInitialUnits` is 262144
  (`ChunkGeometryPool.cs:57-61`) and the fly shows no growth after settle. Revisit
  only if a profile shows a doubling in steady state.
- `TntManager`, `CampfireManager`, `QuernManager`, `DropManager`,
  `FallingBlockManager`, `TopplingTree`, `AmbienceManager`, `TextureAtlas`,
  `Camera` — walked in an earlier round; no allocation, redundancy or algorithmic
  finding above the noise floor. `Camera`'s per-frame collision scans are additional
  evidence for D1's impact rather than a separate entry. (R7 and R11 have since landed
  *in* `Camera` — the view-projection and view-frustum caches — but they were their own
  bullets, not findings from that walk, so this entry still stands.)

**Suggested first pass:** **C5** was taken, with **C12 + D3** as one cheap sweep alongside it. C12
and D3 landed clean and are gone from the backlog. **C5's code landed and its measurement did not
support it** — its bullet above is rewritten as *"needs a different shape"* and stays open, the R3
precedent. Everything in the round is output-preserving, so the gate looked for *zero* structural
change rather than for an acceptable difference. The blow-by-blow write-ups of the earlier R4,
R5/R7/R11 and C3/R8 rounds are in `git log -p -- PERF_TODO.md`; what is kept below is what the next
round needs, not the history.

### What landed

- **C5 · the LodGen pool.** `LodManager.Start` now spawns
  `WorldGenerationConfig.MaxConcurrentLodGenerations` dedicated `Thread`s named `LodGen-{i}`,
  `IsBackground = true`, `Priority = ThreadPriority.BelowNormal`, all running the same `WorkerLoop`;
  `Stop()` needed **no change**, because every worker polls the same volatile `_isRunning`. One
  `Log.WriteLine` of the pool size after the loop is the only way to confirm what a running build
  actually spawned — managed thread names are not observable from
  `Process.GetCurrentProcess().Threads`.
  **Why dedicated `Thread`s and not `ThreadPool` work items** (the `ChunkGenerationWorker` shape
  rather than the `ChunkManager.TryStartMeshRunner` shape): priority control *is* the point.
  `ThreadPool.UnsafeQueueUserWorkItem` cannot set a priority, so LOD builds would run at Normal, in
  direct CPU competition with the render thread during a burst whose profiled CPU rows (see the
  streaming-second breakdown above) already account for most of a 32.9 ms worst window.
  **The pool size is arithmetic, recorded in the constant's own comment** so it can be re-checked
  rather than re-guessed: 12 hardware threads on the reference machine − 1 render thread −
  `MaxConcurrentMeshGenerations` (4 ThreadPool mesh runners at Normal) −
  `MaxConcurrentBlockDataGenerations` (4 dedicated BelowNormal chunk-gen threads) = **3**.
  **That arithmetic is not what the measurement said to ship, and the measurement won.** The
  constant is **1** in the tree; the derivation above is kept in its comment as the thing the capture
  refuted, not as the thing to restore. See C5's bullet for the decision and what it cost to take.
  Everything else in this section is written for N workers and is correct at any N — the pool size is
  the only token that changed.
- **C5 · the claim protocol, the in-flight cap and the retention ceilings.** `_building` (a
  `ConcurrentDictionary` claim set) gated on the **result** of `TryAdd` inside both claim paths;
  `WorldGenerationConfig.MaxLodBuildsInFlight = 8` checked *before* any claim;
  `LodBuildThrottleDelayMs = 1`; `LodManager.MeshBuffers`' retention ceilings 4 -> **16**. The full
  argument is the next section — it is the round's reusable artefact and is the part worth keeping
  whatever happens to the pool size.
- **C5 · the throughput instrument.** `LodManager.CompletedBuildCount` (a **cumulative**, monotone
  `Interlocked` counter incremented in `EnqueueCompletedMesh`) reaches the `VOXEL_DEBUG=1` console
  line as `regions {built}/{live}` (`FrameProfiler.cs:288`), between `chunks N/V` and the `|` that
  begins the section list. It is deliberately **not** on the `X` overlay: those rows are fixed-width,
  and every gate screenshot passes `hideHud: true` while `perf-fly` bakes the HUD in, so keeping it
  off the overlay guarantees it cannot move a gate pixel. The token is `regions` and never `lod`,
  because `lod` is already a `ProfileSection` label and a scraper keyed on it would match the wrong
  field; `regions ([0-9]+)/([0-9]+)` is unambiguous. Counting at *enqueue* rather than at upload is
  deliberate: a build dropped out of range by `ApplyCompletedMeshes` still cost a worker its full
  build, so this measures **builder** throughput. **Keep this instrument** — it is the only way
  anything about LOD builder throughput is measurable at all, and `OpenGLTest.Tests/LodBuildCostProbe.cs`
  (a measurement harness, not a regression guard — it asserts no timing) is the cheapest way a future
  round re-prices the builder.
- **D3.** `ChunkDrawBatch.PassCount` deleted. The private `_passCount` field stays — it is the real
  `MultiDrawElementsIndirect` count. `grep -rn "PassCount" OpenGLTest OpenGLTest.Tests --include=*.cs`
  now returns nothing.
- **C12(b).** `Chunk.GenerateBlockData`'s scalar recount is now
  `_solidCount = BlockCount - _blocks.AsSpan().Count(BlockType.Air)` (`Chunk.cs:507`). Counting *air*
  and subtracting is the right shape rather than an accident: `BlockType.Air` is
  `public const byte Air = 0` and `_blocks` is `byte[]`, so this binds to the vectorised
  `MemoryExtensions.Count<byte>` with no cast, where a `!= Air` predicate would run one comparison
  per cell. `internal int SolidCount` (`Chunk.cs:59`) was added purely as a test seam — nothing in
  the engine reads it — because `_solidCount` and `_blocks` are private and `GetLocalBlock` was the
  only public block reader, so the new form could not otherwise be pinned against a scalar oracle.
  No measurement was taken and none should be invented.
- **C12(c).** `LodManager.RemoveRegion`'s `_pendingCover.Remove(region)` is now an index-tracked
  swap-remove mirroring `LodRegion.ListIndex`: `LodRegion.PendingCoverIndex` (`LodRegion.cs:80`,
  main-thread only), set in `AddRegion`, swapped in `RemoveRegion`, and **reset to -1 for every entry
  in `UpdateBuriedFlags` before the `Clear()`** — without that reset a region added before one pass
  and unloaded after a later one would swap-remove against a stale index and corrupt whichever region
  had since taken that slot. **Honest sizing, recorded in the code too: the measured impact is nil.**
  `_pendingCover` is drained in the same frame it is filled (`AddRegion` bumps `_regionSetVersion`,
  which forces an `UpdateBuriedFlags` pass, which `Clear()`s it unconditionally at the top), so its
  length is bounded by `MaxLodUploadsPerFrame = 4` and the linear `Remove` this replaces scanned at
  most four entries. It lands because it removes an O(n) a future change could make hot and because
  the invariant is now pinned, not because it saves time.
- **C12(a) · `ParticleRing<T>`, and the bullet's own draw-order prescription was wrong.** The bullet
  asked for "a head index plus a **modular walk in the renderers**". That is unnecessary *and*
  weaker, and the corrected argument is the reason no renderer was touched: `LeafParticleRenderer`,
  `BubbleParticleRenderer` and `FireParticleRenderer` each **re-derive their own back-to-front order
  every frame** (`Array.Sort` over a depth array, then indexed access), so source order is already
  discarded by three of the four consumers. `RenderManager.RenderBlockDebris`
  (`RenderManager.cs:2114`) is the one path where submission order reaches the GPU — it walks its
  list with a plain `foreach`. Its blend state is **conditional and must be stated as such**: debris
  always draws with depth writes **on** (`LeafParticleRenderer` restores `DepthMask(true)` at `:174`
  and `RenderBlockDebris` sets no blend state of its own), and it inherits `Blend` still enabled at
  `SrcAlpha`/`OneMinusSrcAlpha` — set at `LeafParticleRenderer.cs:159-160` and never disabled —
  **only on frames where leaves actually drew**, since that renderer returns early at `:103` when the
  count is zero. So submission order *is* visually load-bearing for overlapping semi-transparent
  debris cubes. All four systems are consumed purely as `IReadOnlyList<T>`, so one shared
  `internal sealed class ParticleRing<T> : IReadOnlyList<T>`
  (`OpenGLTest/src/Systems/ParticleRing.cs`, the `ChunkScanFrontier` precedent for a GL-free internal
  helper with its own headless facts) with **oldest-first indexing and an oldest-first enumerator**
  reproduces `List<T>`'s submission sequence exactly — a stronger guarantee than a modular walk, with
  **zero renderer changes**. `RemoveAt(0)` is O(1) (clear the head slot, advance `_head`); interior
  removal shifts the **shorter** side, never worse than `List<T>.RemoveAt`'s unconditional tail
  shift, which is what the four back-to-front reap loops do. `default!` is written into every vacated
  slot, which is load-bearing: all four element types are classes. The swap itself is one line per
  system — only the backing field's declared type changed (`LeafParticleSystem.cs:13`,
  `BubbleParticleSystem.cs:13`, `FireParticleSystem.cs:13`, `BlockDebrisSystem.cs:13`); every call
  site was already `Count` / indexer / `Add` / `RemoveAt`, and every `Particles` property kept its
  exact declared `IReadOnlyList<T>` type.
  **The honest finding: C12(a) buys essentially nothing in play.** The eviction path is only
  realistically reachable by **fire** particles (`FireEmberPerSecond` 36 + `FireSmokePerSecond` 8
  *per lit cell* against `FireParticleMax` 512); ambient leaves settle near 270 of 512, bubbles near
  4 of 128 (`BubblePerSecond` 1 against a 4 s lifetime) and debris near 40 of 512. It lands for the
  clean shared invariant, which is now pinned, not for milliseconds.

### The C5 concurrency argument — the reusable artefact of this round

This is the part to read before touching `LodManager`, its claim paths, or the LOD mesh pool, and it
is worth keeping **even if the pool is resized or backed out to one worker**: it documents what a
single builder was silently guaranteeing. The code's own doc comments carry it in full beside the
code; this is the summary.

**What one builder was guaranteeing, and what N had to restore.** The existing concurrent collections
do **not** cover it. Three things assumed a single builder.

**(1) Same-key ordering — the subtlest fact of the round, and an advisory mark does not close it.**
`MarkEditedColumn` re-dirties any region key that is still in `_queued`, and a key stays in `_queued`
for its **whole build**. So with N workers, between worker A's `_dirtyRegions.TryRemove` and any
registration made *after* the return, the main thread can re-add that key and worker B can take it:
two builds of one region run concurrently and apply in **completion** order, so an older snapshot can
land last — a stale LOD shell over a freshly mined column, the exact artefact `ColumnCaps` exists to
prevent and something a single builder made impossible. The fix is that the **result** of
`_building.TryAdd` is the exclusion gate, taken *inside* each claim path, not an advisory mark
checked beforehand. `TakeDirtyRegion` keeps a `_building.ContainsKey` **pre-filter** that `continue`s
*without* removing (so the dirty mark survives for a later spin), but the guarantee is the `TryAdd`
after the successful `TryRemove`; the loser **re-dirties** (`_dirtyRegions.TryAdd`), so a collision
costs a rebuild delay and never a lost edit.

**(2) The `_queued` rollback in `FindClosestNeededRegion` is mandatory, and a leaked claim is a
permanent horizon hole.** That path does `_queued.TryAdd` then `_building.TryAdd`, and on losing the
second it **must** `_queued.TryRemove` and continue the offset scan. The collision is reachable, not
theoretical: `ApplyCompletedMeshes`' out-of-range branch removes a key from `_queued` while another
worker may still hold it in `_building`, so winning `_queued` while losing `_building` can happen. A
`_queued` entry with nothing building is a region that **never builds and can never re-enter the
scan** — a hole in the horizon that persists for the session.

**(3) Queue depth, the rent/return contract, and the retention ceilings.** One worker cannot outrun 4
uploads a frame, so the completed queue never backed up; three can. Each queued `LodMeshData` pins
four rented lists. Arithmetic, recorded so it is not redone: `Vertex` is 32 bytes (`Pack = 1`), so a
build's four lists are 24576*32 + 36864*2 + 4096*32 + 6144*2 ≈ **1.00 MB**. The queue is therefore
bounded by `MaxLodBuildsInFlight = 8`, checked **before** any claim is taken — checking after would
either leak a `_queued` claim nothing would ever build or silently drop an already-removed dirty mark
— which bounds outstanding rents at 8 waiting + 3 building = **11** (≈ 11 MB). `MeshBufferPool`'s
ceilings therefore rose 4 -> **16**: below 11 a steady fraction of returns would be dropped and
re-rented as fresh ~786 KB LOH `List<Vertex>` allocations, inverting the win C8 landed. 16 retained
is ≈ 15.7 MB, never trimmed once the burst fills the pool; the lever if that idle retention ever
matters is `MaxLodBuildsInFlight`, not the ceiling. The **return** end is still main-thread-only and
unchanged — only the rent end went from one thread to N, and every `Rent`/`Return` takes the instance
lock.

**Why the release must follow `EnqueueCompletedMesh`.** `TryBuildOneRegion` releases the claim in a
`finally` that runs *after* the enqueue, so a rebuild of the same key can only enqueue afterwards:
**queue order is build order**, which is what makes last-applied == newest.

**Why a throttled worker must not sleep the idle poll.** A worker that hits the in-flight cap has
work available and is only waiting for the main thread to drain, so `WorkerLoop` distinguishes
`Idle` (sleep `GenerationDelayIdleMs` = 200 ms) from `Throttled` (sleep `LodBuildThrottleDelayMs`).
That constant is a **coarse** backoff whose real granularity is the OS timer — a 1 ms `Thread.Sleep`
on Windows lands anywhere from ~1 to ~15 ms unless the timer resolution was raised — and correctness
does not depend on it, because the next spin simply re-checks the cap. Do not describe it as "about a
frame".

**Why `FindClosestNeededRegion` deliberately gets NO shared frontier**, unlike C3's chunk scan: a full
scan is at most **591 probes** (197 offsets x 3 levels at `RegionRadius = 8`) against the chunk
lattice's 17077, and it runs **once per build** rather than once per probe. Concurrent callers racing
on `_queued.TryAdd` is precisely what fans workers onto distinct regions, so the race is the
mechanism, not a hazard to remove.

**What is already safe and needed nothing.** `MeshBufferPool` locks every `Rent`/`Return`;
`LodRegion.ColumnCaps` is `Volatile`-ordered with C5 named in its own comment; `Chunk.ColumnCache`
and `TreeGenerator`'s caches are `ConcurrentDictionary`s already shared with the four chunk-gen
threads; `ArrayPool<T>.Shared` is per-core. `LodRegion.BuildMeshData` itself is pure and reentrant —
its only statics are the readonly `Levels` array and `ColumnCaps`, and every scratch grid is
`ArrayPool`-rented and returned in a `finally`. That purity is C5's premise and it held: it is pinned
by `LodBuildPoolTests.RegionBuildsAreIdenticalWhicheverThreadRunsThem`. **Purity is not the same as
being parallelisable at a profit** — see the measurement.

**One real behaviour change was surfaced rather than papered over.**
`LodManagerTests.DirtyRegionsDrainExactlyOnce` had to be amended: it claimed 8 regions through
`FindClosestNeededRegion` and never released them, then expected the dirty drain to hand the same
keys straight back. Under the claim protocol a region still being built is deliberately skipped, so
the drain correctly returned nothing. The amendment releases the eight claims first (what the
production `finally` does the moment a build is enqueued) and asserts `BuildingClaimCount == 0`; the
fact's actual subject — every pending region handed out exactly once, then the set reports empty — is
untouched. The old expectation *was* the old single-builder assumption showing through.

**The narrowed priority claim, in exactly its narrowed form — the obvious stronger version is false.**
`BelowNormal` means the pool cannot **preempt** the Normal-priority render thread or the mesh
runners. It does **not** mean "no pool size can starve the main thread". Priority bounds preemption
only, and two channels reach the frame regardless of who allocated: **(1) GC** — each
`MeshBufferPool` miss falls through to a fresh ~786 KB LOH `List<Vertex>`, and Workstation concurrent
GC suspends the main thread whichever thread allocated; **(2) shared last-level cache and memory
bandwidth**, concretely through `Chunk.ColumnCache`, which N builders sweep harder and `Clear()` more
often. That is exactly why the capture instrumented **gc/s and chunks/s alongside regions/s**, as
controls read off the same `perf` lines rather than as arguments. The data vindicated the narrowing:
at three workers the *frame rate* fell 30 % while the main-thread `lod` row explained only a fraction
of it — priority did its job and the cost arrived through the channels priority does not close.

**What a passing concurrency test does and does not prove.** `LodBuildPoolTests`' four facts
demonstrate the absence of the races they happen to exercise, on one machine, one scheduler and one
deadline — not the absence of races. A genuinely racy variant that never interleaves badly here would
pass every one of them. They were run five times consecutively, green each time, and that is still
not proof. The safety claim rests on the invariants above; the tests are a check on it, not a
substitute. Both `LodManager`'s doc comments and the test class doc say so, and a future round must
not upgrade repeated green runs into evidence. What *does* give the facts teeth is that they were
**mutation-checked**: moving `FindClosestNeededRegion`'s `_queued` rollback onto the winning path
makes `ThreeWorkersBetweenThemClaimEveryRegionExactlyOnce` fail, and deleting the in-flight cap check
makes `TheInFlightCapHoldsUnderThreeProducers` fail with an observed queue depth of **29** against a
bound of 11. The single-threaded protocol facts in `LodManagerTests` were mutation-checked the same
way: turning the `TryAdd` gate back into an advisory `ContainsKey` fails
`TwoClaimsNeverYieldTheSameKeyWhileTheFirstIsHeld` and `ALostDirtyClaimIsReDirtiedNotDropped`, which
is the specific "simplification" those facts exist to catch.

**Standing obligation for whoever touches this next.** Do not weaken any of: the
`_building.ContainsKey` pre-filter in `TakeDirtyRegion`, the `_dirtyRegions.TryAdd` rollback, the
`_queued.TryRemove` rollback in `FindClosestNeededRegion`, the cap-before-claim ordering in
`TryBuildOneRegion`, or the body of `ReleaseRegion`. Each is killed by a named fact.
`LodBuildPoolTests.cs` is also the **only** file in the tree that can execute the `TryAdd` loser
branches at all, and `LodManagerTests.ALostDirtyClaimIsReDirtiedNotDropped` points at it by name in
its doc comment — if it is renamed, that reference must move with it.

### Measurement: C5 is a real but small speedup that is paid for twice over

**The main-thread `lod` row is NOT a C5 result and must never be quoted as one.** `lod` times
`LodManager.Update` — the upload and cover work — while the builders run off-thread. C5 is
*expected* to raise how **often** `MaxLodUploadsPerFrame` is reached, because more completed builds
means more frames sitting at the cap; that is not a regression to "fix" by moving the cap (lowering
it slows convergence, raising it adds main-thread GL work). A naive reading of a future `perf-fly`
capture would score this round as a loss for exactly that reason.

**The instrument.** LOD builds are covered by no profiled section and no log site reported region
counts anywhere in the tree, so the round added one (see **What landed**): the cumulative
`regions {built}/{live}` field. Unlike `chunks {N}/{V}`, which is a **net** counter with the purge
bias C3's rule documents, this one is cumulative and monotone — no bias, no plateau — which is why a
fixed-duration window is legitimate here where C3 had to chase convergence.

**Capture rule, as applied, so it can be repeated verbatim.**

- **Four trees**, all built `~/.dotnet/dotnet.exe build OpenGLTest.sln -c Release` (the system
  `dotnet` has no SDK), all run `--no-build` from their own `OpenGLTest/` directory:
  **A** = `git archive HEAD | tar -x` into the scratchpad, **pristine**, the *pixel* baseline, never
  run in this capture; **B** = the same export plus the one-file instrument patch, the *throughput*
  baseline at one builder; **C** = the working tree *as captured*, at three builders (the shipped
  constant was set back to 1 after the capture, on this evidence — see C5's bullet); **D** = a copy
  of C with
  `MaxConcurrentLodGenerations = 2` and nothing else (verified by `diff -r`: one file, one
  character).
- **Why the throughput baseline carries the instrument patch while the pixel baseline stays
  pristine**, stated rather than glossed: a pristine HEAD **cannot report the quantity at all**. The
  patch is one `Interlocked.Increment` on a worker plus two numbers appended to a console line that
  no `hideHud: true` shot renders, so it cannot move a gate pixel — and tree A exists precisely so
  the pixel gate never has to rely on that argument.
- **Run**: `VOXEL_DEBUG=1 VOXEL_TIME=10 VOXEL_SCRIPT=visual-examples/lod-horizon.json`. One session,
  strictly serial, never concurrent; **one cold run discarded** at the head of each capture; then
  4 + 4 **counterbalanced** — `b1 w1 b2 w2 w3 b3 w4 b4` for N = 3 and `b5 d1 b6 d2 d3 b7 d4 b8` for
  N = 2, two pairs baseline-first and two work-first in each.
- **Window**: leg 1 = everything before the script's second teleport, detected on the first
  `[visual] N/M screenshot ->` line and **never** on `[visual] saved`, which prints several frames
  after the shutter. Drop the run's first `perf` line (it covers the world-load burst), take the next
  5, i.e. **4 intervals**.
- **Quantisation is removed, not bounded.** A `perf` line publishes on the first frame at or past 1 s
  and `Log.WriteLine` adds no timestamp, so each interval is 1 s **plus one frame time**. That bias
  does **not** cancel across trees here, because frame time differs sharply between them (baseline
  ~9 ms, N = 3 ~13-17 ms), so every interval's true length is taken as `1 + frame_ms/1000` off the
  `frame` field on the same line. Every "seconds to N" figure below is corrected; every "intervals
  to N" figure is the uncorrected form of the same thing.
- **Convergence guard**: the interval at which a run's per-interval increment first falls below 10 %
  of that run's window peak. **No run of any tree converged inside the window** (peaks 72-119,
  smallest in-window increment 15), so it never needed shortening.
- **Four quantities off the same lines**, so the last three are controls and not separate
  experiments: LodGen throughput (`regions`), the **headroom check** (mean fps x
  `MaxLodUploadsPerFrame` = the drain ceiling), the **chunk-gen guard** (`chunks N/V`, C3's net-counter
  rule carried over with its caveat), and the **GC guard** (`gc g0/g1/g2 per s` — the channel
  `BelowNormal` priority does not close). **Mean fps in the window was added as a fifth guard in the
  pre-registration**, because "the pool does not starve the main thread during the burst" is a claim
  C5's own design makes and nothing else in the list tests it.

**Decision gate 1 — is the builder even the limiter? CLEARED, decisively.** Baseline throughput
**53-55 regions/s** against a drain ceiling of **433 regions/s** (measured baseline mean fps 108.2 x
`MaxLodUploadsPerFrame` 4): **headroom 7.8-8.2x**. A headless probe (`LodBuildCostProbe`) had put a
single builder's ceiling at ~30 regions/s equal-weight (medians 17.7 / 17.6 / 65.3 ms per build at
levels 0 / 1 / 2) against an assumed 120-200/s ceiling; the measured fps made the real headroom
*wider* than the probe's estimate, not narrower.

**Decision gate 2 — by the letter, branch 4: neither pool size clears, so no headline win is quoted.**
The letter and the intent disagree and both are recorded rather than one being quietly picked.

**The gate's primary statistic is confounded on this scenario, and that is a finding in its own
right.** `(regions_end − regions_start) / window seconds` over a **fixed-index** window compares
*different work*: at the window's opening line the baseline has built 39-47 regions, N = 3 has built
103-120 and N = 2 81-100, so the faster tree enters the window 2-2.6x further along a
**closest-first** fill whose per-region cost rises steeply with distance (36 / 100 / 324 prefetched
columns at levels 0 / 1 / 2). An upward result is unreachable by construction. The replacement —
**wall-clock seconds to reach a fixed cumulative region count**, same work, different time — was
written down in the scratchpad **before tree D was launched even once**, then applied identically to
the already-collected N = 3 data, so it cannot be read as chosen to suit a result.

**Result, N = 3 (tree C) against tree B:**

| Quantity | Baseline (B) | N = 3 (C) | Disjoint? | exact 1-sided p |
|---|---|---|---|---|
| regions/s in window (gate primary, **confounded**) | 55.9, 54.6, 56.4, 50.3 — med 55.3 | 53.5, 50.0, 50.4, 51.5 — med 50.9 | no | 0.0714 down |
| **seconds to 400 regions** (lower better) | 9.5, 9.8, 9.6, 10.4 — med **9.7** | 7.4, 7.4, 8.1, 7.4 — med **7.4** | **yes, faster** | **0.0143** |
| seconds to 250 / 490 regions | med 5.9 / 16.6 | med 4.2 / 12.2 | yes, faster | 0.0143 each |
| chunks/s in window (**censored**, see trap) | 2829.8-3018.2 | 2953.0-3133.8 | no | 0.0714 up |
| **chunks loaded at `perf` line 4** (uncensored) | 12485, 11311, 11235, 11156 | 10253, 10242, 8427, 9617 | **yes, HARMFUL** | **0.0143** |
| **seconds to the full 17077-chunk ring** | med 5.0 | med 6.1 | **yes, HARMFUL** | **0.0143** |
| gc sum/s | 20.2-22.0 — med 21.0 | 21.2-23.2 — med 22.9 | no | 0.0429 up |
| **mean fps in window** | 110.0, 106.0, 110.8, 106.0 — med **108.0** | 79.0, 75.8, 58.8, 74.8 — med **75.3** | **yes, HARMFUL** | **0.0143** |
| `lod` avg ms — **descriptive, not a C5 result** | 1.1, 1.3, 1.0, 2.5 — med 1.2 | 2.0, 2.2, 5.1, 2.7 — med 2.4 | no | 0.0429 up |
| `lod` worst ms — **descriptive, not a C5 result** | 16.9-78.1 — med 32.3 | 20.1-51.5 — med 31.7 | no | 0.6286 up |

N = 3 fails twice: the window statistic is not disjoint upward, **and** the chunk-gen guard is
disjoint in the harmful direction. Gate 2's third branch therefore fired — rebuild at two workers and
repeat the whole capture against a fresh four baseline runs.

**Result, N = 2 (tree D) against tree B runs 5-8:**

| Quantity | Baseline (B) | N = 2 (D) | Disjoint? | exact 1-sided p |
|---|---|---|---|---|
| regions/s in window (gate primary, **confounded**) | 48.9-56.4 — med 52.6 | 50.5-56.5 — med 53.6 | no | 0.3571 up |
| **seconds to 400 regions** (lower better) | 11.2, 10.0, 10.0, 9.3 — med **10.0** | 7.9, 7.6, 8.0, 8.1 — med **8.0** | **yes, faster** | **0.0143** |
| seconds to 250 / 490 regions | med 6.0 / 16.6 | med 4.9 / 13.1 | yes, faster | 0.0143 each |
| **chunks loaded at `perf` line 4** | 10342-12883 — med 11328 | 10580-12236 — med 10967 | **no** — guard cleared | 0.3571 down |
| **seconds to the full 17077-chunk ring** | med 6.1 | med 6.1 | **no** — guard cleared | 0.7857 |
| **gc sum/s** | 19.8-22.0 — med 20.8 | 21.2-22.0 — med 21.8 | **no** (ranges touch at 22.0) | 0.1000 up |
| **mean fps in window** | 101.2, 111.0, 110.0, 110.6 — med **110.3** | 85.0, 93.4, 92.0, 89.4 — med **90.7** | **yes, HARMFUL** | **0.0143** |
| `lod` avg ms — **descriptive, not a C5 result** | 1.0-1.3 — med 1.2 | 1.5-1.9 — med 1.8 | yes, up | 0.0143 |
| `lod` worst ms — **descriptive, not a C5 result** | 11.0-40.7 — med 30.4 | 18.5-42.0 — med 23.8 | no | 0.5571 up |

| | seconds to 400 regions (median) | vs its own baseline | window fps (median) |
|---|---|---|---|
| 1 builder (baseline B) | 9.73 / 10.02 | — | 108.0 / 110.3 |
| N = 3 | 7.42 | **1.31x** | 75.3 (**−30 %**) |
| N = 2 | 7.97 | **1.26x** | 90.7 (**−18 %**) |

`p = 1/70 = 0.0143` is the **floor** at n = 4 + 4, and every disjoint result above sits on it. All
four pairs separate in the same direction in both captures whichever tree ran first, which is what
the counterbalancing was for. **Cross-session control:** eight runs of the identical tree B binary an
hour apart gave seconds-to-400 of 9.73 vs 10.02 and window fps 108.0 vs 110.3 — stable enough that
the two captures can be read against each other, not only each against its own baseline.

**By the letter of gate 2, branch 4 applies and no headline C5 win is quoted. By its intent — did
throughput rise without harming chunk generation or GC? — N = 2 passes**, failing only the statistic
shown to be confounded plus the fps guard added in the pre-registration. **N = 3 fails on every
reading.** On that evidence the shipped constant was set to **1** — both pool sizes declined, the
claim protocol kept; the reasoning and what it cost are in C5's bullet above.

**Mechanistically, why three builders buy 1.31x and not 3x** — the reusable finding, and the reason
the answer is D2 rather than a bigger pool: a LOD build is **terrain generation in disguise**.
`BuildMeshData` prefetches 36 / 100 / 324 chunk columns and **generates on the spot** any that miss
`Chunk.ColumnCache` — the same work, on the same cache, that the four chunk-gen threads are
saturating. LodGen workers do not add throughput to an idle resource. The frame-rate cost has two
parts and only one is the expected one: the `lod` rise accounts for ~0.6 ms of the ~1.9 ms
frame-time increase at N = 2, so **most of the fps cost is contention, not upload work**.

**Two measurement traps found here that will bite the next round if ignored.**

- **Never reuse C3's net loaded-chunk rule on a window in which the chunk ring saturates.** Every run
  reaches 17077 inside this window, so the delta is `17077 − start` and whichever tree started lower
  shows the larger delta: the censored form reports N = 3 loading chunks *faster* (p_up 0.0714) while
  the uncensored form — chunks loaded at `perf` line 4, the last line at which no run of either tree
  has saturated — shows it **disjointly slower**. It reads backwards.
- **Never use a fixed-index window for any quantity produced by a closest-first fill whose per-item
  cost rises with distance.** The faster tree enters the window further along and is doing more
  expensive work, so the statistic cannot separate the trees in the favourable direction however
  large the real effect is. Use time-to-a-fixed-count instead.

No `perf-fly` capture was run this round, so the profile table at the top of this file is untouched by
it, exactly as it was untouched by the C3/R8 and R5/R7/R11 rounds.

### Tests

The suite went **464 -> 485** with the same two known failures
(`BlockMovementTests.LeavesDeclareThreeQuarterMovementSpeed`,
`FallingBlockTests.SevenTowerChainRebuildsFullyWithNoDrops`), which were re-verified on HEAD before
any code was written so a pre-existing flake could not be attributed to this round. Added: +7
`LodManagerTests` (the cumulative counter, the five claim-protocol facts, the `_pendingCover`
swap-remove), +1 `LodBuildCostProbe`, +4 `LodBuildPoolTests`, +1 `MeshingTests`, +4
`ParticleRingTests`, +4 particle eviction-order facts (`LeafParticleTests`, `BlockDebrisTests`,
`BubbleParticleTests` and a new `FireParticleTests`, which is the first fire-particle coverage in the
tree). `LodManagerTests.DirtyRegionsDrainExactlyOnce` was **amended**, not added — see the behaviour
change recorded in the concurrency argument above.

**Every new fact was mutation-checked, and two of the mutations are worth keeping as evidence.**
Turning `ParticleRing.RemoveAt(0)` into a **swap-remove** — the exact alternative C12(a)'s bullet
warned against — fails all four new eviction-order facts while the three pre-existing count-only
tests (`BurstRespectsMaxParticles`, `BurstRespectsMax`, `SystemRespectsMaxParticles`) **all still
pass**: direct evidence that the existing tests would have accepted a silently reordered draw
sequence, and the reason the new facts were needed. Deleting `TryBuildOneRegion`'s in-flight cap
check fails `TheInFlightCapHoldsUnderThreeProducers` with an observed queue depth of **29** against a
bound of 11.

`LodBuildCostProbe.cs` is a **measurement harness, not a regression guard** — it asserts no timing,
only that no build came back empty, because a timing assertion on a shared CI-less dev machine is a
flake generator. Keep it: it is the cheapest way to re-price the LOD builder. Keep
`LodBuildPoolTests.cs` too — it is the only file that can execute the claim paths' `TryAdd` loser
branches at all.

### The visual gate: zero structural change, and the gate saw C5 for the first time

Everything landed this round is output-preserving, so the gate looked for **zero** structural change.
Four pose-stable instruments at 4 + 4 counterbalanced runs each (`rays-halfres` `VOXEL_TIME` 180,
`clouds-mirror` 60, `lod-horizon` 10, `entity-binds` 60), against the **pristine** `git archive HEAD`
export (tree A), the working tree never stashed or mutated; both the `area%` and the **clustered**
statistic (luma difference >= 24 **and** >= 5 of the 25 pixels in the same 5x5 box), judged cross-tree
against the same-build distribution with an exact one-sided Mann-Whitney U.

**All four instruments pass, and every flag was run to ground rather than waved through.**
`rays-halfres` passes all 10 shots on the clustered statistic, with one `area%` flag whose largest
difference over all 28 pairs is 18 levels — not one pixel reaches the structural threshold of 24, the
animation-phase false positive this file already documents. `lod-horizon` passes all 4 shots with all
eight runs reporting identical chunk (17077) and visible counts, and on every shot the *most*-similar
pair of all 28 is a **cross** pair while the least-similar are same-build baseline pairs — the C3
signature. `clouds-mirror` and `entity-binds` flagged, and masking put every flagged cluster on the
distant LOD band.

**The direction was measured, not eyeballed, and it is the first round where the pixel gate could see
the perf change at all.** Over all 16 cross pairs of `entity-binds/13-bind_still_1`, **2906-6707 px**
go baseline-sky -> work-terrain while only **3-92 px** go the other way — a 100-1000x asymmetry in the
same direction every time. Sky-pixel totals: baseline 126023/126023/126033/126031 (spread 10 px),
work 118340/122803/118350/118336. One work run sits between the groups and shows the same asymmetry
against its own siblings (3688 px vs 36), so **the axis is traversed within one tree**: it is arrival
time, not rendering. That is the gate independently seeing what the capture measured — regions
arriving 1.26-1.31x sooner.

**The gate was run against the three-builder tree, and the tree then shipped at one.** That is the
safe direction and the reason it was not re-run: the only cross-tree difference the gate could see
was arrival time, and setting `MaxConcurrentLodGenerations` back to 1 removes exactly that
difference, leaving the shipped tree building LOD regions on one BelowNormal thread as HEAD did.
Everything else this round landed is structurally output-preserving and was gated in the same pass.
If a future round raises the constant again, this is the comparison to re-run, not to re-cite.

**The C5 gate rule, written down because a conclusion is quoted off it:** *C5 changes only **when** a
LOD region arrives, never what it contains, so a region arriving a frame earlier or later is not a
failure; a region that never arrives, or LOD terrain that disagrees with the full-detail chunks at
the seam, is.* The discriminator is same-tree repeatability, and the control is C3's: **raise the
scenario's opening or leg `wait` and re-run both trees** from a scratchpad copy passed by absolute
path, so nothing is written into either tree. Three such controls were run — `clouds-mirror` opening
wait 8 -> 55 s, `lod-horizon` leg waits 40/35/35/35 -> 60/45/45/45, `entity-binds` settle 4 -> 50 s —
and all of them converge every tree to **`chunks 17077` and `regions 591/591`** at every shot, 591
being exactly the 197 offsets x 3 levels the claim scan can hand out. On a settled world both trees
hold the **identical complete set**; nothing is ever missing on either, one builder simply takes
longer. With the waits raised, the pixel figures collapse to the same-build noise floor
(`entity-binds` cross figures ~0.75 % -> 0.02-0.19 %, most-similar pair cross on every shot;
`clouds-mirror` p 0.7357 on both shots) and a same-build baseline pair becomes the least similar.

**`particles.json` is checked in, and it is EXCLUDED from the statistical gate.** None of the four
pose-stable scenarios frames any particle system, so C12(a) needed its own. It has three legs, each
pose found by probing and **confirmed by opening the frames**: leaves (a dense forest stand, looking
up into the canopy underside, a short `mine`), block debris (a bright hillside with the `mine` split
2.0 + 0.4 s so the shutter lands shortly *after* a break rather than mid-break, which is what
produces an open-air scatter instead of debris down a tunnel), and bubbles (`water.json`'s verified
underwater point, a 6 s wait). Every shot passes `hideHud: true`.
**Why it is excluded**, and this must not be "improved" into a pixel instrument: particle spawns are
RNG-driven, `wait` ticks physics once per **rendered** frame so particle height is a function of
frame rate — exactly what a perf round changes — and the eviction path C12(a) touches is unreachable
for three of the four systems at these densities, so the scenario cannot exercise the changed line
for them at all. Folding it into a Mann-Whitney gate would be vacuous in one direction and a
false-alarm generator in the other. Its job is a **presence and draw-order check judged by eye** —
systems present, plausible positions, blended correctly, no missing pass, debris cubes not obviously
re-sorted — while the headless order facts are what pin eviction order. It was run twice end to end
with all six shots opened.
**There is no fire leg, and the reason is structural rather than a missing pose.** The only ignition
site is `CampfireManager.Ignite`, reached only from `World.TryUseSelected`'s branch gated on
`Inventory.SelectedToolInstance > 0`. A firestarter is a **tool**, but `VisualDirector`'s `give`
equips through `Inventory.EquipStackable`, which writes a **non-tool** slot with instance 0, so the
guard fails and ignition is never called — verified headlessly against a real `Inventory` and
confirmed live (a max-count branch pile struck 14 times over 22 s never lit). **Fire particles have
no on-screen coverage**; they are covered by `FireParticleTests` only. A future round that wants fire
on screen needs a `giveTool` op on the director (one call to `Inventory.EquipTool` with a new
instance); that is a director change and belongs to whichever round wants it.

### Carried forward from earlier rounds — still true, still load-bearing

**On C3's chunk-scan frontier — the standing obligation, and the only part of that round a future
change can break.** *If a future change removes a position from `ChunkManager.Chunks` or from
`_queuedChunkPositions` anywhere other than `UnloadDistantChunks` without bumping
`ChunkRemovalVersion`, the frontier can starve that chunk.* Nothing structurally prevents it:
`Chunks` is a **public** `ConcurrentDictionary` and `OpenGLTest.Tests/MeshingTests.cs` already removes
through it. The single-site property is a convention, not an enforced invariant. Two related rules
travel with it: the version bump is published **after** the unload loop and must never be moved up
(a loaded chunk lives in **both** dictionaries, so a position becomes needed at the
`_queuedChunkPositions` removal, not at the `Chunks` one; a bump next to the latter would announce
the transition before it happened and permanently skip the position), and the bump is gated on
`removed > 0` (`UnloadDistantChunks` also runs every `UnloadScanFallbackFrames` = 120 frames, so an
ungated bump would restart four full scans roughly twice a second on a settled world — the exact cost
C3 removed). Three facts guard all of it:
`ChunkScanFrontierTests.FrontierTrustsThePrefixUntilTheRemovalVersionMoves`,
`ChunkManagerTests.UnloadBumpsChunkRemovalVersionOnlyWhenAChunkLeaves` (whose final assertion is
`NeedsChunk(Vector3.Zero)` *after* the bump is observable, so it fails if the increment moves ahead of
the `_queuedChunkPositions` removal), and
`ChunkScanFrontierTests.TheScanCostIsAmortisedNotPerCall`. **C6 shares `UnloadDistantChunks` with
this counter — re-read this paragraph before touching that method.** The full invariant write-up is in
`git log -p -- PERF_TODO.md` and, better, in the doc comments on `ChunkScanFrontier` itself.

**On R8's bit key, so nobody "simplifies" it to `==`.** `UITextCache` compares float keys
**bit-for-bit** through `BitConverter.SingleToInt32Bits`, following R7's `ViewProjKey` precedent,
because a cache hit must *prove* the text would be identical: `$"{-0f:0.0}"` renders `"-0.0"` while
`$"{0f:0.0}"` renders `"0.0"`, and `-0f == 0f` is true, so an `==` key would put the wrong sign on the
coordinate line whenever the player stands just on the negative side of an axis — observed on screen,
not only headlessly. `UITextCacheTests.NegativeZeroDoesNotReuseThePositiveZeroText` is the fact.
One BCL detail worth keeping: **`int.ToString()` returns a shared cached instance for values 0..299 on
.NET 9**, so any allocation-shaped fact using `ReferenceEquals` on a formatted small integer passes
vacuously; `SlotCountsAreCachedPerSlot` uses counts above 299 for exactly that reason.

**On the R5 program and texture-unit caches**, whose design a future reader is most likely to try to
"simplify": `RenderManager`'s `BindUnit(unit, target, tex)` **stays eager** — it always makes the unit
active and skips only a redundant `glBindTexture` — because three things bind on *whatever unit is
live* (`CopyScene`'s `glCopyTexSubImage2D`, the three `Create*Target` methods, and
`TextureLoader.Upload`, reached lazily from `ItemMeshCache` inside the entity pass). `BindLazy` skips
the select too and is used **only** inside `BindWorldTextures`, where the proof is local. The
canonical audit of what can change units 0-4 behind the cache's back is the doc comment on
`InvalidateTextureUnits()` — read it beside the code, not a copy here. The one check that proves the
program cache needs no invalidation list is
`grep -rn "\.UseProgram[(]" OpenGLTest/src --include=*.cs`, which must return exactly one line inside
`Shader.Use()`; **use that exact spelling**, since plainer forms also match doc comments (including
one that quotes the grep and therefore matches itself). The pre-existing gap where `RenderShadows`
unbinds unit 1 and leaves it unbound until the next `BindWorldTextures` is **reproduced, not fixed**;
an implementer who "corrects" it breaks output preservation. And the negative-control lesson:
deleting the UI pass's invalidation entry alone gave **zero** mismatches, as did deleting
`BeginFrame`'s hard reset alone, as did deleting *both* on a daytime run — only both gone **and** a
night run (`VOXEL_TIME=300`) fired the check, because `RenderSun` early-returns before its unit-0
bind at night. **A single-entry deletion is not a sufficient control, and a daytime-only sweep misses
a real state divide.**

**On R7/R11**: `Camera.GetViewProjection()` and `GetViewFrustum()` share one `EnsureViewCache()` keyed
on a `ViewProjKey` compared bit-for-bit (the record's generated equality is deliberately unused,
because `float.Equals` calls `+0f`/`-0f` equal and `NaN`/`NaN` equal, which would break the proof).
`GetViewMatrix()` / `GetProjectionMatrix()` are uncached **on purpose**: they are the correct answer
for the standalone mirror projections and they are the tests' oracle.
`KeyHoldsExactlyTheMatrixInputs` makes editing the key a conscious act; what no headless fact can
catch is a *new* `Camera` field wired into `GetViewMatrix` tomorrow with no matching key entry.

**Gate traps that still apply to any scenario-based comparison:**

- **`wait` ticks physics once per *rendered* frame.** `VisualDirector`'s `wait` calls
  `world.Update(deltaTime, _keys)` once per rendered frame and accumulates real `deltaTime`, so the
  number of physics steps for a given simulated duration is **frame-rate dependent**. Any round that
  makes the engine faster lands at a slightly different camera depth and streaming state, and a naive
  pixel gate reads that as a regression.
- **Chunks and LOD regions fade in on the wall clock** (`RenderManager.cs:1150-1151`, `:1810-1812`,
  `MAX_LIFETIME` 0.5 s), so a region caught mid-fade differs by up to ~208 levels between runs of the
  **same** build. `clouds-mirror.json`'s `14-sky_level` and `16-sky_closeup` frame such a region and
  their full-frame p99 flips between 1 and 122 depending on which side of the percentile it lands —
  **p99 is not a usable whole-frame statistic on those two shots.**
- **The floor is not uniform across the frame.** In `clouds-mirror.json`'s mirror shots the sky region
  is an essentially exact instrument while the water region floors in the *hundreds*, because the
  wave normal is animated from wall-clock `uTime`. Compare per region (fix the horizon row once by
  eye on one baseline image and reuse that crop for every run of the shot) and treat the water region
  as a gross-breakage check rather than a measurement.
- **Session effects exceed the quantity being measured.** The same pristine binary read `cloudPass`
  gpuAvg 1.460, 1.501 and 1.551 in three different sessions. Always re-run the baseline in the same
  session, interleaved run for run, never against stored captures from an earlier session; and give
  the baseline a **cold discarded first run**, since a freshly built binary streams more slowly on its
  first run, which moves both the loaded chunk set and the wall clock at every shot.
- **Budget at least 5 runs per scenario from the start** — and, per this round's finding above, split
  them evenly between the trees. One same-build pair against one cross-tree pair is not a gate.
- `entity-binds.json`'s `19-drops` is a **correct-texture eyeball check, not a pixel instrument**:
  drops bob and settle on run-dependent physics and land in visibly different positions between two
  runs of the *same* build. Its shot numbering is 07/11/12/13/14/19 because each `give` needs a
  grounded `wait` after it — `World.Update` is what refreshes the held id from the inventory, so a
  `give` followed straight by a `screenshot` renders the *previous* held item.
- `clouds.json` is a sky-side regression check and **not** a measuring instrument (every shot but the
  first follows a mid-air teleport plus a `wait`, and `wait` ticks the world, so the camera free-falls
  before the shutter; `clouds_up` alone is pose-stable). `water.json`'s four underwater shots are
  likewise not usable (a 15-25 level same-build floor). `perf-fly.json`'s `screenshot` ops do **not**
  pass `hideHud`, so its shots bake in the live profiler HUD across roughly x 0..570 — useful for
  reading HUD text, useless as a pixel instrument unless those columns are masked.
- Any frame-to-frame statistic must be normalised for wall-clock **sun drift** before it is compared
  across builds, or it reads backwards.
- **`lod` measures only the main-thread `LodManager.Update`** — the upload and cover work — while
  `LodRegion.BuildMeshData` runs on the **`MaxConcurrentLodGenerations` BelowNormal `LodGen-*`
  threads**. Size any further LOD *builder* work with a throughput capture off the
  `regions {built}/{live}` console field (`FrameProfiler.cs:288`, cumulative builds / live regions),
  never off a `lod` row. Note that C5 is *expected* to raise the `lod` average, because more
  completed builds means more frames sitting at `MaxLodUploadsPerFrame`.
- **New: `clouds-mirror`'s five `mirror_*` shots need the per-region crop too, not just the two
  `sky_*` shots.** They pass full-frame while flagging on the sky half (rows 0:335) with the water
  half (335:720) passing — the water half's animated-normal noise buries the sky half's signal. A
  full-frame pass on those shots is not evidence that the far-terrain band agrees.
- **New: `entity-binds` and `clouds-mirror` are MID-STREAM instruments for far LOD, not settled
  ones**, despite being pose-stable (opening waits of 8 s and 8 + 4 s). Any round that changes LOD or
  chunk arrival *timing* will flag them, and the flag must be discharged with the raised-wait control
  rather than argued about — raise the wait until every tree reports the same `chunks N/V` and
  `regions built/live` at every shot, run from a scratchpad copy of the scenario passed by absolute
  path so nothing is written into either tree. The three controls used this round are the cheapest
  way to repeat it.
- **`particles.json` is a presence and draw-order check judged by eye, NOT a pixel instrument**, and
  is excluded from the statistical gate: spawns are RNG-driven, `wait` ticks physics once per
  rendered frame so particle height tracks frame rate, and three of the four systems never reach
  their eviction path at these densities. It has no fire leg — `VisualDirector`'s `give` cannot equip
  a tool, so a firestarter can never be used (see the gate section above).

Next: **C5 left no loose end — its pool size was decided, not deferred.**
`MaxConcurrentLodGenerations` ships at **1**: gate 2's branch 4, with two workers declined as well as
three, because 18 % of burst frame rate is too much to pay for 1.26x on a quantity that already had
7.8-8.2x of drain headroom. The claim protocol, the in-flight cap, the throughput instrument and
their facts all stayed. Do **not** re-open the pool size; the capture is above and a larger pool has
been measured.

Take **R10** first. It is the biggest remaining item and the biggest structural render win (L), its
hold on C5 is discharged, and R5 landing already narrowed its case to the per-region VAO/VBO/EBO
churn plus a bind, three uniforms and a draw per surviving region across three ring walks. Price it
on that alone: C5's re-pricing came out **neutral**, because the pool was declined and the shipped
tree's `lod` row and arrival rate are the baseline ones, so nothing this round re-rates R10 in either
direction. The one number C5 did hand it is the size of the set it walks — **591 live regions**, the
same at every pool size.

After that, in rough order of confidence: **C6** (`_playerPlaced.RemoveWhere`), which shares
`UnloadDistantChunks` with C3's removal counter — **re-read the standing obligation in Carried
forward before touching that method**, it is the one place a careless edit can starve a chunk
permanently. **D1** (packed chunk keys) is a broad-but-mechanical L, best taken entirely on its own,
and it would also make the frontier's per-probe `NeedsChunk` cheaper. **D2** has been promoted by
this round's evidence and is now the most interesting CPU item after R10: C5 measured that LOD builds
are terrain generation in disguise and that `Chunk.ColumnCache`'s 16384-entry wholesale `Clear()` is
the shared resource both the chunk-gen threads and the LOD builders contend on — cutting that work is
the lever C5's thread pool was reaching for and missed, and with the pool declined it is the *only*
lever left on that contention. **D5** is measurement-only: log
`FreeBlockCount` on the pool-growth line and capture it mid-stream and just after a teleport; take
the measurement, not the rewrite, since its own bullet doubts there is a win. **R6** is S but is a
*trade-off* rather than a win — gating the GPU timing queries on the overlay gives up the deliberate
"the first second after `X` already has data" property — so it is **reserved for a human decision and
must not be taken autonomously**. **R3** stays parked; read its bullet before re-opening it.

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
