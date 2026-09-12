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
| Underwater | ~10-14 ms | 72-100 | `raysPass` GPU 1.5-2.3 ms while submerged |
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
4. **Underwater `raysPass`** (1.5-2.3 ms GPU) and **`cloudPass`** (0.7-1.5 ms when
   clouds in view). Clouds were already cut 63 % (item 15); remainder is steady, not spiky.
5. **Fluid wake storm** at spawn over the sea — now bounded (queue 100-196k -> 8-12k),
   secondary to item 1.
6. **Screenshot encode** 7-18 ms — scripted runs only, but fix anyway (item 20).
7. **`uiPass`** 0.5-1 ms settled — the `X` overlay itself; subtract before comparing.

## Open (next round)

Not implemented yet. Impact / risk / effort are estimates; validate each with the
`perf-fly` protocol above before trusting it. The entries below the passes repeat
nothing from **Ruled out** — overlaps are called out inline.

### GPU / render passes

- [ ] **R2 · Half-res underwater rays** — `UnderwaterRaysRenderer` +
  `rays-fragment.glsl`, driven at `RenderManager.cs:1295-1308`; `raysPass`
  1.5-2.3 ms, the #4 hitch. March into a half-res target and upsample additively;
  copy the scene depth half-res too (`:1301`). **Impact high · risk low-med · effort M.**
- [ ] **R3 · Half-res scene copies for water** — `CopyScene`
  (`RenderManager.cs:924-936`), called with colour+depth at `:1490` and depth at
  `:1301`. Refraction is low-frequency, so downsample through an FBO blit instead
  of a full-res `CopyTexSubImage2D`. The shader's normalised UV/NDC math
  (`test-fragment.glsl:267-279`) is resolution-independent, so this is plumbing.
  **Impact med · risk low-med · effort M.**
- [ ] **R4 · Cloud batching / mirror pre-pass** — `CloudRenderer.Render` /
  `DrawCopies` (`CloudRenderer.cs:71-128`): up to nine fixed-offset copies drawn
  twice (depth pre-pass + colour), plus again in the mirror; `cloudPass` 0.7-1.5 ms.
  Instance the 3x3 into one draw per pass; consider dropping the pre-pass for the
  mirror only. Keep the main-pass pre-pass — it is the once-per-pixel blend guard.
  **Impact med · risk low-med · effort M.**
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
- [ ] **R12 · Three buffer binds per pool upload** — `ChunkGeometryPool.Upload`
  (`ChunkGeometryPool.cs:102-128`) binds `CopyWriteBuffer` to the VBO, then the
  EBO, then 0 (`:119,122,125`) around its two `BufferSubData` calls, up to
  `MaxMeshUploadsPerFrame` = 32 times a frame per pool
  (`WorldGenerationConfig.cs:16`). `glNamedBufferSubData` (4.5 DSA; the context is
  4.6) writes both with no binding at all, which also retires the comment's reason
  for routing through `CopyWriteBuffer` in the first place (avoiding a stray
  `ElementArrayBuffer` bind rewriting whichever VAO is current). Feeds `chunkApply`
  (up to 5.8 ms streaming). This is *not* the ruled-out *indexed mesh upload* audit —
  the index and vertex data and their layout are unchanged; only the three
  `BindBuffer` calls wrapping the two writes go away.
  **Impact low · risk low · effort S.**

### CPU / streaming

- [ ] **C1 · LOD growth-min is O(cells x window^2)** — `LodRegion.BuildMeshData`
  (`LodRegion.cs:209-252`). Level 2 reads a 22x22 window per 8-block cell
  (~0.5 M `FineHeight` calls per region) plus a separate `cellSize^2` canopy scan
  per cell. A rectangular min is separable (horizontal then vertical), ~10x fewer
  reads. Worker-side, like C9: `BuildMeshData` runs on the LodGen thread
  (`LodManager.cs:111`) while `lod` measures only the main-thread
  `LodManager.Update` (`World.cs:242-244`), so the win is LodGen throughput during
  the #1 hitch, not a smaller `lod` row.
  **Impact high · risk med · effort M** (needs an exact-equality test).
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
- [ ] **C9 · One broken block makes every LOD height sample a dictionary probe** —
  `BuildMeshData` sets `hasCaps = !ColumnHeightCaps.IsEmpty` once
  (`LodRegion.cs:177`) and `FineHeight` (`:179-189`) then does a
  `ConcurrentDictionary` lookup on an `(int, int)` tuple per sample while it holds
  (`:185`). The cap table is never emptied (`LowerColumnBelow`, `:93-108`), so the
  first block the player mines turns a plain array read into a hashed probe for the
  ~0.5 M `FineHeight` calls a level-2 region build makes (the count C1 measures) —
  for the rest of the session, in every region, including ones nowhere near the edit.
  Bucket the caps by chunk column and resolve the bucket once per prefetched
  heightmap (`:170-173`): a region with no capped column then pays nothing, and a
  capped one pays one probe per chunk column instead of one per block. Distinct from
  C1, which cuts how often `FineHeight` is called — this cuts what a call costs, and
  the two compose. Distinct from D2, which is about `ColumnHeightCaps` growing
  without bound, not the read-path probe. Worker-side like C1.
  **Impact med · risk low · effort M.**
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
  `LodRegion.ColumnHeightCaps` (`LodRegion.cs:87`) also grows unbounded over a long
  editing session. **Impact med · risk low-med · effort M.**
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
  the half-res route (R2/R3) is the allowed lever.
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

**Suggested first pass:** C10 and C8 landed this round, both worker-side. C10
guards the two carve calls in `Chunk.GenerateBlockData` on
`column.MaxHeight >= lowerBoundY`, so `NoiseCaves.Carve` and `WormCarver.Carve` are
skipped on chunks whose columns all end below the chunk floor — the sea chunks the
`needsWater` clause keeps alive over their own sea floor — removing a 1000-call cave
noise lattice per such chunk with bit-identical block output. The identity claim is
pinned by an all-solid carver probe, its positive control (so the probe cannot pass
vacuously), a submerged-chunk equality test, and a cross-tree block hash over 360
generated chunks spanning land, shoreline and fully submerged columns. C8 rents the
four output lists of `LodRegion.BuildMeshData` from a LOD-sized `MeshBufferPool`
owned by `LodManager` — 24576/36864 opaque, sized from the measured per-level peak of
~21.5k vertices rather than the old 8192 literal, and 4096/6144 water — returned on
the main thread after the upload or on the out-of-range drop, so a region build no
longer LOH-allocates its output. Neither shows in a profiled section: size C10 with a
chunks/s generation-rate capture over the water-bay leg and C8 with the streaming
gen0/gen2 GC counts before pricing either. Next: C1 together with C9 (measure the
pair against LodGen throughput), then R2 (against `raysPass`), the biggest single GPU
prize left. R10 is the biggest structural render win; hold it until the cheaper LOD
items above have been measured, since they may move `lod` enough to reprice it — and
it overlaps R5.

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
