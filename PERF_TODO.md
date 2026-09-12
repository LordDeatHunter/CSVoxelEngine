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
scenarios rather than the fly — R2's `rays-halfres.json` `raysPass` pair, and this round's
`clouds-mirror.json` `cloudPass` recapture, which found no measurable change — and both
live in the git history of this file (`git log -p -- PERF_TODO.md`). They are different
scenarios: do not mix them with a `perf-fly` number.

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
   (`clouds-mirror.json`'s shore hold, see **Suggested first pass**), so it is *not* a
   like-for-like correction of the 0.7-1.5 ms figure above.
5. **Fluid wake storm** at spawn over the sea — now bounded (queue 100-196k -> 8-12k),
   secondary to item 1.
6. **Screenshot encode** 7-18 ms — scripted runs only, but fix anyway (item 20).
7. **`uiPass`** 0.5-1 ms settled — the `X` overlay itself; subtract before comparing.

## Open (next round)

Not implemented yet. Impact / risk / effort are estimates; validate each with the
`perf-fly` protocol above before trusting it. The entries below the passes repeat
nothing from **Ruled out** — overlaps are called out inline.

### GPU / render passes

- [ ] **R3 · Half-res scene copies for water — resolved and PARKED this round** —
  `CopyScene` (`RenderManager.cs:1016-1028`), called with colour+depth at `:1619` and
  depth-only at `:1429`. The condition R2 left on this bullet was resolved against the code
  rather than implemented, and it settles three ways.
  **(a) The depth copy cannot move.** `CopyScene(bool colour)` copies depth
  *unconditionally* — only the colour half is gated by the parameter — and both call sites
  write the same `_sceneDepthTex`, which R2's depth-aware upsample pins at full res. So the
  `:1619` call keeps paying a full-res `DepthComponent24` `CopyTexSubImage2D` whatever R3
  does, and a second, half-res depth used only by the water pass is the same *additive* cost
  (an extra texture plus an extra reduction, on top of a full-res copy that still has to
  happen) that R2 already rejected on the rays side. Only the RGBA8 colour copy is left.
  **(b) The shader claim holds.** `test-fragment.glsl`'s refraction really is
  resolution-independent: `uv = gl_FragCoord.xy * uInvViewport` (`:267`) against
  `uInvViewport = 1/_viewportSize` (`RenderManager.cs:1362`), bilinear `texture()` fetches
  (`:270-272`), a `uInvViewProj` unprojection (`:276`) — no `texelFetch` and no pixel indices
  anywhere in the file. The shader would need zero changes.
  **(c) But the ceiling is tiny and the change is not "sample less".** The colour copy is
  gated behind `if (_nearWaterVisible)` and sits inside a `waterPass` measured at **0.09 ms
  GPU total** (the compute-frustum-cull capture under **Ruled out** below) — a figure that
  also contains every translucent chunk draw and every LOD water sheet. That capture
  demonstrably *included* the colour copy: the same line reports `reflect 0.83 ms`, and
  `RenderReflection` early-returns unless `_nearWaterVisible`
  (`RenderManager.cs:1053-1054`) — the very flag that gates the copy — so near water was on
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
- [ ] **R5 · Render-state rebind cache** — `BindWorldTextures()` runs 18x a frame
  (`RenderManager.cs:1336,1495,1543,1603,1627,1649,1699,1715,1745,1772,1797,1832,1889,1921,1967,1994,2012,2046`)
  and `worldShader.Use()` 20x, most of them back-to-back inside `EntityPass`
  (`World.cs:330-346`). Track the bound program and unit->texture bindings and skip
  redundants; invalidate when the sun/cloud/rays/UI passes swap state.
  **Impact low · risk low · effort S.**
- [ ] **R6 · Gate GPU timing queries behind the debug flag** — `BeginGpuPass` /
  `EndGpuPass` (`RenderManager.cs:255-271`) and `BeginGpuFrame` (`:228-246`) issue
  ~40-70 GL query calls a frame regardless of the overlay. Gating on `X` gives up
  "the first second after X already has data" (a deliberate property) — decide the
  trade. Different from the ruled-out *GL debug context*.
  **Impact low-med · risk low · effort S.**
- [ ] **R7 · Cache the camera view-projection** — `Camera.GetViewProjection`
  (`Camera.cs:989`) rebuilds view+proj at 22 call sites a frame. Dirty-flag it.
  **Impact low · risk low · effort S.**
- [ ] **R8 · UI per-frame string allocations** — `UIRenderer.cs:344` (`FPS`),
  `:397` (compass), `:435` (coords), plus the hotbar's `.ToString()` counts. Format
  only when the value changes, or into a reusable buffer; feeds settled GC.
  **Impact low · risk low · effort S.**
- [ ] **R10 · Every LOD region owns a `Mesh`** — `LodRegion` creates a VAO + VBO +
  EBO per region and another set for its water sheet (`LodRegion.cs:110-128`), and
  each draw is a `BindVertexArray` + `DrawElements` behind three uniform sets:
  `RenderManager.RenderLod` (`RenderManager.cs:1577-1586`), the mirror's region loop
  (`:1047-1054`) and the water sheets (`:1517-1524`). Three radius-8 rings
  (`LodRegion.cs:39-44`) are several hundred regions, so a water frame walks them
  three times paying a bind, three uniforms and a draw per survivor, plus GL object
  create/delete churn as regions stream in and out (`LodManager.cs:219-243`). Give
  LOD its own `ChunkGeometryPool` + `GeometryAllocator` and push regions through
  `ChunkDrawBatch.Draw` (`ChunkDrawBatch.cs:202-216`) exactly as chunks already are,
  so each pass becomes one `glMultiDrawElementsIndirect`. The CPU cull stays where it
  is, so this is *not* the ruled-out *compute frustum cull*. Overlaps R5 — three of
  the binds R5 counts are the LOD passes' (`RenderManager.cs:1543` is `RenderLod`'s),
  so landing R10 first shrinks R5's target; measure R5 after, not before.
  **Impact med-high · risk med · effort L.**
- [ ] **R11 · Chunk AABBs and the view frustum are rebuilt per pass** — four passes
  recompute `chunk.Position * Chunk.Size` and `min + new Vector3(Chunk.Size)` for
  every renderable chunk: `RenderShadows` (`RenderManager.cs:740-741`),
  `RenderReflection` (`:1011-1012`), `RenderWorld` (`:1356-1357`) and `CullWater`
  (`:1425-1426`); three of them independently build a `Frustum` from the identical
  unchanged `camera.GetViewProjection()` (`:1340`, `:1414`, `:1546`), paying six
  plane extractions and normalisations (`Frustum.cs:10-59`) three times over. Store
  the world-space min/max on `Chunk` once — `Position` is get-only (`Chunk.cs:58`) —
  and build the view frustum once a frame beside the cached view-projection. Not R7,
  which caches the matrix rather than the planes derived from it; the shadow and
  mirror frusta use different matrices (`_lightViewProj`, `_reflViewProj`) and are
  untouched. **Impact low · risk low · effort S.**

### CPU / streaming

- [ ] **C3 · Chunk-gen workers re-scan the whole offset list** —
  `ChunkGenerationWorker.FindClosestNeededChunk` (`ChunkGenerationWorker.cs:100-112`)
  restarts the ~15k-entry sorted-offset scan on every call, two `ContainsKey` per
  offset, on four threads; once the world fills, each scan runs to the end before
  sleeping. Shared frontier/cursor, or a per-worker persistent cursor. This is
  algorithmic — distinct from the ruled-out *worker priorities*.
  **Impact med-high · risk low-med · effort M.**
- [ ] **C5 · Parallelise / cap LOD region builds** — `LodManager` builds on one
  BelowNormal thread (`LodManager.cs:87-94`); region builds are pure and
  independent. A small worker pool would parallelise the streaming `lod` burst,
  mindful of contention with the four chunk-gen threads and the mesh pool.
  **Impact med · risk med · effort M.**
- [ ] **C6 · `_playerPlaced.RemoveWhere` scans the whole set per unload** —
  `ChunkManager.UnloadDistantChunks` (`ChunkManager.cs:164-167`): O(unloads x
  placements). Per-chunk buckets or a spatial index.
  **Impact low-med · risk low · effort M.**
- [ ] **C7 · Per-frame `Inventory.CanAccept` delegate** — `World.Update`
  (`World.cs:250`) passes a method group, allocating a delegate every frame. Cache
  it in a field. **Impact negligible · risk low · effort S.**
- [ ] **C11 · The LOD worker snapshots the dirty set every loop** —
  `LodManager.WorkerLoop` calls `TakeDirtyRegion` on every iteration
  (`LodManager.cs:101-124`, the call at `:107`) and `TakeDirtyRegion` enumerates
  `_dirtyRegions.Keys` (`:156-165`), which takes the dictionary's locks and
  materialises a full key copy even when the set is empty — which it is for most of
  the worker's spins. `ChunkManager` already documents and uses the alternative: a
  `foreach` over the dictionary itself is lock-free, copies nothing, and supports
  removal while enumerating (`ChunkManager.cs:139-141`, `:251-252`). Same change here.
  **Impact low · risk low · effort S.**
- [ ] **C12 · Bundled micro-allocations and O(n) evictions** — three unrelated
  one-liners, grouped because each fix is mechanical, the way R8 groups the UI string
  sites. (a) The four particle systems evict at capacity with `List<T>.RemoveAt(0)`,
  an O(n) shift over up to 512 entries (`LeafParticleSystem.cs:129-130`,
  `BubbleParticleSystem.cs:55-56`, `FireParticleSystem.cs:59-60`,
  `BlockDebrisSystem.cs:40-41`); a head index makes it O(1). Not purely mechanical:
  these lists are drawn in order under `SrcAlpha`/`OneMinusSrcAlpha`
  (`LeafParticleRenderer.cs:159-170`), so the replacement must preserve draw order —
  a head index plus a modular walk in the renderers, not a swap-remove.
  (b) `Chunk.GenerateBlockData` recounts solids with a scalar `foreach` over all 4096
  bytes (`Chunk.cs:446-450`), which `BlockCount - _blocks.AsSpan().Count(BlockType.Air)`
  does vectorised. (c) `LodManager.RemoveRegion` drops the region from
  `_pendingCover` with a linear `List.Remove` on every unload (`LodManager.cs:308`).
  **Impact low · risk low-med (a), low (b, c) · effort S.**

### Data structures / caching

- [ ] **D1 · Packed chunk keys** — `ChunkManager.Chunks` / `_dirtyChunks` /
  `_queuedChunkPositions` / `_generatingMeshes` are keyed by `Vector3`
  (`ChunkManager.cs:23,82-83,92`), paying a 3-float hash on every probe (neighbour
  checks, the dirty scan, `TryResolveNeighbors`, entity lookups). `FluidManager`
  already packs a `long` to avoid exactly this (`FluidManager.cs:32-35,898-899`).
  **Impact med · risk med · effort L** (broad but mechanical). `LodManager` is
  already keyed by integer tuples, so this is chunk-side only.
- [ ] **D2 · Cache eviction is a wholesale `Clear()`** — `Chunk.ColumnCache`
  (`Chunk.cs:597-599`) and `WormCarver.CellCache` (`WormCarver.cs:111-112`) drop
  everything at their cap, so a far teleport recomputes (part of the teleport
  hitch). LRU / generation-stamped eviction would keep reuse across a move.
  `LodRegion.ColumnCaps` (`LodRegion.cs:105`) also grows unbounded over a long
  editing session: since C9 it holds one dense `Chunk.Size * Chunk.Size` cap bucket
  per *edited chunk column*, never evicted — cheaper than the old per-fine-column
  entries once a column is heavily mined, more expensive for a single isolated
  block. **Impact med · risk low-med · effort M.**
- [ ] **D3 · Dead code** — `ChunkDrawBatch.PassCount`
  (`ChunkDrawBatch.cs:117`) is never read. **Impact nil · effort S.**
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
- `Shader` uniform caching (`Shader.cs:98-108`), `MeshBufferPool`, `ChunkDrawBatch`,
  `BlockCubeCache`, `ModelBaker`, `Vertex` — all already cached, pooled, batched or
  packed; re-read this round, nothing new. (`Shader` is also in the ruled-out
  omnibus line; recorded here so it is not re-opened from the other direction.)
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
  `Camera` — walked this round; no allocation, redundancy or algorithmic finding
  above the noise floor. `Camera`'s per-frame collision scans are additional
  evidence for D1's impact rather than a separate entry.

**Suggested first pass:** R4 and R12 landed this round. **R3 was resolved and parked rather than
implemented** — the short version is that R2's composite pins the scene *depth* copy at full res,
leaving only a colour copy inside a pass whose whole measured cost is 0.09 ms, and halving that
copy would buy a perceptual gate; the full finding is in R3's bullet above.

**R4 — cloud instancing.** The per-copy arithmetic came out of `CloudRenderer` first, into a
GL-free helper: `CloudCopies` (`OpenGLTest/src/Systems/CloudCopies.cs`) with `Drift`, `Snap` and
`Gather`, the last running the 3x3 ring in the original `ix`/`iz` nesting order, frustum-testing
each copy's AABB and packing the survivors to the front of a caller-supplied span. That is the one
headlessly testable part of R4 and it is pinned by `OpenGLTest.Tests/CloudCopiesTests.cs` (8
facts); the equivalence proof is `GatherOffsetsMatchThePerCopyArithmetic`, exact float equality
against an inline reimplementation of the pre-extraction expression over 60 snap/drift/field
combinations, plus `GatherPacksSurvivorsToTheFrontPreservingOrder` for the same set in the same
relative order under a frustum that provably culls. The draw was then instanced:
`cloud-vertex.glsl`'s `uniform vec2 uOffset` became `layout (location = 2) in vec2 vOffset`
(locations 0 and 1 were the only ones in use), the C# side allocates a nine-entry instance VBO with
`VertexAttribDivisor(2, 1)`, uploads `offsets[..count]` with one `NamedBufferSubData`, and each
sweep became one `glDrawElementsInstanced`. **The CPU cull is exactly where it was** — the instance
count *is* the survivor count, so a culled copy is still not drawn. The zero-survivor early return
sits beside the `CloudOpacity <= 0` guard, **before any GL call**, because returning after
`_shader.Use()` or `BindVertexArray(_vao)` would leave that binding current for the mirror path.
`CloudTests.CloudVertexShaderTakesAPerInstanceOffset` pins the GLSL half of the CPU/shader
contract. Covered **only** visually: `VertexAttribDivisor(2, 1)` itself, whose absence renders one
untiled cloud field instead of the 3x3, and the GL draw structure — the VAO bracket, the two sweeps
and the `ColorMask`/`DepthMask` toggles between them.

**Both depth pre-passes were kept.** The main-pass one is the once-per-pixel blend guard, as the
bullet always said. The mirror's was considered and **deliberately declined** — recorded here so
nobody re-opens it blind: the cloud mesh is closed and watertight
(`CloudTests.RoundedMeshIsWatertight`), so a view ray crosses at least two surfaces, and the
mirror's `CullFace(Front)` (`RenderManager.cs:1178`) removes the far side of a *single* mass but
not overlapping fillet pieces and not two separate masses stacked along one ray. Dropping it would
darken clouds in the water but not in the sky — a mirror/sky disagreement. And with nine draws
already collapsed to one, what remains to win there is a single depth-only instanced draw on a
sub-full-res target.

**R4's win is submission-side and nothing else**, and the capture says so. Instancing removes zero
vertices and zero fragments. What stands is arithmetic off the code: per `Render` call, up to 18
`DrawElements` each behind a `uOffset` uniform set collapse to exactly 2 `DrawElementsInstanced`
plus one `NamedBufferSubData`, and `Render` runs twice a frame (main pass + mirror, the mirror at
most every other frame under reflection temporal reuse) — **up to 34 fewer draw calls and up to 34
fewer uniform updates a frame**. The `cloudPass` recapture found **no measurable change in either
half and nothing is quoted from it**: the GPU difference came in under a tenth of the within-tree
run-to-run spread (the half that should not move did not move), and the CPU difference did not
exceed the baseline's own spread — that bracket averages 0.01-0.02 ms in total, i.e. it sits on
`FrameProfiler`'s F2 print resolution, so a submission-side saving is invisible there by
construction. Note too that `cloudPass` brackets only the **main** pass (`World.cs:367-371`); the
mirror's cloud draw folds into `reflect` (`World.cs:306-313`) with no bracket of its own, and
reflection temporal reuse makes that row's per-window composition incomparable — which is why the
mirror half is argued from draw count and never from a timing.

**R12 — DSA pool uploads.** `ChunkGeometryPool.Upload` no longer binds anything: the three
`BindBuffer(CopyWriteBuffer, ...)` calls are gone and the two writes are
`gl.NamedBufferSubData(_vbo, ...)` / `(_ebo, ...)`. Same buffers, same offsets, same spans, same
bytes, same layout — **up to 3 binds x up to 32 uploads (`MaxMeshUploadsPerFrame`) x 2 pools = up
to 192 client-side GL calls a frame removed** during streaming. `CreateBuffers`' and `Grow`'s own
`CopyWriteBuffer` binds stay, because `glBufferStorage` and `glCopyBufferSubData` still need bound
targets; `AGENTS.md:171` was corrected to match (`NamedBufferSubData uploads`). **No unit test was
added and none should be — the change introduces no new arithmetic.** It was proved instead by a
temporary readback: every write read back off the GPU at its computed offset and byte-compared
against its source span, the identical patch applied to both the working tree and a pristine-HEAD
export, one `perf-fly` run each — 112198 and 111606 verified uploads, **zero mismatches** on either
pool for either write on either tree — plus a negative control (vertex readback offset shifted one
byte) that made mismatches track uploads 1:1 while the untouched index check stayed at zero, so the
check was provably capable of failing. The instrumentation was removed from both trees afterwards.
`chunkApply` was recaptured too and **nothing is quoted**: the difference came in at half the
within-tree spread, which was expected — the row is CPU-side, it times the whole apply loop rather
than the two writes, and its per-window content is set by how many meshes generation happened to
deliver that second. A cross-tree upload-sequence hash was not available, and that is measured
rather than assumed: the two verification runs uploaded different numbers of chunks, because mesh
completion comes off worker threads.

**Gate machinery, for whoever measures next.** `visual-examples/clouds-mirror.json` is new this
round and is the cloud instrument: a grounded shore vantage over the water bay, a pose-stable
teleport+screenshot burst framing cloud reflections in the water (`mirror_graze`, `mirror_wide`),
three repeats of one pose as a same-build noise floor, two clouds-against-sky shots, and a
grounded, dry 12 s hold at the end that publishes the `cloudPass` windows. Bare teleport+screenshot
bursts with no `wait` between them tick no physics, so the pose is pixel-stable; a `wait` after a
mid-air teleport resumes free-fall, so any held pose must be grounded, and a hold that ends
underwater makes `RenderClouds` early-return and publishes empty `cloudPass` windows. Its header
carries the detail; three traps matter beyond it:

- **The floor is not uniform across the frame.** In the mirror shots the sky region is an
  essentially exact instrument (a level or two) while the water region floors in the *hundreds* —
  the wave normal is animated from wall-clock `uTime`, so the surface is in an arbitrary phase
  cross-run. Compare per region, never as one whole-frame number, and treat the water region as a
  gross-breakage check (a missing 3x3 copy, an offset in the wrong place, the untiled single field
  a missing divisor produces) rather than as a measurement.
- **A 3-run floor under-samples the same-binary tail** on shots where the differing fraction sits
  near the p99 knee — `clouds_up`, `clouds_closeup` and `sky_closeup` all swing on a small change
  in cloud-edge pixel count. Use the full within-vs-cross pairing matrix there, or a 3-run floor
  will read sampling noise as a regression.
- **Session effects exceed the quantity being measured.** The *same* pristine binary read
  `cloudPass` gpuAvg 1.460, 1.501 and 1.551 in three different sessions this round. Re-run the
  baseline in the same session as the change, interleaved run for run; never difference against
  stored captures from an earlier session, and give the baseline its cold first run too — a freshly
  built binary streams more slowly on its first run, which moves both the loaded chunk set and the
  wall clock at every shot.

`clouds.json` is the sky-side regression check, not a measuring instrument: every shot but the
first follows a mid-air teleport plus a 0.3 s `wait`, and `VisualDirector.Drive` ticks the world
for `wait`, so the camera free-falls before the shutter. (`clouds_up` waits 3 s from y=30.5 and
simply lands back at the spawn pose, which is why it alone is pose-stable.) And `perf-fly.json`'s
`screenshot` ops do **not** pass `hideHud`, unlike both cloud scenarios, so its shots bake in the
live profiler HUD across roughly x 0..570 — numbers that change every frame, which alone puts about
half the frame's pixels above 2 levels *within* one binary. Any pixel gate on `perf-fly` must mask
those columns, or the scenario must gain `hideHud: true`. Two traps carried forward from R2:
`water.json`'s four underwater shots are **not** a usable instrument (free-falling camera plus
drifting bubble particles give a 15-25 level same-build floor), and any frame-to-frame statistic
must be normalised for wall-clock sun drift before it is compared across builds, or it reads
backwards. Carried forward from earlier rounds: `lod` measures only the main-thread
`LodManager.Update` (`World.cs:241-243`) while `LodRegion.BuildMeshData` runs on the LodGen thread,
so size any further LOD work with a LodGen throughput capture during the #1 streaming hitch rather
than off a `lod` row.

Next: **R5**, then **R7 + R11** as one pass. R5 is the largest of the cheap render-side items still
standing — 18 `BindWorldTextures()` and 20 `worldShader.Use()` a frame, most of them back-to-back
inside `EntityPass` — it is pure bookkeeping with no visual surface, so it needs no perceptual
gate, and its risk is confined to the invalidation list (the sun, cloud, rays and UI passes swap
state behind its back). R10's bullet notes that landing R10 first would shrink R5's target, but R10
is L and held behind the cheaper LOD items, so take R5 now and re-price the LOD binds against it
afterwards rather than parking an S item behind an L one. R7 and R11 belong together: R7 caches the
view-projection `Camera.GetViewProjection` rebuilds at 22 call sites, R11 caches the *planes* three
passes independently re-derive from that same unchanged matrix plus the per-chunk world AABBs four
passes recompute — both S, both output-preserving, and both as headlessly testable as this round's
`CloudCopies` was, since `Frustum` and the AABB arithmetic are already GL-free. R10 remains the
biggest structural render win; hold it until the cheaper LOD items above have been measured, since
they may move `lod` enough to reprice it — and it overlaps R5.

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
  (thread-priority tuning — Open C3 is an algorithmic change, not this),
  shader ALU (4-tap PCF, 8 point lights), uniform location lookups, indexed mesh
  upload — all audited, not the problem.
