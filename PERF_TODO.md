# Performance TODO

Baseline (2026-09-07): ~60 FPS average on a 10-core / 16-thread PC, Debug build launched from
Rider's default run configuration. VSync and the Silk.NET frame/update limiters are already off
(`WindowManager.cs`), and the on-screen FPS counter (`Program.OnRender`) measures render frames
correctly, so the 60 FPS is a real per-frame cost floor rather than a cap.

Work the items top to bottom, one per session. After each item: build Release, run, press `X`,
record before/after FPS and the per-section ms from item 1's overlay in the "Measurements"
table at the bottom, then tick the box.

## Ruled out by the audit (do not re-check)

VSync / frame limiter; GL debug context (removed in 2242e44); per-frame `Console.WriteLine` or
`gl.GetError`; worker thread priorities (BelowNormal, sleep when idle, 4 gen + 1 LOD + 4 mesh
tasks on 16 logical CPUs); fluid tick (scheduled cells only, capped per frame); shader ALU cost
(4-tap PCF, 8 point lights); uniform *location* lookups (cached in `Shader`); mesh upload path
(indexed, old mesh disposed).

**Early-Z / `discard` split (item 5, measured 2026-09-07 on the RTX 3070):** the alpha-test
`discard` in `test-fragment.glsl` does not cost early depth rejection on this GPU — the driver
keeps the early depth *test* and only defers the depth *write*. Splitting every chunk into a
solid range (program without `discard`) and a cutout range (program with it) added a second draw
call (and uniform set) for every visible chunk holding leaves and made GPU time worse, not better: spawn 1.72 -> 1.87 ms, forest 1.83 -> 2.13 ms
(cutout-first order: 1.98), and CPU `worldPass` +0.15 ms from the extra draws and uniform
uploads. Do not retry unless a different GPU shows `worldPass` GPU time dominated by hidden
fragments; the mesher/renderer change is preserved in the stash named in item 5.

## Items

- [x] **1. Frame-time instrumentation.** (done 2026-09-07: `src/FrameProfiler.cs`, `X` overlay,
  per-second `perf` console line, startup build/GPU line; see Measurements) New `src/FrameProfiler.cs` with named `Stopwatch`
  sections around each update phase (`Camera.Update`, `ChunkManager.Update` split into
  unload/apply/dirty, `LodManager.Update`, fluid, drops/TNT/toppling, raycast) and each render
  pass (shadows, LOD, world, translucent, UI), plus one GL `TIME_ELAPSED` query per frame with a
  2-deep ring (never stalls) to separate CPU-bound from GPU-bound. Show the 1 s averages in the
  `X` debug overlay (`World.RenderUI` / `UIRenderer.DrawFps`) and append them to the once-per-
  second console debug line in `Program.OnUpdate` so a headless log captures them. Print
  `Debugger.IsAttached`, `#if DEBUG` configuration and `GCSettings.IsServerGC` once at startup.
  Done when the overlay breakdown roughly sums to the frame time.

- [x] **2. Run Release by default.** (done 2026-09-07: `Directory.Build.props` defaults
  `Configuration` to Release, AGENTS.md commands say `-c Release`; the Rider solution
  configuration dropdown is local state and must be switched by hand) Switch the Rider run configuration to Release. Add
  `Directory.Build.props` at the repo root with
  `<Configuration Condition="'$(Configuration)'==''">Release</Configuration>` so plain
  `dotnet run` / `dotnet build` default to Release; update the commands in `AGENTS.md` to say
  `-c Release` and note Debug is several times slower in the voxel loops. Keep Debug itself
  unoptimised so breakpoints work. Re-measure and record.

- [x] **3. Stop snapshotting the chunk dictionary; render from a flat list.** (done 2026-09-07:
  `ChunkManager.Renderable` + `Chunk.RenderIndex`, `LodManager.RegionList` + `LodRegion.ListIndex`,
  unload scan only on camera-chunk change or every 120 frames, `ProcessDirtyChunks` enumerates
  lock-free and drops chunks missing a neighbour since `QueueChunk` re-dirties them on arrival,
  `ChunkManager.MeshVersion` and `LoadedChunkCount` added; see Measurements)
  `ConcurrentDictionary.Keys/.Values` lock every segment and copy the whole table (~17k chunks,
  the load volume is a full 3D sphere of radius 16) into a new list on every call. Today that
  happens 4x per frame (`ChunkManager.UnloadDistantChunks`, and `World.Render` passing
  `Chunks.Values` to three passes) plus `Regions.Values` twice, and every pass then walks all
  ~17k chunks to skip the unmeshed ones. Fix: `ChunkManager` keeps a main-thread
  `List<Chunk> Renderable` (chunks with `HasMesh || HasTranslucentMesh`, maintained in
  `ApplyCompletedMeshes` / `ImmediateRemesh` / unload, `Chunk.RenderIndex` for O(1) swap-remove)
  and the render passes take `IReadOnlyList<Chunk>`. Same for `LodManager.Regions`.
  `UnloadDistantChunks` uses the plain lock-free `foreach (var kvp in Chunks)` enumerator and
  runs only when the camera chunk changed. `ProcessDirtyChunks` replaces `.Keys.ToArray()` +
  full sort with a reusable list and a partial selection of the closest
  `MaxDirtyChunksPerFrame`. Done when `git grep -n "\.Keys\|\.Values" OpenGLTest/src` shows
  nothing on the per-frame path.

- [x] **4. Upload frame-constant uniforms once per pass.** (done 2026-09-07: `ApplyFrameUniforms`
  once per frame in `BeginFrame`, `SetDrawUniforms` = 3 calls via cached locations, `uFlash`
  reset after the TNT loop, `Shader` gained int-location overloads and `GetUniformArrayLocations`,
  item-drop atlas bind hoisted; see Measurements) `RenderManager.ApplyLightingUniforms`
  re-sends ~22 frame-global uniforms plus two 8-element arrays per draw (every visible chunk,
  LOD region, drop, TNT, tree), and `Shader.SetUniformArray3` builds 16 interpolated strings
  per call. Split into `ApplyFrameUniforms()` once per pass after `worldShader.Use()` and a
  per-draw path setting only `uModel`, `uMVP`, `uAlpha` (and `uFlash` where TNT needs it).
  Cache array element locations as `int[]`. Done when the world pass issues 3 `glUniform*`
  calls per chunk.

- [x] **5. Restore early-Z: separate cutout faces from solid faces.** (implemented and measured
  2026-09-07, then REVERTED: it regressed both views, see Measurements and "Ruled out". The full
  change, 7 files + 1 test, is kept as `git stash` "PERF_TODO item 5: early-Z cutout/solid
  split"; `git stash show -p stash@{0}` to inspect, `git stash apply` to revive on a GPU where the
  discard actually costs early-Z.) `test-fragment.glsl` has a
  data-dependent `discard` and is used for every opaque draw, which disables early depth
  rejection for all terrain. Mesher emits cutout-block quads (`BlockType` `cutout` flag; add a
  flat `bool[]` mirror if needed) into a second index list sharing the vertex list;
  `Mesh` records `OpaqueIndexCount` and can draw an index range. `Shader` gains a `defines`
  prefix; compile the fragment shader twice (solid variant without `discard`, cutout variant
  with `#ifdef CUTOUT`). Draw all solid ranges, then all cutout ranges (one extra `UseProgram`
  per pass). Verify leaf holes, no z-fighting, shadow pass still alpha-tests leaves.

- [x] **6. Recompute LOD buried flags only on change.** (done 2026-09-07: `LodManager.Update`
  reruns `UpdateBuriedFlags` only when `ChunkManager.MeshVersion`, a new `_regionSetVersion`
  (bumped in `AddRegion`/`RemoveRegion`) or the camera chunk changed. `MeshVersion` was widened
  to also bump on a chunk's first mesh — even an empty one, since `IsMeshed` is what the cover
  test reads and air chunks never enter `Renderable` — and on every unload. The bounded slice
  was not needed: while streaming the pass runs at most as often as before. `lod` 0.15 -> 0.01 ms
  at rest; see Measurements. Implemented by opencode + Muse Spark 1.3 from a spec, 2 new tests.)
  `LodManager.UpdateBuriedFlags` runs every
  frame for every region; level-0 regions inside the chunk radius do 16 columns x several
  chunk-Y dictionary lookups each even when nothing changed. `ChunkManager.MeshVersion`
  (bumped whenever `Renderable` changes) already exists since item 3; add a region-set version; rerun only
  when either or the camera chunk changed. Optionally process level-0 regions in a bounded
  slice per frame.

- [x] **7. Cheaper mesh builds (streaming hitches, GC).** (done 2026-09-07: `Chunk.IsFullyEnclosed`
  fast path; new `MeshBufferPool` owned by `ChunkManager` recycles the opaque vertex/index lists,
  returned on the main thread after upload or a stale drop, at most 128 lists kept and lists that
  grew past 4x the rent size dropped; `WorldGenerationConfig.MaxWakeChunksPerFrame = 16` bounds
  `WakeLoadedChunks`; 12 new tests, 51 total. The "flat mirrors" sub-item was already true:
  every flag read goes through `OccludesById`/`PassesLightById`/`CastsAoById`/`FluidById`, and
  the one `ById[]` per solid voxel fetches the renderer and model. Implemented by opencode +
  Muse Spark 1.3 from a spec; the pool cap and the retained-size ceiling were tuned afterwards.
  **What the numbers said:** the fast path fires for only ~2% of builds in this world (672 of
  31k over a run; caves breach almost every underground chunk), so worker build time is
  unchanged (~0.26 ms/build). A pool cap of 16 missed a third of all rents, because up to 64
  builds start in a frame while only 32 lists return per frame; 128 misses <1% while streaming.
  `fluidWake` was already <=0.01 ms on both the land and the sea flight, so the cap is a bound,
  not a saving. Managed allocation over a 23 s flight fell 1566 -> ~800 MB, gen2 GCs 14 -> 4-5;
  the frame time itself did not move. Of the remaining ~800 MB, ~320 MB is still inside builds
  (3.5% of builds outgrow 4096 vertices, max seen 35k, and sea chunks grow the unpooled
  translucent lists) and the rest is worldgen/LOD: candidates for a later item, not for 7.)
  In `Chunk.GenerateMeshData`: fast path
  for fully enclosed solid chunks (all voxels occluding and all six 16x16 neighbour boundary
  planes occluding => empty mesh, skip the 46^3 light BFS), mirroring the existing
  `_solidCount == 0` branch. Pool the `List<Vertex>(4096)` / `List<ushort>(6144)` per worker
  instead of allocating ~200 KB per build. Read flat `BlockType` mirrors instead of
  `BlockType.ById[blockId]` per voxel where only flags are needed. Add `MaxWakeChunksPerFrame`
  to `WorldGenerationConfig` and cap `FluidManager.WakeLoadedChunks`.

- [ ] **8. Move block-edit remesh off the main thread.** (implemented 2026-09-07, NOT yet
  measured: `Chunk.RemeshAfterBatchEdit` now routes self + light radius through `MarkAreaDirty`
  with no `skip` instead of a synchronous `ImmediateRemesh`; `World.FellWood`'s lone-log branch
  goes through the sync single-block `RemoveBlockAt` so a one-block mine still updates
  instantly. No priority set: the dirty budget sorts closest-first and edited chunks sit at
  the camera. Single-block mine/place, bucket `SetCellImmediate` and the `ImmediateRemesh`
  test are untouched. Still to do: Release build, blast/fell timing + 1-2-frame visual check,
  then tick.) `ChunkManager.ImmediateRemesh` runs the
  full mesh build (light BFS included) synchronously per block break/place, and TNT/felling do
  it for every touched chunk in one frame. Route through `StartMeshGeneration` with a priority
  key applied first in `ApplyCompletedMeshes`, or keep sync only for the single edited chunk
  and make batches async.

- [x] **9. Pack the vertex format.** (done 2026-09-08: `Vertex` 48 -> 24 bytes, asserted
  by a new `VertexPackingRoundTrips` test; constructor signature unchanged so `Chunk.cs`,
  `LodRegion.cs` and `Primitives.cs` emit exactly as before. UV as normalised ushort, Sun/Block/
  Sway as bytes, normal as a 0..5 face-index byte in `CubeFaces` order decoded by a nested
  ternary in `test-vertex.glsl` (sloped fluid tops and spun pebbles snap to their dominant
  axis; no shipped model uses rotation). `Mesh.cs` attribs re-pointed; shadow/ui/break/
  selection shaders needed no change (locations 0/1/2/6 keep their semantics).
  `TopplingTree` writes 255/0 bytes; pool comment updated to ~96 KB per list. 71 tests pass.
  See Measurements: frame rate unchanged within noise, screenshots identical.)
  `Vertex` is 48 bytes: float normal (6 possible directions),
  three floats for 8-bit quantities, float UVs. Target ~24 bytes (normal as face index byte
  reconstructed in the vertex shader, UV as normalised `ushort`, `Sun`/`Block`/`Sway` as
  bytes). Touches `Vertex.cs`, `Mesh.cs`, `Chunk.cs`, `LodRegion.cs`, `test-vertex.glsl`,
  `shadow-vertex.glsl`. Do last.

- [ ] **11. Binary greedy mesher + GPU draw path** (tracked phase by phase in `MESHER_PLAN.md`;
  see its Status section). Phase 1 done 2026-09-08: every mesh texture is a `TEXTURE_2D_ARRAY`
  (block atlas one slice per tile, sprites/font/crosshair/sun single-slice), `Vertex` 24 -> 32
  bytes with float tile-local UVs and a `ushort Layer` at attribute location 7, all eight
  world/shadow/UI/sun shaders sample `sampler2DArray`. This is the prerequisite for merged quads
  to tile their texture; it changes no geometry, so the frame is expected unchanged (not
  re-measured — the vertex grew 8 bytes, and item 9 showed vertex fetch is not the bottleneck).
  128 tests pass; the three visual scenarios were diffed pixel-by-pixel against the pre-change
  build (result recorded in `MESHER_PLAN.md`).
  Phase 2 done 2026-09-08: binary greedy mesher (`Chunk.EmitGreedyFaces`, `MeshScratch`),
  pixel-identical by construction — it merges only within identical block/AO/light/submerged
  keys and only along axes where the key's AO is constant; `GreedyMeshingTests` rasterises both
  paths into unit cell faces and compares every one. Headless geometry over generated chunks
  (per-quad -> greedy, opaque + translucent vertices): spawn forest 5x5x6 chunks 308.6k ->
  258.3k (-16 %, 9.4 -> 7.9 MB), sea/beach 5x5x5 194.8k -> 156.1k (-20 %), deep caves 5x5x4
  146.7k -> 126.1k (-14 %). Why so little: of 3258 grass-top cells around spawn only ~95
  adjacent pairs share a key — the noise terrain is rough enough that corner AO differs between
  almost every pair of neighbouring surface cells, and canopy faces (leaf-vs-leaf, both sides
  kept) fragment on AO the same way; sunlight was 15 on every grass top, so light is not the
  limiter. Merging across AO/light steps would change the picture, so this is the ceiling for
  an exact mesher on this terrain. Build time per non-empty chunk (pool thread, includes the
  light flood): forest 1.27 -> 1.41 ms, sea 0.78 -> 0.79, deep 0.39 -> 0.40. Frame at rest
  unchanged (see Measurements); screenshots identical apart from wave/sway phase and
  streaming progress. 141 tests pass.
  Phase 3 done 2026-09-08: the window requests a GL 4.6 core context (`WindowManager`,
  `APIVersion(4, 6)`) and every shader declares `#version 460 core`; no behaviour change — the
  startup line reports `GL 4.6...`, all shaders compile, screenshots unchanged. macOS (GL 4.1) is
  no longer supported. 140 tests pass (`ShaderVersionTests` added).
  Phase 4 done 2026-09-09: chunks no longer own `Mesh` objects. `Chunk.UpdateMesh` uploads into
  two shared `ChunkGeometryPool`s (opaque, translucent — one `BufferStorage` VBO + EBO + VAO
  each, a first-fit `GeometryAllocator` free list in units of 64 vertices / 96 indices, growth by
  doubling with `CopyBufferSubData`) and keeps a `GeometrySlice`; each of the four chunk passes
  (shadow, reflection, opaque front-to-back, translucent back-to-front) still culls and sorts on
  the CPU but now fills a persistent-mapped 3-slot ring of `DrawElementsIndirectCommand`s plus a
  `vec4(origin, alpha)` ChunkInfo SSBO and issues one `glMultiDrawElementsIndirect`, with the
  chunk vertex shaders compiled a second time under `#define CHUNK_DRAW` (SSBO at
  `uDrawBase + gl_DrawID`, `uViewProj`) while entities and LOD keep `SetDrawUniforms`. Measured
  interleaved against a HEAD worktree on a Quadro T1000 (two pairs, scripted 40 s at the spawn
  view, 2148 visible chunks both trees): `worldPass` 4.74 / 3.67 -> 0.50 / 0.51 ms, `shadows`
  1.31 / 1.23 -> 0.26 / 0.27, `waterPass` 1.83 / 1.19 -> 0.09 / 0.09, `reflect` 3.63 / 3.59 ->
  0.83 / 0.85 — the four chunk passes together 11.5 -> 1.7 ms — and frame 28.20 / 32.12 -> 26.56
  / 30.73 ms (35 / 31 -> 38 / 33 FPS). Read it the way the phase-2 row reads: the session was
  GPU-bound (CPU sum == frame, and `gpu` == frame on the lines where the timer query resolved),
  so much of the collapse is the stall relocating into `lighting`, the frame's first GL call
  (15.0 -> 24.2 ms) — the *unconverted* `lodPass` (0.28 -> 0.10) and `uiPass` (1.15 -> 0.25)
  shrank ~3x as well. What exceeds that redistribution is the part MDI actually bought:
  `worldPass` 9.5x and `waterPass` 20x. Pools grow only during the first stream-in (opaque
  65536 -> 131072 -> 262144 units, translucent 8192 -> 16384) and settle at 244801 / 262144
  units opaque (478 of 512 MB, 93 % occupancy) and 12732 / 16384 translucent; raising
  `ChunkGeometryPool.OpaqueInitialUnits` to 262144 would trade 512 MB allocated up front for the
  two mid-stream growth copies. 153 tests pass (new: `ShaderDefinesTests`,
  `GeometryAllocatorTests`, `ChunkDrawShaderTests`); `basic`, `glass` and `water` diffed against
  the HEAD worktree with a same-build control run as the noise floor — every difference is wave
  phase, cloud drift, sub-block camera position or streaming progress. Details in
  `MESHER_PLAN.md`; see the `11 (phase 4)` Measurements row.

- [ ] **10. GC and runtime settings.** (the `chunk.lifetime` half is **done** 2026-09-09 as part of
  `MESHER_PLAN.md` Phase 4; the GC and runtime settings below are still open.)
  `GCSettings.LatencyMode = SustainedLowLatency` in
  `Program.Main`; explicit `<ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>`
  and `<TieredPGO>true</TieredPGO>` in the csproj. Do not enable Server GC without measuring.
  ~~Replace `chunk.lifetime += 1.0 / 60.0` in `RenderManager.RenderWorld` with real delta
  time.~~ Done: `RenderWorld` takes the delta from the `time` it already received
  (`_window.Time`), clamped to 0.1 s so a hitch cannot snap every streaming chunk to full alpha,
  so the 0.5 s fade is 0.5 s of wall clock at any frame rate. `LodRegion.Lifetime` still steps
   at 1/60.

- [ ] **12. Per-pass GPU timing.** The profiler's single whole-frame `TIME_ELAPSED` query
  (`RenderManager.BeginGpuFrame`) cannot say which pass is expensive, and on a GPU-bound machine
  every `*Pass` CPU row just absorbs the stall (phase 4 saw `lighting` balloon to 24 ms on the
  T1000). GL forbids nesting `TIME_ELAPSED`, but consecutive non-overlapping ranges are legal, so
  give each render pass its own query object, `BeginQuery`/`EndQuery` in sequence across
  `World.Render`, and read each ring slot a frame or two late exactly like `BeginGpuFrame` does.
  Surface the per-pass GPU ms in the `X` overlay next to the CPU column. Do this before trusting
  items 13-15 - the whole point is to stop guessing which pass dominates on the T1000.

- [ ] **13. Cut the reflection pass's redundant geometry.** (attempted 2026-09-11, **REVERTED**:
  dropping the LOD cascade from the mirror and capping mirrored chunks at 192 blocks left a hard
  terrain/sky seam in the reflection, and the water's animated wave-normal sample offset
  (`nW.xz * 0.08` in `test-fragment.glsl`) turned that seam into a visible flicker. **A planar
  mirror needs continuous far terrain - do not retry a hard cutoff.** Any future attempt must keep
  the horizon filled, e.g. reuse the previous frame's reflection texture (one-frame lag), lower
  `WaterReflectionScale` further, or draw only the coarsest LOD level as a horizon fill, rather
  than removing far geometry outright.) The reflection pass re-renders every visible opaque chunk
  plus the entire far-terrain LOD cascade plus the cloud layer into a half-res target;
  `WaterReflectionScale` cuts fill but not vertices, so the pass is expensive on a GPU-bound
  machine - but the answer is not to leave the mirror with less geometry than the real frame.

- [ ] **14. Half-rate shadow map.** (attempted 2026-09-11, **REVERTED**.) After item 13's revert
  the water still flickered, and this was the only remaining change able to move the image on a
  two-frame cadence: the reflection is re-rendered every frame and samples the shadow map, so
  refreshing the map only every 2nd frame made the mirrored shading step at fps/2. The map and
  `_lightViewProj` did move together, so if this was the cause the artifact is the cadence itself,
  not a matrix mismatch - do not retry without a way to keep the reflection in step. For reference,
  the idea was: the shadow camera snaps to whole texels (`UpdateShadowCamera`) and the sun moves at
  0.6 deg/s (`DayLengthSeconds = 600`), so a one-frame-old map is visually identical for the main
  pass; `_shadowRenderDue` gated the redraw and `_shadowMapValid` forced one when the sun came back
  up. Halving the shadow pass is still worthwhile if it can be made flicker-free; pair with
  `ShadowMapSize 1024` (item 16) for another 4x fill cut.

- [ ] **15. Cheaper clouds.** (implemented 2026-09-11, *not yet measured in frame time:* `CloudCellSize`
  8 -> 16 and `CloudMaxLevels` 3 -> 2, with the noise wavelengths pinned in blocks so the sky
  keeps its physical scale; `CloudTests` updated to assert the configured max. Headless geometry:
  the full field mesh fell **683245 verts / 2985036 indices -> 254182 / 1090806 (-63 %)**, and the
  renderer draws it up to nine times twice per frame, so this should be a real win. Confirm the
  sky still reads and watch the reflection's clouds.) `CloudField.BuildMesh` fillets the configured
  cell grid (128x128 at 16-block cells), and `CloudRenderer` then draws that mesh up to nine times (3x3 copies), twice each
  (depth pre-pass + colour), in the main frame and again inside the reflection. Coarsening the
  cell to 16 blocks quarters the triangle count (Cells 256 -> 128) and dropping to two layers
  trims the tall cores. **The noise spacings must be expressed in blocks, not cells**, or
  coarsening the grid silently doubles the size of every cloud (the original 64-cell / 8-cell
  spacings were tuned at an 8-block cell).

- [ ] **16. GPU quality knobs, exposed and documented.** (`WorldGenerationConfig` only, no
  logic.) On a T1000 the honest big levers are render distance and pass quality, not CPU work:
  `ChunkRadius` (16 chunks = 256 blocks), the `LodRegion.Levels` cascade (out to 2048),
  `ShadowMapSize` / `ShadowRadiusBlocks`, `WaterReflectionScale`, `CloudCellSize` /
  `CloudMaxLevels`. Decide whether to bake a "low" preset or leave them as hand-tuned constants;
  if a preset is wanted, drive it from one quality level instead of scattering magic numbers.

## Measuring

Repeatable run (see AGENTS.md for the env vars): `VOXEL_SPAWN=0,30,0`, `VOXEL_TIME=60` (high sun
so the shadow pass runs), `VOXEL_LOOK=-90,-10`, stdout redirected to a log, wait ~15 s for the
world to settle, read the last debug lines. The forest view used for item 5 is `VOXEL_LOOK=-130,8` from the same spawn (1313 visible chunks, canopy filling the upper half). Take the "before" from a Release build of HEAD in a throwaway `git worktree` so both numbers come from the same session and GPU clocks.

Streaming run used for item 7: same spawn, `VOXEL_LOOK=-90,-35`, press `X` once the window
handle exists (~4 s), at 15 s double-tap Space, hold Space 6 s, hold W 10 s, read the `perf`
lines from take-off on (GC counts per second, `chunkApply`, `fluidWake`). Worker-side costs
(build time, allocation, pool misses) are invisible to the profiler; temporary `// TEMP-DIAG`
counters appended to the debug line and stripped afterwards were the way to see them.

Per-item regression checks: chunk borders (3), lighting/fog/shadows unchanged (4), leaf holes
and water blending (5), no LOD shells visible from caves (6), no holes in deep terrain (7),
block edits still remesh neighbours (8). `FastNoiseLite.cs` must stay untouched and
`grep -rn TEMP-DIAG OpenGLTest/src` must be empty before each commit.

## Baseline reading (after item 1, 2026-09-07)

Spawn view, high sun, standing still, RTX 3070, 17077 chunks loaded, ~2000 visible:

| Build | FPS | frame ms | CPU sum | GPU | worldPass | waterPass | shadows | chunkDirty | chunkUnld | GC gen0/s |
|---|---|---|---|---|---|---|---|---|---|---|
| Debug | 72 | 13.9 | 13.5 | 6.0 | 6.0 | 2.4 | 1.5 | 1.1 | 0.6 | 22 |
| Release | 100 | 10.0 | 9.6 | 4.7 | 4.6 | 1.8 | 1.0 | 0.4 | 0.4 | 29 |

What it says:
- **CPU-bound in both builds.** The GPU finishes in half the frame. Items 3 and 4 (dictionary
  snapshots, per-draw uniforms) are the right next steps; item 5 (early-Z) is GPU-side and only
  pays off once the CPU side is under the GPU time.
- **Release is only 1.4x faster than Debug**, so the code paths, not the JIT, are the bulk.
- **worldPass is ~half the CPU frame** with only ~2000 visible chunks: that is the uniform storm
  plus the 17k-entry walk, exactly items 3 and 4.
- **~29 gen0 GCs per second at rest** (and a gen2 every second): the string allocations in
  `Shader.SetUniformArray3` per draw (item 4) and the dictionary snapshots (item 3).
- **chunkDirty costs 0.4 ms every frame while standing still**, so `_dirtyChunks` never empties:
  chunks at the rim of the load sphere lack a neighbour and are re-snapshotted and re-sorted
  every frame. Item 3 should park chunks waiting on neighbours in a separate set (or skip the
  sort when nothing is ready) instead of re-sorting them forever.
- The overlay itself costs ~0.85 ms in `uiPass` (0.17 off, 1.0 on); subtract it when comparing.

## Measurements

| Item | Build | View | FPS before | FPS after | Notes |
|---|---|---|---|---|---|
| baseline | Debug (Rider) | spawn | ~60 | | user report, 2026-09-07 |
| 1 | Debug | spawn, still | 72 | 72 | instrumentation only; see Baseline reading |
| 1 | Release | spawn, still | 100 | 100 | instrumentation only; see Baseline reading |
| 2 | Release | spawn, still | 100 | 100 | same binary as the Release baseline, now the default; frame 10.0 ms, CPU 9.7, GPU 4.2, worldPass 4.2, waterPass 1.9, shadows 1.05, chunkDirty 0.6, chunkUnld 0.4, gen0 25/s |
| 3 | Release | spawn, still | 100-110 | 145 | frame 6.9 ms, CPU 6.5, GPU 1.9; worldPass 3.4, waterPass 1.2, shadows 0.44, chunkDirty 0.02, chunkUnld 0.00, gen0 28/s (unchanged: item 4's strings). Flying ~100 blocks streams chunks with no holes, loaded count returns to 17077 |
| 4 | Release | spawn, still | 145 | 243 | frame 4.1 ms, CPU 3.8, GPU 1.75; worldPass 1.5, waterPass 0.55, lodPass 0.12, gen0 0/s at rest (was 28). Lighting/fog/shadows/leaf sway identical to the item-3 screenshot; TNT strobe and block drops render as before |
| 5 | Release | spawn, still | 247-250 | 241-244 | REVERTED. before: frame 4.02, CPU 3.72, GPU 1.72, worldPass 1.45-1.50, shadows 0.42; after (solid then cutout): frame 4.14, CPU 3.84, GPU 1.87, worldPass 1.64. Cutout-first order: GPU 1.86-1.93, worldPass 1.61. Leaf holes, shadows and glass identical in screenshots |
| 6 | Release | spawn, still | 241-248 | 241-250 | `lod` 0.15 -> 0.01 ms (two rounds each, interleaved); frame 4.04-4.15 -> 3.99-4.14, CPU 3.75-3.82 -> 3.70-3.83, GPU 1.72-1.79 -> 1.71-1.75. The 0.14 ms saving is real but within the frame-to-frame noise of `worldPass` (1.44-1.59), so FPS is unchanged. Flight + 10 s walk: `lod` 0.02-0.04 while streaming (before: 0.16 constant), horizon identical, no holes or doubled shells |
| 7 | Release | spawn, still | 252-268 | 236-266 | two interleaved pairs; frame 3.75-4.20 -> 3.75-4.23 ms, GPU 1.70-1.76 both. The rest frame has no mesh builds, so nothing changed; run-to-run swing is ~0.4 ms, always interleave |
| 7 | Release | flight (look -90,-35; climb 6 s, W 10 s; 23 s window) | 212 avg | 208-210 avg | frame avg 5.03 -> 4.81-4.93 (noise); GC gen0/gen1/gen2 in the window 76/40/14 -> 58-68/27-29/4-5; managed alloc 1566 -> 796-816 MB (temporary `GC.GetTotalAllocatedBytes` counter); ~8000 mesh builds per window, pool misses 906 with cap 16 -> 59-69 with cap 128, 0.28 -> 0.25 ms per build, 158 builds took the enclosed fast path; `chunkApply` avg 0.04-0.05 both. Screenshots identical, no holes |
| 7 | Release | sea flight (spawn 45,30,-45, look 0,-35) | - | - | alloc 1060 -> 534 MB, GC 57/29/9 -> 52/22/3, `fluidWake` avg 0.001 / max 0.01 ms in both builds, `fluidTick` avg 0.013 -> 0.020; ~7000 builds per window. Shoreline and canopy identical |
| 5 | Release | forest (look -130,8), still | 297-313 | 280-283 | REVERTED. before: frame 3.21-3.37, CPU 2.95-3.09, GPU 1.81-1.87, worldPass 1.05-1.12; after: frame 3.55, CPU 3.27, GPU 2.13, worldPass 1.32. Cutout-first: frame 3.47, GPU 1.98, worldPass 1.26. 1313 visible chunks |
| 9 | Release | spawn, still | 329 avg | 312 avg | FPS is the on-screen counter averaged over 3 settled still shots (15 s settle + 2 s apart) from a scripted run — no X overlay in a headless session, so no per-section ms. Intra-run swing is 280-345 either way, so the frame is unchanged within noise, as expected: vertex fetch was never the bottleneck (GPU ~1.7 ms vs CPU ~3.8 ms) and a rest frame builds no meshes. What moved is memory: vertex lists 48 -> 24 B/vertex (~96 KB per pooled 4096-list), asserted by `VertexPackingRoundTrips`. Leaf holes, water blending, lighting/fog, HUD icons identical before/after; clean log, no shader or chunk-gen errors |
| 11 (phase 2) | Release | spawn (0,30,0), look -90,-10, VOXEL_TIME=60, still, scripted 20 s, debug overlay on | 54-57 / 40-42 | 51-60 / 38-39 | two interleaved pairs (HEAD worktree vs branch), same 1769 visible chunks. The session was GPU-bound with low clocks (gpu ~= frame): frame 17.5-18.4 / 24.0-24.8 ms head vs 16.7-19.5 / 25.4-25.7 ms branch, worldPass 4.1-5.4 both. No mesh builds happen at rest, so no change was expected; what moved is resident geometry (-14..-20 % vertices, see item 11). The CPU section that absorbs the GPU wait differs: `lighting` 2.8-3.9 ms on head, `shadows` 4.7-7.5 ms on the branch, with the sum and the frame equal - a redistribution of the stall, not a cost |
| 11 (phase 4) | Release | spawn (0,30,0), look -90,-10, VOXEL_TIME=60, still, scripted 40 s, debug overlay on | 35 / 31 | 38 / 33 | two interleaved pairs (HEAD worktree vs branch), 2148 visible chunks in three of the four runs (head run 2 settled at 1769, a count the head tree also produced in the 20 s runs, so the cull is unchanged). Medians over the settled window (chunkApply 0.00, gc 0/0/0): frame 28.20 / 32.12 -> 26.56 / 30.73 ms, worldPass 4.74 / 3.67 -> 0.50 / 0.51, shadows 1.31 / 1.23 -> 0.26 / 0.27, waterPass 1.83 / 1.19 -> 0.09 / 0.09, reflect 3.63 / 3.59 -> 0.83 / 0.85; the four chunk passes together 11.5 -> 1.7 ms. The session was GPU-bound (CPU sum == frame; `gpu` == frame on the lines where the timer query resolved, `--` on the rest), so most of that is the stall relocating rather than a saving: the unconverted `lodPass` 0.28 -> 0.10 and `uiPass` 1.15 -> 0.25 shrank too, and `lighting`, the frame's first GL call, absorbed it (15.0 -> 24.2 ms). Pools grow during the first stream-in only (opaque 65536 -> 131072 -> 262144 units, translucent 8192 -> 16384); steady state 244801/262144 units opaque (478 of 512 MB) and 12732/16384 translucent, no growth after settle. 153 tests |
| 13 | Release | spawn, still | T1000 baseline (item 11 phase 4: ~38/33) | REVERTED | LOD dropped from the mirror + mirrored chunks capped at 192 blocks. Reverted - the hard terrain/sky seam is a real defect - but the flicker persisted after this revert, so the seam was not the cause |
| 14 | Release | spawn, still | T1000 baseline (item 11 phase 4: ~38/33) | REVERTED | Half-rate shadow map. After item 13's revert the flicker remained, and this was the only remaining change on a two-frame cadence (the reflection re-renders every frame and samples the shadow map). Reverted; revisit only with a flicker-free design, measured via item 12 |
| 15 | Release | spawn, still | PENDING | PENDING | Cloud cell 16 blocks, max 2 layers, wavelengths pinned in blocks. Headless field mesh 683245/2985036 -> 254182/1090806 verts/indices (-63 %); frame time still to measure. Watch the sky scale and the reflection's clouds |
