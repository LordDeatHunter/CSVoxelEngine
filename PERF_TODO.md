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
- [ ] **R8 · UI per-frame string allocations** — `UIRenderer.cs:344` (`FPS`),
  `:397` (compass), `:435` (coords), plus the hotbar's `.ToString()` counts. Format
  only when the value changes, or into a reusable buffer; feeds settled GC.
  **Impact low · risk low · effort S.**
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
  churn as regions stream. Price it on those terms alone.
  **Impact med-high · risk med · effort L.**

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

**Suggested first pass:** R5, R7 and R11 landed this round, as one pass. All three are
output-preserving bookkeeping with no visual surface, so the gate looked for *zero* structural
change rather than for an acceptable difference. **R3 is unchanged and still parked** — its bullet
above carries the finding.

**R5a — the program cache, correct by construction.** `Shader.Use()` (`Shader.cs:103-108`) now holds
a `private static uint _boundProgram` and returns without a GL call when that handle is already
bound. One static field covers all twelve shader-owning classes with **no invalidation list at
all**, because there is exactly one `glUseProgram` call in the tree. The check that proves it, and
the one to re-run before trusting the cache:

```
grep -rn "\.UseProgram[(]" OpenGLTest/src --include=*.cs
```

It must return exactly one line, inside `Shader.Use()`. **Use that spelling.** Two plainer forms
cannot return one line and will mislead whoever runs them: the unqualified `grep -rn "UseProgram"`
also matches a doc comment in `RenderManager.cs` ("...survive any number of `UseProgram`
switches"), and the plain-paren `"\.UseProgram("` also matches `Shader.cs`'s own comment, which
quotes the grep and therefore matches itself. The bracket class `[(]` matches the real call and not
the comment. `Dispose` clears `_boundProgram` when it deletes its own handle so a recycled GL name
can never produce a false hit; shaders are only disposed at shutdown, so that is belt-and-braces.

**R5b — the texture-unit cache, and why there are three primitives rather than one.**
`OpenGLTest/src/Systems/TextureUnitCache.cs` is a GL-free `internal sealed class` (the
`CloudCopies` / `HalfResTarget` / `ReflectionReuse` precedent) tracking units 0-4 plus the active
unit. `RenderManager` drives it through three private primitives sitting just above
`BindWorldTextures` (`RenderManager.cs:1310`, `:1324`, `:1338`):

- `BindUnit(unit, target, tex)` **stays eager** — it always makes the unit active (the cache may
  still skip the `glActiveTexture` itself) and then skips only a redundant `glBindTexture`. This is
  the load-bearing design decision of R5 and the one a future reader is most likely to try to
  "simplify" away. It exists because three things bind on *whatever unit is live*: `CopyScene`'s
  `glCopyTexSubImage2D` (`RenderManager.cs:1033-1043`), the three `Create*Target` methods (whose own
  comment at `:977-978` states they assume unit 0 on entry), and `TextureLoader.Upload`
  (`TextureLoader.cs:91,116`), reached lazily from `ItemMeshCache` *inside* the entity pass. A lazy
  select would change the active-unit trajectory at those points; eager keeps it byte-identical to
  pre-R5 at every point in the frame.
- `BindLazy` skips the select too, and is used **only inside `BindWorldTextures`**, where the proof
  is local: nothing between its five binds reads the active unit, and the method ends with an
  unconditional `ActiveUnit(0)` restoring its documented postcondition. Do not use it anywhere else
  without repeating that argument.
- `InvalidateTextureUnits()` (`:1388`). **Its doc comment is the canonical audit** — every site that
  can change unit 0-4 or the active unit behind the cache's back, plus the matching "provably cannot
  invalidate" list. Read it there rather than trusting a copy here: it is maintained beside the code
  and this file is not. Two of its entries look redundant and are not — the
  `ItemMeshCache.GlStateChanged` callback, which fires from `BuildTexture` because item textures are
  built *lazily mid-pass*, and the note that `RenderSun` early-returns *before* its unit-0 bind on
  night frames, which the negative control below turned into the load-bearing sentence of the whole
  audit.

`BindWorldTextures()` is now five `BindLazy` calls plus that trailing `ActiveUnit(0)`. Thirteen
other render-path bind sites were routed 1:1 through `BindUnit`/`ActiveUnit`; every create/delete
site was left raw and paired with an invalidation entry. The pre-existing gap where `RenderShadows`
unbinds unit 1 and leaves it unbound until the next `BindWorldTextures` is **reproduced, not
fixed** — this round was output-preserving, and an implementer who "corrects" it breaks that.

**R7 and R11 are key-validated, not dirty-flagged.** `Camera.GetViewProjection()` and the new
`Camera.GetViewFrustum()` both go through one `EnsureViewCache()`, keyed on a `ViewProjKey` record
struct holding *exactly* the mutable fields the two builders read — `Position`, `_shoulderViewDir`,
`_shoulderOffset`, `Yaw`, `Pitch`, `_roll`, `_bobHeight`, `_bobStrafe`, `_currentFov`,
`_aspectRatio` (`NearPlane`/`FarPlane` are `const` and deliberately excluded) — compared
**bit-for-bit** through `BitConverter.SingleToInt32Bits`. The record's generated equality is
deliberately unused: `float.Equals` calls `+0f`/`-0f` equal and `NaN`/`NaN` equal, which would break
the argument. That argument is: a hit means the inputs were bit-identical, so the recomputation
would have produced a bit-identical matrix — output preservation is a **proof**, not a hope, and it
needs none of the wiring the bullet's literal "dirty-flag it" would have required at the ~14 camera
write sites (`Update`, the `OnMouseMove` callback, `TestTeleport`, `PushOutOf`, the
`FramebufferResize` lambda). Matrix and frustum share the one key and the one miss branch, so they
cannot drift apart. `GetViewMatrix()` and `GetProjectionMatrix()` were left **uncached on purpose**:
they are the correct answer for the three standalone mirror `GetProjectionMatrix()` calls, and they
are the tests' oracle.

R11's chunk half stores `WorldMin`/`WorldMax` on `Chunk`, computed in the constructor with
character-for-character the expressions the passes used, so the values are bit-identical; `Position`
is get-only and assigned once, so there is no invalidation path at all. Note that R11's bullet named
only `RenderWorld` and `CullWater` as re-deriving the camera frustum — **`RenderLod` built the
identical third one from the identical matrix** and was taken too. Its *region* AABBs are
per-region, not per-chunk, and stayed out of scope, as did the shadow and mirror frusta
(`_lightViewProj`, `_reflViewProj`).

**What the tests pin, and what only GL covers.** 30 new facts across
`CameraViewProjectionCacheTests` (16), `ChunkBoundsTests` (3) and `TextureUnitCacheTests` (11); the
suite went 417 -> 447 with the same two known failures
(`BlockMovementTests.LeavesDeclareThreeQuarterMovementSpeed`,
`FallingBlockTests.SevenTowerChainRebuildsFullyWithNoDrops`). The camera class carries **one
single-field fact per key field**, driven through an `internal TestSetViewState` seam added for
exactly that, precisely because an oracle-only fact —
`Assert.Equal(cam.GetViewMatrix() * cam.GetProjectionMatrix(), cam.GetViewProjection())` — **passes
with no cache at all**, and a walking drive moves several key fields at once, so a field *dropped*
from the key would be invisible. `KeyHoldsExactlyTheMatrixInputs` reflects the key's shape against a
hard-coded allowlist, making an edit to the key a conscious act. What those facts **cannot** catch
is a *new* `Camera` field wired into `GetViewMatrix` tomorrow with no matching key entry; that
failure mode is visible (a camera input that stops working), not subtle. `Frustum` is a readonly
struct of private planes with no equality, so the frustum facts compare *behaviour*:
`IntersectsAABB` over a fixed lattice, with both outcomes required to occur so no fact can pass
vacuously. Covered **only** by GL and by nothing headless: `Shader.Use`'s actual skip and the actual
`glActiveTexture`/`glBindTexture` elision. `Shader` owns a GL handle with no GL-free seam, and R5
adds no new *arithmetic* to `RenderManager`, so following the R12 precedent the GL call sites got no
unit test. Those halves are covered by the harness below and by the screenshot gate.

**The invalidation list was proved by measurement, not by reading.** A temporary `VOXEL_BINDCHECK=1`
harness (R12's readback precedent) verified *every skipped call* against GL: `GL_CURRENT_PROGRAM` in
`Shader.Use`, the cache's **recorded** active unit in `ActiveUnit`, and the unit's real binding in
`BindUnit`/`BindLazy` — the latter saving and restoring the live active unit so the harness could
not corrupt the trajectory it was measuring. Counters sat behind a *separate* `VOXEL_BINDSTATS=1`
flag, so a counting run carries no `glGetInteger` stalls and its timing stays usable. **Eleven
scripted runs, 33 504 frames, 2 717 848 verified skips (1 789 480 program, 298 662 active-unit,
629 706 bind), zero mismatches of any kind.** The two non-scriptable entries —
`CreateReflectionTarget`'s recreate path and `SetViewport` — were driven **mechanically rather than
by hand**, which is a departure from the plan and a better one: a probe fired inside the render loop
at fixed frames in all eleven runs, changing `GameSettings.ReflectionIndex` three times and resizing
the viewport down and back. `Program.OnFramebufferResize` does nothing but call
`RenderManager.SetViewport`, so the `RenderManager`-side path an OS resize reaches is exactly the
one the probe reached; what is *not* covered is Silk's event plumbing above it, which touches no
texture state. All instrumentation was removed afterwards —
`grep -rn "VOXEL_BINDCHECK\|VOXEL_BINDSTATS" OpenGLTest/` is empty.

**The negative control did not behave as predicted, and that is the most reusable thing this round
learned.** Deleting the UI pass's invalidation entry alone (the plan's chosen control): **zero**
mismatches. Deleting `BeginFrame`'s hard reset alone: **zero**. Deleting *both*, on a daytime run:
still **zero**. Only with both gone *and* the run at night (`VOXEL_TIME=300`) did the check fire —
2437 mismatches over 971 frames on `basic.json` (2.51/frame) and 5097 over 2053 frames on
`clouds-mirror.json` (2.48/frame), linear in frames rendered and every one the same shape,
`unit 0 Texture2DArray expected 1 actual 26`: the atlas expected, the UI's own texture actually
bound. Restoring both entries returned the identical night run to 0 mismatches over 972 frames. The
mechanism is that the two entries are **mutually redundant covers for one hazard** — the UI pass's
raw unit-0 bind crossing the frame boundary — and that on a day frame `RenderSun` rebinds unit 0
before anything can skip; only at night, where it early-returns *before* its bind, does the stale
record survive to be skipped. Both entries stay: the redundancy is defence in depth. The lesson for
any future state cache here is that a single-entry deletion is not a sufficient control, and that
the sun's day/night branch is a real state divide a daytime-only sweep will miss.

**Measurement: the skipped calls are large in count and invisible in the profiler, and no timing is
quoted as a saving.**

- *Arithmetic off the source.* `BindWorldTextures()` has **18** call sites and HEAD's body issued
  **10** raw GL calls each (5 `glActiveTexture` + 5 `glBindTexture`) — up to 180 client-side calls a
  frame. After R5, a fully-matching replay costs **0** and the entity-pass restore after an
  item-sprite swap costs **1**, both pinned by `TextureUnitCacheTests`. **Do not generalise 10 -> 0
  to the thirteen `BindUnit`-routed sites:** they keep their eager select and save at most the one
  redundant bind each. `worldShader.Use()` has 20 call sites, and `SetWorldUniform` /
  `ApplyFrameUniforms` each flip two programs per call.
- *Measured, and only from `VOXEL_BINDSTATS` runs.* Capture rule: from `OpenGLTest/`,
  `VOXEL_BINDSTATS=1 VOXEL_DEBUG=1 VOXEL_TIME=60 VOXEL_SCRIPT=visual-examples/<s>.json dotnet run -c
  Release --no-build`, one run each; counters are cumulative from process start (load frames
  included), read off the single final line printed in `Dispose` and divided by the frame count on
  that same line. `basic.json`, 1083 frames: **85.29** skipped `glUseProgram`, **9.07** skipped
  `glActiveTexture`, **17.00** skipped `glBindTexture` per frame — **111.4** client-side GL calls
  elided a frame. `perf-fly.json`, 6943 frames: 86.99 / 8.39 / 20.40 — **115.8** a frame. Worth
  saying plainly, since R5's bullet framed the item as a texture-bind problem: **the program half
  alone is larger than both texture halves combined.**
- **Never take a timing from a `VOXEL_BINDCHECK` run** — every skip there carries a `glGetInteger`
  pipeline stall and those runs render at roughly half rate. Skipped-call counts come from
  `VOXEL_BINDSTATS`; timings never come from `VOXEL_BINDCHECK`.
- *R7 / R11 arithmetic.* 20 static `GetViewProjection()` call sites remain in `RenderManager` (21
  before R11 took `CullWater`'s), but the per-*frame* number is better than the site count suggests:
  every `camera.*` reference in `RenderManager` is a read and `Camera.Update` runs before the render
  block (`Program.cs:321` against `:541`/`:544`/`:548`/`:565`), so the pose is fixed across the
  frame and the rebuild count is **at most one per frame** — one `CreateLookAt`, one
  `CreateFromAxisAngle`, one `CreatePerspectiveFieldOfView` and one 4x4 multiply. `new Frustum(` in
  `RenderManager` went **5 -> 2** (only `_lightViewProj` and `_reflViewProj` remain), each build
  being six plane extractions and normalisations; `chunk.Position * Chunk.Size` went **8 -> 0** and
  `new Vector3(Chunk.Size)` **4 -> 0**, removing a three-multiply-plus-three-add per renderable
  chunk per pass across four passes.
- **Timing: no profiler row resolves the change on either scenario, and nothing is quoted.** Capture
  rule, applied identically to both trees: both built Release and run `--no-build`, same session,
  strictly serial and interleaved, the baseline given a cold discarded first run, `VOXEL_TIME=60`
  fixed, `VOXEL_DEBUG=1` on both; the instrument is `FrameProfiler`'s per-second `perf` line, the
  first line of every run dropped (it covers the world-load burst), a run's value for a row being
  the mean of that row's per-second CPU averages, and the trees compared by the **spread** of
  per-run values rather than by a single pair. `perf-fly.json` at 6 runs per tree in a
  **counterbalanced** design (3 pairs baseline-first, 3 pairs work-first) plus `basic.json` at 4
  runs per tree: **every row overlaps**, including every row this round touches. The whole-frame
  means differ by less than half the run-to-run spread, so that difference is not quotable either.
  Why the row cannot resolve it: `FrameProfiler` prints section figures at F2 milliseconds
  (`FrameProfiler.cs:292,303`), a 0.01 ms quantum, while the whole saving is ~112-116 client-side GL
  calls plus at most one matrix rebuild and two frustum builds a frame — and the same-tree
  run-to-run spread on the very rows targeted is already 1-6 quanta. Same situation that left the R4
  round's `cloudPass` and `chunkApply` unresolvable. **"No measurable change, and here is why the
  row cannot resolve it" is the answer**, not a number reached for to fill the gap.

**The visual gate found zero structural change, and it was established by measurement rather than by
eye.** 20 scripted runs across the four pose-stable scenarios — `entity-binds.json`,
`clouds-mirror.json`, `rays-halfres.json`, `lod-horizon.json` — at 5 runs each: 3 against a pristine
`git archive HEAD` export built in a scratchpad and 2 against the working tree, which was never
mutated to produce the baseline. Five runs per scenario give 4 same-build pairs and 6 cross-tree
pairs, so *cross* is compared against the **distribution** of same-build rather than against one
sample of it. Cross-tree pairs came out statistically indistinguishable from same-build pairs, and
on most shots the most-similar pair of all ten is a **cross** pair — which could not happen if the
change had altered output. Six shots flagged on the weaker single-pair test and all six were run to
ground as sampling or streaming effects, none as a rendering change. The three highest-value frames
were also read by eye on both trees: the held block cube, the held **item sprite** (the riskiest
path in the round — the only place a non-atlas `Texture2DArray` lands on unit 0 and is restored by
`BindWorldTextures` inside the same method) and two ground drops all render with their correct
textures.

**Gate machinery, for whoever measures next.** Two scenarios are new or changed this round.
`visual-examples/entity-binds.json` frames what R5 actually risked and nothing checked in did: the
entity pass's unit-0 ping-pong — a held block (cube path, unit 0 = atlas), a held *item* (the sprite
path, which is also what triggers `ItemMeshCache`'s lazy texture build, so the **first** held-item
frame runs a different path from the repeats), ground drops, and three repeats of one pose as a
same-build floor. Every shot passes `hideHud: true`. Its shot numbering is **07/11/12/13/14/19**,
not 1..6, because each `give` needs a grounded `wait` after it: `World.Update` is what refreshes the
held id from the inventory, so a `give` followed straight by a `screenshot` renders the *previous*
held item. Its `give` op needed a one-line change to the visual-test tooling (not to game code):
`VisualDirector.cs:80` is now `var id = ContentRegistry.Require(step.Block!);` — `RequireBlock` is
blocks-only by design, and `Require` returns a **`byte`**, so there is no trailing `.Id`; that
detail already cost one compile. Item ids survive `Inventory.Add`/`EquipStackable` unchanged. Note
also that `19-drops` is a **correct-texture** check and not a pixel instrument: drops bob and settle
on run-dependent physics, so they land in visibly different positions between two runs of the *same*
build. Five traps beyond that, three of them new:

- **New: `wait` ticks physics once per *rendered* frame.** `VisualDirector`'s `wait` calls
  `world.Update(deltaTime, _keys)` once per rendered frame and accumulates real `deltaTime`, so the
  *number* of physics steps for a given simulated duration is **frame-rate dependent**. Any round
  that makes the engine faster therefore lands at a slightly different camera depth and chunk
  streaming state, and a naive pixel gate reads that as a regression. This is the single most
  important thing to know before designing the next gate.
- **New: chunks fade in on the wall clock.** `RenderManager.cs:1150-1151` and `:1810-1812` draw at
  `min(1, lifetime / MAX_LIFETIME)` with `MAX_LIFETIME` = 0.5 s of wall clock, so a distant LOD
  region caught mid-fade differs by up to ~208 levels between runs of the **same** build.
  `clouds-mirror.json`'s `14-sky_level` and `16-sky_closeup` both frame such a region, and because
  it straddles about 1 % of the frame the **full-frame p99 flips between 1 and 122** depending on
  which side of the percentile it lands — p99 is not a usable whole-frame statistic on those two
  shots. Use the area statistic (% of pixels above 2 levels) or a region crop.
- **New: one same-build pair versus one cross-tree pair is not a sufficient gate** for an
  output-preserving round. It produced four false alarms here that only extra runs cleared. Budget
  5 runs per scenario from the start. The same lesson applies to timing: with 3 runs per tree a
  single row *will* separate by chance — this round's first capture flagged `lighting` as
  work-lower, which is mechanistically impossible (`SetLighting` holds the frame's cache **miss**,
  which now does strictly more work than before), and the check that caught it was **reversing the
  interleave order**, not adding more runs of the same order.
- **The floor is not uniform across the frame.** In `clouds-mirror.json`'s mirror shots the sky
  region is an essentially exact instrument (a level or two) while the water region floors in the
  *hundreds* — the wave normal is animated from wall-clock `uTime`, so the surface is in an
  arbitrary phase cross-run. Compare per region, never as one whole-frame number, and treat the
  water region as a gross-breakage check rather than as a measurement.
- **Session effects exceed the quantity being measured.** The *same* pristine binary read
  `cloudPass` gpuAvg 1.460, 1.501 and 1.551 in three different sessions. Re-run the baseline in the
  same session as the change, interleaved run for run; never difference against stored captures from
  an earlier session, and give the baseline its own cold first run — a freshly built binary streams
  more slowly on its first run, which moves both the loaded chunk set and the wall clock at every
  shot.

Carried forward, still true: `clouds.json` is a sky-side regression check and **not** a measuring
instrument (every shot but the first follows a mid-air teleport plus a `wait`, and `wait` ticks the
world, so the camera free-falls before the shutter; `clouds_up` alone is pose-stable). A 3-run floor
under-samples the same-binary tail on shots where the differing fraction sits near the p99 knee
(`clouds_up`, `clouds_closeup`, `sky_closeup`). `perf-fly.json`'s `screenshot` ops do **not** pass
`hideHud`, so its shots bake in the live profiler HUD across roughly x 0..570 — numbers that change
every frame, which alone puts about half the frame's pixels above 2 levels *within* one binary; mask
those columns or give the scenario `hideHud: true`. `water.json`'s four underwater shots are **not**
a usable instrument (free-falling camera plus drifting bubbles give a 15-25 level same-build floor).
Any frame-to-frame statistic must be normalised for wall-clock sun drift before it is compared
across builds, or it reads backwards. And `lod` measures only the main-thread `LodManager.Update`
(`World.cs:241-243`) while `LodRegion.BuildMeshData` runs on the LodGen thread, so size any further
LOD work with a LodGen throughput capture during the #1 streaming hitch rather than off a `lod` row.

Next: **C3**, then **C7 + C11 + R8** as one cheap sweep. C3 is the largest un-taken item aimed at
hitch #1 — chunk streaming, still the only phase that actually drops frames (100+ fps settled down
to 25-55 while streaming) — and it is an algorithmic change to `FindClosestNeededChunk`, distinct
from the ruled-out *worker priorities*. It is also the right shape for this workflow: the method is
GL-free, so "the same chunk is chosen" can be pinned headlessly against the current scan exactly as
`ChunkBoundsTests` pinned the AABB arithmetic and `CloudCopies` pinned the cloud offsets, and it
needs no perceptual gate. Its one real risk is that four worker threads would share the new
frontier/cursor, so the round's design work is the concurrency argument, not the scan. C7, C11 and
R8 then bundle as one cheap pass with no visual surface: C11 has a **documented in-repo precedent
for the exact replacement** (`ChunkManager.cs:139-141`, `:251-252` — a lock-free `foreach` over the
dictionary instead of a key snapshot), C7 is a one-field delegate cache, and R8 feeds settled GC;
all three are S and one file each. **C12** is the same shape but keep it separable rather than
folded in: its part (a) is *not* purely mechanical, since the particle lists are drawn in order
under alpha blending, so a head index needs a modular walk in the renderers and a draw-order
argument, while (b) and (c) are one-liners. **R6** is S but is a *trade-off* rather than a win —
gating the GPU timing queries on the overlay gives up the deliberate "the first second after `X`
already has data" property — so it wants a human decision before it is taken, not an autonomous
round. **D5** still says *measure first* and its own bullet doubts there is a win; if it is picked
up, take only the measurement (log `FreeBlockCount` on the pool-growth line and capture it
mid-stream and just after a teleport), not the rewrite. **R10** remains the biggest structural
render win but is L, and R5 landing has made its case **narrower, not wider** — the bind saving is
gone and only the per-region VAO/draw/uniform churn is left. Hold it until C5 and C11 have been
measured, since they may move the `lod` row enough to re-price it. **D1** is broad-but-mechanical L
and is best taken on its own; **D2** is M and pays mainly on the teleport hitch; **D3** is
nil-impact dead code that can ride along with anything. **R3** stays parked — read its bullet before
re-opening it.

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
