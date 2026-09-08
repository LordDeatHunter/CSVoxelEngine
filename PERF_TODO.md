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

- [ ] **10. GC and runtime settings.** `GCSettings.LatencyMode = SustainedLowLatency` in
  `Program.Main`; explicit `<ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>`
  and `<TieredPGO>true</TieredPGO>` in the csproj. Do not enable Server GC without measuring.
  Replace `chunk.lifetime += 1.0 / 60.0` in `RenderManager.RenderWorld` with real delta time.

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
