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
under `VOXEL_DEBUG=1` on both trees, but quoted nothing from it (every row overlapped, see
**Suggested first pass**), so it is not a recapture and the table above is untouched by it.

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
  churn as regions stream. Price it on those terms alone. **Hold it until C5 has been
  measured**, since C5 may move the `lod` row enough to re-price this one.
  **Impact med-high · risk med · effort L.**

### CPU / streaming

- [ ] **C5 · Parallelise / cap LOD region builds** — `LodManager` builds on one
  BelowNormal thread (`LodManager.cs:87-94`); region builds are pure and
  independent. A small worker pool would parallelise the streaming `lod` burst,
  mindful of contention with the four chunk-gen threads and the mesh pool.
  **Impact med · risk med · effort M.**
- [ ] **C6 · `_playerPlaced.RemoveWhere` scans the whole set per unload** —
  `ChunkManager.UnloadDistantChunks` (`ChunkManager.cs:164-167`): O(unloads x
  placements). Per-chunk buckets or a spatial index.
  **Impact low-med · risk low · effort M.**
- [ ] **C12 · Bundled micro-allocations and O(n) evictions** — three unrelated
  one-liners, grouped because each fix is small and local, not because they share a
  subsystem. (a) The four particle systems evict at capacity with `List<T>.RemoveAt(0)`,
  an O(n) shift over up to 512 entries (`LeafParticleSystem.cs:129-130`,
  `BubbleParticleSystem.cs:55-56`, `FireParticleSystem.cs:59-60`,
  `BlockDebrisSystem.cs:40-41`); a head index makes it O(1). Not purely mechanical:
  these lists are drawn in order under `SrcAlpha`/`OneMinusSrcAlpha`
  (`LeafParticleRenderer.cs:159-170`), so the replacement must preserve draw order —
  a head index plus a modular walk in the renderers, not a swap-remove.
  (b) `Chunk.GenerateBlockData` recounts solids with a scalar `foreach` over all 4096
  bytes (`Chunk.cs:495-499`), which `BlockCount - _blocks.AsSpan().Count(BlockType.Air)`
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
  (`Chunk.cs:646-648`) and `WormCarver.CellCache` (`WormCarver.cs:111-112`) drop
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

**Suggested first pass:** **C3** landed, with **C7 + C11 + R8** as one cheap sweep alongside it. All
four are output-preserving with no intended visual surface, so the gate looked for *zero* structural
change rather than for an acceptable difference. **R3 is unchanged and still parked** — its bullet
above carries the finding. The blow-by-blow write-ups of the earlier R4 and R5/R7/R11 rounds are in
`git log -p -- PERF_TODO.md`; what is kept below is what the next round needs, not the history.

### What landed

- **C3 · the shared scan frontier.** `OpenGLTest/src/Systems/ChunkScanFrontier.cs` — a GL-free
  `internal sealed class` (the `CloudCopies` / `TextureUnitCache` precedent) holding one *immutable*
  node `(Center, RemovalVersion, Index)` swapped by `Interlocked.CompareExchange`, shared by all
  four chunk-gen threads of a `ChunkGenerationWorker`. `FindClosestNeededChunk`
  (`ChunkGenerationWorker.cs:118`) is now two lines — sample the camera chunk and
  `ChunkManager.ChunkRemovalVersion`, then `_frontier.FindNext(camChunk, version, _needsChunk)` — in
  place of the full walk of the sorted offset array. `SortedOffsets` is `internal` so tests can build
  a frontier over the real array: **17077 entries**, exactly the `ChunkRadiusSq = 256` lattice count
  and exactly the settled `LoadedChunkCount` (the old bullet's "~15k" was an estimate; do not carry
  it forward, and note that an intermediate figure of 17157 appeared in this round's own working
  notes and is also wrong). `_needsChunk` caches the `ChunkManager.NeedsChunk` method group, since
  the scan calls it once per probed offset.
- **C3's counter.** `ChunkManager.ChunkRemovalVersion` (`ChunkManager.cs:74`), incremented once per
  unload pass that actually dropped a chunk (`:228`), gated on `removed > 0`.
- **R8 · the HUD string cache.** `OpenGLTest/src/Systems/UITextCache.cs`, GL-free and
  render-thread-only (so no locking), with four members — `Fps`, `CompassReadout`, `Coords`, and
  `SlotCount` keyed *per slot*. `UIRenderer` holds one `_textCache` (`:189`) and calls it at the four
  per-frame sites: `:385` (FPS), `:438` (compass readout), `:476` (coords), `:652` (hotbar count).
  Only the **formatting** is cached — every `DrawText` / `DrawTextCentred` / `MeasureSize` call still
  runs every frame, so R9's glyph batching sees identical submissions.
  `DrawInventoryPanel`'s `.ToString()` sites (`:723`, `:1193`, `:1259`) were deliberately left alone:
  panel-only, not named by the bullet.
- **C11 · the LOD dirty drain.** `LodManager.TakeDirtyRegion` (`:202`) walks the
  `ConcurrentDictionary` itself instead of `.Keys`. `FindClosestNeededRegion` (`:220`) and a new
  one-line `AttachCamera` (`:113`, split out of `Start`) were promoted to `internal` so the drain
  could be driven headlessly against its **real producer path** rather than against a hand-seeded
  set; `Start` and `WorkerLoop` are otherwise untouched.
- **C7.** `World._canAcceptDrop` (`World.cs:48`), one cached `Func<byte, bool>` assigned in the
  constructor (`:130`) — after the field initialisers run, since the `Inventory` auto-property is
  initialised in declaration order and a field initialiser declared above it would capture null.

### The C3 concurrency argument — the reusable artefact of this round

This is the part to read before touching chunk streaming, `ChunkManager`'s dictionaries, or the
frontier itself. The code's own doc comments carry it in full beside the code; this is the summary.

**The invariant.** If the published node is `(C, V, K)`, then *every offset index below `K` was
observed not-needed by a worker that had sampled exactly centre `C` and removal version `V`*. A
caller trusts that prefix **only** when its own freshly sampled `(center, version)` pair matches;
otherwise it scans from index 0. The node's three fields are `readonly` and written before the
exchange, which is a full fence, so a reader that sees the reference sees all three consistently.

**Why the trusted prefix stays valid.** `NeedsChunk(p)` is
`!Chunks.ContainsKey(p) && !_queuedChunkPositions.ContainsKey(p)` (`ChunkManager.cs:815`). An *add*
to either dictionary only makes a position *less* needed, which is the safe direction, so only a
**removal** can turn a position needed. Today there is exactly one removal site for each dictionary —
`Chunks.TryRemove` at `ChunkManager.cs:200` and `_queuedChunkPositions.TryRemove` at `:210` — and
both are inside `UnloadDistantChunks`, which bumps the version after the loop. So between one bump
and the next, the only positions that can turn needed belong to an unload pass currently in flight,
and that pass's own bump necessarily follows it.

**Why the bump is published *after* the loop, and must never be moved up.** A loaded chunk lives in
**both** dictionaries: `QueueChunk` `TryAdd`s the position to `_queuedChunkPositions` (`:574`) and
never removes it on the success path. So a position does **not** become needed at the
`Chunks.TryRemove` at `:200`; it becomes needed ten lines later at the `_queuedChunkPositions`
removal at `:210`, after `RemoveRenderable`, `MeshVersion++`, `chunk.Dispose()` and the loaded-count
decrement. A bump placed next to `Chunks.TryRemove` would announce the transition *before it had
happened*, and a worker probing inside that window would publish a frontier that skips the position
**permanently** — exactly the starvation this design exists to prevent, reachable in exactly the
scenario the old bullet worried about. The increment therefore counts removals and fires once, after
the `foreach` closes. That is also ~800 fewer interlocked ops per chunk-border crossing than a
per-removal bump would have cost.

**The bounded imprecision, stated as a cost rather than a hazard.** The end-of-loop bump does not
make the frontier instantaneously exact: while an unload pass is in flight, positions turn needed
while the version still reads its old value, so a call that sampled that old version can skip one.
It is skipped by *that call only* — the published node carries the version it was computed under and
is therefore **self-invalidating**: the bump that ends the loop discards every such node, and the
next call scans from 0. The worst case is latency, not starvation: a worker that returned null just
inside the window sleeps `GenerationDelayIdleMs` (200 ms, `WorldGenerationConfig.cs:33`) before
rescanning. That is not a regression — the pre-change full scan had the identical hole (sweep to the
end, a position turns needed behind the read head, sleep 200 ms) — and the other three workers,
sampling after the bump, find it immediately.

**The camera-reverses case, by name.** A camera that goes C → C′ → C without any worker ever
observing C′ is closed by the **version**, not by the centre: the unload at C′ bumped it, so the node
is discarded even though the centre matches again. This is also why the design no longer depends on
the coincidence that generation and unload share a radius (`ChunkManager.cs:197`) — that equality is
now only why the frontier *survives* a still camera, not why it is *safe*.

**Why the exchange is never retried.** Retrying a failed `CompareExchange` is the only way `FindNext`
could spin, so a lost race is simply dropped. Losing only shrinks the frontier (more scanning), never
skips. A late publish carrying a stale version is likewise harmless: nobody whose version sample is
current will match it, so everyone full-scans until a current node is published. A successful CAS
chain is strictly monotone in index, because `Publish` succeeds only when the node reference is
exactly the one the call read and only ever offers `index > start`. And `index <= start` publishes
nothing, so a converged worker polling every 200 ms allocates no `Node` and executes no loop body at
all.

**Why a hit publishes `i` and not `i + 1`.** Only indices the call actually *proved* not-needed are
published. It costs one re-test of the returned offset on the next call and removes any need to
reason about whether `QueueChunk` succeeded, was deduped, or threw.

**Disagreeing threads degrade, never break.** `Camera.Position` is an unsynchronised
`{ get; private set; }` (`Camera.cs:20`) already read by the worker threads before this round; that
was not changed and nothing here depends on fixing it. Four threads that briefly disagree about the
centre each reset the node and scan from 0 — i.e. the pre-C3 behaviour for the sub-frame window in
which they disagree, and never worse. A `NaN` centre never matches and always full-scans.

**The wake model is untouched.** `FindNext` introduces no waiting, no signalling and no sleeping;
`WorkerLoop` keeps its existing `GenerationDelayIdleMs` idle poll. There is no wake to miss.

**The standing obligation — read this before adding a removal.** *If a future change removes a
position from `ChunkManager.Chunks` or from `_queuedChunkPositions` anywhere other than
`UnloadDistantChunks` without bumping `ChunkRemovalVersion`, the frontier can starve that chunk.*
Nothing structurally prevents it: `Chunks` is a **public** `ConcurrentDictionary` and
`OpenGLTest.Tests/MeshingTests.cs:49` already removes through it. The single-site property is a
convention, not an enforced invariant. Two facts guard it —
`ChunkScanFrontierTests.FrontierTrustsThePrefixUntilTheRemovalVersionMoves` (converge, free a
low-index position **without** bumping, assert the frontier deliberately does not find it, then bump
and assert it comes straight back: this fails if the bump is ever dropped or misplaced) and
`ChunkManagerTests.UnloadBumpsChunkRemovalVersionOnlyWhenAChunkLeaves` (whose final assertion is
`NeedsChunk(Vector3.Zero)` *after* the bump is observable, which fails if the increment is moved
ahead of the `_queuedChunkPositions` removal).

**Why the bump is gated on `removed > 0`.** `UnloadDistantChunks` runs on a chunk-border crossing
*or* every `UnloadScanFallbackFrames` = 120 frames (`ChunkManager.cs:171`), even when nothing has
moved. An ungated bump would therefore fire roughly twice a second on a fully settled world and
restart four full scans forever — the exact cost C3 exists to remove. The gate's no-bump half is
pinned empirically, not merely argued: the first half of
`UnloadBumpsChunkRemovalVersionOnlyWhenAChunkLeaves` is a *real* unload scan (it runs because
`_lastUnloadCamChunk` is a `Vector3?` that starts null) which finds nothing and must leave the
version untouched.

**What a passing concurrency test does and does not prove.** `ChunkScanFrontierTests`' four-thread
coverage fact and its freed-behind-the-frontier stress fact demonstrate the absence of the races they
happen to exercise, on one machine and one scheduler — not the absence of races. They were run five
times consecutively, green each time, and that is still not proof. The safety claim rests on the
invariant above; the tests are a check on it, not a substitute. Both the frontier's doc comment and
the test class doc say so, and a future round must not upgrade "five green runs" into evidence. Note
also that two threads returning the *same* position is expected and benign — `QueueChunk` dedupes at
`_queuedChunkPositions.TryAdd` (`ChunkManager.cs:574`) — so no fact asserts distinctness of returns.

### The R8 bit key, so nobody "simplifies" it to `==`

`UITextCache` compares float keys **bit-for-bit** through `BitConverter.SingleToInt32Bits`, following
R7's `ViewProjKey` precedent, because a cache hit must *prove* the text would be identical.
`$"{-0f:0.0}"` renders `"-0.0"` while `$"{0f:0.0}"` renders `"0.0"`, and `-0f == 0f` is true, so an
`==` key would put the wrong sign on the coordinate line whenever the player stands just on the
negative side of an axis. This is neither hypothetical nor only a headless fact: the HUD read below
captured `XYZ -0.0 / 70.5 / -0.0` **on screen** on both trees.
`UITextCacheTests.NegativeZeroDoesNotReuseThePositiveZeroText` is the fact that justifies the key.

One BCL detail measured on the way and worth keeping: **`int.ToString()` returns a shared cached
instance for values 0..299 on this runtime (.NET 9)**, so any future allocation-shaped fact using
`ReferenceEquals` on a formatted small integer passes vacuously. `SlotCountsAreCachedPerSlot` uses
counts above 299 for exactly that reason.

### Tests

The suite went **447 -> 464** with the same two known failures
(`BlockMovementTests.LeavesDeclareThreeQuarterMovementSpeed`,
`FallingBlockTests.SevenTowerChainRebuildsFullyWithNoDrops`): +2 `LodManagerTests`, +6
`UITextCacheTests`, +1 `ChunkManagerTests`, +6 `ChunkScanFrontierTests`, +2
`ChunkGenerationWorkerTests`. C7 and the `UIRenderer` wiring deliberately add none — `World` and
`UIRenderer` both need live GL/window objects that no test builds, so their coverage is the compiler
plus the HUD eyeball read below. Note that the suite declares 405 `[Fact]`/`[Theory]` attributes
against 464 executed cases; `[Theory]` expansion accounts for the gap, so reconcile a delta by
counting attributes per file rather than against the executed total.

C3's equivalence is pinned two ways, both headless and both against the **deleted** scan preserved
verbatim as an oracle: `ChunkScanFrontierTests.FrontierReturnsExactlyWhatAFullScanWouldReturn`
(2000 rounds against a full-scan reference with the centre moving and positions freed mid-run, then a
second phase over a *settled* sphere, which is what produces the null answers the fact's own
anti-vacuity guard needs) and `ChunkGenerationWorkerTests.WorkerPicksTheSameChunkTheFullScanWould`
(400 rounds end-to-end against the real `ChunkManager.NeedsChunk` and a real `Camera`, with two
camera moves mid-run). `SortedOffsetsAreClosestFirstAndCoverTheRadius` pins the *property* the
frontier's closest-first claim rests on — non-decreasing `LengthSquared`, no duplicates, set-equal to
the `ChunkRadiusSq` lattice — and explicitly **does not** pin tie order among equidistant offsets,
because `BuildSortedOffsets` uses an unstable `List.Sort`.

### The visual gate: zero structural change, and three new traps

63 scripted runs across the four pose-stable scenarios — `entity-binds.json` (`VOXEL_TIME` 60),
`clouds-mirror.json` (60), `rays-halfres.json` (180), `lod-horizon.json` (10) — against a pristine
`git archive HEAD` export built in a scratchpad, the working tree never mutated to produce a
baseline. Every shot passed on the structural statistic; every flag was run to ground as sampling or
as *earlier* chunk arrival, none as a rendering change. Three things learned that belong beside the
traps already recorded below.

- **New, and the most important: the pose-stable scenarios carry a per-run latent noise scalar, and
  an unbalanced run design manufactures false marginals.** A 1-D MDS on the structural difference
  matrix recovers 88-95 % of the variance with the **same run ordering on every shot of a scenario**,
  and the two trees interleave fully along it — on `lod-horizon` the single *least*-similar pair of
  all 28 was two runs of the same pristine baseline binary, and the most-similar pairs were
  cross-tree. A deliberate calibration run at `VOXEL_TIME=11` instead of `10` (a one-second, 0.6°
  sun advance) sat further from all eight real runs than any of them sat from each other, so the
  statistic's entire dynamic range on that scenario is a sub-second wall-clock **sun phase** reaching
  the frame through shadow-map texel alignment. The prescribed 3-baseline + 2-work design put both
  work runs adjacent at one end of that axis by session position alone and produced a false marginal
  p = 0.057-0.086; a 4th baseline run added *late* in the session landed at the opposite extreme and
  collapsed it to p = 0.62-0.68. **Use equal run counts per tree, with neither tree owning the early
  or the late slots.** It is the same lesson as "reversing the interleave, not adding runs, is what
  catches a false positive", applied to the pixel gate.
- **New: the `area%` statistic (fraction of pixels above 2 levels) does not discriminate on two of
  the four scenarios.** It reads 8-30 % for two runs of the *same* binary on `lod-horizon` (the sun
  drift above) and saturates near 43 % for almost every pair on `rays-halfres` (the underwater
  animation phase that scenario's own header documents). The statistic that discriminates is the
  previous round's **clustered** one: per-pixel luma difference >= 24 **and** at least 5 of the 25
  pixels in the same 5x5 box also flagged, so isolated dither/quantisation speckle fails the
  connectivity test while a moved ridge or a missing chunk survives it. Report both; where they
  disagree, the clustered statistic is the authority. On `rays-halfres` the clustered figure was
  `0.0000` on essentially every pair of every shot while `area%` flagged two shots — that is the
  shape of an animation-phase false positive. And judge *cross* against the **distribution** of
  same-build (an exact one-sided Mann-Whitney U over the pairs), never against one sample of it.
- **New: the gate could not see R8 at all, and `perf-fly` cannot substitute.** Every `screenshot` op
  in all four pose-stable scenarios passes `hideHud: true` — verified 10/10 `rays-halfres`, 7/7
  `clouds-mirror`, 4/4 `lod-horizon`, 6/6 `entity-binds` — and `Program.cs:563-564` skips
  `world.RenderUI` on those frames, so no HUD text reaches any gate shot. `perf-fly.json` hides
  nothing, but it exercises only **one** of R8's four sites: the compass readout and the coordinate
  line are gated on `HasCompassEquipped(inventory)` (`RenderManager.cs:2458`) and `perf-fly` gives the
  player nothing, so neither ever draws, and with no items there are no hotbar counts either. A HUD
  check needs a throwaway scenario that **equips a compass last** (`VisualDirector`'s `give` calls
  `EquipStackable(SelectedIndex)`, so each `give` overwrites the previous one) and is run with
  `VOXEL_DEBUG=0` so the profiler panel does not cover the leftmost hotbar slots, which is where
  `give` always lands its stack. Two such scenarios were driven from the scratchpad on both trees and
  rendered character-identical text (`N 000` / `SE 124` / `W 269` / `E 090`;
  `XYZ 8.5 / 1.6 / -12.5`, then `XYZ -0.0 / 70.5 / -0.0`, then `XYZ -0.0 / 2.6 / -0.0`; hotbar `178`
  -> `185` after a give -> `59` after a block swap), which also demonstrates the cache invalidating
  rather than sticking.

**The C3 gate rule, and how it was discharged.** C3 changes *when* chunks arrive, never what they
contain, so on a streaming-heavy scenario **a chunk arriving a frame earlier or later is not a
failure; a chunk that never arrives is.** The discriminator is same-tree repeatability: a difference
that varies between two runs of one tree is sampling; a feature present in all runs of one tree and
absent from all runs of the other, at a shot taken well after its teleport, is a real change. Both
halves were checked. On `lod-horizon`, at all four shots, **all eight runs report identical loaded
chunk counts (17077) and identical LOD region counts (718 / 1265 / 926 / 1259) with bit-identical
camera poses** — nothing late, nothing missing. On `clouds-mirror`, whose sky shots are taken ~7 s in
and *are* mid-stream, the working tree was consistently **ahead** (1256-1296 LOD regions against the
baseline's 943-1161, with one baseline run not even finished loading chunks) — i.e. more far terrain
present, never less. The decisive control, and the reusable technique: **raise the scenario's opening
`wait` and re-run both trees.** With the opening wait taken from 8 s to 55 s (the modified scenario
kept in the scratchpad and passed by absolute path, so nothing was written into either tree), all four
runs reach exactly 17077 chunks / 1296 regions at every shot and a **cross-tree** pair becomes the
most similar of all (sky region clustered `0.0000`) while a same-build baseline pair does not. Two
different trees produced a near-identical frame; two runs of the same tree did not.

One scraping trap found the hard way: the `[visual] saved <path>` log line is printed several frames
**after** the shutter, by which time the director has already run the next teleport. Key any log
scraping on the `[visual] N/M screenshot ->` line instead; keying on `saved` reads the wrong frame's
pose and chunk counts entirely, and it cost one wrong diagnosis here.

### Measurement: C3's generation rate separates cleanly; nothing else is quotable

The four chunk-gen worker threads are covered by **no profiled section**, so no `FrameProfiler` row
can measure C3 and none was reached for. The honest instrument already exists, and the next round
should not have to rediscover it: under `VOXEL_DEBUG=1` the per-second `perf` line carries
`chunks {N}/{V}` (`FrameProfiler.cs:272`), where `N` is `ChunkManager.LoadedChunkCount`
(`ChunkManager.cs:80`). **`N` is a NET counter** — incremented in `QueueChunk` (`:582`), decremented
in `UnloadDistantChunks` (`:209`) — so the quantity is a *net loaded-chunk delta*, never "chunks
generated"; each `lod-horizon` leg begins with a mass purge of the previous ring, and that bias is
identical on both trees.

**Capture rule, applied identically to both trees.** Both trees built Release and run `--no-build`;
the baseline is a `git archive HEAD` export re-verified pristine before use; one session, strictly
serial, never concurrent, each run from its own tree's `OpenGLTest/` directory:
`VOXEL_DEBUG=1 VOXEL_TIME=10 VOXEL_SCRIPT=visual-examples/lod-horizon.json dotnet run -c Release
--no-build`. One cold baseline run first, **discarded**. Then 4 runs per tree in a **counterbalanced**
order — `b1, w1, b2, w2, w3, b3, w4, b4`, i.e. two pairs baseline-first and two pairs work-first. The
window is leg 1, everything before the script's *second* teleport: drop the run's first `perf` line
(it covers the world-load burst), then take consecutive lines from there until `N` first reaches 99 %
of its maximum over the leg; the run's value is `(N_end - N_start) / intervals`, one interval counted
as one second. Compare the trees by the **spread** of per-run values, never by a single pair.

**Result — net loaded-chunk delta per second, leg 1 of `lod-horizon`:**

| Tree | Per-run values | Spread | Median |
|---|---|---|---|
| Baseline (672c113) | 2498.0, 2573.8, 2770.0, 2732.8 | 2498.0-2770.0 | 2653.3 |
| Working tree (C3) | 3203.7, 3190.7, 4326.0, 4221.5 | 3190.7-4326.0 | 3712.6 |

The spreads are **disjoint** (baseline max 2770.0 < working min 3190.7), the ratio of medians is
**1.40**, and the exact one-sided permutation p = 1/70 = **0.0143** — the smallest value attainable
at n = 4 + 4. All four pairs separate in the same direction regardless of which tree ran first, which
is what the counterbalancing was for. A second, fixed-duration form of the same capture (net delta
over exactly the first **two** intervals of the window, which removes the whole-second quantisation
of the window end) is also disjoint in the same direction: baseline 5333-6098, working 7602-8675,
ratio 1.54, same p. The 8 `lod-horizon` gate logs from the visual step — a different,
*non*-counterbalanced design in an earlier session, so explicitly not part of this capture and not
mixed into the spreads — are disjoint the same way (baseline 2409.8-2817.0, working 3070.7-4244.0).

Caveats, all real. A `perf` line publishes on the first frame at or past 1 s of accumulated wall time
and the accumulator is then *zeroed* rather than decremented (`FrameProfiler.cs:203`, `:281`), so an
interval is >= 1 s by up to one frame; counting it as 1 s overstates both trees — ~0.9 % baseline
against ~1.3 % working tree, a ~0.4 % edge to the working tree, two orders below the ~40 % gap, so it
does not create the separation. The in-window mean frame times are **not** usable as a frame-cost
claim, because the working tree's window spans 2-3 intervals against the baseline's 4 and therefore
covers only the densest part of the burst; no frame-time conclusion is drawn. And a single run is
coarse, since the window end is quantised to whole seconds — four runs per tree with disjoint spreads
is what makes the figure quotable; one pair would not.

**Why the rate moves, mechanistically.** During the fill the near ring is already loaded, so the
first needed offset sits out near the rim and the pre-change scan walked most of the 17077-entry list
before **every** generated chunk, on each of four threads. The frontier amortises that walk to
roughly once per centre, which holds because `UnloadDistantChunks` runs only on a chunk-border
crossing or every 120 frames (`ChunkManager.cs:171`) and the bump is gated on `removed > 0`, so
version bumps batch into the crossing frame instead of arriving per unloaded chunk. Measured on the
real array single-threaded, a full fill costs just over **2 probes per offset in total** (each index
is probed once as the scan walks past it, and once when the next call re-tests the offset it
returned, since a hit publishes `i` and not `i + 1`);
`ChunkScanFrontierTests.TheScanCostIsAmortisedNotPerCall` pins that under 3x, a bound the pre-change
O(offsets)-per-call scan misses by four orders of magnitude.

**The settled case is arithmetic off the source, not a measurement.** `LoadedChunkCount` converges to
exactly 17077, which is exactly the offset count, so on a settled world every offset is in `Chunks`
and `NeedsChunk`'s `&&` short-circuits after **one** `ContainsKey`. 17077 offsets x 1 probe x 4
threads x 5 scans/s (`GenerationDelayIdleMs` = 200 ms) is on the order of **342 000 dictionary probes
a second before the change and none after** — the frontier is converged, `Publish` early-returns, the
loop body never executes and no `Node` is allocated, so a 200 ms poll costs one volatile read and two
comparisons per worker. 683 000 is the upper bound if neither dictionary held the position; the older
"two `ContainsKey` per offset" framing over-counts the settled case for exactly this reason.

No `perf-fly` capture was run this round, so the profile table at the top of this file is untouched by
it, exactly as it was untouched by the R5/R7/R11 round.

### Carried forward from earlier rounds — still true, still load-bearing

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
- `lod` measures only the main-thread `LodManager.Update` while `LodRegion.BuildMeshData` runs on the
  LodGen thread, so size any further LOD work with a **LodGen throughput capture** during the #1
  streaming hitch rather than off a `lod` row.

Next: **C5**, then **C12 + D3** as one cheap sweep. **C5** is the largest remaining item still aimed
at hitch #1 — chunk streaming is still the only phase that actually drops frames, and `lod` is its
top row at up to 10.7 ms. Region builds are pure and independent, which is the right shape for a small
worker pool, and this round has just demonstrated that the streaming burst *is* measurable with a
scripted capture rather than a profiler row, so C5 can be priced honestly: use a **LodGen throughput**
capture during the burst (regions built per second, the same shape as the net loaded-chunk rule
above), not the main-thread `lod` row, which only measures `LodManager.Update`. The thing to design
for is **contention**: C5's pool would compete for cores with the four chunk-gen threads — now
hungrier per unit time, since C3 removed the scan that was throttling them — and with the mesh pool.
`R10`'s own bullet says to hold R10 until C5 has been measured, so C5 also unblocks the biggest
remaining render item. Then **C12 + D3** as the cheap sweep: C12's parts (b) (`Chunk.GenerateBlockData`'s
scalar solid recount, which `BlockCount - _blocks.AsSpan().Count(BlockType.Air)` does vectorised) and
(c) (`LodManager.RemoveRegion`'s linear `List.Remove` on `_pendingCover`) are one-liners, and D3 is
nil-impact dead code that rides along with anything — but **C12(a) must carry its own draw-order
argument**: the four particle lists are drawn in order under `SrcAlpha`/`OneMinusSrcAlpha`, so
replacing `RemoveAt(0)` needs a head index plus a **modular walk in the renderers**, not a swap-remove,
and that part is not mechanical. **C6** is next-after — it shares `UnloadDistantChunks` with C3's
counter, so whoever takes it should re-read the standing obligation above before touching that method.
**D1** (packed chunk keys) is a broad-but-mechanical L, best taken on its own, and it would also make
the frontier's per-probe `NeedsChunk` cheaper. **D2** is M and pays mainly on the teleport hitch.
**D5** is measurement-only: log `FreeBlockCount` on the pool-growth line and capture it mid-stream and
just after a teleport — take the measurement, not the rewrite, since its own bullet doubts there is a
win. **R6** is S but is a *trade-off* rather than a win — gating the GPU timing queries on the overlay
gives up the deliberate "the first second after `X` already has data" property — so it wants a human
decision, not an autonomous round. **R10** remains the biggest structural render win but is L, and R5
landing narrowed its case to the per-region VAO/draw/uniform churn alone. **R3** stays parked — read
its bullet before re-opening it.

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
