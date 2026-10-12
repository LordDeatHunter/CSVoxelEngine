# Performance TODO

The **Open (next round)** section is **empty**: this file carries no live backlog any more. Every item
it ever held is now closed — implemented (**What landed**), ruled out (**Ruled out**), or relocated to
`TODO.md` because it was never a perf item. Per-item history lives in git
(`git log --oneline -- PERF_TODO.md FLUID_PERF.md`). What the file is *for* from here on is the
measurement protocol, the last profile snapshot, the accumulated gate machinery and the do-not-retry
**Ruled out** list; read the terminal hand-off at the end before opening anything.

Machine for every number below: Release, Quadro T1000 (GL 4.6, driver 597.06),
12 CPUs, `SustainedLowLatency`, `VOXEL_TIME=60`, `VOXEL_DEBUG=1`.

## How to measure

Repeatable fly (checked in): `OpenGLTest/visual-examples/perf-fly.json` — 10 s
settle at spawn, teleport + sprint-walk legs over land, water bay (228,-138) and
a far teleport (-120,100). Shots land in `OpenGLTest/visual/perf-fly/` (gitignored).
Run from `OpenGLTest/` so `assets/` resolves:

```powershell
$env:VOXEL_DEBUG="1"; $env:VOXEL_TIME="60"; $env:VOXEL_SCRIPT="visual-examples/perf-fly.json"
dotnet run --project VoxelEngine.csproj -c Release --no-build
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
   R10 has since cut the `lod` row's settled median **0.51 -> 0.41 ms** on `perf-fly` (region
   upload stopped creating GL objects), but its **worst-frame** behavior on a teleport did not
   move in either direction — see **Measurement: R10**. The 2-3 ms teleport `lod` figure in the
   table above is therefore still the figure to beat, and it was not beaten.
2. **Reflection + world pass when water is on screen** — `reflect` GPU 0 -> 8 ms,
   `worldPass` GPU up to 5.6 ms. Looking at the bay costs ~2x dry land; the
   `reflect` figure predates reflection temporal reuse and is pending re-capture.
3. **Geometry pool doubling mid-stream** (opaque to 512 MB) — one-time copy hitch.
   There are now **three** pools, not two: R10 added `lod` at 163840 units (**350 MB**), sized off
   a measured peak of 117844 units (72 %) so it never grows on `perf-fly` or on any of the four
   gate scenarios. Committed VRAM across the three is ~1.05 GB on the reference machine and it
   allocates cleanly there; a fourth pool should re-check that budget rather than assume it.
4. **Underwater `raysPass`** (1.5-2.3 ms GPU, pre-half-res — pending re-capture) and
   **`cloudPass`** (0.7-1.5 ms when clouds in view — pre-R4, also pending a `perf-fly`
   re-capture). Clouds were already cut 63 % (item 15); remainder is steady, not spiky.
   R4 has since removed the nine-draws-per-sweep structure — each sweep is one instanced
   draw — but that change is *submission-side only* (the same nine copies are still
   rasterized twice per pass), and the recapture found **`cloudPass` unchanged within
   run-to-run spread in both halves**; the win is up to 34 fewer draw calls and up to 34
   fewer uniform updates a frame. That recapture is a different scenario
   (`clouds-mirror.json`'s shore hold; the R4 write-up is in `git log -p -- PERF_TODO.md`), so it
   is *not* a like-for-like correction of the 0.7-1.5 ms figure above.
5. **Fluid wake storm** at spawn over the sea — now bounded (queue 100-196k -> 8-12k),
   secondary to item 1.
6. **Screenshot encode** 7-18 ms — scripted runs only, but fix anyway (item 20).
7. **`uiPass`** 0.5-1 ms settled — the `X` overlay itself; subtract before comparing.

## Open (next round)

**Nothing is open. This section is empty**, and it is kept only so that a reader who arrives looking
for the backlog finds the disposition rather than a dangling list. The four entries that stood here
were all closed on **2026-09-20**, under an explicit human instruction to finish or dispose of every
remaining bullet, and none of them is backlog any more.

**R6 was implemented** — GPU timer queries now run every frame while the `X` overlay is up and on one
frame in `WorldGenerationConfig.GpuTimingIdleInterval` while it is down; its entries under **What
landed** record what shipped, what it measured and what the gate gives up. **R3 moved to Ruled out
(do not retry without new evidence)** with findings (a)/(b)/(c) intact: it had been resolved against
the code and parked in an earlier round, it was not re-litigated here, and the human closing this
round chose to stop carrying it rather than unpark it. **C5 was split** — the mechanism that shipped
is under **What landed**, the *pool-size lever alone* is under **Ruled out** with the capture that
killed it, and `WorldGenerationConfig.MaxConcurrentLodGenerations` ships at **1**. **D2b was
relocated to `TODO.md` `M-11`**, because its entries are irreproducible player edits, which makes it
a correctness item entangled with save/load (`M-1`) rather than a perf item; the one-line pointer
under **Not chasing** below keeps the part that still costs this file something.

### Not chasing (recorded so it is not re-proposed)

- Hierarchical / compute GPU cull — the ruled-out entry measures post-MDI
  cull+sort+fill at ~2%; revisit only if CPU cull+sort exceeds ~1 ms.
- Shader ALU cuts (the caustic Voronoi net, 8 point lights, 4-tap PCF) — ruled out;
  the half-res route is the allowed lever. R2 has landed on that route (the underwater
  rays march). R3 was the other half of it and was **repriced down to parked and then closed** —
  read its entry under **Ruled out** below rather than treating it as a live lever: the rays
  composite pins the scene *depth* copy at full res, leaving only a color copy inside a pass whose entire
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
  finding above the noise floor. `Camera`'s per-frame collision scans were recorded
  here as additional evidence for D1's impact rather than as a separate entry; **D1 has since
  landed**, and those scans reach the table through `ChunkManager.GetBlockGlobal`
  (`ChunkManager.cs:759`), which now shifts straight into `ChunkMap`'s `int` overload with no
  intermediate `Vector3`. (R7 and R11 have since landed
  *in* `Camera` — the view-projection and view-frustum caches — but they were their own
  bullets, not findings from that walk, so this entry still stands.)

- **`LodRegion.ColumnCaps` unbounded growth — moved out of this file to `TODO.md` `M-11`** (it was
  **D2b** here). Its entries are *irreproducible player edits*, so dropping one is **data loss, not a
  recompute**: that makes it a **correctness** item entangled with save/load (`TODO.md` `M-1`), not a
  perf item, and the full reasoning traveled with it — why it cannot go on `GenerationalCache`, why
  eviction cannot ride chunk unload, `LowerColumnBelow`'s load-bearing publish ordering, and the
  ~1 KB-per-edited-chunk-column arithmetic. **What stays here is the cost it imposes on this file's
  own instruments:** because `ColumnCaps` is process-global with no reset hook and `BuildMeshData`
  gates on `hasCaps = !ColumnCaps.IsEmpty` (`LodRegion.cs:302`, read at `:312`), any test class that
  mines near the origin flips a *later* build of the same region, which makes
  `LodBuildPoolTests.RegionBuildsAreIdenticalWhicheverThreadRunsThem` and
  `LodHeightSamplingTests.SeparableMinMatchesTheNestedWindowAtEveryLevel` **intermittent in full-suite
  runs** — reproduced on pristine HEAD, and seen again during the R6 round on a tree whose only
  functional addition was a GL-free static class no test reaches. Expect them, re-run once to confirm
  the failing name **moves**, and **never "fix" them by touching `ColumnCaps`**.

**This round took R10, alone, as the file's closing paragraph said to.** It is the largest
structural change of the series and it **landed**: LOD regions no longer own GL objects, and the
three per-region ring walks are one `glMultiDrawElementsIndirect` each. The round was priced
two-sidedly on purpose — R10 replaces GL object deletion with free-list scanning, so its own churn
claim had a named counter-mechanism — and the honest outcome is that the claim **split**: the
submission side and the arrival side are disjoint wins, the departure/teleport side is neutral in
both directions and is quoted nowhere. R10's pool sizing also corrected a number that was wrong
everywhere in this file: the LOD resident *ceiling* is **759**, not the settled **591**. Everything
in the round is output-preserving, so the gate looked for *zero* structural change rather than for
an acceptable difference. The blow-by-blow write-ups of the earlier R4, R5/R7/R11, C3/R8 and
C5/C12/D3 rounds are in `git log -p -- PERF_TODO.md`; what is kept below is what the next round
needs, not the history.

### What landed

- **R6 · GPU timer queries are gated with an idle keep-alive, and the either/or the bullet framed was
  not forced.** The bullet said gating on `X` gives up "the first second after X already has data" and
  told a reader to decide that trade. Read against the code it mis-stated its own cost **twice**.
  First, `GpuRingSize = 3` (`RenderManager.cs:220`), so a query issued on the frame `X` is pressed
  drains **three frames later, not a second later**. Second, and this is the one that matters, the
  property the overlay actually delivers is **not sample recency**: `FrameProfiler.Publish`
  (`FrameProfiler.cs:297`) rebuilds `_lines` and `_consoleLine` only **once a second** and clears the
  GPU accumulators there, so what a player sees the instant `X` is pressed is the **last published
  snapshot**. The property is therefore "the once-a-second snapshot standing when `X` is pressed
  already carries GPU figures instead of `--`", and a fixed duty cycle preserves it **by
  construction**. **What shipped:** `GpuTimingGate.ShouldTime(bool overlayOn, long frameIndex)`
  (`src/Rendering/GpuTimingGate.cs:52`) — GL-free and unit-tested on the `ReflectionReuse` precedent,
  because `RenderManager` needs a live GL context and **no test in this suite has one**;
  `WorldGenerationConfig.GpuTimingIdleInterval = 8` (`:291`), which keeps
  `ceil(fps / GpuTimingIdleInterval)` samples per always-run pass in every publish window (~12 at
  100 fps); the gate **latched once per frame** in `RenderManager.BeginGpuFrame(bool overlayOn)`
  (`:308`) and read by `BeginGpuPass` (`:355`) and `EndGpuPass` (`:371`). The single call site is
  `Program.cs:571`, passing the live `debugEnabled` X-toggle state on the render thread — no new
  cross-thread mechanism. The interval is **chosen, not measured**: it is a one-token lever, and by
  this file's own rule (`Generations`, C5's pool size) moving it owes a counted capture, which is said
  where the constant is declared.
- **R6 · the two structural rules that must not be "cleaned up".** **(1) The latch is per frame and is
  never re-read mid-frame.** `EndGpuPass` advances `_gpuPassSegUsed[s]`, so a toggle landing between a
  `BeginGpuPass` and its `EndGpuPass` would open a `TIME_ELAPSED` range that nothing closes and desync
  pass pairing for the rest of the frame — hence one read in `BeginGpuFrame`, held in a field for the
  whole frame. **(2) The drain loop stays unconditional.** A `_gpuPassPending` entry written while the
  gate was open must still be cleared after it closes, or that ring slot is re-used with a stale
  pending flag forever. `EndGpuFrame` is untouched for the same family of reason: the ring must keep
  advancing every frame or the three-frame drain latency breaks. The consequence of (2) is the shape
  of the saving and is worth stating: **the drain frame is not free** — with `GpuRingSize == 3` the
  queries issued on timed frame k are drained on frame **k+3**, which is itself a gate-closed frame.
- **R6 · what it costs, MEASURED as a count and not as a time.** R6's quantity is countable, so it was
  counted. `RenderManager.Dispose` (`:2863`) logs one cumulative line before any GL teardown —
  `GPU timer queries: N GL calls over F frames, T timed (X per frame)` — which prints in **every** run
  (see the instrument note in the hand-off for why it is a shutdown line and not a console token).
  Same scenario both times (`entity-binds`, `VOXEL_TIME=120`, run from `OpenGLTest/`, scenario copies
  by absolute path with an `output` override so neither tree's `visual/` was written):
  **with `VOXEL_DEBUG=1`** — the gate is open on every frame and that path is byte-identical to HEAD,
  so this figure **is** the always-on cost — **92,690 GL calls over 2,453 frames, 2,453 timed = 37.8
  per frame**; **without `VOXEL_DEBUG`** — the gated path — **12,140 calls over 2,801 frames, 351
  timed = 4.3 per frame**, an **8.7x** reduction, with `351 = ceil(2801 / 8)` exactly. A second
  scenario corroborates the always-on figure: `perf-fly` under `VOXEL_DEBUG=1` read **222,364 calls
  over 5,938 frames = 37.5 per frame**. So the always-on cost on this machine is **37.5-37.8 calls a
  frame** — just *below* the bullet's estimated "~40-70", so **quote the measurement, not the
  estimate**. Structural ceiling for context: 12 bracket sites (11 sections, `EntityPass` twice) = at
  most 24 `BeginQuery`/`EndQuery` plus at most 24 `GetQueryObject` = 48, and 37.8 implies about 9.4 of
  the 12 ranges opening on an average frame, which is what the conditional passes predict.
  **Per-cycle shape, also measured:** 12,140 / 351 = **34.6 calls per 8-frame cycle** — about one
  always-on frame's worth per cycle, split so that frame k issues the `BeginQuery`/`EndQuery` brackets
  and frame k+3 issues the `GetQueryObject` drain for exactly those, with the other six issuing none;
  it sits slightly under the always-on per-frame figure because the drain loop skips slots with
  nothing pending. All seven scored gate runs reproduced the duty cycle independently (timed frames
  331/2645, 353/2824, 347/2769, 346/2763, 329/2627, 330/2639, 321/2568 — `ceil(frames/8)` in every
  one). **No time win is claimed anywhere**, deliberately: no test has a GL context, the `perf-fly`
  60 s window is saturated and isolates nothing, the two counted runs differ in frame count (2,453 vs
  2,801) because the scenario is wall-clock driven so any fps comparison between them would read
  scheduler noise, and **lesson 6 in the hand-off is explicit that an unpriced count is not a time**.
- **R6 · what is given up, all three parts, and the alternative if the third is ever judged too
  expensive.** (i) Idle-window averages are built from about `fps / GpuTimingIdleInterval` samples per
  always-run pass instead of one per frame, so they are **thinner**. (ii) The `~` worst-frame column
  **under-samples** while the overlay is down, so a max recorded during an idle period can survive
  into the first window after `X`. (iii) The one that bites: a pass that does **not run every frame**
  can collect no samples at all across a publish window and read `--` where an always-on build showed
  a figure. `SelectPass` is the live example — it is bracketed inside `if (_hasSelectedBlock)`
  (`World.cs:525-548`), so with the gate closed the chance of a frame both entering it *and* being a
  timed frame falls 8x. **(iii) is a reasoned prediction, not an observed fact**: confirming it needs
  someone to start without `VOXEL_DEBUG`, press `X` and read the GPU column, and no headless run can
  do that (the transition has no run coverage at all — see below), so it was not confirmed and is not
  written here as if it had been. If it is ever judged unacceptable, the named alternative is an
  **arming window** — force a few fully-timed frames on the off->on transition instead of running a
  fixed duty cycle — which keeps almost all of the saving on long idle periods.
- **R6 · a measured finding that materially softens the headline trade, taken on the ALWAYS-ON path.**
  HEAD already leaves the later passes out of the standing snapshot much of the time. `entity-binds`
  under `VOXEL_DEBUG=1` (20 publish windows) carried GPU figures for
  `lighting`/`shadows`/`reflect`/`lodPass` in **20/20** windows, but `worldPass`, `entityPass` and
  `waterPass` in only **11/20**, `cloudPass` and `raysPass` in **10/20**, `uiPass` in **8/20** and
  `selectPass` in **5/20**; `perf-fly` reproduces the same gradient on **both** trees (work 58 windows
  vs head 59: `lighting` 58/58 vs 59/59, `lodPass` 57/58 vs 57/59, `worldPass` 27 vs 26, `waterPass`
  24 vs 24, `selectPass` 16 vs 11). So the property R6's bullet framed as being sacrificed is, for
  passes after `lodPass`, **already only about half reliable on HEAD** on this machine, and about a
  quarter reliable for `selectPass`. The gate magnifies a pre-existing condition rather than creating
  a new class of defect. The mechanism was **not diagnosed** — out of scope — and the behavior is
  identical on both trees, so it is HEAD behavior and **not a regression**: do not file it as a new
  bug without evidence.
- **R6 · how it is covered, and the one case that has no run coverage and cannot get any.**
  `GpuTimingGateTests` adds three facts: the gate times **every** frame while the overlay is on; over
  a frame count deliberately **not** a multiple of the interval it fires `ceil(N / interval)` times;
  and the **min and max gap** between timed frames are **both exactly** `GpuTimingIdleInterval`, with
  frame 0 timed. Every magnitude is expressed as a multiple of the config constant and never as an
  arithmetic literal over it, per `ReflectionReuseTests`' stated rule, so a retune cannot turn into a
  spurious failure. The **off-to-on transition has no run coverage and cannot get any**:
  `VisualDirector` dispatches exactly eight ops — teleport, look, wait, walk, mine, place, give,
  screenshot (`VisualDirector.cs:34-94`) — and **none of them is a key press**, so no scripted
  scenario can press `X`. The third test is what stands in for it, and it is a test of the **policy**,
  not of the running renderer. The wiring itself (`BeginGpuFrame` / `BeginGpuPass` / `EndGpuPass`)
  stays unit-uncovered because nothing in the suite constructs a GL context; that gap is real and is
  not papered over. What did verify the wiring is a `VOXEL_DEBUG=1` scripted run whose `perf` line
  still printed non-`--` GPU figures for every pass, and the shutdown line's own counts.
- **R6 · the visual gate saw zero, and it could not have seen the surface that matters.** Timer
  queries write no pixels, so the gate looked for **zero structural change**: `entity-binds`, 7 runs
  per tree interleaved in one session after a cold discarded run each, both trees run **without**
  `VOXEL_DEBUG` — the only configuration in which the work tree executes the new gated path, and all
  seven work runs logged 4.3 calls/frame, proving they did — against a pristine `git archive HEAD`
  (8b743c7) build in a scratchpad, never a stash. Residue **0 px on all six shots at 7+7, and already
  0 at 5+5**, with the documented accidental-agreement collapse reproducing (14 px at 3+3 -> 0 -> 0).
  The exact null over the 126 balanced relabelings at 5+5 gave null maxima of 289 / 444 / 289 / 659 /
  1,848 / 5,699 px with a null median of 0 and the real split ranking 126/126 on every shot, so the
  instrument had ample power and found nothing. Within-tree constants were 89.9-96.9 %, reproducing
  this file's recorded 91-98 %. **Now the part that zero does not prove:** all six `entity-binds`
  screenshots pass `hideHud: true` (verified 6/6), so an **overlay** change is *structurally
  invisible* to the scored gate — the zero is a gross-breakage check, not evidence about the overlay.
  `perf-fly` is the only HUD-baking scenario, and the run made on it was `VOXEL_DEBUG=1` on **both**
  trees, which is **tautological**: with `VOXEL_DEBUG=1` the gate is open every frame (5,938/5,938
  timed) and the path is identical to HEAD. It is labeled that way rather than offered as evidence
  about the gated path. (Its per-section GPU-column populations were compared anyway and are
  indistinguishable; the `perf` console line and the overlay rows are built in the same `Publish()`
  from the same accumulators, so the console line is a faithful proxy for the overlay's GPU column.)
- **R6 · the reservation this file placed on it was lifted deliberately, and two documents were
  corrected because the gate falsified them.** The hand-off said R6 "must not be taken autonomously";
  it was taken on **2026-09-20** under an explicit human instruction to finish or dispose of every
  remaining open bullet. The reservation text is **kept** in the hand-off rather than deleted, so a
  later reader can see that it was lifted on purpose and what was decided under it.
  `AGENTS.md:55-62` said "Timings are collected every frame whether or not the overlay is up, so the
  first second after pressing `X` already has data" — false for the GPU half the moment the gate
  landed — and now states the CPU/GPU split, names `GpuTimingGate.ShouldTime` and
  `RenderManager.BeginGpuFrame`, and carries the `selectPass` caveat. `FrameProfiler`'s class doc
  opened "always collected"; it now splits the same claim and records the sampling-rate consequence.
  **This is NOT the ruled-out *GL debug context* item**, which is a different entry under **Ruled
  out**.
- **C5 · closed: the mechanism is what landed, and the pool is ruled out.** Recorded here because C5
  sat in the open list long after its decision was taken, which made a closed item look like backlog.
  **What shipped and stays:** the TryAdd-gated `_building` claim with both rollbacks,
  `MaxLodBuildsInFlight = 8` checked before any claim, `LodBuildThrottleDelayMs = 1` and the
  Idle/Throttled split, the `CompletedBuildCount` throughput instrument (`regions {built}/{live}` on
  the `VOXEL_DEBUG=1` console line) and `LodManager.MeshBuffers`' retention ceilings raised to 16 —
  all **correct at any N**, and pinned at 3 by `LodBuildPoolTests`, whose stress width is deliberately
  fixed at 3 and **not** read from the shipped constant (or lowering the constant would have silently
  defused them). The detail is in the three C5 bullets further down this section and in **The C5
  concurrency argument**. **What did not ship is the pool itself:**
  `WorldGenerationConfig.MaxConcurrentLodGenerations` ships at **1**, the pool-size lever alone is
  under **Ruled out**, and its capture stands under **"Measurement: C5"**, untouched. Do not
  re-propose "a bigger pool".
- **D5 · The geometry free list is an offset-keyed, max-augmented treap, and exact first fit survived
  the rewrite.** `GeometryAllocator`'s offset-sorted `List<(int, int)>` is gone; free blocks are now
  nodes in a treap keyed on `Offset`, each carrying `MaxCount`, the largest free `Count` over its
  subtree (`GeometryAllocator.cs:51-64`). That augmentation is the whole trick: `TryAllocate`
  (`:132-154`) is a **leftmost descent** — `AllocateFrom` (`:161-197`) goes left whenever the left
  subtree still holds a block large enough, else takes this node if it fits, else goes right — which
  is the **lowest-offset block that fits, by construction**, in O(log n). `Free` (`:206-281`) is one
  neighbor descent that finds predecessor and successor together, then either widens a neighbor in
  place or inserts one node, with the both-sides merge additionally removing the successor.
  `LargestFreeBlock` (`:125`) is now an O(1) read of the root's `MaxCount` where it was an O(n) walk,
  and `FreeBlockCount` (`:94`) is a maintained counter. The **public surface did not change** —
  `TryAllocate`, `Free`, `Grow`, `Capacity`, `UsedUnits`, `FreeBlockCount`, `LargestFreeBlock` plus
  this round's three counters — so `ChunkGeometryPool` was not touched by the rewrite at all.
- **D5 · The bullet's own first suggestion — a size-bucketed free list — was ruled out by the tests,
  not by taste.** A bucketed list hands back a best fit out of a bucket.
  `GeometryAllocatorTests.AllocatesFirstFitDeterministically`, `FreeIsReusedFirstFit`,
  `ASameSizeReallocationAfterAFreeReusesTheSameOffset` and
  `MidTeleportFragmentationPeaksAtAKnownBlockCount` pin **exact first-fit offsets**, so any structure
  that ships must reproduce them. That is what forced the offset-ordered, subtree-max-augmented shape
  and made this **effort L, not the bullet's M**. All ten pre-existing allocator facts are green and
  **unedited** (the test file's diff is pure insertion); exactly two of the round's own instrument
  facts were re-valued, each with its new number derived in a rewritten comment. Two facts were added:
  `TheFreeListDescentVisitsLogarithmicallyManyNodes` (at the pinned 380-block peak a partial fit costs
  exactly 5 node visits, an exact fit 5, and a request nothing can satisfy exactly **1** — the root's
  subtree maximum — against a bound of `2 * ceil(log2(380))` = 18) and
  `EveryOffsetTheFlatListProducedIsReproduced`, which steps the treap and a **verbatim copy of the old
  flat first fit** in lockstep through four thousand interleaved allocations, frees and six doubling
  growths, comparing every offset, every success/failure verdict, and used units / block count /
  largest block after each one. That last fact is the general claim; the ten pinned facts are only a
  handful of fixed sequences.
- **D5 · Two implementation choices that are load-bearing and must not be "cleaned up".** A partial fit
  shaves the front off its node **in place**, raising the key and leaving the priority alone, so an
  allocation is **one** descent; delete-plus-reinsert would have made it three and broken the
  logarithmic bound the new fact asserts. The mutation is safe because coalesced blocks are never
  adjacent, so a raised key can never pass its successor. Priorities are SplitMix64 over the offset
  with ties broken by offset, so the tree's shape depends on no insertion history and the exact-valued
  facts stay exact. Nodes are recycled through a private spare list, so steady-state churn allocates
  nothing on the render thread — without it every insert would have traded the scan win for a GC
  regression in exactly the mass-unload frames this item is about. The treap is **not concurrent and
  must not become so**: `GeometryAllocator` is render-thread-only by design.
- **D5 · What it bought, priced honestly, and what it did not buy.** One GATE-A re-run at the identical
  setting against the three scored before runs: worst-frame scan steps **7,289,632 / 7,268,352 /
  7,338,750 -> 340,457** (21.4x), of which opaque **6,341,025 -> 257,702** (24.6x) and lod
  **7,701 -> 3,288** (2.3x); max `S_sec` **59,205.6 -> 2,327.1** steps/frame (25.4x). **The step counts
  are not comparable at one price and this file does not quote them as if they were** — a flat-list
  step is one 8-byte tuple read out of a contiguous prefetched array (measured **1.1468 ns**), a treap
  step is a pointer chase through a 48-byte instance (measured **3.33 ns/node at 380 nodes**, **5.12
  at the 2,177-node opaque peak** in churned layout). At the pessimistic **5.1218 ns/node** the worst
  frame goes **8.335 ms -> 1.744 ms (4.8x)** and `S_sec` **0.06790 -> 0.01192 ms (5.7x)**. Headless, on
  the pinned LOD storm, **144,400 steps became 8,372** (17.2x). **1.744 ms still clears this round's
  own 0.20 ms BURST bar, so the allocator cost was not eliminated — the QUADRATIC term was.** The flat
  list charged O(list length) per free while the length itself grew with the unload; the treap charges
  O(log length). What remains is proportional to how many chunks one frame disposes, which is
  `UnloadDistantChunks`' unbounded batch — a separate item nobody has opened. The full tables,
  including the confirmation microbenchmarks, are in **"Measurement: D5"**.
- **D5 · The premise the bullet was written on is measured false, and this is the round's most
  transferable finding.** The bullet framed the item around the **LOD** pool's paired, uncapped churn
  and cited the pool's 93 % steady-state utilization as evidence of a short list. Both are wrong.
  **High utilization says how full the buffer is, not how many holes the occupancy is threaded
  through.** The `lod` pool is not the problem at all: its free list peaks at **74-83 blocks**, about
  21 % of R10's pinned 380-block ceiling, and its worst frame cost **7,913 steps** — 5.5 % of the
  144,400-step / 0.289 ms arithmetic ceiling that "Measurement: D5" derives for it. What actually fired
  was the **opaque** pool at **2,264-2,295** free blocks, six times the LOD pool's pinned worst case,
  contributing **87-90 %** of every fired frame while `lod` contributed **0.014-0.114 %**. Anyone who
  reopens this path measures the opaque pool first.
- **D5 · The scouts' open correction, confirmed: the chunk side has no per-frame FREE cap either.**
  `ChunkManager.UnloadDistantChunks` (`ChunkManager.cs:228-267`) walks the whole `Chunks.Packed` table
  and disposes **every** out-of-range chunk in one frame; only the **upload** paths are capped
  (`WorldGenerationConfig.MaxMeshUploadsPerFrame`, 32). So the bullet's "one free per disposed chunk"
  was an **unbounded burst on the chunk pools exactly as on the LOD pool**, and the 17077 -> 4991
  teleport figure is what one frame can hand to `Free`. The two sides also differ in a way that
  matters: chunks arrive in `ConcurrentDictionary` **hash order**, which bears no relation to
  allocation offset, so under the old flat list those frees stopped roughly **halfway** down the list
  instead of falling through it — which is how 6.35 million steps came out of a single frame at a list
  length of ~2,200. LOD's ascending-offset storm fell through instead, giving the clean
  `Sum(2k+1), k=0..379 = 380^2 = 144,400` steps / 0.289 ms ceiling recorded in "Measurement: D5"; that
  derivation is written down there so no future round re-derives it.
- **D5 · The prepared remedy was left unused, and the reason is not the one the file expected.**
  Fusing a region's shell and sheet into **one** paired allocation (R10's prepared remedy, which the
  bullet said "may be the cheaper half") was **not taken**. It failed the pre-registered selection
  rule's **first** condition, not its fragmentation precondition: the rule required the `lod` pool to
  contribute **>= 60 %** of the fired frame's scan steps and it contributed **0.1 %** — a miss by a
  factor of ~550 — so the precondition was **never reached and is recorded as not-evaluated rather
  than untested**. **The hazard it guards against is real and no test encodes it, so it is carried
  here in prose:** a fused pair is 176-181 units (`ChunkGeometryPool.cs:72`) where its halves floor at
  64 each, so a fused request fits **strictly fewer holes** than its two halves — and at the pinned
  380-block peak, where `LargestFreeBlock == 128`, it fits **none at all**, turning two allocations
  that each succeed on the first hole into one full scan, a failure and a pool grow. Halving the call
  count is not halving the work. Do not re-propose fusing without measuring that.
- **D5 · The instrument stays, and it is the standing free-list instrument from here on.**
  `GeometryAllocator` carries three counters — `ScanSteps` (`:104`), `AllocatorCalls` (`:110`) and
  `PeakFreeBlockCount` (`:119`, updated inside `Free` **and** inside `Grow`, because both unload paths
  are unbounded per frame and an end-of-frame sample reads the drained list). `ChunkGeometryPool`
  passes four of them through (`:128-142`) and the growth log line (last statement of `Grow`,
  `:277-278`) now reports `free blocks` and `largest`, which is what D5's bullet literally asked for —
  though it is **not** the capture instrument, because it fires only on a doubling and **no pool grew
  once in any of the seven scored `perf-fly`/teleport runs this round**. The capture instrument is the
  per-second `geo` token on `FrameProfiler`'s **console** line (fed from `World.cs` beside the existing
  `SetCounts` call, render thread only). They ship unconditionally: Release is the only configuration
  measured or shipped here, an `#if`-guarded counter would be untestable headlessly, and each is one
  integer add or compare per **call**. The token is documented for a cold reader in the hand-off at the
  end of this file.
- **D1 · The chunk tables are keyed on a packed `long`, and `Vector3` stayed the outward key.**
  `ChunkManager.Chunks` is now a `ChunkMap` (`Chunks/ChunkMap.cs`) over a private
  `ConcurrentDictionary<long, Chunk>` (`ChunkManager.cs:22`), and `_dirtyChunks` /
  `_queuedChunkPositions` (`:115-116`), `_generatingMeshes` (`:125`) and C6's `_playerPlacedByChunk`
  (`:163`) hold packed keys directly. **The key type does not change outward.** `Chunks` still takes
  and returns `Vector3` on its indexer, `TryGetValue`, `ContainsKey` and `TryRemove`, so **zero call
  sites were edited to make the tree compile** — the wrapper type is the whole of D1's
  public-surface change, across a surface roughly 136 call sites wide. The member audit that made
  that possible ran over `OpenGLTest`, `OpenGLTest.Tests` and `OpenGLTest.ItemDebugger` **before**
  the wrapper was written and found the used surface to be exactly indexer / `TryGetValue` /
  `ContainsKey` / `TryRemove` / `Values` / `Count` plus one internal `foreach`: no `Keys`,
  `GetOrAdd`, `AddOrUpdate` or `TryAdd` anywhere. `ChunkMap` deliberately has **no `GetEnumerator`**
  — nothing needed one after the round, and its absence turns a future `foreach (var kvp in Chunks)`
  into a compile error that forces an explicit choice between `.Values` and the internal `.Packed`.
  It stays a `ConcurrentDictionary`, never a `Dictionary` behind a lock: chunk-gen workers write it
  and mesh-build pool workers read it.
- **D1 · `ChunkMap.TryRemove` is a plain passthrough, on purpose.** It bumps nothing and has no
  side effect, so `MeshingTests.cs:49`'s direct removal behaves exactly as it did at HEAD and
  `ChunkScanFrontier`'s doc point 10 — "`Chunks` is public and MeshingTests.cs:49 already removes
  through it, so the single-removal-site property is a convention, not something the type system
  enforces" — stays **literally true and unedited**. Making the wrapper bump `ChunkRemovalVersion`
  would have looked like a strengthening and would in fact have changed C3's contract while
  invalidating the sentence that records it. Do not do it later either.
- **D1 · The packer is `ChunkKey` (`src/Chunks/ChunkKey.cs`), 21 bits an axis, reusing `FluidManager`'s
  budget line for line.** `Pack(cx, cy, cz)` is
  `((cx & PackMask) << 42) | ((cy & PackMask) << 21) | (cz & PackMask)` (`ChunkKey.cs:31-37`)
  against `FluidManager`'s identical expression (`FluidManager.cs:36-37,938-939`), and `SignExtend`
  is a literal copy (`ChunkKey.cs:70` / `FluidManager.cs:952`). The two are **not** collapsed onto
  one helper — `FluidManager`'s packer is block-scale, `ChunkKey`'s is chunk-scale — and copying the
  constant rather than sizing a new one keeps them readable side by side. It is a shared
  `internal static class` rather than private helpers because it has two genuine consumers in
  different files (`ChunkManager` and `ChunkMap`), which `FluidManager`'s packer does not.
  **The representable range is asymmetric, and that asymmetry is the contract, not a bug:**
  **-1,048,576..+1,048,575 chunks = -16,777,216..+16,777,215 blocks**, because 21 bits signed is not
  +/-2^20 — `Pack(1 << 20, 0, 0)` keeps bit 20, which *is* the sign bit, so it unpacks to
  `-(1 << 20)`. `ChunkKeyTests.OnePastThePositiveEdgeDeliberatelyWrapsToTheNegativeEdge` pins that
  wrap deliberately, so nobody later "fixes" the asymmetry by writing a `+2^20` round-trip fact this
  packer cannot pass. Every path in **floors, never truncates** — `Pack(Vector3)` through
  `MathUtils.FloorToInt`, `PackBlock` through an arithmetic shift — which is exactly
  `VoxelCoords.ChunkPosOf`'s convention and the trap C6's
  `PlacementBucketsAreKeyedByFlooredChunkPosition` already guards. One deliberate behavior
  difference follows, recorded rather than hidden: a **fractional or non-finite** chunk position
  matched no key at HEAD and now floors into one (`(int)float.NaN` is 0, so a NaN center would probe
  chunk `(0,0,0)`). An audit of every `Chunks[` and every probe caller found none that can produce
  one; `ChunkScanFrontier`'s point 7 contemplates a NaN center and is unaffected, because that path
  never reaches a dictionary.
- **D1 · `UnloadDistantChunks` kept its shape, so C3's standing obligation survives intact.**
  The method is a key-type substitution in place and nothing else: the bump is still **after** the
  loop and still gated on `removed > 0` (`ChunkManager.cs:266`), `removed` is bit-identical at every
  iteration, and the false-to-true transition for `NeedsChunk` still lands at the
  `_queuedChunkPositions` removal (`:253`), **not** at the `Chunks` removal (`:243`). What changed is
  that the loop enumerates `Chunks.Packed` and hands `kvp.Key` straight to all four removals plus
  `_playerPlacedByChunk.Remove`, instead of packing the position back up once per unloaded chunk;
  `pos` comes from `ChunkKey.ToVector3(key)`, which is exact and independent of `Chunk.Position`.
  The three named guard facts are **green and unedited** —
  `ChunkManagerTests.UnloadBumpsChunkRemovalVersionOnlyWhenAChunkLeaves`,
  `ChunkScanFrontierTests.FrontierTrustsThePrefixUntilTheRemovalVersionMoves`,
  `ChunkScanFrontierTests.TheScanCostIsAmortizedNotPerCall` — and `ChunkScanFrontierTests` was not
  touched at all. `ChunkScanFrontier`'s doc points 2 and 4 were refreshed (prose only) to quote the
  post-D1 `NeedsChunk` and to cite lines that resolve.
- **D1 · Rekeying changes dictionary enumeration order, three sites observe it, and all three
  are order-insensitive at convergence — so a per-frame output comparison is the WRONG gate.** This
  is the paragraph a later round needs so it does not rediscover an inherent consequence. Bucket
  layout changes by construction when the key type changes, and (1) `ProcessDirtyChunks` caps its
  scan at `WorldGenerationConfig.MaxDirtyScanPerFrame` (1024) over `foreach (var kvp in
  _dirtyChunks)`, so *which* dirty chunks a frame even considers is a function of bucket order;
  (2) the over-budget branch sorts `_dirtyCandidates` with an **unstable** `List.Sort` on `distSq`
  alone and takes the first `MaxDirtyChunksPerFrame` (64), and distance ties are everywhere on a
  chunk lattice, so ties break by insertion order; (3) `UnloadDistantChunks`'s
  `foreach (var kvp in Chunks.Packed)` (`ChunkManager.cs:236`) feeds `RemoveRenderable`'s
  swap-remove, setting `Renderable` / `TranslucentRenderable` order, which `RenderManager`'s
  **unstable** back-to-front translucent sort (`RenderManager.cs:1781`) then observes for
  equidistant chunks. Each is order-insensitive in its *converged* result — nothing is lost, only
  deferred: out-of-range and unloaded dirty entries are dropped on sight, and missing-neighbor ones
  are re-added by `QueueChunk` when the neighbor arrives. So **D1 is output-preserving at
  convergence, not per frame**; a mid-stream pixel difference is expected rather than a regression,
  and the round's authority was an order-independent sorted cross-tree hash with pixels demoted to a
  settled-state smoke check.
- **D1 · Rejected alternatives, each priced rather than asserted.** *(a) Comparer-only* —
  keeping `Vector3` keys and passing a packing `IEqualityComparer<Vector3>` — was **measured, not
  dismissed**: arm A' below is a ~1 ns improvement on settled probes and a **pessimization** on
  mixed hit/miss (19.8 ns against HEAD's 18.0), and it never approaches a real `long` key. Its
  bucket layout is bucket-for-bucket identical to the `long` table, which makes it a clean layout
  control: with distribution held identical, `Vector3` keys still cost ~6 ns more per probe, so the
  residue is the key *type* — a 12-byte key, `Vector3` equality on every node visit, a wider node,
  and the non-inlinable interface call a custom comparer forces — not hash arithmetic and not bucket
  luck. *(b) Changing the key type outward* to `long` or a struct would have rippled through ~136
  call sites for no win over the wrapper. *(c) Converting `ChunkScanFrontier.FindNext` and
  `ChunkGenerationWorker`'s sorted offsets to integer offsets*, where **the largest remaining win on
  this path actually sits**, was blocked this round for a stated reason: it forces edits to
  `ChunkScanFrontierTests`, and `FrontierTrustsThePrefixUntilTheRemovalVersionMoves` and
  `TheScanCostIsAmortizedNotPerCall` had to stay green **and unedited**. Recorded as a prepared
  remedy, the way D5 records the paired-allocation one; a future round that takes it owes those two
  facts a careful re-derivation rather than a rewrite.
- **D1 · Sites deliberately left on the `Vector3` overload, each for a named reason, and none of
  them pays for it.** `World.cs:965`, `:1069`, `:1084`, `:1554` — one probe per player action (break,
  fell, place), and `:1069`/`:1084` need the `Vector3` for their `HashSet<Vector3> touchedChunks`
  anyway; there is deliberately **no `World` step** in this round because it would have been empty.
  `TntManager.cs:108-109` and `:168-169` — both locals feed `touchedChunks.Add`, so the `Vector3` is
  needed regardless, and `:182` iterates that same set. `FluidManager.cs:154` — compares the probe
  result against `chunk.Position`, one probe per newly-loaded chunk.
  `ChunkManager.ApplyCompletedMeshes` — holds a `Vector3` from the completed-mesh queue tuple for
  the `DistanceSquared` gate on the same line, and drains at most a few entries a frame. **None pays
  a penalty:** arm H1 below measured the `Vector3` overload *through* `ChunkMap` at **41-47 %
  cheaper** than HEAD's `Vector3`-keyed probe, nowhere near the 1.05x threshold the pre-registered
  rule set for calling it a cost. They simply forgo the further ~2.4 ns/probe the int-in-hand path
  buys.
- **D1 · What took the `int` fast path, and why those sites and not others.** Every site that
  probes a **distinct key per operation** took it, because those are the sites with no pack to
  amortize: `Chunk.TryResolveNeighbors` (`Chunk.cs:1060`, **26 probes per mesh build**, on pool
  threads), `LodManager.cs:731` (the column prefetch C5 measured at **36 / 100 / 324 chunk columns
  per region build**, on the LodGen thread), `ChunkManager.HasAllNeighbors` (`:892`),
  `GetBlockGlobal` / `ChunkAt` (`:759`, `:772`, whose intermediate `Vector3` was built from the same
  shifts and is simply gone), `QueueChunk`'s six-neighbor fan-out, `MarkAreaDirty`'s triple loop,
  `FluidManager.WakeBorders`' six border probes, and `TntManager` / `FallingBlockManager`'s
  shift-derived lookups. The six cardinal offsets now come from one shared
  `Vector3Directions.CardinalOffsets` array rather than hand-transcribed literals, pinned equal to
  `CardinalDirections` by a fact so a reorder of one cannot silently diverge from the other.
- **D1 · 20 new facts, no existing test edited, suite 770 -> 790.** **+11 `ChunkKeyTests`**
  (positive and negative round trips per axis, the sign boundary, both representable edges, the
  deliberate wrap one past the positive edge, axis independence, a 531441-key collision sweep over
  -40..40 per axis, `Pack(Vector3)` agreeing with `Pack(int,int,int)`, `PackBlock` agreeing with
  `VoxelCoords.ChunkPosOf`, `ToVector3` inverting `Pack`, and `CardinalOffsets` matching
  `CardinalDirections`), each mutation-checked against the specific defect it names. **+7
  `ChunkMapTests`** pinning the wrapper's contract rather than its internals. **+2
  `ChunkRekeyReplayProbe`**, the cross-tree gate, written **before the first production edit** and
  unchanged since — which is why it compiles on a pristine HEAD export and is the round's authority.

- **C6 · Player placements are bucketed by chunk, so an unload drops a bucket instead of scanning
  the world.** `ChunkManager._playerPlaced` was one flat `HashSet<(int X, int Y, int Z)>` holding
  every cell the player has ever built, and `UnloadDistantChunks` called `RemoveWhere` on it **once
  per unloaded chunk** — O(unloads x placements), with the whole set walked whether or not any of it
  lay in the dropped chunk. It is now `_playerPlacedByChunk`, keyed by the **same chunk key `Chunks`
  is keyed on**, so the unload site's key is the one it already holds. C6 shipped it as
  `Dictionary<Vector3, HashSet<(int X, int Y, int Z)>>` keyed by `VoxelCoords.ChunkPosOf(x, y, z)`
  and left D1 un-pre-empted; **D1 has since taken it with the rest**, so it is now
  `Dictionary<long, HashSet<(int X, int Y, int Z)>>` (`ChunkManager.cs:163`) keyed by
  `ChunkKey.PackBlock(x, y, z)` — identical flooring, so C6's own facts did not move.
  The structure stays **plain and non-concurrent** on purpose: all nine call sites are
  main-thread (`Chunk.cs`, `BucketPlaceItem.cs`, `FluidManager.cs`, `TntManager.cs`, `World.cs`),
  nothing enumerates the set, and AGENTS.md reserves the concurrent collections for genuinely shared
  state. The three accessor signatures (`IsPlayerPlaced` / `MarkPlayerPlaced` /
  `UnmarkPlayerPlaced`) are unchanged — they are the whole contract, so no call site was edited.
- **C6 · The empty-bucket drop is load-bearing, and it is what keeps the index from leaking.**
  `UnmarkPlayerPlaced` removes the bucket as soon as its last cell goes; without that the index would
  grow **one permanent entry per chunk ever edited**, trading the scan for a slow leak — the obvious
  way to get this refactor wrong. It also returns early when the bucket is absent, so the
  unconditional unmarks from `FluidManager` and `TntManager` on blocks that were never placed stay
  allocation-free no-ops. `internal int PlayerPlacedChunkCount` was added for diagnostics/tests only,
  so the invariant is pinnable. Three new facts guard the round:
  `ChunkManagerTests.UnloadForgetsPlacementsOnlyInsideTheUnloadedChunk` (chunk-scoped purge, with
  guard assertions on `Chunks` membership so it cannot pass without an unload having happened),
  `PlacementBucketsAreKeyedByFlooredChunkPosition` (the floor-versus-truncate key trap — the deleted
  `(int)pos.X * Chunk.Size` *truncated*, which was safe only because it read `Chunks`' own integral
  keys, whereas the accessors need `ChunkPosOf`'s floor or block `(-1,-1,-1)` files with `(0,0,0)`)
  and `UnmarkingTheLastCellDropsItsBucket`. Each was checked against the specific defect it guards in
  a scratch copy: a flat `HashSet` fails facts 1 and 3; a truncating bucket key fails fact 2.
- **C6 · `UnloadDistantChunks` kept its shape, and the C3 frontier obligation survives unchanged.**
  This is the part worth reading before the next edit to that method. C6 is a **seven-for-one line
  swap** inside the existing `if (!Chunks.TryRemove(pos, out var chunk)) continue;` branch: the three
  `x0/y0/z0` locals and the four-line `RemoveWhere` call were deleted and `_playerPlacedByChunk.Remove(pos);`
  put in their place, in the same position (immediately after `_dirtyChunks.TryRemove`). **Nothing
  else in the method changed** — not the `foreach (var kvp in Chunks)` enumeration, not the distance
  gate, not `removed++`, and above all not the post-loop
  `if (removed > 0) Interlocked.Increment(ref _chunkRemovalVersion);` or its comment. The bump stays
  **after** the loop and stays **gated**, and both reasons still hold: a position only becomes needed
  at the `_queuedChunkPositions.TryRemove(pos, out _)` above, so a bump published earlier would
  announce a transition that has not happened; and the 120-frame fallback scan on a settled world
  still removes nothing, so an ungated bump would still restart four scans twice a second. The edit
  **cannot** weaken either, because `_playerPlacedByChunk` is invisible to `NeedsChunk`, to `Chunks`,
  to `_queuedChunkPositions` and to the frontier, and `removed`'s value is bit-identical at every
  iteration. The three guarding facts were **not edited** and pass:
  `ChunkManagerTests.UnloadBumpsChunkRemovalVersionOnlyWhenAChunkLeaves`,
  `ChunkScanFrontierTests.FrontierTrustsThePrefixUntilTheRemovalVersionMoves`,
  `ChunkScanFrontierTests.TheScanCostIsAmortizedNotPerCall`.
- **D6 · The last three `Clear()`-at-cap worldgen caches now evict generationally too.**
  `TreeGenerator.ShapeCache`, `TreeGenerator.CanopyCache` and `CactusGenerator.ShapeCache` all
  carried the pre-D2 pattern — `if (Count >= limit) Clear()` on the miss path, which threw the whole
  working set away *and* evaluated `ConcurrentDictionary.Count`, an all-locks barrier, on **every
  miss**. All three now run on `GenerationalCache<TKey, TValue>` at **unchanged capacity** (tree
  shapes 16384, canopy tops 4096, cactus stems 16384), so peak residency does not move — only the
  eviction policy does. The knobs moved into `WorldGenerationConfig` beside their owning generators'
  other constants (`TreeShapeCacheCapacity` / `TreeShapeCacheGenerations`, `TreeCanopyCacheCapacity` /
  `TreeCanopyCacheGenerations`, `CactusShapeCacheCapacity` / `CactusShapeCacheGenerations`),
  following `ColumnCache*` / `CellCache*`. **All three ship at G = 4**, matching
  `ColumnCacheGenerations` and `CellCacheGenerations`, so all **five** `GenerationalCache` sites now
  read alike. AGENTS.md's worldgen bullet names all five and their knobs, and its two forbidden
  examples (`LodRegion.ColumnCaps`, `TreeGenerator.CanopyDisturbed`) are unchanged.
- **D6 · G = 4 was not assumed — it was the branch the round's own capture selected, for each cache
  separately.** Lesson 4 says `Generations` may only be moved against a real capture, and the losing
  regime is real (G = 2 was measurably *worse* than the wholesale clear for `ColumnCache`), so the
  capture ran **before** any conversion, carrying a decision rule written in final wording before a
  number was read: convert at G = 4 if the interleaved sequence's generational misses are <= the
  wholesale-`Clear()` misses; otherwise either re-price that one cache's G against *that* table, or
  leave it unconverted and rewrite its share of the bullet. **The rule did not fire for any of the
  three** — the worst G = 2 row across all three caches is an exact tie, and every G = 4 interleaved
  row is at or below the clear. So no `Generations` value was derived this round; the capture is what
  licensed leaving all three at 4. Full tables: **"Measurement: C6 + D6"**.
- **D6 · What it bought, in `WormCarver.CellCache`'s honest framing** (that half of D2 bought
  *uniformity, not hit rate*, and this round is mostly the same shape). On every sequence taken
  **alone**, misses
  equaled the distinct key count under *both* policies at *every* G — nothing is evicted before its
  last use, because the rings are swept closest-first and neighboring regions share border keys. The
  two policies **tie exactly wherever the working set fits under the cap**, and the gap only opens
  once the stream runs past capacity, widening with how far past (tree shapes: 0.00 % at 4k and 8k
  distinct keys, -0.13 % at 16k, -5.32 % at 32k, -8.90 % at 64k). So what D6 mostly buys is
  **uniformity plus the removal of the all-locks `ConcurrentDictionary.Count` from the miss path**,
  with a real but modest miss reduction only in the outrun-the-cache regime. Do not read a hit-rate
  win into it beyond those rows. The canopy cap does bite, and the mechanism is
  `LodRegion.BuildMeshData` (`LodRegion.cs:524`) — the cache's **only** engine caller — asking
  `TryGetCanopy` once per **fine column** of a region: measured distinct chunk columns per ring are
  **3152 / 12608 / 50432** at levels 0 / 1 / 2 against a 4096 cap, so it is the level-1 and level-2
  rings that overrun it, not level 0. (The radius-8 ring `LodManager.BuildSortedOffsets` builds is a
  **disc of 197 regions**, not a 17x17 square — an earlier estimate of "~4.6k level-0 columns" came
  from the square and is wrong.) `TreeGenerator.ShapeCache` is the one of the three genuinely shared
  between two streams (`TreeGenerator.Stamp` on the generation workers and `BuildCanopyTops` under
  the far-LOD sweep), so it is the one where the wholesale `Clear()` had two populations to throw
  away at once; `CactusGenerator.ShapeCache` has exactly one caller (`CactusGenerator.Stamp`) and
  nothing sweeps it, so only a desert traverse can trip its cap.
- **D6 · The design wrinkle the next reader will hit: `GetOrAdd`'s factory is key-only, so both
  `ShapeCache`s recompute rather than capture.** The two miss paths used to close over a `baseY` and
  (for trees) a `biome` computed earlier in `TryGetTrunk` / `TryGetCactus`. The factories are instead
  **static, non-capturing `Func<,>` fields** (`TreeGenerator.BuildShapeFor`,
  `CactusGenerator.BuildShapeFor`, `TreeGenerator.BuildCanopyFor`) that recompute
  `HeightAt(x, z) + 1` and `TerrainGenerator.BiomeAt(x, z)` **from the key, on a miss only** —
  mirroring `Chunk.ComputeColumn` and `WormCarver.BuildCellFor`. That is a deliberate trade: a
  capturing lambda would allocate a closure at the call site on **every call including hits**, on the
  four chunk-gen threads and the LOD builder, which is exactly the traffic this round is trying to
  reduce. It is a behavior-preserving *claim*, so it is **pinned, not asserted**:
  `WorldgenShapeCacheEvictionTests.TreeCactusAndCanopyShapesSurviveForcedCacheEviction` asserts
  `baseY == Chunk.GetColumnHeights(...)[...] + 1` and `shape.TrunkBaseY == baseY` for both generators,
  cold, on a hit, and again after a forced eviction. **`TreeGenerator.CanopyDisturbed` was not
  touched** — player-edit state, `TODO.md` `M-10`, `GenerationalCache` invariant 10 — so
  `using System.Collections.Concurrent;` stays in `TreeGenerator.cs` for it alone, and
  `LodRegion.ColumnCaps` (then `D2b` here, now `TODO.md` `M-11`) was likewise left entirely alone.
- **D2 · Both deterministic worldgen caches evict generationally instead of clearing wholesale.**
  `Chunk.ColumnCache` and `WormCarver.CellCache` were `ConcurrentDictionary` plus
  `if (Count >= limit) Clear()` on the miss path. Both now run on
  `GenerationalCache<TKey, TValue>` (`WorldGen/GenerationalCache.cs`): G generation dictionaries
  published as **one immutable node swapped by `Interlocked.CompareExchange`** — structurally the
  same protocol as C3's `ChunkScanFrontier.Node` and C5's TryAdd `_building` claim — where a read
  probes the newest map and returns, a miss computes into the newest, and when the newest fills its
  `capacity / G` insert budget the node rotates and the **oldest** generation is dropped. Nothing is
  ever cleared wholesale. **Capacities are unchanged** (16384 columns, 4096 cells), so peak
  residency does not move; the knobs moved into `WorldGenerationConfig` (`ColumnCacheCapacity` /
  `ColumnCacheGenerations`, `CellCacheCapacity` / `CellCacheGenerations`), which removes a standing
  deviation from AGENTS.md's "tuning knobs live in `WorldGenerationConfig`" rule rather than adding
  one. Ships at **G = 4**.
- **D2 · Promotion is restricted to the oldest generation, and that is the design's load-bearing
  decision.** A hit in the newest or any middle generation is a **pure read with no write** — which
  is what `GetColumnData` is today, and it is called from the main thread as well as five background
  threads, so per-hit LRU bookkeeping would have put a bucket-lock write on the frame budget and on
  the exact path this round exists to decontend. Only a hit in the *oldest* generation — the one the
  next rotation drops — is promoted forward (a second chance). Measured promotion rate at G = 4:
  **11.31 % of accesses** on the mixed burst, **0 %** on chunk generation alone. The safety argument
  rests on one invariant, stated as 10 numbered points in the type's doc comment: **values are a
  pure deterministic function of their key and immutable after publication**, so an evicted, stale
  or duplicated entry can only ever cost a recompute and can never surface as a *wrong* value.
- **D2 · What it bought, and what it did not.** Chunk generation's own misses while a LOD ring sweep
  runs fall **1219 -> 733 (-40 %)**; avoidable recomputes overall fall **5473 -> 3413 (-38 %)**. The
  `CellCache` half bought **uniformity, not hit rate** — its 4096-cell cap sits one to two orders of
  magnitude above its ~150-cell working set and its `Clear()` essentially never fired in play. The
  third part of the old D2 bullet, `LodRegion.ColumnCaps`, was **deliberately not taken**: its
  entries are irreproducible player edits, so eviction there is data loss, not a recompute. It is
  now its own item — it was `D2b` here and is now `TODO.md` `M-11`, having turned out to be a
  correctness item rather than a perf one. Full write-up, gate and table: **"Measurement: D2"**.
- **R10 · LOD regions no longer own GL objects; all three ring walks are one multi-draw each.**
  `LodRegion` had a `Mesh` (VAO + VBO + EBO) for its terrain shell and a second `Mesh` for its sea
  sheet. Both are gone. A region now holds `OpaqueSlice` / `WaterSlice` — two `GeometrySlice?`s in
  one shared `ChunkGeometryPool` — and `RenderManager.RenderLod`, the mirror's region loop in
  `RenderReflection` and the water sheets in `RenderWorldTranslucent` each became `BeginPass` + an
  `Add` loop + one `ChunkDrawBatch.Draw(LodGeometry.Vao, chunkShader, …)`, i.e. one
  `glMultiDrawElementsIndirect`. The CPU frustum cull did **not** move — this is not the ruled-out
  *compute frustum cull*.
- **R10 · One LOD pool, not two, and why.** `RenderManager.LodGeometry` (`RenderManager.cs:42`,
  constructed at `:382`, disposed at `:2562`) holds **both** the shells and the sheets. The chunk
  side's opaque/translucent split exists for a *sizing* reason (512 MB vs 64 MB, stated in the
  constants' own comments), not a correctness one, and it does not transfer: a region's two meshes
  are born from one `LodMeshData` and die in the same `Dispose`, so their lifetimes never diverge.
  One pool means one VAO for all of LOD and **one free list for D5 to observe**. They stay two
  *separate* slices in that one pool — fusing them into a paired allocation would halve LOD's
  allocator traffic but needs a new pool API; that is recorded in D5's bullet as a prepared remedy,
  deliberately not folded in here.
- **R10 · `LodInitialUnits`, and the 591-settled / 759-resident correction that outlives R10.**
  `ChunkGeometryPool.LodInitialUnits = 163840` (320 MB of vertices + 30 MB of indices), set from
  measurement: `perf-fly` peaked at **118004 of 131072 units (90 %)** at 660 resident regions and
  never grew, but 90 % is not headroom. Occupancy is a steady **176-181 units per resident region**
  (shell plus sheet). **The sizing runs off the resident ceiling, not the settled count, and that
  distinction was wrong everywhere in this file before R10:** generation scans at `RegionRadius`
  (`LodManager.cs:122`, 197 integer offsets at radius 8, x 3 `LodRegion.Levels` = the familiar
  **591**) while **retention and the out-of-range upload drop test use `RegionRadius + 1`**
  (`LodManager.cs:438`, `:474`) — 253 offsets per level, so **759 regions can be resident at once**.
  That hysteresis band is occupied exactly while the camera is moving, which is when the pool is
  under pressure; `lod-horizon` was observed at 665 resident. 759 x 176-181 projects to ~137400
  units, which 131072 does not cover; 163840 does, with the doubling still in reserve. At 163840 the
  re-verified peak is **117844 (72 %)** and the pool grew in **none** of the 12 `perf-fly` runs or
  the 42 visual-gate runs. Both counts are pinned headlessly by `LodGeometryPoolTests`
  (`TheSettledRegionSetIsFiveNinetyOne`, `TheResidentCeilingIsSevenFiftyNine`), derived from
  `LodRegion.Levels` rather than hard-coded, so changing any level's `RegionRadius` fails a test
  instead of silently under-sizing the pool.
- **R10 · the null-pool seam, which ~20 headless call sites depend on.** `LodRegion`'s constructor
  is `LodRegion(ChunkGeometryPool? pool, LodMeshData data)` — **pool first**, so the existing
  `new LodRegion(null!, data)` sites still bind — and `LodManager`'s primary constructor is
  `LodManager(ChunkGeometryPool? lodGeometry = null)`, so every `new LodManager(null!)` compiles
  untouched. A region uploads only the lists that are non-empty and takes
  `_pool = pool ?? throw new InvalidOperationException(…)` immediately before each upload — **two**
  guards, not one, because an all-sea region legitimately has a sheet and no shell — mirroring
  `Chunk.UpdateMesh`. Empty meshes need no pool at all, which is what keeps `LodManagerTests`,
  `LodBuildPoolTests`, `LodBufferPoolTests`, `LodColumnCapTests`, `LodBuildCostProbe` and
  `FallingBlockTests` running headlessly. Pinned by `LodManagerTests.AnEmptyRegionMeshNeedsNoPool`
  and `ANonEmptyRegionMeshWithoutAPoolThrows` (which covers **both** guards). **Do not "tidy" those
  parameter orders** — roughly 500 lines of burial and cover-flag coverage stop compiling.
  `LodManager`'s `GL` parameter is gone entirely; it had exactly one use.
- **R10 · LOD shares `RenderManager`'s single `_chunkDraws` instance, and the reason is a hazard
  worth recording accurately.** A second `ChunkDrawBatch` is **possible** — it would simply have to
  `glBindBufferBase` inside `Draw`, or take its own binding point. Today's class does **neither**:
  `Allocate` binds the SSBO to `SsboBinding = 0` once at construction (`ChunkDrawBatch.cs:259`) and
  `Draw` never rebinds, so a second instance would silently make every chunk draw read LOD's
  per-draw data **with no GL error**. That is latent, not live, because R10 shares the one instance
  — but any future round that wants a second instance must fix the binding first. Sharing also buys
  one `BeginFrame`, one fence set and one cursor.
- **R10 · the draw ring's command budget is per *frame*, cumulative across every `BeginPass` — not
  per pass.** `_cursor` resets only in `BeginFrame` (`ChunkDrawBatch.cs:143`) and `BeginPass` grows
  when `_cursor + maxCommands > _capacityPerSlot` (`:155-156`). The shadow pass alone reserves
  `chunks.Count` (17077 in the gate scenarios) against 32768 per slot, and R10 adds three more
  reservations of <= 591 each. `Grow` calls `glFinish` (`ChunkDrawBatch.cs:272`), a full pipeline
  stall, and logs at `:293` — so that line is the instrument. **It appeared in none of the 12
  `perf-fly` runs and none of the 42 gate runs, on either tree.** The margin is comfortable but not
  infinite; a fourth pass should re-check it.
- **R10 · LOD draws through `chunkShader`, the existing `CHUNK_DRAW` variant. No new shader
  variant, no shader edit** — and the compatibility argument is recorded here so a future round need
  not re-derive it line by line. `worldShader` and `chunkShader` are the **same**
  `test-vertex.glsl` / `test-fragment.glsl` pair, differing only by `#define CHUNK_DRAW`
  (`RenderManager.cs:24`, `:329-332`). A region's model matrix was always a pure translation, so the
  variant's `TO_WORLD(p) = p + chunkOrigin` (`test-vertex.glsl:18`) reproduces
  `(uModel * vec4(p,1)).xyz` (`:23`) exactly; the vertex shader's own comment at
  `test-vertex.glsl:95` — *"uModel is a pure translation for chunks/LOD, so the normal is
  unchanged"* — is precisely the assumption the `CHUNK_DRAW` branch makes when it sets
  `fNormal = vNormal`. The water and `lodLeaves` alpha flags live in the **shared** fragment shader,
  so they are unaffected; `LodMeshData.Indices` / `WaterIndices` are already `List<ushort>`,
  matching `ChunkGeometryPool.Upload`'s signature, so no index-width conversion appears anywhere.
  `Add`'s `(origin, alpha)` pair is byte-for-byte what the old `SetDrawUniforms` call computed:
  origin `new Vector3(region.X * region.SizeBlocks, 0f, region.Z * region.SizeBlocks)` and alpha
  `MathF.Min(1f, (float)(region.Lifetime / LodRegion.MAX_LIFETIME))`. `RenderLod` still advances
  `Lifetime` and the mirror still does not — that asymmetry is preserved verbatim.
- **R10 · the `uAlphaCutoff` restore moved programs, and `SetDrawUniforms` is now entity-only.** In
  `RenderWorldTranslucent` the two `worldShader.SetUniform(_locAlphaCutoff, 0f/0.5f)` lines are
  gone; instead `chunkShader.SetUniform(_locChunkAlphaCutoff, 0.5f)` **moved** from just after the
  chunk translucent `Draw` to just after the LOD water `Draw` (`RenderManager.cs:1738`, `:1768`), so
  the sheets still submit at cutoff 0 and the chunk path still ends at 0.5. Pass order is unchanged:
  chunk translucent multi-draw first, LOD water multi-draw second. `worldShader`'s cutoff is no
  longer touched by that pass at all and stays at the 0.5 `BeginFrame` sets. `SetDrawUniforms`
  (`RenderManager.cs:1498`) is now reached only by entity, block-cube, item and outline draws, and
  its doc comment says so. `Mesh` stays in the tree for exactly those consumers.
- **R10 · the two LOD sort comparators are now `LodRegion.CompareOpaqueDraw` /
  `CompareWaterDraw`** (`LodRegion.cs:184`, `:198`), taking plain scalars so they can be pinned with
  no `LodRegion` instance, no `LodMeshData` and no `TestContent`. `RenderLod`'s and `CullWater`'s
  `Sort` lambdas delegate to them (`RenderManager.cs:1816`, `:1715`) with their original rationale
  comments carried verbatim. **This matters more than it looks:** `glMultiDrawElementsIndirect`
  executes commands in array order, so since R10 these orderings **are** the submission order — a
  future refactor that reorders an `Add` loop silently changes depth resolution and water blending.
  `LodDrawOrderTests` pins both, mutation-checked (inverting the level comparison in either
  comparator fails its own fact and the disagreement fact with it). The mirror's region list is
  **deliberately unsorted** (it adds `(region, 0f)`, as it always has) and now carries a comment at
  `RenderManager.cs:1183` saying so — do not "fix" it into a comparator.
- **R10 · `ApplyCompletedMeshes` frees a replaced region *before* uploading its rebuild**
  (`LodManager.cs:501-508`). The `Regions.TryRemove` block moved above the construction, carrying
  `replaced.Lifetime` across in an object initializer. The reason is pool occupancy: a same-sized
  rebuild now lands in the exact hole it just vacated, so a rebuild leaves the free list
  **unchanged** instead of appending one — pinned by
  `GeometryAllocatorTests.ASameSizeReallocationAfterAFreeReusesTheSameOffset`, and it means rebuilds
  contribute no net allocator traffic at all. **The one behavioral difference, recorded in the code
  comment too:** if `ChunkGeometryPool.Upload` throws — reachable only when the allocator still
  cannot fit after a grow (`ChunkGeometryPool.cs:156-157`) — the replaced region is already out of
  `Regions` and `RegionList` with nothing scheduled to rebuild it, where before the reorder the
  stale region survived. Accepted deliberately: that throw means the pool could neither fit nor
  grow, which is unrecoverable and already fatal in practice. The happy path is identical.
- **R10 · `MaxLodUploadsPerFrame` was NOT retuned, and that is a decision, not an oversight.** It
  stays at 4 and still bounds the same quantity — regions becoming resident per frame — but each
  unit of that work is now strictly cheaper: 6 GL object creations + 4 `glBufferData` per arriving
  region become 1-2 `glNamedBufferSubData`. The one **new** tail risk is that an upload inside the
  cap window can trigger `ChunkGeometryPool.Grow`, a full-pool GPU copy already ranked hitch #3; the
  mitigation is the sizing above plus the growth log line, not a new knob. Retuning the cap would
  have been an unmeasured knob change smuggled into a structural round.
- **R10 · the C5 artifacts were untouched and sit upstream of everything R10 changed.** The
  `_building` claim protocol with both rollbacks, `MaxLodBuildsInFlight`, `LodBuildThrottleDelayMs`,
  the Idle/Throttled split, `MaxConcurrentLodGenerations = 1` and the `CompletedBuildCount`
  instrument are all *before* `ApplyCompletedMeshes` in the pipeline; `CompletedBuildCount` counts at
  **enqueue**, deliberately, so it still measures builder throughput and R10 cannot have moved it.
  `LodRegion.BuildMeshData` was not touched, and the out-of-range drop branch still touches no GL.
- **R10 · one side effect worth knowing about, which is NOT a creditable result.** `Shader.Use`
  caches the bound program (`Shader.cs:103-108`). HEAD bound `worldShader` in all three LOD paths,
  so `RenderWorld`'s `chunkShader.Use()` was a real `glUseProgram` every frame; it is now a no-op.
  `worldPass` CPU moved with it (0.86-0.89 -> 0.82-0.86, disjoint at 4+4 but **touching at 6+6**),
  and the measurement rule below excludes that row anyway. Recorded as a mechanism, quoted as
  nothing.
- **R10 · one step of the sequence committed ~280 MB of VRAM for a pool with no consumer.** That was
  the plumbing step and it existed for exactly one step; it is not a shipped condition. Noted only so
  a bisect landing on it is not mistaken for a leak.

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
  refuted, not as the thing to restore. The decision and what it cost to take are in the C5 closeout
  at the head of this section and in the pool-size entry under **Ruled out**.
  Everything else in this section is written for N workers and is correct at any N — the pool size is
  the only token that changed.
- **C5 · the claim protocol, the in-flight cap and the retention ceilings.** `_building` (a
  `ConcurrentDictionary` claim set) gated on the **result** of `TryAdd` inside both claim paths;
  `WorldGenerationConfig.MaxLodBuildsInFlight = 8` checked *before* any claim;
  `LodBuildThrottleDelayMs = 1`; `LodManager.MeshBuffers`' retention ceilings 4 -> **16**. The full
  argument is the next section — it is the round's reusable artifact and is the part worth keeping
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
  anything about LOD builder throughput is measurable at all, and `OpenGLTest.Tests/Lod/LodBuildCostProbe.cs`
  (a measurement harness, not a regression guard — it asserts no timing) is the cheapest way a future
  round re-prices the builder.
- **D3.** `ChunkDrawBatch.PassCount` deleted. The private `_passCount` field stays — it is the real
  `MultiDrawElementsIndirect` count. `grep -rn "PassCount" OpenGLTest OpenGLTest.Tests --include=*.cs`
  now returns nothing.
- **C12(b).** `Chunk.GenerateBlockData`'s scalar recount is now
  `_solidCount = BlockCount - _blocks.AsSpan().Count(BlockType.Air)` (`Chunk.cs:507`). Counting *air*
  and subtracting is the right shape rather than an accident: `BlockType.Air` is
  `public const byte Air = 0` and `_blocks` is `byte[]`, so this binds to the vectorized
  `MemoryExtensions.Count<byte>` with no cast, where a `!= Air` predicate would run one comparison
  per cell. `internal int SolidCount` (`Chunk.cs:59`) was added purely as a test seam — nothing in
  the engine reads it — because `_solidCount` and `_blocks` are private and `GetLocalBlock` was the
  only public block reader, so the new form could not otherwise be pinned against a scalar oracle.
  No measurement was taken and none should be invented.
- **C12(c).** `LodManager.RemoveRegion`'s `_pendingCover.Remove(region)` is now an index-tracked
  swap-remove mirroring `LodRegion.ListIndex`: `LodRegion.PendingCoverIndex` (`LodRegion.cs:85`,
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
  (`RenderManager.cs:2131`) is the one path where submission order reaches the GPU — it walks its
  list with a plain `foreach`. Its blend state is **conditional and must be stated as such**: debris
  always draws with depth writes **on** (`LeafParticleRenderer` restores `DepthMask(true)` at `:174`
  and `RenderBlockDebris` sets no blend state of its own), and it inherits `Blend` still enabled at
  `SrcAlpha`/`OneMinusSrcAlpha` — set at `LeafParticleRenderer.cs:159-160` and never disabled —
  **only on frames where leaves actually drew**, since that renderer returns early at `:103` when the
  count is zero. So submission order *is* visually load-bearing for overlapping semi-transparent
  debris cubes. All four systems are consumed purely as `IReadOnlyList<T>`, so one shared
  `internal sealed class ParticleRing<T> : IReadOnlyList<T>`
  (`OpenGLTest/src/Particles/ParticleRing.cs`, the `ChunkScanFrontier` precedent for a GL-free internal
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

### The C5 concurrency argument — the reusable artifact of this round

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
land last — a stale LOD shell over a freshly mined column, the exact artifact `ColumnCaps` exists to
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
being parallelizable at a profit** — see the measurement.

**One real behavior change was surfaced rather than papered over.**
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

### Measurement: D1 — the only instrument that can resolve a rekeying is an isolated probe cost

**Say first which instrument can and cannot resolve this item, because most of them cannot.**
*Can*: an isolated **off-engine microbenchmark of the probe itself, in nanoseconds, per call-site
shape** — probe cost is the only quantity D1 changes. *Cannot*: any `perf-fly` field (the 60 s
window is saturated for both chunks and LOD, lesson 1, so every end-state throughput field measures
the scenario ceiling); any counted replay in D2's or C6's style (**D1 changes probe cost, not probe
count** — the counts are bit-identical by construction, which is exactly what the cross-tree hash
exists to prove); any in-engine frame-time row (single-digit nanoseconds against a ~9 ms frame
holding ~10^5 other operations). **No engine-level or frame-time number is quoted anywhere in this
round**, and none may be derived from these figures later.

**The decision rule was written in final wording before the harness existed and before a single
number was read** (the D6 precedent), and it is reproduced verbatim so the branch that fired can be
checked against it:

> - FULL D1 iff `median(B) <= 0.85 * median(A)` AND `median(C) <= 0.85 * median(H0)`, each on
>   SETTLED, in at least 4 of the 5 scored sweeps, with an absolute saving of at least 1 ns.
> - REDUCED D1 if B is within +/-15 % of A but C clears its bar: the conversions still all run, and
>   the bullet records plainly that `NeedsChunk` and the frontier are NEUTRAL and the win is bought
>   entirely on the int-in-hand paths. This is a landing, not a no-go.
> - INVERTED case: if C clears but B is MORE than 15 % worse than A, land the reduced form and
>   additionally record that the frontier's probe got dearer, and state the prepared remedy
>   (integer offsets in `ChunkScanFrontier`, blocked this round because it forces edits to
>   `ChunkScanFrontierTests` and the two named facts must stay unedited).
> - H1 IS A DIAGNOSTIC, NOT A SEQUENCING GATE. Its job is to price the sites the round deliberately
>   leaves on the `Vector3` overload. If `median(H1) > 1.05 * median(H0)`, the bullet must state the
>   per-probe cost those sites pay and bound it by their probe counts, rather than claiming the
>   change is free everywhere.
> - If neither B nor C clears its bar, LAND NOTHING and rewrite D1's bullet with the finding, on the
>   R3/C5 precedent.
> - A' is reported for the record. Comparer-only is taken only if A' beats BOTH B and C, which would
>   contradict the `EqualityComparer<TKey>.Default` devirtualization argument and would itself be
>   the round's finding.
> - WHATEVER the numbers, no engine-level or frame-time claim is quoted anywhere.

Tie-break wording, also fixed before the capture: "within +/-15 % of A" means
`0.85*median(A) <= median(B) <= 1.15*median(A)`; the 1 ns floor applies per probe-group as reported;
and if C fails its bar the round lands nothing whatever B does.

**The harness.** A self-contained scratchpad console project (net9.0, Release, workstation
concurrent GC, `TieredPGO`, matching `VoxelEngine.csproj`) referencing **nothing** from the
engine. It rebuilds the workload locally: the radius-16 closest-first offset ring (17077 offsets —
the exact count `ChunkGenerationWorker.BuildSortedOffsets` produces at `ChunkRadius` 16), plus local
copies of the `ChunkKey` pack expression and of `MathUtils.FloorToInt`. 6.83 M probe-groups per
measurement, 1 warm-up + **5 interleaved scored sweeps** per workload, arms rotated run for run so
no arm owns a position, a consumed checksum so nothing is dead-code-eliminated, and **four separate
process launches**. It asserts nothing.

**The arms are call-site SHAPES, not implementations**, because packing amortizes across the
dictionary operations a site performs per key, and the source pins the reuse counts exactly.
*Shape N* (`NeedsChunk`): one key, up to two `ContainsKey` probes. *Shape H*
(`HasAllNeighbors`, `GetBlockGlobal`/`ChunkAt`, `Chunk.TryResolveNeighbors`, `LodManager.cs:731`):
one distinct key per probe, no reuse at all.

| arm | shape | what it is | SETTLED median ns (4 runs) | STREAMING median ns |
| --- | --- | --- | --- | --- |
| A | N | `Vector3` keys — HEAD | 17.76 / 16.49 / 15.81 / 16.05 | 17.95 / 17.92 / 18.36 / 18.04 |
| A' | N | `Vector3` keys + packing `IEqualityComparer` | 15.24 / 15.37 / 15.08 / 15.05 | 19.82 / 19.84 / 19.88 / 19.76 |
| B | N | `long` keys, pack once from the `Vector3` — D1 | 9.21 / 9.33 / 9.22 / 9.25 | 12.84 / 12.65 / 12.67 / 12.35 |
| H0 | H | `Vector3` keys — HEAD | 18.12 / 16.91 / 16.36 / 16.34 | 13.54 / 13.26 / 13.56 / 13.38 |
| H1 | H | `long` keys, pack per probe from a `Vector3` | 9.64 / 9.66 / 9.74 / 9.55 | 10.67 / 10.67 / 10.81 / 10.66 |
| C | H | `long` keys, pack per probe from ints in hand | 6.98 / 7.17 / 7.03 / 7.17 | 7.34 / 7.44 / 7.50 / 7.47 |

Units are ns **per probe-group** — per key for shape N, per probe for shape H. SETTLED fills the
dictionaries with all 17077 ring keys (the frontier's worst case, a full scan finding nothing);
STREAMING fills half and mixes hit and miss.

**The branch that fired: FULL D1**, on both pre-registered SETTLED comparisons, in **5 of 5** scored
sweeps in **each of four independent process launches**:

- `B` vs `A`: ratio **0.518 / 0.566 / 0.583 / 0.576**, saving **8.55 / 7.16 / 6.59 / 6.80 ns**.
- `C` vs `H0`: ratio **0.385 / 0.424 / 0.430 / 0.439**, saving **11.14 / 9.73 / 9.33 / 9.17 ns**.

**The two arms are reported separately on purpose, and an arm-C number must never be read as the
frontier's win.** Arm B *is* `NeedsChunk` and therefore the C3 frontier: it wins **on its own**, by
6.6-8.6 ns per key, so this is the FULL branch and not the REDUCED one — the frontier is **not**
neutral. Arm C is the int-in-hand paths (`TryResolveNeighbors`, the LOD column prefetch,
`HasAllNeighbors`, `GetBlockGlobal`): a separate and larger win, 9.2-11.1 ns per probe, on sites
that probe a distinct key every time. An extra diagnostic that feeds no branch, SETTLED-QUEUED — the
only settled case where `NeedsChunk` actually performs **two** probes, absent from `Chunks` but
present in `_queuedChunkPositions` — has `B/A` at **0.41-0.47** (A 22.55-25.32, B 10.49-10.85),
because amortizing one pack over two probes is worth more still. Note the shape of the SETTLED
workload when reading arm B: the `&&` short-circuits, so on SETTLED `NeedsChunk` performs **one**
probe, and arm B's single pack amortizes over that one probe and still wins outright.

**H1's verdict on the deliberately untaken sites: they pay nothing.** The rule's threshold was
`median(H1) > 1.05 * median(H0)`; measured `H1/H0` is **0.532 / 0.571 / 0.595 / 0.585** — the
`Vector3` overload *through* `ChunkMap` is **41-47 % cheaper** than HEAD's `Vector3`-keyed probe.
So `World.cs:965/1069/1084/1554`, `TntManager.cs:108-109/168-169/182`, `FluidManager.cs:154` and
`ApplyCompletedMeshes` come out cheaper than HEAD anyway and merely forgo arm C's further ~2.4 ns.
There is no per-probe cost to state against them.

**This corrects a planning assumption, and the correction is recorded rather than buried.** The plan
asserted that a `Vector3` probe *through* `ChunkMap` must be strictly more work than at HEAD (three
floors, three converts, a pack and a `long` hash against one `Vector3.GetHashCode`). Measured, that
holds only for the key **derivation**, and derivation is not what dominates. The attribution
diagnostics, run after the scored sweeps and report-only, say where the gap actually lives:

1. **Key derivation with the dictionary removed** (ns per key, median of 5, three runs):
   `Vector3.GetHashCode` **5.20 / 5.25 / 5.09**; pack-from-`Vector3` then `long` hash
   **5.34 / 5.35 / 5.44**; pack-from-ints then `long` hash **2.59 / 2.57 / 2.55**. So floor + pack +
   `long` hash costs the **same** as `Vector3.GetHashCode` to within +0.15..+0.35 ns, and packing
   from ints already in hand saves ~2.6 ns. Derivation explains the arm C edge over B and H1 almost
   exactly, and explains **none** of the `long`-versus-`Vector3` gap.
2. **Bucket layout** of three dictionaries holding the same 17077 positions: `Vector3` + default
   comparer `buckets=75431 used=15279-15320 maxChain=4-5 mean=1.115`; `Vector3` + packing comparer
   `used=16988 maxChain=2 mean=1.005`; `long` `used=16988 maxChain=2 mean=1.005`. The packing-
   comparer table is bucket-for-bucket identical to the `long` one, which makes arm A' a clean
   **layout control**: with distribution held identical, `Vector3` keys still cost ~15.1 ns against
   the `long` table's ~9.2 ns. That ~6 ns residue is the key **type** — a 12-byte key, `Vector3`
   equality on every node visit, a wider node, and the interface call a custom comparer forces — not
   hash arithmetic and not bucket luck. (One run's default-comparer table happened to sit at 36353
   buckets with `maxChain=5` and timed the same, so table size is not driving the result either.)

**Honest limits of this instrument.** Single-threaded, off-engine, one hot 17077-key working set,
measuring isolated probe cost in nanoseconds and nothing else. **It says nothing about frame time,
and no frame-time claim is made from it.** The engine-level effect of D1 was **not isolated**, and
the round says so plainly rather than implying a win nothing measured: `perf-fly` cannot see it
(saturated window), a counted replay cannot see it (the counts are identical by construction), and
an in-engine frame-time row cannot see single-digit nanoseconds against a ~9 ms frame.

#### The visual gate: D1 is output-preserving at convergence, and only a converged hash can say so

**The authority is an exact cross-tree hash, and it was written before the first production edit.**
`ChunkRekeyReplayProbe` drives a **real** `ChunkManager` through a real load / unload /
neighbor-resolution sequence and folds the resulting state into two FNV-1a 64 hashes. It names only
API present on **both** trees — no `ChunkMap`, no `ChunkKey`, no `Packed`, no `int` overload — and it
never enumerates `Chunks` itself, so it compiles **unchanged** on a pristine HEAD export. Both lines
reproduced byte-identically after every production step and on both trees:

    [d1hash-a] hash=0x5c85567614c237ee registered=243 afterPass1=75 left=48 placements=167
               unmarked=24 survivingPlacements=45 probes=64 verts=48 idx=72 removalVersion=2
               loaded=-195 placedChunks=27
    [d1hash-b] hash=0xa6d87257e5de3dbf meshed=9 chunks=75 loaded=0 dirty=0 generating=0

Fact A stays entirely on the **synchronous** path (chunks registered through the indexer, so
`ProcessDirtyChunks` returns at its `IsEmpty` guard and dispatches nothing) and asserts the three
dispatch counters are zero after every `Update`, as a guard that the fact stayed there rather than
folding a race as data — `DirtyCount` and `GeneratingMeshCount` are genuinely nondeterministic at an
arbitrary fold point, because `BuildMeshFor` removes from `_generatingMeshes` on a pool thread. Fact
B covers the **async** dirty path by spinning `Update` to quiescence under a **bounded and asserted**
iteration count and folding only the converged meshed set. Both folds are over **sorted** state, so
both are order-independent by construction — which is what keeps them valid once per-frame
enumeration order is known to change. Negative controls confirm they are not vacuous: a truncating
placement key (the floor-versus-truncate trap) leaves every structural assertion passing and moves
`[d1hash-a]` to `0xe92875e5f826e389`; dirtying six neighbors in `MarkDirty` breaks fact B alone.

**The enumeration-order change was pre-registered before the gate ran**, per the What-landed bullet
above: a mid-stream pixel difference is an expected consequence of reordered per-frame mesh
selection, not a bug, and the round did not bisect one. Pixels were therefore demoted to a
**settled-state smoke check** on `clouds-mirror`, `entity-binds` and `rays-halfres` (settle wait
raised 6 s -> 30 s), run from a scratchpad copy by absolute path against the pristine export, with a
cold discarded run per tree and **5 scored runs per tree per scenario**, interleaved in one session.
`lod-horizon` and `particles` were eyeball-only and entered no statistic.

**Result: zero structural change, and at the final run count the residue is exactly zero
everywhere.** Under R10's within-tree-constant instrument (a pixel counts only where all runs of one
tree agree exactly, all runs of the other agree exactly, and the two constants differ), the **5+5**
work/head residue over 23 scored shots was **0 px on 18 of them and 1, 1, 2, 3, 3 px on the other
five** — at most **0.00033 %** of a 921600-px frame and **10 pixels in total across the whole gate**,
nine of the ten a single-LSB difference on one channel. The exact permutation null over all 126
balanced relabelings of the same ten runs was already decisive at that point: arbitrary relabelings
reach **12083, 45668 and 1862 px** on the same shots, three to four orders of magnitude above any
real split, and no real split carrying a nonzero residue ranked better than **10th of 126** (every
better rank belongs to a shot whose real residue is zero, where 92-125 splits sit tied at that same
value). The one non-LSB survivor, opened, was one pixel of a 2-px foliage speckle whose own
per-frame count varies 47-105 **within** each tree (HEAD 47/79/73/58/65, work 59/69/105/75/66).

**Those ten pixels were accidental five-run agreement, and adding runs removed them.** The gate was
re-run in a second session against the current binaries — a cold discarded run plus 2 more scored
runs per tree for `entity-binds` and `rays-halfres`, re-scored at **7+7** by an independent
implementation of the same instrument against the exact null over all C(14,7)/2 = **1716** balanced
relabelings. The real residue is **0 px on all six `entity-binds` shots** (was 1 and 3) and **0 px
on all ten `rays-halfres` shots** (was 2, 1 and 3), against null maxima of 617-9947 px and up to
2090 px respectively; `clouds-mirror` was already 0 px on all seven of its shots at 5+5 and was not
re-shot. **So across all 23 scored shots the cross-tree residue is zero.** The mechanism is visible
in the data: the fresh runs differ from each tree's own stored 5-run constant on 0.1-10 % of that
constant's pixels, so the "within-tree constant" shrinks as runs are added and an accidental
agreement collapses while a real effect could not.

**And the instrument's own trap fired twice, at two different run counts, which is worth carrying
forward.** At 3+3, `entity-binds/07-held_block` looked like a finding: 1094 px, rank 1 of 10,
p = 0.10; at 5+5 the same shot's residue is **exactly 0**. Then the ten surviving pixels at 5+5 went
the same way at 7+7. Both were accidental agreement within a tree, one level apart. A real tree
effect can only survive extra runs; an accident collapses — so **the run count is part of the
instrument, and a nonzero residue at any fixed count is a hypothesis, not a finding, until more runs
fail to remove it.**

**Two more instrument defects were observed and are recorded so the next round does not rediscover
them:** `clouds-mirror` and `rays-halfres`
photograph **different camera poses run to run within the same tree** at these wait settings
(`clouds-mirror/08-mirror_still_0` sits at z -135.50 / -160.50 / -106.50 across HEAD's own runs), so
at those two the same file name is not the same view — the `lod-horizon` defect, appearing on two
more scenarios, identically on pristine HEAD. Only `entity-binds` is pose-stable. Separately, the
raised-wait control never reports equal streaming counts: `scrape.py` labels every shot
tree-disjoint, but that is a **4-tuple artifact** — with five distinct values per tree, four noisy
fields rarely repeat a whole tuple. Per **field**, all **92** shot-fields (23 shots x `chunks N`,
`chunks V`, `regions built`, `regions live`) overlap and **86 of 92** have the work tree's whole
range **inside** HEAD's, so **no count separates the trees**. The six that are not inside are
`chunks N` and `chunks V` on `rays-halfres`'s three `*_still_*` shots, where both trees straddle the
same streaming transition and the ranges still overlap. **No wall clock is quoted from any of these
runs.**

### Measurement: C6 + D6 — both items are counting-shaped, so both are counted

**No `perf-fly` capture was run, deliberately.** Lesson 1 below: the 60 s window is **saturated for
both chunks and LOD** — all ten counterbalanced runs of the previous round ended at the identical
`chunks 17077` and `regions 907-913/645` — so every end-state throughput field measures the
scenario's ceiling and isolates nothing, and a frame-time claim on this family would need a
time-to-N rule on a non-saturating window pre-registered before the capture. C6 and D6 are both
**counting-shaped**, so each got an exact deterministic replay in `ColumnCacheReplayProbe`'s style
instead. **Nothing in this section is a timing**, and no wall clock is compared anywhere in it.

**(a) D6's capture ran BEFORE the conversions, and its decision rule bound them.**
`OpenGLTest.Tests/WorldgenShapeCacheReplayProbe.cs` (`ShapeAndCanopyMissesUnderBothEvictionPolicies`)
is the direct analog of `ColumnCacheReplayProbe`: fixed key sequences, a real `GenerationalCache`
with an **identity factory** so no terrain is generated, deterministic counts on any machine, output
through `ITestOutputHelper`, no timing asserted. It compiles and passes **unchanged on pristine
HEAD** (verified by copying it into the `git archive HEAD` export, building and running it there,
where it printed the same three verdicts). It models **only** the wholesale-clear side and reads the
generational side straight off the real type's `Misses` / `Promotions` / `Rotations`;
`ColumnCacheReplayProbe` carries a second hand-written model only because that round needed the
newest-vs-middle hit composition, which the type deliberately does not count. Three sequences per
cache (hot alone / bulk alone / **interleaved**), G swept over {2, 4, 8}, and for the two shape
caches a sweep over **pinned** distinct-key cardinalities 4096 / 8192 / 16384 / 32768 / 65536 rather
than a trunk acceptance rate (two samples of that rate disagreed by ~34 % and nothing reconciled
them, so no figure here depends on either).

The **interleaved** row is the only one that decides anything — a bulk sweep alone trips the cap
under both policies. Verbatim from the run (`generational` vs `clear-at-cap`, misses):

```
canopy (TreeGenerator.CanopyCache, cap 4096)
  lod-ring-L0 (hot)   accesses 25216 (rawAsks 806912)   distinct 3152    misses 3152  under clear AND G=2/4/8 alike, wholesaleClears 0
  lod-ring-L2 (bulk)  accesses 100864 (rawAsks 12910592) distinct 50432  misses 50432 under every policy, clear wholesaleClears 12
  INTERLEAVED         accesses 113472                   distinct 50432
      clear-at-cap        misses 51924   wholesaleClears 12
      generational G=2    misses 51880   -0.08%   promotions 527  rotations 25
      generational G=4    misses 51880   -0.08%   promotions  32  rotations 50
      generational G=8    misses 51880   -0.08%   promotions   6  rotations 101
  VERDICT: CONVERT AT G=4 (rule not fired).

tree-shape (TreeGenerator.ShapeCache, cap 16384) — INTERLEAVED rows, by distinct-key target T
  T= 4096   clear  4163 | G=4  4163   0.00%   wholesaleClears 0
  T= 8192   clear  8345 | G=4  8345   0.00%   wholesaleClears 0
  T=16384   clear 16710 | G=2 16690 | G=4 16689 (-0.13%) | G=8 16657 (-0.32%)   wholesaleClears 1
  T=32768   clear 37716 | G=2 35950 (-4.68%) | G=4 35709 (-5.32%) | G=8 35582 (-5.66%)   wholesaleClears 2
  T=65536   clear 82834 | G=2 78311 (-5.46%) | G=4 75464 (-8.90%) | G=8 74273 (-10.34%)  wholesaleClears 5
  VERDICT: CONVERT AT G=4 (rule not fired).

cactus-shape (CactusGenerator.ShapeCache, cap 16384) — INTERLEAVED rows, by T
  T= 4096   clear  4593 | G=4  4593   0.00%   wholesaleClears 0
  T= 8192   clear  9196 | G=4  9196   0.00%   wholesaleClears 0
  T=16384   clear 18629 | G=2 18571 (-0.31%) | G=4 18507 (-0.65%) | G=8 18272 (-1.92%)  wholesaleClears 1
  T=32768   clear 38564 | G=2 = G=4 = G=8  38009 (-1.44%)   wholesaleClears 2
  T=65536   clear 78732 | G=2 = G=4 = G=8  76095 (-3.35%)   wholesaleClears 4
  VERDICT: CONVERT AT G=4 (rule not fired).
```

**Read this table honestly.** The feared losing regime did **not** appear: the worst G = 2 row across
all three caches is an exact tie, not a loss. But neither is there a hit-rate win to claim in ordinary
play — the two policies tie **exactly** wherever the working set fits under the cap, and the gap only
opens once the stream outruns capacity. `ReportDecision` asserts that at least one interleaved row per
cache actually tripped the cap, so no verdict can be vacuous; the `wholesaleClears 0` rows stay in the
table as real points of the swept range. Two design notes, both recorded in the probe's doc comment:
runs of identical **consecutive** keys are collapsed before replay (exactly state-preserving for every
number here — a single level-2 canopy ring is 12.9M raw asks otherwise — and both raw and collapsed
access counts are printed), and the shape sweeps reach their target cardinality through an acceptance
threshold, so the **achieved** distinct count lands slightly above the pinned target (T = 16384 gives
16594 on the tree sweep); target and achieved are both printed.

**(b) D6's output-preservation gate is an exact cross-tree hash with the caches forced through
eviction.** `WorldgenShapeCacheEvictionTests.TreeCactusAndCanopyShapesSurviveForcedCacheEviction`,
modeled on `ColumnCacheEvictionTests`. It touches only API present on **both** trees — no
`GenerationalCache`, no rotation counter — so the identical file runs on the pristine export. It
resolves three probes by **searching** (first accepted trunk from block `(-52000, 52000)` under
`TerrainGenerator.IsForest`; that trunk's own chunk column as the canopy probe; first accepted cactus
from `(40480, 32288)`), asserts each probe is non-degenerate, pins the `baseY` identities the new
static factories must preserve, folds all three probe values into an FNV-1a 64 hash, captures
reference identity of the two `Wood` lists and the `CanopyColumns` instance, then runs **three**
4-thread sweeps, re-fetches, asserts `!ReferenceEquals` on each (fail-closed: a sweep that failed to
evict fails the gate loudly rather than passing vacuously) and re-asserts the hash. Both trees printed
the same line, character for character:

```
work tree:            [hash] pre=0xc519c177162fed76 post=0xc519c177162fed76 canopy=8192 cactusAccepted=34032 trunkAccepted=65213 tree=(-51982,52008) cactus=(40498,32288)
pristine HEAD export: [hash] pre=0xc519c177162fed76 post=0xc519c177162fed76 canopy=8192 cactusAccepted=34032 trunkAccepted=65213 tree=(-51982,52008) cactus=(40498,32288)
```

Timings are printed on a separate `[elapsed]` line by design, so the `[hash]` line is byte-comparable
whole rather than field by field. **Three sweeps, not two, and the correction matters for the next
round:** the plan's arithmetic — 8192 canopy chunk columns x a 1024-column `TreeReachBlocks = 8`
footprint = ~8.4M `TryGetTrunk` calls, therefore `ShapeCache` is evicted as a side effect — counts
**calls**, but the cache stores only **accepted trunks**. That grid spans 1472 x 1472 = 2,166,784
distinct block columns and accepts **9786** trunks, under the 16384 capacity, so it could not have
aged the tree probe out. A separate trunk sweep (3008 x 3008 = 9,048,064 distinct columns from
`(60000, -20000)`, **65213** accepted) was therefore restored, sized off *measured acceptance*; the
cactus sweep is 29,000,000 columns from `(24096, 40480)`, **34032** accepted. Anti-vacuity bars are
**pinned literals** (`canopy == 8192`, accepted stems and trunks `>= 32768` — twice the pinned 16384
capacity, covering capacity + capacity/G for every G >= 2), never read from `WorldGenerationConfig`,
on `SweepColumns`' argument that a gate sized off the constant it guards defuses itself. Every probe
sits **outside** every sweep rectangle so no sweep can promote the entry it is meant to evict. One
suite-hygiene edit traveled with it: `WorldgenTests` and the new class share the
`[Collection("Worldgen shape cache")]` xUnit collection, because `WorldgenTests.TrunkShapeIsCachedAcrossChunks`
asserts instance identity across two adjacent `TryGetTrunk` calls and is the one assertion in the
suite a concurrent `ShapeCache` rotation could falsify.

**(c) C6's authority is a cross-tree hash over a real unload, not a transcription of the deleted
predicate.** This is the trap the gate exists to avoid: if the predicate were transcribed subtly
wrong, a model-versus-model probe would have both models agreeing with each other *and* with
production while all three differed from HEAD, and it would pass in exactly the case it is for. So
`PlayerPlacedUnloadReplayProbe.UnloadPreservesExactlyTheSameSurvivingPlacements` drives a **real**
`ChunkManager` through a **real** unload sequence using only API present on both trees
(`MarkPlayerPlaced` / `UnmarkPlayerPlaced` / `IsPlayerPlaced` / `Chunks` / `Update` /
`Camera.TestTeleport`; it deliberately does **not** name `PlayerPlacedChunkCount`, which does not
exist on pristine HEAD). 98 headless chunks straddling the unload radius on both signs, 510 deduped
scripted placements from a probe-local fixed-seed SplitMix64 (chunk corners, cells in never-registered
chunks, and 73 cells unmarked again before the passes), two real unload passes with guard assertions
on `Chunks` after each, then `IsPlayerPlaced` probed over the full candidate list and the survivors
sorted and folded into FNV-1a 64. Survivor count is asserted strictly between 0 and the candidate
count, so the gate cannot pass with everything or nothing surviving. Identical on both trees:

```
work tree:            [c6hash] surviving=153 hash=0xf6044cb438286a22 candidates=510 unmarked=73 chunksRegistered=98 chunksLeft=32
pristine HEAD export: [c6hash] surviving=153 hash=0xf6044cb438286a22 candidates=510 unmarked=73 chunksRegistered=98 chunksLeft=32
```

**And the gate was shown to bite rather than assumed to.** Against two deliberately broken copies of
the *work* tree (built in the scratchpad from a `git ls-files` tar — neither the working tree nor the
export was ever mutated), the line moves on the survivor count as well as the hash: a **truncating**
bucket key gives `surviving=315 hash=0x153a10fc23ee2884`, and **omitting** the unload purge gives
`surviving=437 hash=0xabfa2c2476dd1a37`, against the true `surviving=153 hash=0xf6044cb438286a22`.

**(d) C6's measurement is arithmetic made visible, not a saving.**
`PlayerPlacedUnloadReplayProbe.UnloadScanCostAgainstPlacementCount` replays one unload storm sized
from this file's own recorded capture (17077 -> 4991 loaded chunks, i.e. 12086 unloads) against two
probe-local models: OLD, a flat `HashSet` scanned by the predicate this round deleted, transcribed
verbatim and quoted in the doc comment, counting every cell the predicate examines; NEW, per-chunk
buckets counting one dictionary removal per unload. The two models' surviving sets are asserted equal
on every row as a consistency check on the transcription — explicitly **not** the authority, which is
(c). Positions only, no `Chunk` objects, so the whole file costs under half a second.

```
[c6count] storm=12086 unloaded chunks (perf-fly capture: 17077 -> 4991 loaded) - counts only, no wall clock is read anywhere in this fact
[c6count] placements | unloads | old cell-examinations | new bucket-removals | ratio
[c6count]        250 |   12086 |               2227567 |               12086 |    184.3x  (bucketsActuallyDropped=125 survivors=125)
[c6count]       1000 |   12086 |               9099613 |               12086 |    752.9x  (bucketsActuallyDropped=493 survivors=500)
[c6count]       4000 |   12086 |              36449693 |               12086 |   3015.9x  (bucketsActuallyDropped=1855 survivors=2000)
```

**No wall-clock saving is claimed from this.** The old cost is linear in placements x unloads and the
new one is not, which is the whole point — but the real placement count is entirely
workload-dependent, which is why three rows are printed instead of one number, and `grep -n Stopwatch`
over that file finds nothing.

**(e) The suite.** Baseline re-established on this session's machine before anything was edited:
**763 total / 759 passed / 4 failed**, the four being
`ContentTests.DisplayMatrixPosesSpritesAsConfigured`,
`BlockMovementTests.FrictionDefaultsToPointNineNineAndIceIsLower`,
`BlockMovementTests.LeavesDeclareThreeQuarterMovementSpeed` and
`FallingBlockTests.SevenTowerChainRebuildsFullyWithNoDrops` — none touching caching or unload, and all
four reproduced on the pristine export. The round ends at **770 / 766 / 4** with the same four names:
+1 (D6 capture probe), +1 (D6 eviction gate), +3 (C6 facts), +2 (C6 probe). Four consecutive
full-suite runs at the end of the round all came back 770 / 766 / 4 and **neither** documented
`ColumnCaps` intermittent fired in any of them. It was observed live **once in three runs on an
unmodified tree** at the start of the round (`LodHeightSamplingTests.SeparableMinMatchesTheNestedWindowAtEveryLevel`,
expected 6112 / actual 6080), which is the pre-existing behavior `D2b` (now `TODO.md` `M-11`)
describes — so the round's new 4-thread sweeps are **not** recorded here as having made the suite
flakier; the evidence across the final runs is that they did not.

**(f) The visual gate: zero structural change, and both flags discharged as per-run confounds.**
Pre-registered in writing before any capture, and the crop fixed by eye off a shot **predating** this
round's runs:

> *Both items are output-preserving by construction — C6 unloads exactly the same placements at
> exactly the same moment (same branch, same iteration), and D6's values are pure functions of their
> key, so a hit, a miss and an eviction must all produce identical trees and cacti. The gate looks for
> ZERO structural change; any difference is a bug to diagnose, not noise. `lod-horizon` (4 shots)
> primary, `rays-halfres` (10 shots) second; counterbalanced work/head/work/head/work/head in ONE
> session with a cold discarded run per tree, the head side being the `git archive HEAD` export built
> in the scratchpad — never a stash, never a mutated working tree. Statistic: the clustered luma rule
> (delta >= 24 and >= 5 of the 25 pixels in a 5x5), counts scraped off `[visual] N/M screenshot ->`;
> cross-tree counts must lie inside the same-build control range, and R10's within-tree-constant
> instrument read ONLY with the exact permutation null (p = rank/10 over the 10 distinct 3-vs-3
> relabelings) computed before any residue is read. Any flagged shot must be discharged with the
> raised-wait control before it is called a regression.*

Both instruments **flagged under rule 5a at their stock waits**, and **every flag was discharged as a
per-RUN confound reproduced identically on pristine HEAD** — that literal acceptance was not met and
is not claimed. `lod-horizon`'s control range was 60639-163955 flagged pixels of a 614400-pixel crop,
and the cause is in the logs: every run of **both** trees logs exactly 44 `Respawned` lines, and at
three of its four shots the camera is in free fall at the spawn xz, so those teleports never hold; the
smallest pair in that matrix is a **cross-tree** pair while the same-build pair is an order of
magnitude worse. Its one converged shot showed perfect 3-vs-3 separation (p = 0.1) and was chased to
the bottom rather than waved away: the frames are identical in terrain, seabed, cliff, selection
outline and LOD band and differ only in **bubble particles** (the confound `rays-halfres.json`'s own
header documents), 76.3-92.4 % of flagged pixels are bubble-colored, and the residual is a spatially
**smooth** global dimming of **-0.7958 luma levels** (work mean 69.3601 vs head 70.1559 over the
246209 pixels bubble-free in all six runs) near the sunset ramp, not geometry. Applying the
high-pass normalization that scenario's header mandates for exactly this, the permutation null goes to
**p = 0.5, T = -0.111** — i.e. cross-tree pairs are marginally *more* similar than same-build pairs.
The strongest single fact is the controlled `rays-halfres` capture (settle raised 6 s -> 30 s, 12 runs,
6 per tree, capture plus an independent replication): **every one of the ten shots has cross-tree pairs
at exactly 0 flagged pixels**, with a cross-tree median of 0-6 pixels out of 921600 on eight of ten,
and the residual spikes are one run at a time — a *different* run per shot, each disagreeing with its
own tree as much as with the other, traced to frame-rate hitches. **No structural change was found**,
which is what both items predict by construction. **No wall-clock or fps figure from any visual run is
quoted anywhere in this file** (lesson 1, and the same rule that keeps the eviction gate's `[elapsed]`
line out of the write-up).

**Two pre-existing instrument defects were found that are NOT this round's, and are not perf items.**
(i) **`lod-horizon` is broken on this machine and should not be used as a visual instrument until it
is fixed**: the player is teleported 30 blocks above each vantage, falls through not-yet-collidable
terrain, hits the kill plane and respawns — 44 times per run at stock waits, **111** at 90 s waits,
identically on pristine HEAD, so raising the waits makes it *worse*. Three of its four shots are
photographs of a free-falling camera at the spawn xz rather than of their scored vantages, and the
fourth lands on the seabed. Its waits also run long (145 s of scripted wait takes ~367 s of wall clock
to reach the second shot), so all four shots land near the sunset ramp instead of the clock its header
chose. (ii) **`rays-halfres` is a good instrument but only with its settle wait raised**: at the stock
6 s the camera has not finished sinking (settle depth varied -5.30 to -8.15 between runs) and the
underwater fade is frozen at whatever that settle computed, so same-build pairs scored up to 653193
flagged pixels; at 30 s the same-build control range is exactly `[0,0]` on several shots. Expect the
last two burst shots to be the hitch-sensitive ones. File (i) against `TODO.md`, not here.

### Measurement: D2 — the win is exact and counted, not timed

**(a) The premise was re-verified before anything was built, and only qualitatively.** D2 inherited
its case from C5's "a LOD build is terrain generation in disguise". That was re-checked against the
current tree two ways. Structurally: `LodRegion.BuildMeshData` (`:295-314`) still computes
`chunkSpan = spec.RegionBlocks / Chunk.Size + 2` and prefetches `chunkSpan^2` heightmaps, i.e.
**36 / 100 / 324** columns at levels 0 / 1 / 2, verbatim, and all three levels still mesh the same
32x32 cell grid. Empirically: `LodBuildCostProbe.RegionBuildCostPerLevel` still shows level 2
materially the most expensive of the three while levels 0 and 1 sit close together, so the per-level
spread still tracks the *column count* and not the mesh — and its near-zero rows are still present,
which is the cache-hit signature the probe's own doc predicts. **Premise holds.**
**No percentage and no per-column rate is quoted from that probe, deliberately.**
`LodBuildCostProbe.cs:20-38` states that nothing in it asserts a timing, that its numbers are biased
in both directions and that it is an order-of-magnitude go/no-go reading rather than a rate to
quote; and this file's own gate traps forbid comparing a fresh run against a stored capture from an
earlier session. So C5's stored 17.7 / 17.6 / 65.3 ms medians were **not** compared against the new
run, and no cross-tree cost claim is made from it. Every wall-clock figure in this round was measured
directly in the step that spent it.

**(b) The mechanism is sharper than the old bullet said, and the correction matters.** The old
bullet framed the defect as "a far teleport recomputes". That is true but secondary. Chunk
generation's own working set is *small* — `ChunkRadius = 16` is a disc of **933 distinct columns**
once each build's 3x3 heightmap neighborhood is counted (`Chunk.FillLightColumnHeights`,
`Chunk.cs:246-251`) — while **one level-2 ring sweep is ~51k distinct columns**. Against a
16384-entry cap the sweep tripped the wholesale `Clear()` **three times in a single pass**
(measured, see (f)), and each trip threw away chunk generation's permanently-hot 933 columns along
with the sweep's own one-shot ones. The defect was the LOD sweep repeatedly evicting the chunk-gen
hot set — which is C5's "chunk generation slows *disjointly* at three builders", seen from the
victim's side.

**(c) The memory arithmetic was wrong in the tree and is corrected; this is why the cap did not
rise.** `Chunk.cs`'s old comment claimed 16384 entries was "~40 MB". A `ColumnData` is seven
256-element arrays plus scalars (`int[]` Heights / Bed / WaterLevel, `float[]` EntranceWeight /
CaveIntensity, `byte[]` DirtDepth, byte-backed `Biome[]`) — about **5.9 KB**, so the real ceiling at
16384 is roughly **96 MB**. The "~40 MB" figure predated Bed, WaterLevel and Biomes. A bigger cache
was therefore never the cheap option, and **capacity is unchanged at 16384**: the fix is *more
generations at the same capacity*, so peak residency does not move. The corrected arithmetic now
lives in `WorldGenerationConfig.cs` beside the constants.

**(d) A second, separate cost was removed on the same path.** Both old sites tested
`if (Cache.Count >= limit) Cache.Clear();` on **every miss**. `ConcurrentDictionary.Count` acquires
*every lock in the table*, and the miss path is exactly the path four chunk-gen threads and the LOD
builder share. `GenerationalCache` counts its own successful inserts with a single
`Interlocked.Increment` instead and never calls `Count` outside test-only diagnostics.

**(e) Promotion is restricted to the oldest generation, on purpose, and the reason is the hot
path.** A true LRU needs a shared write per hit. `Chunk.GetColumnData` is a pure `TryGetValue` read
today and is called from the **main thread** as well as the five background threads
(`FluidManager`, `SlimeManager`, `World`, `Camera`), so per-hit bookkeeping would land in the frame
budget and would put a bucket-lock write on the common case *under a sweep* — the exact contention
this round exists to reduce. So `GenerationalCache.GetOrAdd` probes the newest map and returns; a
hit in any middle generation returns **without writing**; only a hit in the **oldest** generation —
the one the next rotation drops — is promoted forward. That is a second-chance policy: an entry hot
enough to matter is certainly touched during the 1/G window before its eviction, while every other
hit stays write-free. The cost this adds is counted exactly rather than assumed — see the promotion
column in (f). `GenerationalCacheTests.AHitInTheNewestGenerationNeverCallsTheFactoryAndNeverWrites`
is the fact that fails if a later round widens promotion back to every non-newest hit.

**(f) The round's number is an exact replay, not a frame-time window.** `ColumnCacheReplayProbe`
replays the real key sequences — the radius-16 chunk-gen disc with each build's 3x3 neighborhood,
each LOD ring's prefetch keys in `BuildMeshData`'s own emission order, and the two interleaved —
against both policies with a factory that does no work, so it generates no terrain, runs in ~0.9 s
and is **deterministic** (two runs produced byte-identical tables). The `Clear()`-at-cap policy is
reimplemented in the probe as a reference oracle. The printed table, verbatim:

```
[probe] capacity=16384 chunkDiscRadius=16 lodRegionRadius=8 chunkGenPasses=3 mixedRatio=1 chunk-gen key per 8 lod keys
[probe] factory is identity: this measures the eviction POLICY, not terrain.

[probe] sequence=chunk-gen accesses=21519 distinctKeys=933 chunkGenAccesses=21519
[probe]   clear-at-cap(cap=16384): misses=933 (4.34%) wholesaleClears=0 chunkGenMisses=933
[probe]   generational(cap=16384,G=2): misses=933 (4.34%) vs clear-at-cap 0.00%  newestHits=20586 olderGenHits=0 promotions=0 (0.00% of accesses) rotations=0 (predicted 0) chunkGenMisses=933
[probe]   generational(cap=16384,G=4): misses=933 (4.34%) vs clear-at-cap 0.00%  newestHits=20586 olderGenHits=0 promotions=0 (0.00% of accesses) rotations=0 (predicted 0) chunkGenMisses=933
[probe]   generational(cap=16384,G=8): misses=933 (4.34%) vs clear-at-cap 0.00%  newestHits=20586 olderGenHits=0 promotions=0 (0.00% of accesses) rotations=0 (predicted 0) chunkGenMisses=933

[probe] sequence=lod-ring-L0 accesses=7092 distinctKeys=3428 chunkGenAccesses=0
[probe]   clear-at-cap(cap=16384): misses=3428 (48.34%) wholesaleClears=0 chunkGenMisses=0
[probe]   generational(cap=16384,G=2): misses=3428 (48.34%) vs clear-at-cap 0.00%  newestHits=3664 olderGenHits=0 promotions=0 (0.00% of accesses) rotations=0 (predicted 0) chunkGenMisses=0
[probe]   generational(cap=16384,G=4): misses=3428 (48.34%) vs clear-at-cap 0.00%  newestHits=3664 olderGenHits=0 promotions=0 (0.00% of accesses) rotations=0 (predicted 0) chunkGenMisses=0
[probe]   generational(cap=16384,G=8): misses=3428 (48.34%) vs clear-at-cap 0.00%  newestHits=3012 olderGenHits=652 promotions=0 (0.00% of accesses) rotations=1 (predicted 1) chunkGenMisses=0

[probe] sequence=lod-ring-L1 accesses=19700 distinctKeys=13156 chunkGenAccesses=0
[probe]   clear-at-cap(cap=16384): misses=13156 (66.78%) wholesaleClears=0 chunkGenMisses=0
[probe]   generational(cap=16384,G=2): misses=13156 (66.78%) vs clear-at-cap 0.00%  newestHits=5694 olderGenHits=0 promotions=850 (4.31% of accesses) rotations=1 (predicted 1) chunkGenMisses=0
[probe]   generational(cap=16384,G=4): misses=13156 (66.78%) vs clear-at-cap 0.00%  newestHits=4372 olderGenHits=2172 promotions=0 (0.00% of accesses) rotations=3 (predicted 3) chunkGenMisses=0
[probe]   generational(cap=16384,G=8): misses=13156 (66.78%) vs clear-at-cap 0.00%  newestHits=2328 olderGenHits=4216 promotions=0 (0.00% of accesses) rotations=6 (predicted 6) chunkGenMisses=0

[probe] sequence=lod-ring-L2 accesses=63828 distinctKeys=51524 chunkGenAccesses=0
[probe]   clear-at-cap(cap=16384): misses=55550 (87.03%) wholesaleClears=3 chunkGenMisses=0
[probe]   generational(cap=16384,G=2): misses=52592 (82.40%) vs clear-at-cap -5.32%  newestHits=4858 olderGenHits=0 promotions=6378 (9.99% of accesses) rotations=7 (predicted 7) chunkGenMisses=0
[probe]   generational(cap=16384,G=4): misses=51718 (81.03%) vs clear-at-cap -6.90%  newestHits=2540 olderGenHits=8460 promotions=1110 (1.74% of accesses) rotations=12 (predicted 12) chunkGenMisses=0
[probe]   generational(cap=16384,G=8): misses=51588 (80.82%) vs clear-at-cap -7.13%  newestHits=636 olderGenHits=11434 promotions=170 (0.27% of accesses) rotations=25 (predicted 25) chunkGenMisses=0

[probe] sequence=MIXED accesses=101947 distinctKeys=51524 chunkGenAccesses=11327
[probe]   clear-at-cap(cap=16384): misses=56997 (55.91%) wholesaleClears=3 chunkGenMisses=1219
[probe]   generational(cap=16384,G=2): misses=61715 (60.54%) vs clear-at-cap +8.28%  newestHits=26321 olderGenHits=0 promotions=13911 (13.65% of accesses) rotations=9 (predicted 9) chunkGenMisses=848
[probe]   generational(cap=16384,G=4): misses=54937 (53.89%) vs clear-at-cap -3.61%  newestHits=17137 olderGenHits=18347 promotions=11526 (11.31% of accesses) rotations=16 (predicted 16) chunkGenMisses=733
[probe]   generational(cap=16384,G=8): misses=52345 (51.35%) vs clear-at-cap -8.16%  newestHits=7910 olderGenHits=41230 promotions=462 (0.45% of accesses) rotations=25 (predicted 25) chunkGenMisses=757
```

What that table establishes, at the shipped `ColumnCacheGenerations = 4`:

- **The headline is the chunk-gen row of MIXED, not the total.** Chunk generation's own misses while
  the LOD sweep runs fall **1219 -> 733, a 40 % cut in recomputes of the hot set**. That is exactly
  the defect (b) names, measured from the victim's side.
- **Quote the total only with its framing.** Total misses go 56997 -> 54937, **-3.61 %** — but 51524
  of those misses are the *unavoidable distinct-key floor* that no cache can remove. Expressed as
  avoidable work, **avoidable recomputes fall 5473 -> 3413, a 38 % cut**. Do not quote -3.61 % on
  its own; it understates the effect by folding in work no policy could avoid.
- **The wholesale `Clear()` fires 3 times in one level-2 pass** (`wholesaleClears=3`), confirming the
  old bullet's "roughly three times over" arithmetic against a replayed key sequence rather than an
  estimate.
- **The promotion cost, exact: 11.31 % of accesses on MIXED** (11526 shared `TryAdd` writes in
  101947 accesses), 1.74 % on the level-2 sweep alone, and **0.00 % on chunk-gen alone and on levels
  0 and 1**. That is higher than the design assumed. It is a *write rate*, not a contention
  measurement — it does not price lock convoy. If a future capture ever shows the `chunkDirty` row
  regressing, promotion is the first mechanism to check, and disabling it entirely (pure segmented
  eviction, no second chance) is the fallback that costs only hit rate.
- **The predicted level-1 loss did NOT occur, and the prediction is retracted.** The design's stated
  risk was a cyclically revisited working set sitting between the retention band and capacity —
  level 1's ~13.2k distinct columns against a 16384 cap. The replay shows **every ring sweep taken
  alone misses exactly once per distinct key under both policies at every G**: nothing is evicted
  before its last use, because a ring is swept closest-first and neighboring regions share border
  columns, so reuse is *local in the sequence*. The band is real arithmetic that this workload never
  sits in. Record the measurement, not the prediction.
- **`G = 2` is genuinely WORSE than the old wholesale clear** (+8.28 % misses on MIXED, stable across
  interleave ratios 2 / 8 / 32). Two generations put the promotable window at half the cache:
  promotions run at 13.65 % of accesses and buy a retention band too coarse to hold anything.
  **`ColumnCacheGenerations` has a real lower edge and must never be set to 2.**
- **`G = 8` was considered and deliberately not taken.** It is better on MIXED totals (-8.16 %) and
  cuts promotions 25x (0.45 %), but it is **worse on the one sub-metric that is D2's own stated
  defect** — chunk-gen misses 757 vs 733 — because at eight generations the second-chance window is
  an eighth of the cache and a hot column is less likely to be touched inside it. The two candidates
  save *different work* (a chunk-gen miss on a generator thread vs a LodGen miss on the builder) and
  this probe uses a free factory, so it has **no cost weighting with which to trade them**. It is a
  one-token re-pricing for a later round with a real capture; it was not taken blind.
- **The sequences and the table are pinned constants, not live reads**, per the
  `LodBuildPoolTests.StressWorkers` precedent. If a later round retunes `LodRegion.Levels`,
  `ChunkRadius` or `ColumnCacheCapacity`, the probe will keep replaying the 2026-09-19 shape and its
  table will silently stop describing production. Each constant names its source in its comment.
  The MIXED interleave ratio has no source constant at all, so it was **sensitivity-checked**: the
  ordering is identical at ratios 2, 8 and 32 (G=2 +7.62 / +8.28 / +7.83 %, G=4 -6.46 / -3.61 /
  -3.29 %, G=8 -10.17 / -8.16 / -7.83 %) and only magnitudes move.

**Retention, stated correctly.** Two separate claims, because only the first is unconditional.
*Memory ceiling:* each map accepts exactly `capacity / G` successful inserts during its tenure as
the newest, so total entries are bounded by `capacity` **at all times, whatever the promotion
rate** — that is the claim the ~96 MB arithmetic above rests on, and it is pinned by
`GenerationalCacheTests.ResidencyNeverExceedsCapacityUnderPromotions`. *Retention:* an entry read at
least once while it sits in the oldest generation is carried forward; one never read there is
dropped when that generation rotates out. **Distinct-key residency is strictly *below*
`capacity * (G-1)/G` whenever promotions occur**, because a promotion consumes an insert slot and
briefly occupies two dictionary nodes. Do **not** restate a flat `capacity * (G-1)/G` retention
floor — it is false under any workload with revisits, which is the real workload. (Observed peak
residency between calls is `capacity - 1`, not `capacity`: the insert that would reach the ceiling
is by construction the one that fills the newest generation's budget and therefore rotates, dropping
a full oldest map before the caller returns.)

**(g) `WormCarver.CellCache` was converted for uniformity, and that half of D2 was NOT a win.** D2
assumed the two sites were symmetric. They are not. Cells are 64 blocks (`CarverCellShift = 6`),
`Carve` asks a `(2 * WormReachCells + 1)^2` = 25-cell window per chunk, and the whole
`ChunkRadius = 16` load sphere spans **on the order of 150 cells** against a 4096-cell cap — one to
two orders of magnitude of headroom — and **the LOD builder never touches this cache at all** (it
reads heightmaps only). Its `Clear()` essentially never fired in play. It was converted anyway
because AGENTS.md documents the two sites as one deliberate pattern and a silent divergence is worse
than the conversion, and because it removes the all-locks `Count` from the miss path — **not** for
hit rate. The measurable half of D2 is `Chunk.ColumnCache` alone. There is deliberately **no
dedicated eviction test** for `CellCache`: forcing its 4096-cell cap means carving across 256x256
chunk columns, ~65k column generations, an order of magnitude beyond what the suite should spend.
Its policy coverage is `GenerationalCacheTests`; its output coverage is the whole-chunk half of
`ColumnCacheEvictionTests`, which runs `Carve`.

**(h) The gate. The hash is the authority, and it passed cross-tree.** The rule, fixed before any
comparison and reproduced verbatim from `ColumnCacheEvictionTests`' class doc:

> *D2 is output-preserving by construction — a cache is a cache, so the same column must generate
> the same blocks whether it was served as a hit, a miss or an eviction. The gate looks for ZERO
> difference: the hash must be bit-identical in-run on each tree AND identical between the pristine
> baseline and the work tree. Any difference is a bug to diagnose, not noise to accept.*

`ColumnCacheEvictionTests.WorldgenAndLodSurviveForcedCacheEviction` folds one FNV-1a 64 hash over a
64-column lattice of full `ColumnData`, the 4096 block bytes of two generated surface chunks (the
composed path, including `WormCarver.Carve`, the carvers, water fill and trees) and all three
`LodRegion.BuildMeshData` levels over the same area, before and after a **32768-column, 4-thread
sweep** — 2x the capacity, which trips the baseline's `Clear()` at least twice and ages the probe
out on the work tree for any `G >= 2`. The file uses only API present on **both** trees, so it was
copied unchanged into a pristine `git archive HEAD` export (never a stash, never a mutated working
tree) and run there.

  work tree: `[hash] pre=0x6d4eba16cb182624 post=0x6d4eba16cb182624 sweep=32768 threads=4 elapsed=1681ms`
  pristine:  `[hash] pre=0x6d4eba16cb182624 post=0x6d4eba16cb182624 sweep=32768 threads=4 elapsed=1885ms`

`pre == post` within each run **and** the two trees agree bit-for-bit. **Anti-vacuity is not
assumed:** the fact first asserts the probe column is not flat and the probe chunk holds at least
two distinct block ids, then asserts `!ReferenceEquals` on the probe `ColumnData` across the sweep —
which proves the eviction actually happened on *both* policies without needing any work-tree-only
counter. That guard is load-bearing and was seen to fire: at `SweepColumns = 4096` the probe
survived and the fact failed. **Do not shrink the sweep below `2 * 16384`** or the gate goes vacuous
on one tree or the other. Negative control: a pristine export overlaid with the work tree's D2 files
and one generated block perturbed moves the hash to `0x54e783e4d4909d81`. The prescribed "offset one
height by a constant" control **cannot** fail this gate — a deterministic offset moves pre and post
identically — so the control used was *stateful* (a counter bumping `Heights[0]` on every
computation), which is what actually models "the generator returns different bytes on recompute".
Sweep cost, measured not predicted: 267 ms for 4096 columns cold, 1681-1970 ms for 32768 isolated,
3.9-4.9 s inside the parallel suite; whole-suite wall clock moved 13-14 s -> 14-16 s.

**The `lod-horizon` pixel run is a smoke check on top of that, and it passed — by a stronger route
than its own fallback allowed.** Rule pre-registered before any image was opened: counterbalanced
work/head/work/head/work/head at `VOXEL_TIME=20` with a cold discarded run per tree, clustered luma
statistic (delta >= 24 and >= 5 of the 25 pixels in a 5x5) on a crop fixed once by eye, counts
scraped off `[visual] N/M screenshot ->`. **"Zero structural change" was explicitly NOT the
criterion**, and that was decided in advance: `wait` ticks physics once per *rendered* frame and
regions fade in on the wall clock, so a round that changes streaming speed by construction — as D2
does — lands at a different camera depth and fade phase, and a naive pixel gate reads that as a
regression. *Result 1:* cross-tree clustered counts lie **inside** the same-build control range on
every shot (`03-lod_a` control area 7.72-19.93 % vs cross 8.30-20.80 %; `09-lod_c` control
9.23-21.91 % vs cross 12.22-25.15 %) — the ~8-22 % same-build floor is the sun-phase-through-shadow-
map effect this file already records. *Result 2:* R10's within-tree-constant instrument gives a
non-zero residue (309 / 2728 / 140 / 82 px on lod_a-d), which **cannot be read on its own here**
because only 14-32 % of pixels are run-constant at all, so accidental 3-run agreement is common. So
the null was computed exactly: of the `C(6,3)/2 = 10` distinct 3-vs-3 splits of the same six runs,
exactly one is the real tree split, and it ranks **6th / 2nd / 4th / 10th (p = 0.60 / 0.20 / 0.40 /
1.00)** — on `12-lod_d` the real split is the *smallest* residue of all ten, and arbitrary
relabelings reach 1039 / 3077 / 1469 / 1372 px. **Tree identity explains nothing**, so no
streaming-depth diagnosis was even needed, and nothing reads as localized geometry change. All five
checked-in pose-stable instruments still run against the current tree and none was dropped
(rays-halfres 10 shots, clouds-mirror 7, entity-binds 6, particles 3, lod-horizon 4).

**New reusable machinery, and it belongs in the gate-traps list: R10's constant instrument needs an
exact permutation null.** This file records that instrument as standing machinery for any
output-preserving round but gives it no null. On a scenario where 70-85 % of pixels vary run to run,
a bare residue count is unreadable — it would have looked like a finding here. With 3 runs per tree
there are 10 distinct 3-vs-3 relabelings of the same six runs, exactly one of which is real, so
`p = rank / 10` costs nothing beyond the runs already budgeted. **Always compute it before reading a
residue off that instrument.**

**(i) The perf capture isolated NOTHING, and nothing from it is quoted.** Rule pre-registered before
the first run: `perf-fly.json`, `VOXEL_DEBUG=1 VOXEL_TIME=60`, one cold discarded run per tree then
5 kept per tree interleaved in one session, five fixed fields, and a field quotable **only** if the
two trees' 5-run sets are disjoint. `regions_built` was chosen as the LodGen-throughput field
precisely because this file already forbids sizing builder work off a `lod` row. Every field
overlapped — `regions_built` 909-913 vs 907-913, `chunks_loaded` 17077 vs 17077 (identical),
`chunkDirty` med 0.134 vs 0.136, `lod` med 0.450 vs 0.443, `frame_max` med 16.190 vs 15.960 — so by
the rule no number is quoted and no permutation test was run. **Why, as an instrument finding rather
than a shrug: `perf-fly` SATURATES inside its 60 s window on both trees.** All ten kept runs end at
the *identical* `chunks 17077` and at `regions <built>/645` with built 907-913, so both trees finish
essentially all the work the scenario offers before the shutter and every end-state field measures
the **scenario's ceiling, not either tree's rate**. This is the same trap already recorded for the
chunk ring ("never reuse C3's net loaded-chunk rule on a window in which the chunk ring saturates"),
now shown to apply to the LOD ring as well. A future round wanting a frame-time number from D2's
family needs a **time-to-N rule on a non-saturating window, pre-registered before the capture**;
deriving one from these logs after the fact is exactly what this series' methodology forbids, so it
was not done. **The round's number is (f), not this capture** — exact, deterministic, reproducible,
and independent of any window isolating anything. The profile table at the top of this file is
untouched by this round.

**Suite.** 752 / 748 passed / 4 failed at the round's start; **763 / 759 / 4** at its end — the four
pre-existing failures and no others, with 11 new facts added (9 `GenerationalCacheTests`, 1
`ColumnCacheEvictionTests`, 1 `ColumnCacheReplayProbe`). The four, by name, all unrelated to caching:
`ContentTests.DisplayMatrixPosesSpritesAsConfigured`,
`BlockMovementTests.FrictionDefaultsToPointNineNineAndIceIsLower`,
`BlockMovementTests.LeavesDeclareThreeQuarterMovementSpeed`,
`FallingBlockTests.SevenTowerChainRebuildsFullyWithNoDrops`.
**A pre-existing intermittent family was found and proven not to be D2's**:
`LodBuildPoolTests.RegionBuildsAreIdenticalWhicheverThreadRunsThem` and
`LodHeightSamplingTests.SeparableMinMatchesTheNestedWindowAtEveryLevel` each fail in roughly 1
full-suite run in 4-6, never in isolation — **reproduced on a pristine `git archive HEAD` build
carrying zero D2 code** (2/12 and 1/5 runs there). Both build regions at the origin, and the cause
is `LodRegion.ColumnCaps` being process-global with no reset hook, so a parallel test class mining
near the origin legitimately changes a later build. Do not chase either as a D2 bug; the item was
tracked as `D2b` here and is now `TODO.md` `M-11`, with the pointer under **Not chasing**.

**Tooling notes for the next round.** `dotnet test -l normal` **silently drops**
`ITestOutputHelper` output — use `--logger "console;verbosity=detailed"` for `LodBuildCostProbe`,
the `[hash]` line and the replay table. `Copy-Item` restoring a file from a backup sets its
`LastWriteTime` from the *source*, so MSBuild reports "Build succeeded" while doing nothing and the
suite runs the stale binary — touch the file after any restore. And numpy 2 removed value-based
casting, so `587 * a[:,:,1]` on an `int16` image array silently overflows; widen to `int32` before
the multiply or a luma-delta table comes out plausible and wrong.

### Measurement: R10 — a disjoint submission-side win, and a churn claim that SPLIT

**The rule, written down and fixed in the scratchpad before the first working-tree run, and
reproduced here verbatim because conclusions are quoted off it:**

> *R10 changes only how LOD geometry is submitted and where it is stored — never which regions
> exist, what they contain, when they arrive, or the order they draw in. A difference in
> `regions N/M`, `chunks N`, region arrival timing or draw order between trees is a gate failure,
> not a result. The only rows R10 may be credited with are `lod` (region upload and unload, where
> the per-region GL object churn lived) and the LOD-drawing CPU sections `lodPass`, `reflect` and
> `waterPass`. `lod` is tested in both directions: R10 replaces GL object deletion with free-list
> scanning, so a regression there is as much a result as a saving.*

**Method.** Baseline = `git archive HEAD` exported into the scratchpad and built there; the working
tree was never stashed or mutated. `perf-fly.json`, `VOXEL_DEBUG=1 VOXEL_TIME=60`, `--no-build -c
Release`, run from each tree's own `OpenGLTest/`, strictly serial and never concurrent. One cold run
discarded, then **4 + 4 counterbalanced** (P W W P / W P P W), plus **2 + 2 more** after the pool
constant was corrected, to confirm the quoted figures describe the shipped binary. Exact one-sided
Mann-Whitney U in both directions (`p = 1/70 = 0.0143` is the floor at 4 + 4), plus explicit min/max
disjointness. Headline p-values are reported at the **pre-registered** n = 4 + 4; the 6 + 6 figures
are supporting, not a substituted design. Survivor and departure counts needed an instrument neither
tree carries, so — following C5's tree-B precedent — a **copy** of the working tree was patched,
built and run in the scratchpad; the repo was never patched.

**1. Countable, no profiler needed (R4's method), from the instrumented run rather than estimated.**
Settled world: **592-666 resident regions** (median 641); opaque walk survivors 120-159 (median
145), mirror walk 125-151 (median 136), water sheets 96-141 (median 121) — **median 402, max 436
surviving regions across the three walks of one water frame**. HEAD paid, per water frame,
402 x (`BindVertexArray` + 3 `glUniform` + `DrawElements`) = **402 binds, 1206 uniform uploads and
402 draws**, plus 402 CPU `Matrix4x4.CreateTranslation` + `model * viewProj` compositions and
402 x (2 mat4 + 1 float) of uniform payload. R10 pays **3 binds, 3 `glUniform1i` and 3
`glMultiDrawElementsIndirect`**, plus 402 `ChunkDrawBatch.Add` writes into the mapped SSBO ring.
Per region **arrival**, 6 GL object creations + 4 `glBufferData` become 1-2
`glNamedBufferSubData`; per **departure**, 6 `glDelete*` become up to 2 `GeometryAllocator.Free`.

**2. The two-sided churn test — the headline, and it is reported in both directions.** Stated before
the figures, as the rule requires: *a `lod` regression on the teleport leg is a reportable result and
a reason to re-price R10 downward, and the countable cut in item 1 may not be used in its place.*

- **The departure half is NEUTRAL, measured, and nothing is quoted from it.** `tp14` (far teleport)
  `lod` avg **touches at 0.92** between the groups and so fails disjointness; the `tp14` and `tp10`
  worst-frame rows overlap, and on `tp10` the **work tree trends higher** — the free-list-scan
  counter-mechanism showing its face without reaching significance. All four teleport rows are
  quoted nowhere. **Why neither side resolves, mechanically:** in one baseline run the publish window
  containing the largest unload of the fly reports `lod 0.19~0.49`, so HEAD's *entire* deletion burst
  cost at most 0.49 ms. Driver-side `glDelete*` really is deferred and cheap — there was little to
  win, and correspondingly little to lose to the scans that replaced it.
- **The burst is an order of magnitude smaller than the arithmetic ceiling.** Only **5 frames in a
  60 s fly have any departure at all**, the largest being **148 regions in one frame** (660 -> 512
  resident): HEAD ~888 `glDelete*` against R10 <= 296 `Free`, not the 1518 the 759-region ceiling
  allows. perf-fly's 424-block teleport clears level 0 and part of level 1 only (the levels reach
  512 / 1024 / 2048 blocks).
- **The arrival half IS credited.** `lod` settled-window median **0.51 -> 0.41 ms**, disjoint
  (P min 0.50 vs W max 0.425), p = **0.0143** at 4 + 4 and 0.0011 at 6 + 6. With departures occupying
  5 frames of a 60 s run, this row is essentially all upload.

**Credited rows, all disjoint, all in the same direction in every counterbalanced pair whichever
tree ran first:**

| Row | HEAD | R10 | Disjointness | exact 1-sided p (4+4 / 6+6) |
|---|---|---|---|---|
| `lodPass` CPU | med **0.16 ms** | med **0.05 ms** (3.2x) | P min 0.15 vs W max 0.05 | **0.0143** / 0.0011 |
| `waterPass` CPU | med **0.13 ms** | med **0.03 ms** (4.3x) | P min 0.13 vs W max 0.03 | **0.0143** / 0.0011 |
| `lod` settled | med **0.51 ms** | med **0.41 ms** | P min 0.50 vs W max 0.425 | **0.0143** / 0.0011 |

**Quoted nowhere, listed so the omissions are visible:** `reflect` CPU (0.695-0.725 vs 0.695-0.78,
not disjoint either way — the mirror's region loop is a small share of a pass that also carries the
mirror chunk draw, the sun disc and the clouds); all four teleport `lod` rows; `worldPass` CPU
(disjoint at 4 + 4, **touching at 6 + 6**, and excluded by the rule regardless). **The GPU guards did
not move** — `lodPass` GPU 1.41-1.43 on both trees, `waterPass` GPU 0.73-0.75 vs 0.72-0.75,
`worldPass` GPU 3.035-3.18 vs 3.06-3.18 — so the CPU saving is not paid on the GPU.

**Guards the rule demands, all clear.** Max `chunks` **17077** on all 12 runs. Final
`regions 928/666` **identical on all 12 runs, both trees**. Per-second arrival trajectories differ
only within the same-tree spread. `fps`, `frame`, total `cpu`, `gc`, `chunkApply` and `shadows` all
overlap. **No `Chunk draw ring grew` line on either tree in any run** — R10 introduced no `glFinish`
stall.

**Growth signals, read off the same runs on both trees.** (a) The `lod` pool **never grew**, but
peaked at **118004 of 131072 units (90 %)** at 660 resident regions, and occupancy is a steady
176-181 units per resident region — so the 759-region resident ceiling projects to ~137400 units,
which 131072 does not cover. 90 % is not headroom, so `LodInitialUnits` was raised **131072 ->
163840** and its comment rewritten to record the observed figures the way `OpaqueInitialUnits`
records 244801/262144. Re-verified at 163840: **117844 (72 %)**, no growth, and the two corrected-tree
runs land inside the earlier group on every credited row. The only other growth line on either tree
is the pre-existing `translucent` doubling, identical on both. (b) `Chunk draw ring grew`: never, on
either tree, in any run.

**This was a `perf-fly` capture, but it does not update the profile table at the top of this file:**
it read only the LOD-related rows against a same-session baseline. The table remains the old
snapshot, as it has been since C3/R8.

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
- **Quantization is removed, not bounded.** A `perf` line publishes on the first frame at or past 1 s
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
claim protocol kept; the reasoning and what it cost are in the C5 closeout under **What landed** and
in the pool-size entry under **Ruled out**.

**Mechanistically, why three builders buy 1.31x and not 3x** — the reusable finding, and the reason
the answer is D2 — since landed, see "Measurement: D2" — rather than a bigger pool: a LOD build is
**terrain generation in disguise**.
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
  expensive work, so the statistic cannot separate the trees in the favorable direction however
  large the real effect is. Use time-to-a-fixed-count instead.

No `perf-fly` capture was run this round, so the profile table at the top of this file is untouched by
it, exactly as it was untouched by the C3/R8 and R5/R7/R11 rounds.

### Tests

R10 took the suite **485 -> 503** with the same two known failures, and added no GL to any of them.
The 18 new facts follow the series precedent — extract the GL-free arithmetic and pin it with exact
equalities. **+8 `LodGeometryPoolTests`** (a new GL-free file touching only `ChunkGeometryPool`'s
constants and statics and `LodRegion.Levels`): `ChunkGeometryPool.UnitsFor`'s two ceilings, the
region shell's worst-case rent (384 units), a full sheet (64), a flat shell floor (64), and the
**591 settled / 759 resident** counts derived from `LodRegion.Levels` rather than hard-coded, plus
the two pool-size floors. **+4 `LodDrawOrderTests`** pinning `LodRegion.CompareOpaqueDraw` /
`CompareWaterDraw` and that the two passes deliberately disagree on level order — mutation-checked
by inverting each comparator's level comparison. **+3 `GeometryAllocatorTests`**: same-size
reallocation into the freed hole, a full 1518-slice stream-in/stream-out cycle coalescing back to
one block, and the mid-teleport fragmentation peak of exactly **380** free blocks (the figure D5's
bullet now quotes). **+3 `LodManagerTests`**: the rebuilt region inheriting the replaced region's
`Lifetime`, an empty region mesh needing no pool, and a non-empty one without a pool throwing —
the last two being the headless seam ~20 call sites depend on. `LodBuildPoolTests` and
`LodBuildCostProbe` are wall-clock-deadline harnesses tuned to this machine; R10 does not touch the
builder path and **no flakiness was observed in them — nothing was loosened**.

Before R10, the suite went **464 -> 485** with the same two known failures
(`BlockMovementTests.LeavesDeclareThreeQuarterMovementSpeed`,
`FallingBlockTests.SevenTowerChainRebuildsFullyWithNoDrops`), which were re-verified on HEAD before
any code was written so a pre-existing flake could not be attributed to this round. Added: +7
`LodManagerTests` (the cumulative counter, the five claim-protocol facts, the `_pendingCover`
swap-remove), +1 `LodBuildCostProbe`, +4 `LodBuildPoolTests`, +1 `MeshingTests`, +4
`ParticleRingTests`, +4 particle eviction-order facts (`LeafParticleTests`, `BlockDebrisTests`,
`BubbleParticleTests` and a new `FireParticleTests`, which is the first fire-particle coverage in the
tree). `LodManagerTests.DirtyRegionsDrainExactlyOnce` was **amended**, not added — see the behavior
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

### The visual gate: R10 is output-preserving, and the sharper test that proved it

**The R10 gate rule, fixed before any image was compared and reproduced verbatim:**

> *R10 is output-preserving at the pixel level — same geometry, same origins, same alphas, same
> order, different submission — so the gate looks for ZERO structural change and any difference is a
> bug to diagnose, not noise to accept. Specifically: a missing region, a region drawn at the wrong
> offset (terrain from one region appearing over another's footprint, which is what a mis-derived
> `BaseVertex`/`FirstIndex` looks like), a missing or double-blended water sheet, an LOD sheet drawn
> with the cutout on (the `uAlphaCutoff` move), or LOD terrain disagreeing with the full-detail
> chunks at the seam. The one sub-threshold difference that is expected and acceptable is
> silhouette-level float noise from `uViewProj * (p + origin)` replacing `(model*viewProj) * p`; it
> must not reach the clustered threshold anywhere.*

**Run counts: 42 runs in all** — 4 + 4 counterbalanced per pose-stable instrument (`lod-horizon`
`VOXEL_TIME` 10, `clouds-mirror` 60, `rays-halfres` 180, `entity-binds` 60), plus a 4 + 4 settled
`clouds-mirror` control, plus 2 `particles` runs judged by eye and excluded from the statistics.
Baseline was the pristine `git archive HEAD` export built in the scratchpad; the working tree was
never stashed or mutated and the one scenario override lived in a scratchpad copy passed by absolute
path. Statistics are the previous round's `gate.py`/`gate44.py` verbatim so the numbers stay
comparable: `area%` reported, the **clustered** statistic (luma delta >= 24 **and** >= 5 of the 25
pixels in the same 5x5 box) the authority, cross pairs judged against the same-build distribution
with an exact one-sided Mann-Whitney U. Counts scraped off `[visual] N/M screenshot ->`, never
`[visual] saved`.

**Every instrument passes.**

- **`lod-horizon`** (8 runs), the instrument built for exactly this change and the primary gate:
  all 4 shots pass, **0/16 flags on every shot and both statistics** (clustered p 0.6167-0.7316).
  **All eight runs report identical counts at all four shots** — `chunks 17077`, the same
  visible-chunk counts, and the same cumulative-builds/live-regions pairs — so no raised-wait control
  was needed. The C3 signature is back: on 3 of 4 shots the most-similar pair of all 28 is a **cross**
  pair while the least similar is a same-build **baseline** pair.
- **`clouds-mirror`** (8 runs): all 7 shots pass full-frame **and** on both halves of the fixed
  horizon crop. Last round the sky half flagged on all five `mirror_*` shots; this round it does not.
- **`clouds-mirror` settled control** (8 runs, opening wait 8 -> 55 s): every run of both trees reaches
  exactly `chunks 17077/1296` and `regions 591/591`; all 7 shots pass the clustered statistic. One
  `area%` flag traced through the full 28-pair matrix to a single spread-out **baseline** run — the
  four work runs are tight among themselves and every cross pair above the same-build max is a
  pairing with that one run.
- **`rays-halfres`** (8 runs): all 10 shots pass; `clust%` is 0.0000 on all 28 pairs on 7 of them.
  Two `area%` flags are the documented animation-phase false positive — **maxdelta over all 28 pairs
  is 16 and 15 levels**, so not one pixel reaches the structural threshold of 24.
- **`entity-binds`** (8 runs): all 6 shots pass. Last round's five-shot flag does not appear, which
  is itself a cross-check: that flag was *arrival rate*, and R10 does not touch arrival.
- **`particles`** (2 runs, by eye, excluded): all six shots opened — leaves with visible alpha fades,
  8-12 debris cubes correctly textured and depth-sorted against terrain and each other, bubbles
  blended and correctly ordered in front of the seabed. No missing pass.

**The one difference found, and how it was run to ground rather than waved through.** The gate's own
cross-vs-same design can only ever conclude "indistinguishable from run-to-run noise", which is
weaker than this rule demands, so the round added a sharper test: a pixel counts only if **all four
baseline runs agree exactly, all four work runs agree exactly, and the two constants differ**, which
removes the sun-phase and streaming axes entirely. It finds a deterministic per-tree residue of at
most **335 px of 921600 (0.036 %)**, of which 0-57 px locally satisfy the 5-of-25 connectivity test.
Three independent lines identify it: (1) it **tracks LOD screen coverage exactly** — zero on all ten
underground `rays-halfres` shots, 2 px on `entity-binds` (both in the horizon band), and 172 of 190
(91 %) inside a 60 px horizon band on `lod-horizon/06-lod_b`; (2) **every flagged pixel sits on a
strong edge** — baseline local 3x3 luma range median 41-71 and 10th percentile 25-36 against a
whole-frame median of 0-20, with none in a flat interior, which a wrong offset, a missing region, a
missing sheet or a wrong cutoff would necessarily produce; (3) **seen directly** — on the settled
control's `14-sky_level` the largest difference in the whole frame is **exactly one pixel**, foliage
on all four baseline runs and sand on all four work runs, with its right and lower neighbors already
sand on *both* trees. Amplified crops of the largest cluster show 1-3 px specks tracing individual
LOD cell edges: no shifted feature, no missing patch, no displaced footprint. This is precisely the
silhouette float noise the rule names as expected, and it is invisible to the gate's own statistic.

**So: R10 is output-preserving but NOT bit-exact,** and that should not be claimed. `gl_Position`
moved from a CPU-composed `(model * viewProj) * p` to `uViewProj * (p + origin)`; with region origins
out to 2048 blocks the composed matrix is where precision was actually being lost, so the new path is
if anything the better-conditioned one, but the last bits differ. A future round wanting a
pixel-exact LOD path would have to compose the matrix the old way.

**Structural checks across all 46 logs:** no `Chunk draw ring grew` line on either tree; the only
pool-growth line anywhere is the pre-existing `translucent` doubling, 10 times on each tree; **the
`lod` pool never grew in any run**, corroborating the 163840-unit sizing across four scenarios beyond
`perf-fly`. No exception, no unhandled error, no `glGetError` report.

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
`Chunks` is a **public** `ConcurrentDictionary` and `OpenGLTest.Tests/Chunks/MeshingTests.cs` already removes
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
`ChunkScanFrontierTests.TheScanCostIsAmortizedNotPerCall`. **This obligation is standing, not spent:
re-read this paragraph before touching that method, whatever the item.** `C6` has since landed
*inside* `UnloadDistantChunks` without disturbing any of it — a seven-for-one line swap within the
existing `if (!Chunks.TryRemove(pos, out var chunk)) continue;` branch, with the bump still **after**
the loop and still gated on `removed > 0`, `removed` untouched, and the new `_playerPlacedByChunk`
invisible to `NeedsChunk`, to `Chunks`, to `_queuedChunkPositions` and to the frontier; the three
facts above were not edited. So the obligation reaches the next editor **exactly as C3 left it**, and
the fact that one round threaded it safely is not a license for the next to skip the paragraph. The
full invariant write-up is in `git log -p -- PERF_TODO.md` and, better, in the doc comments on
`ChunkScanFrontier` itself.

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
night run (`VOXEL_TIME=300`; the day cycle has since doubled to 1200 s, which puts night at 600) fired the check, because `RenderSun` early-returns before its unit-0
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
- **Chunks and LOD regions fade in on the wall clock** (`RenderManager.cs:1162-1163`, `:1822-1826`,
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
- Any frame-to-frame statistic must be normalized for wall-clock **sun drift** before it is compared
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
- **New (R10): a real per-tree difference can be invisible to the cross-vs-same-build statistic, and
  the within-tree-constant test is how you see it.** The run-to-run noise floor on these scenarios —
  sun phase reaching the frame through shadow-map texel alignment — is **two orders of magnitude
  larger** than the deterministic residue R10 actually produced (<= 0.036 % of a frame), so a
  Mann-Whitney against the same-build distribution can only ever say "indistinguishable from noise".
  That is not the same as "no change", and it is weaker than a zero-structural-change rule demands.
  The instrument that resolves it: count a pixel only if **all runs of the baseline agree exactly,
  all runs of the work tree agree exactly, and the two constants differ**. Then confirm what it
  finds three ways rather than asserting it is noise — does the residue track the changed subsystem's
  on-screen coverage (it should be *zero* where that subsystem does not draw); does every flagged
  pixel sit on a strong local luma edge (a wrong offset, a missing object or a wrong blend mode
  necessarily produces flat-interior differences too); and what does the single largest difference in
  the frame actually look like when opened. This should become standing machinery for any
  output-preserving round.
- **New (R10): `lod-horizon` needed no raised-wait control**, unlike `clouds-mirror` and
  `entity-binds`. All eight runs converge to identical chunk, visible-chunk **and** region counts at
  every shot at its stock 40/35/35/35 waits. `clouds-mirror` is still not settled at its stock 8 s
  opening wait (regions 302-342 across runs on *both* trees) and still needs the control.
  **SUPERSEDED for `lod-horizon` (C6 + D6 round): it no longer converges on this machine at all**, and
  raising its waits makes it *worse* (44 respawns per run at stock waits, 111 at 90 s, identically on
  pristine HEAD). Three of its four shots now photograph a free-falling camera at the spawn xz. Treat
  the R10 sentence above as a record of what was once true, not as guidance — the current state and
  its diagnosis are at the end of **"Measurement: C6 + D6"**, and `rays-halfres` with a raised settle
  wait is the instrument that works today.

### Measurement: D5 — the decision rule, written before the instrument existed

**The rule below was written in final wording before the instrument was built and before a single
number was read** — the D6 precedent, restated by D1 — and it is reproduced verbatim here so the
branch that fired can be checked against it by a reader who has only this file. D5's own bullet is
emphatic that the item is measure-first and doubts there is a win at all; the rule exists precisely
so that a marginal number cannot be talked into a win after the fact, and so that the decline branch
is as pre-authorized as the two implementation branches.

**The ceiling the bars are set against, derived before they were chosen.**
`GeometryAllocatorTests.MidTeleportFragmentationPeaksAtAKnownBlockCount` frees in **ascending offset
order**, and `GeometryAllocator.Free`'s insertion loop (`GeometryAllocator.cs:94-107`) breaks only
when `block.Offset > offset` — so every free in that storm arrives above every block already in the
list, never breaks, and scans the whole list. Per departed region *k* the two frees cost *k* and
*k + 1* steps (the shell's free finds the list *k* long; the sheet's finds it *k + 1* long and then
coalesces backwards onto the shell, so the count ends at *k + 1*), and the storm therefore totals
Sum(2k + 1) for k = 0..379 = 380^2 = **144,400 scan steps**, i.e. **0.289 ms** at the rule's
2.0 ns/step. That is the `lod` pool's provable, **already-pinned reachable ceiling** — what the
759-region resident ceiling can actually produce, not an estimate. (The absolute arithmetic ceiling,
every other 64-unit *slice* departing independently, would be 759^2 = **576,081** steps / 1.15 ms,
but it is unreachable by construction: one `LodRegion.Dispose` frees both of a region's slices, so
holes are always region-sized.) Every bar in the rule is chosen **against this derivation, not above
it** — except the one that is deliberately above it, which says so and says why.

> **Instruments.** `GeometryAllocator.ScanSteps` (cumulative free-list entries examined by
> `TryAllocate` and `Free`, accumulated **once per call from the loop index**, never once per
> iteration), `GeometryAllocator.PeakFreeBlockCount` (lifetime high-water `FreeBlockCount`, updated
> inside `Free` and inside `Grow`'s non-coalescing branch, so an intra-frame peak cannot be missed by
> an end-of-frame sample), a **per-window `FreeBlockCount` max**, and `FreeBlockCount` itself — all
> three pools, published once a second on the `perf` console line as the `geo` token, which also
> carries the window's **raw frame count**. `S_worst` is the largest per-frame total scan-step count
> across the three pools inside a published window; `S_sec` is that window's total divided by its raw
> frame count (never by the rounded `fps` field).
>
> **Pricing constant, fixed here and not revisable after a capture: 2.0 ns per scan step.** One step
> is one 8-byte `(int, int)` read from a contiguous `List<>` backing array plus one or two integer
> compares — about 8 cycles at 4 GHz, for a loop a sequential prefetch keeps at roughly one element
> per cycle. 2.0 ns is several times the real cost, **deliberately**, so the DECLINE branch can only
> be conservative. The converse does not hold, which is why every FIRE branch carries a confirmation
> obligation.
>
> **Windows.** GATE-A: three `perf-fly.json` runs at `VOXEL_DEBUG=1 VOXEL_TIME=60` — the harness
> setting this file's Method block pins and every recorded number in this file was taken at,
> notwithstanding the scenario file's own header comment saying 120. This is the sustained sprint
> streaming and the 424-block teleport R10 characterized: **the largest allocator traffic a player
> actually produces**. GATE-B: three runs of the scratchpad `d5-teleport.json` scenario at
> `VOXEL_TIME=120` (its waits total 70 s, so 60 would truncate the return leg) — settle, 6000-block
> teleport, settle, teleport back — which evicts every chunk and all three LOD levels in a single
> frame. That is **the synthetic ceiling, not a player action**.
>
> - FIRE iff ANY of:
>   - BURST: `S_worst * 2.0 ns >= 0.20 ms` on GATE-A in at least 2 of the 3 runs. This bar sits
>     **below** the `lod` pool's 0.289 ms reachable ceiling derived above, so LOD traffic can fire it
>     and branch (b) is genuinely selectable.
>   - SUSTAINED: `S_sec * 2.0 ns >= 0.10 ms` in at least one published second, in at least 2 of the 3
>     GATE-A runs.
>   - TELEPORT: `S_worst * 2.0 ns >= 0.50 ms` on GATE-B in at least 2 of the 3 runs. **This bar sits
>     ABOVE the `lod` pool's 0.289 ms reachable ceiling, on purpose and with the consequence stated:**
>     GATE-B exists to price the **chunk** pools, whose free list has no comparable pinned ceiling,
>     whose unload is likewise uncapped (`ChunkManager.UnloadDistantChunks` disposes every
>     out-of-range chunk in one frame) and whose departures arrive in `ConcurrentDictionary` hash
>     order rather than offset order, so the insertion scan averages half the list instead of falling
>     through. A GATE-B fire therefore **always selects branch (a)**; branch (b) can only be selected
>     by a GATE-A fire. **A GATE-B miss is not evidence about LOD.**
> - BRANCH SELECTION, applied to the frame or second that fired: take **(b) the paired shell+sheet
>   allocation** iff BOTH (i) the `lod` pool contributed **>= 60 %** of that quantity's scan steps,
>   AND (ii) the **fragmentation precondition** holds — the `lod` pool's `LargestFreeBlock` at its
>   observed peak `FreeBlockCount` is at least the largest fused pair size in play (176-181 units
>   typical, up to ~362 worst). Precondition (ii) is not hypothetical:
>   `MidTeleportFragmentationPeaksAtAKnownBlockCount` pins `LargestFreeBlock == 128` at the 380-block
>   peak, where a 176-unit fused request fits **no** hole, scans all 380 blocks, fails, and grows the
>   pool — **strictly worse** than two 64-90-unit requests that each succeed on the first hole. If (i)
>   holds and (ii) fails, take **(a)**; if (i) holds, (ii) fails and the only trigger was LOD-only,
>   **DECLINE** and record that fusing was ruled out **by the precondition**, not left untested.
>   Otherwise take **(a) the O(log n) free list**. If a GATE-A trigger and the GATE-B trigger disagree
>   on the branch, **GATE-A wins**: the realistic window decides what ships.
> - CONFIRMATION OBLIGATION ON ANY FIRE: before landing code, price one scan step with an off-engine
>   microbenchmark in D1's harness shape (scratchpad net9.0 Release console project, the exact loop
>   over a `List<(int, int)>` of the measured length, median of 5 sweeps). If the measured ns/step
>   puts the fired quantity back under its bar, the fire is **withdrawn**, the round takes the DECLINE
>   branch, and both numbers are recorded.
> - DECLINE (c) iff no trigger fires. Nothing is implemented beyond the instrument; D5 leaves
>   "Open (next round)" and is rewritten as **measured-and-declined** on the R3 / C5 / D1-no-go
>   precedent. The instrument is KEPT iff it is O(1) per allocator call, allocates nothing, and never
>   touches `FrameProfiler._lines` — all three true by construction, so keeping it is the expected
>   outcome, and the console token is what lets a future reader re-derive the number without
>   rebuilding the harness.
> - `FreeBlockCount` and the two peaks **are diagnostics, not gates**. They explain `S_worst` and say
>   how far the real list sits from the pinned 380-block ceiling. A high peak with a low `S_worst`
>   still lands nothing.
> - If a GATE-B run shows **no mass-unload frame at all** (no frame in which any pool's per-window
>   `FreeBlockCount` max rises above its settled value), the capture **FAILED** and is re-run. A
>   settled number is never read as if it were the storm.
> - WHATEVER the numbers, **no frame-time claim is quoted from any `perf-fly` throughput field**, and
>   no `lod` or `chunkUnld` profiler row is quoted as this item's cost — the 60 s window is saturated
>   (lesson 1), and those rows hold the work the scan sits beside, not the scan.
> - **What the output gate can and cannot see, fixed here too.** The cross-tree replay hash gates
>   branches (a) and (c). It **cannot** gate branch (b): `GeometryAllocator` is untouched by (b) and
>   `LodRegion.BuildMeshData` is a static pure builder, so both hashes are byte-identical under (b)
>   **by construction**, and the seam (b) changes — `ChunkGeometryPool.Upload` — needs a GL context
>   and cannot be replayed headlessly. Under (b) the gate is instead the new exact-equality facts over
>   `GeometrySlice`'s derived draw parameters plus the `entity-binds` pixel check, and this round says
>   so rather than claiming a gate it does not have.

**The GATE-B scenario, so it is reproducible without being checked in.** It lives in the scratchpad
rather than in `visual-examples/`, because it is a synthetic allocator stressor and not a visual
instrument — it takes no screenshot at all. `VisualScenario.Load` reads its path with
`File.ReadAllText` (`VisualScenario.cs:53-58`), so it is run from `OpenGLTest/` by **absolute** path.
Body verbatim:

```json
{
  "steps": [
    { "op": "wait", "seconds": 20 },
    { "op": "teleport", "pos": [6000.5, 90.5, 6000.5], "yaw": -90, "pitch": -10 },
    { "op": "wait", "seconds": 25 },
    { "op": "teleport", "pos": [0.5, 90.5, 0.5], "yaw": -90, "pitch": -10 },
    { "op": "wait", "seconds": 25 }
  ]
}
```

Six thousand blocks clears **every** retention set with room to spare, which is the point: chunk
retention is 256 blocks (`WorldGenerationConfig.ChunkRadius` 16 chunks), and LOD retention is
`RegionRadius + 1` = 9 regions at each level (`LodRegion.Levels`, all three specs at
`RegionRadius: 8`), so even level 2 with its 256-block regions reaches only 2304 blocks. Both legs
therefore evict the whole resident set in one frame on the chunk side and on all three LOD levels,
which is the only way to observe a free list at a length no player will produce.

**GATE-A result.**

Three scored `perf-fly.json` runs at `VOXEL_DEBUG=1 VOXEL_TIME=60`, one cold run discarded first, all
six off one Release build. Every figure below is scraped from the `geo` console token; the logs are
`d5-gateA-cold.log` and `d5-gateA-run{1,2,3}.log` and the maxima re-derive from them with a grep.

| run | `S_worst` (steps in one frame) | split opaque / translucent / **lod** | lod share | `S_worst` @ 2.0 ns | max `f` o/t/l | max `pkw` o/t/l | lifetime `pk` o/t/l | max `S_sec` | `S_sec` @ 2.0 ns |
|---|---|---|---|---|---|---|---|---|---|
| 1 | 7,289,632 | 6,354,047 / 927,672 / **7,913** | 0.109 % | **14.579 ms** | 1965 / 473 / 74 | 2138 / 606 / 74 | 2274 / 784 / 81 | 58,496.7 | **0.11699 ms** |
| 2 | 7,268,352 | 6,341,025 / 919,626 / **7,701** | 0.106 % | **14.537 ms** | 1956 / 417 / 74 | 2228 / 623 / 79 | 2295 / 781 / 82 | 59,205.6 | **0.11841 ms** |
| 3 | 7,338,750 | 6,409,592 / 920,785 / **8,373** | 0.114 % | **14.678 ms** | 1954 / 442 / 74 | 2183 / 633 / 78 | 2264 / 790 / 83 | 59,685.6 | **0.11937 ms** |

Source lines, verbatim. Run 1's worst frame, in published window 47 —
`geo f 36/19/69 pkw 685/106/73 pk 2274/784/81 scan 6403231/932491/8498 worst 7289632=6354047/927672/7913 frames 149` —
and run 1's `S_sec` maximum, in window 23, where the opaque list is at its longest settled length —
`geo f 1965/473/29 pkw 2138/606/29 pk 2237/631/29 scan 3601151/317150/975 worst 3306671=3071585/234977/109 frames 67`,
i.e. (3,601,151 + 317,150 + 975) / 67 frames = 58,496.7 steps per frame. `S_sec` is divided by the
token's raw `frames` field, never by the line's rounded `fps`, exactly as the rule requires.

Two fields the table cannot carry, said plainly rather than estimated. **Steps per allocator call is
not derivable from this capture**: `GeometryAllocator.AllocatorCalls` exists and is pinned headlessly,
but it was not among the fields `FrameProfiler.SetGeometryCounts` publishes, so the `geo` token carries
steps and list lengths but not call counts. Nothing in this write-up depends on it, and no per-call
figure is quoted. **`LargestFreeBlock` was never logged**, because the growth log line that carries it
fires only on a pool doubling and `grep -c 'grew to'` returns **0 in all six scored logs** — none of the
three pools grew once in six runs. That is a result in its own right: it confirms R10's finding that
these pools are sized where they settle, and it means the scan cost measured here is the cost of walking
a free list inside a pool that never had to expand.

**GATE-B result.**

Three scored runs of the scratchpad `d5-teleport.json` at `VOXEL_DEBUG=1 VOXEL_TIME=120`, one cold run
discarded first, same Release build. Logs `d5-gateB-cold.log` and `d5-gateB-run{1,2,3}.log`.

**Validity condition first, before any number is read from these runs.** The rule voids a GATE-B capture
that shows "no mass-unload frame at all". All three runs show one unmistakably. In run 1 the outbound
teleport lands in published window 20 —
`geo f 1/1/1 pkw 727/200/1 pk 2177/720/2 scan 5727148/632029/1026 worst 6358578=5725853/631816/909 frames 288` —
where the opaque pool's settled `f` collapses from 727 to **1** (every chunk evicted, the whole list
coalesced back to one block) while the lifetime `pk` jumps from 728 to **2177** inside that single frame.
Runs 2 and 3 do the same in windows 19 and 20 (`pk` 703 -> 2179 and 776 -> 2193). The peak is intra-frame
and invisible to the end-of-frame `f` and `pkw` samples, which is precisely why the rule required the
high-water mark to live inside `Free` and `Grow` rather than in an end-of-frame read; had the instrument
been an end-of-frame sample it would have reported a free list of length 1 for the largest allocator event
in the engine. The return leg shows a second, smaller storm (run 1 window 45, worst frame 2,445,072 steps).

| run | `S_worst` (steps in one frame) | split opaque / translucent / **lod** | lod share | `S_worst` @ 2.0 ns | max `f` o/t/l | max `pkw` o/t/l | lifetime `pk` o/t/l | max `S_sec` | `S_sec` @ 2.0 ns |
|---|---|---|---|---|---|---|---|---|---|
| 1 | 6,358,578 | 5,725,853 / 631,816 / **909** | 0.014 % | **12.717 ms** | 1242 / 257 / 14 | 1259 / 336 / 14 | 2177 / 720 / 14 | 32,622.9 | 0.06525 ms |
| 2 | 6,446,725 | 5,816,046 / 629,740 / **939** | 0.015 % | **12.893 ms** | 1306 / 370 / 23 | 1310 / 374 / 23 | 2179 / 707 / 23 | 34,759.5 | 0.06952 ms |
| 3 | 6,452,386 | 5,825,939 / 625,540 / **907** | 0.014 % | **12.905 ms** | 1268 / 251 / 11 | 1294 / 330 / 11 | 2193 / 718 / 11 | 35,099.8 | 0.07020 ms |

GATE-B's worst frame is *smaller* than GATE-A's, which is not a paradox and is worth stating so a future
reader does not read it as one: the 6000-block teleport evicts a resident set that had only ~20 s to build
at spawn, whereas `perf-fly`'s legs teleport out of a set that a full sustained sprint has already grown to
its ceiling. The synthetic ceiling scenario is not the larger event; the player-produced one is.

**Which branch the rule names.**

Each trigger against its bar, in the rule's own order and wording.

- **BURST** — "`S_worst * 2.0 ns >= 0.20 ms` on GATE-A in at least 2 of the 3 runs." Measured
  **14.579 / 14.537 / 14.678 ms** in runs 1 / 2 / 3. **FIRES, 3 of 3, by a factor of ~73.**
- **SUSTAINED** — "`S_sec * 2.0 ns >= 0.10 ms` in at least one published second, in at least 2 of the 3
  GATE-A runs." Measured **0.11699 / 0.11841 / 0.11937 ms**. **FIRES, 3 of 3**, but by only ~1.19x, which
  matters below.
- **TELEPORT** — "`S_worst * 2.0 ns >= 0.50 ms` on GATE-B in at least 2 of the 3 runs." Measured
  **12.717 / 12.893 / 12.905 ms**. **FIRES, 3 of 3, by a factor of ~26.**

**Confirmation obligation, discharged before any branch is named.** The rule: "before landing code, price
one scan step with an off-engine microbenchmark in D1's harness shape... If the measured ns/step puts the
fired quantity back under its bar, the fire is withdrawn." Scratchpad `d5bench`, net9.0 Release console
project, `Free`'s insertion-scan loop verbatim over a `List<(int, int)>` in the fall-through case, median
of 5 sweeps after a discarded warm-up, at the three lengths this capture actually produced:

```
[d5bench] length=380  iterations=105263 sweeps=5 median=1.5207 ns/step min=1.1494 max=1.9911
[d5bench] length=1000 iterations=40000  sweeps=5 median=1.1526 ns/step min=1.1412 max=1.2491
[d5bench] length=2177 iterations=18373  sweeps=5 median=1.1468 ns/step min=1.1287 max=1.1573
```

**1.1468 ns/step** at the measured opaque peak length of 2177 — the rule's 2.0 ns was conservative by
1.74x, as it was designed to be. Re-applying each fired trigger at the measured price:

- BURST: 7,289,632 x 1.1468 ns = **8.360 ms** (runs 2, 3: 8.335, 8.416 ms) vs a 0.20 ms bar. **Stands.**
- TELEPORT: 6,358,578 x 1.1468 ns = **7.292 ms** (runs 2, 3: 7.393, 7.400 ms) vs a 0.50 ms bar. **Stands.**
- SUSTAINED: 58,496.7 x 1.1468 ns = **0.06708 ms** (runs 2, 3: 0.06790, 0.06845 ms) vs a 0.10 ms bar.
  **WITHDRAWN.** It cleared the bar only at the deliberately pessimistic constant and does not clear it at
  the real one, so the rule's own clause applies and both numbers are recorded. This is the confirmation
  obligation doing exactly the job it was written for, and it is recorded as a withdrawal rather than
  quietly dropped.

Two triggers survive confirmation with three and one and a half orders of magnitude of margin. **The rule
FIRES.**

**Branch selection**, applied to the frames that fired. The rule: take (b) the paired shell+sheet
allocation "iff BOTH (i) the `lod` pool contributed >= 60 % of that quantity's scan steps, AND (ii) the
fragmentation precondition holds".

Condition (i) **fails, and not marginally.** In GATE-A's worst frames the `lod` pool contributed
**7,913 / 7,701 / 8,373** steps out of 7.27-7.34 million — **0.106 % to 0.114 %**, roughly one part in
nine hundred. In GATE-B's it contributed 907-939 out of ~6.4 million, **0.014 %**. The opaque pool alone
is **87 % (GATE-A) to 90 % (GATE-B)** of every fired frame and the two chunk pools together are **99.89 %**. Condition (i) required
60 % from `lod`; the measurement is off by a factor of 550. Condition (ii) is therefore not reached, and
is not evaluated: the rule only asks for it once (i) holds, and fusing cannot be justified by a pool that
contributes a tenth of a percent of the cost whatever its fragmentation looks like. The paired allocation
remains prepared and unused, and remains carrying the hazard the rule pinned — a fused 176-181-unit request
fits strictly fewer holes than its two halves — so it must not be re-proposed on call-count arithmetic.

The rule's other clause points the same way: "A GATE-B fire therefore **always selects branch (a)**;
branch (b) can only be selected by a GATE-A fire." GATE-B fired; GATE-A fired and named the chunk pools;
the two gates agree, so the tie-break ("if a GATE-A trigger and the GATE-B trigger disagree on the branch,
GATE-A wins") is not needed.

> **The rule names branch (a): the O(log n) free list.**

And it names it for a reason the bullet did not anticipate. D5's own text frames the defect around the LOD
pool's paired, uncapped churn; the measurement says the LOD pool is **not the problem at all** — its traffic
is real but three orders of magnitude below the chunk pools'. What fires is
`ChunkManager.UnloadDistantChunks` disposing every out-of-range chunk in one frame, in
`ConcurrentDictionary` hash order rather than offset order, against an opaque free list that sits at
~1950-2270 blocks. That is the scan worth making logarithmic, and it is the *opaque* pool's, so any
implementation of (a) must be measured on the opaque pool and not on the LOD one.

Note also what this does **not** claim. 8.36 ms is the cost of the scan in the single worst frame of a run,
not a steady-state frame-time cost: the SUSTAINED trigger was withdrawn precisely because the per-frame
average does not clear a 0.10 ms bar at the real price. The honest statement is that the free-list scan is
a **stall amplifier on mass-unload frames** — frames that are already the worst in the run — and is noise
the rest of the time. No `perf-fly` throughput field and no profiler row is quoted anywhere above.

**What the numbers say about the list length.**

The bullet's premise was that "the pool's own comment reports 93 % steady-state utilization, which implies
a short list". **The premise is false, and this is the round's most transferable finding.** High utilization
says how much of the buffer is occupied; it says nothing about how many holes the occupancy is threaded
through. Measured:

- The `lod` pool behaves exactly as R10 predicted and as the bullet assumed. Its free list peaked at
  **74 blocks settled / 81-83 lifetime** on GATE-A and **11-23** on GATE-B, against the **380-block**
  ceiling `MidTeleportFragmentationPeaksAtAKnownBlockCount` pins. Real LOD churn reaches **~21 %** of its
  own pinned ceiling, because departures are geographically clustered and the list coalesces as it drains.
  Its worst-frame scan cost, **7,913 steps**, is **5.5 %** of the **144,400-step / 0.289 ms** arithmetic
  ceiling derived at the top of this section — and 0.009 ms at the measured price. The derivation was
  correct and the pool never comes near it.
- The **opaque chunk pool** is where the assumption breaks. Settled `f` reaches **1954-1965** blocks in a
  sustained sprint and the intra-frame lifetime peak reaches **2264-2295** on GATE-A and **2177-2193** on
  GATE-B — **six times** the LOD pool's pinned worst case and **twenty-six times** its own real LOD
  counterpart. The translucent pool sits at 417-473 settled and 781-790 peak, itself above the LOD ceiling.
- So the item's arithmetic was right and its subject was wrong. The 380-block figure was a genuine ceiling
  for the pool it was derived from; nobody had ever measured the pool that actually matters. The chunk
  pools have no pinned ceiling, no per-frame free cap (`ChunkManager.UnloadDistantChunks`,
  `ChunkManager.cs:228-267`, disposes every out-of-range chunk in one frame), and an unload order unrelated
  to offset, so their frees do not fall through the insertion loop — they stop about halfway, which is why
  6.35 million steps come out of one frame.

For a future reader deciding whether to re-open this: the quantity to watch is the **opaque** pool's `f`
and `pk` on the `geo` token, not `lod`'s, and the event to watch is a mass unload, not steady streaming.


**What branch (a) cost and bought, measured the same way.**

The rewrite that landed: `GeometryAllocator`'s offset-sorted `List<(int, int)>` became an offset-keyed
treap whose every node carries the maximum free `Count` over its subtree. That augmentation is what keeps
EXACT first fit while making the search logarithmic — descend left whenever the left subtree still holds a
block large enough, else take this node if it fits, else go right, which is the lowest-offset block that
fits by construction. The bullet's own first suggestion, a size-bucketed free list, could not be used: it
yields a best fit out of a bucket, and `AllocatesFirstFitDeterministically`, `FreeIsReusedFirstFit`,
`ASameSizeReallocationAfterAFreeReusesTheSameOffset` and `MidTeleportFragmentationPeaksAtAKnownBlockCount`
pin exact first-fit offsets. All ten stand unedited.

**A step is no longer the same machine operation, so the two counts are not comparable at one price.** The
flat list's step was one 8-byte tuple read out of a contiguous backing array, which `d5bench` priced at
**1.1468 ns**. A treap step is a pointer chase through a 48-byte class instance. A companion microbenchmark
(`d5treapbench`, the same harness shape — Release console, median of 5 sweeps after a discarded warm-up,
running `Free`'s neighbor descent verbatim against a real treap) prices it at **3.33 ns/node at 380 nodes**
and **3.84 (key-order) / 5.12 (churned) ns/node at 2,177 nodes**, the measured opaque peak. Every figure
below is priced at the **pessimistic 5.1218 ns/node**: the allocator recycles nodes through a spare list, so
after a few minutes of churn its heap layout is the churned one, not the freshly built one.

One re-run of GATE-A at the identical setting (`VOXEL_DEBUG=1 VOXEL_TIME=60`,
`VOXEL_SCRIPT=visual-examples/perf-fly.json`, one cold run discarded first), against the three scored runs
recorded above:

| | steps before (3 runs) | steps after | priced before | priced after |
|---|---|---|---|---|
| `S_worst` (worst single frame, all pools) | 7,289,632 / 7,268,352 / 7,338,750 | **340,457** | 8.360 / 8.335 / 8.416 ms | **1.744 ms** |
| — of which opaque | 6,354,047 / 6,341,025 / 6,409,592 | 257,702 | | |
| — of which translucent | 927,672 / 919,626 / 920,785 | 79,467 | | |
| — of which lod | 7,913 / 7,701 / 8,373 | 3,288 | | |
| max `S_sec` (per-frame mean in a published second) | 58,496.7 / 59,205.6 / 59,685.6 | **2,327.1** | 0.06708 / 0.06790 / 0.06845 ms | **0.01192 ms** |

**21.4x fewer steps in the worst frame, 4.8x less time once the steps are priced honestly** (25.4x and
5.7x on `S_sec`). The opaque pool, which was 87 % of the cost, falls 24.6x on its own.

It is the same workload, not a lighter one: the after run's free-list lengths are within noise of the three
before runs — settled `f` 1901/439/70 against 1954-1965/417-473/74, per-window `pkw` 2185/648/74 against
2138-2228/606-633/74-79, lifetime `pk` 2254/771/80 against 2264-2295/781-790/81-83. No pool grew in the
after run either (`grep -c 'grew to'` returns 0), so the comparison is not confounded by a doubling.

**What that does and does not settle.** The fired triggers were `S_worst * price >= 0.20 ms` (BURST) and the
GATE-B teleport bar; 1.744 ms still clears the BURST bar, so the worst mass-unload frame still pays a
measurable allocator cost. What the rewrite removed is the QUADRATIC term, not the cost: the flat list
charged O(list length) per free with the length itself growing with the unload, and the treap charges
O(log length). `ScanStepsAtTheMidTeleportPeakArePinned` pins the same change headlessly on the LOD storm —
**144,400 steps became 8,372**, a 17.2x reduction, and the new figure is derivable rather than measured
(four right-spine descents per departed region, so about 4 * Sum(H_k) for k = 1..380 = 8,415). A failing
allocation now costs exactly **1** step, the root's subtree maximum, where the flat list walked all 380
blocks to prove the same thing, and `LargestFreeBlock` became an O(1) read of the root instead of an O(n)
walk — which is what the growth log line calls, though it fires only on a doubling.

**What the gate could see.** The cross-tree replay probe's `[d5hash-layout]` and `[d5hash-draw]` are
byte-identical across the rewrite, and so is `[d5offsets]` — which under branch (a) is the load-bearing one,
because first fit is preserved and a moved offset would be a defect rather than a policy change. Beyond the
hashes, `EveryOffsetTheFlatListProducedIsReproduced` steps the treap and a verbatim copy of the old flat
first fit through four thousand interleaved allocations, frees and growths in lockstep and compares every
offset, every success/failure verdict, and the used units, block count and largest block after each one.
That is the general claim; the ten pinned facts are only a handful of fixed sequences.

#### The visual gate: D5 is output-preserving, and the exact cross-tree hash is what says so

**The baseline is a pristine export, never a stash.** `git archive HEAD | tar -x` into the scratchpad at
commit **9b7a244** (`9b7a24485c22d831bc443a47d58696a85b1d0835`, "Key chunk dictionaries by a packed long"),
built there with `~/.dotnet/dotnet.exe build OpenGLTest.sln -c Release` (0 warnings, 0 errors). The real
working tree was never mutated to make the baseline and nothing was stashed: the three stashes `git stash
list` reports all sit on `5124d26`, three commits before HEAD, and predate this round entirely — a future
round checking "no stash" should confirm the count is still **3**, not that it is zero, and must not tidy
away someone else's parked work to make a literal check pass.

**The authority is the exact cross-tree replay hash, and it was written before the first production edit.**
`GeometryLayoutReplayProbe` (step 2 of this round, authored against HEAD's API alone and copied *unchanged*
into the export) drives a **real** `GeometryAllocator` through a 759-region paired stream-in, a clustered
departure phase, an in-place rebuild phase, a shuffled full drain and a re-stream, and separately drives
`LodRegion.BuildMeshData` across all three LOD levels. All three lines are **byte-identical** between the
pristine export and the work tree:

        pristine 9b7a244 export                                 work tree (branch (a) landed)
    [d5hash-layout] hash=0xd5d14e2ba2e58664                 [d5hash-layout] hash=0xd5d14e2ba2e58664
        regions=759 departed=255 rebuilt=72 drained=1008        regions=759 departed=255 rebuilt=72 drained=1008
        restreamed=253 live=506 used=45177 capacity=163840      restreamed=253 live=506 used=45177 capacity=163840
    [d5offsets]     hash=0xd39df70fecc23f61                 [d5offsets]     hash=0xd39df70fecc23f61
        freeBlocks=1 largest=118663                             freeBlocks=1 largest=118663
    [d5hash-draw]   hash=0x730e80860e9f62c3                 [d5hash-draw]   hash=0x730e80860e9f62c3
        regions=6 wetLevels=3 dryLevels=3                       regions=6 wetLevels=3 dryLevels=3
        verts=40692 waterVerts=4128                             verts=40692 waterVerts=4128

**Why those two hashes and not the offsets — the D1 reasoning, restated.** `[d5hash-layout]` folds only
*policy-independent* state: the live `(clientId, unitCount)` pairs in sorted client order, `UsedUnits`, and
`Capacity`. `[d5hash-draw]` folds the drawn geometry — `Vertices.Count`, `Indices.Count`,
`WaterVertices.Count`, `WaterIndices.Count` and `ChunkGeometryPool.UnitsFor` per side — which is what
`firstIndex` / `baseVertex` / `count` derive from and therefore what actually reaches the screen. Slice
*offsets* are a placement detail: an allocator policy may legitimately move them while drawing the identical
image, so hashing them as a gate would fail a correct change. That is the same reasoning D1 recorded when
per-frame enumeration order moved and a mid-stream pixel difference had to be pre-registered as expected
rather than bisected. `Capacity` *is* folded into `[d5hash-layout]` deliberately, because a policy that
grows the pool more often is an observable regression even though it moves no pixel; it did not grow (163840
on both trees).

**`[d5offsets]` did not move, and under branch (a) that is a result rather than a formality.** It is reported,
not asserted — but the branch that landed preserves exact first fit, so a moved offset here would be a
**defect**, not a legitimate policy change. It is the inverse of the D1 situation: there, a moved enumeration
order was expected; here, a moved offset would mean the leftmost descent is not reproducing first fit. It
matched byte-for-byte over a workload of 1008 paired allocations, 255 clustered departures, 72 in-place
rebuilds, a shuffled full drain and a 253-region re-stream — placement the ten pinned facts never exercise.

**The gate's limit, stated in one line because the branch that landed is (a).** The rule pre-registered in
this section warned that no cross-tree hash could have gated branch **(b)** — `GeometryAllocator` is untouched
by (b), `LodRegion.BuildMeshData` is a static pure builder, so neither sits on either hash's code path, and
the seam (b) changes (`ChunkGeometryPool.Upload`) needs a GL context and cannot be replayed headlessly. That
caveat is **moot here**: (b) was not taken, the allocator *is* on both hashes' code path, and the gate is a
real one. It is recorded only so a future round proposing (b) knows a matching hash would prove nothing
about it.

**Pixels are a smoke check, and this round followed the file's own instrument record rather than the task
brief's.** The brief named five pose-stable checked-in scenarios (`rays-halfres`, `clouds-mirror`,
`lod-horizon`, `entity-binds`, `particles`). **That is wrong for this machine and the correction is already
in this file** — see the carried-forward defect list above: `lod-horizon` free-falls and respawns 44-111
times a run so three of its four shots photograph the spawn point, and `clouds-mirror` and `rays-halfres`
photograph different camera poses run-to-run *within the same tree* at their stock waits. Only
`entity-binds` was scored. Following the brief instead of the file would have burned the whole gate budget
rediscovering two known defects.

Method: scratchpad copies of `entity-binds.json` by absolute path, each carrying an `output` override
pointing at a per-run scratchpad directory — so **neither tree's `visual/` was written at all** and the gate
mutated nothing anywhere. Fixed `VOXEL_TIME=120` (its own header requires it: the sun angle is wall-clock
driven, so an unset clock lights the two trees differently and the comparison reads a difference that is not
there). One cold discarded run per tree, then scored runs interleaved work/head in a single session. Scored
with R10's within-tree-constant instrument — a pixel counts only where every run of one tree agrees exactly,
every run of the other agrees exactly, and the two constants differ — against the exact permutation null over
all balanced relabelings of the same runs.

**Result: zero. And the run count did its job again, exactly as D1 recorded it would.**

| `entity-binds` shot | 3+3 residue | 5+5 residue | 7+7 residue | 5+5 null max (126 splits) |
| --- | --- | --- | --- | --- |
| `07-held_block`   | 29 px | **1 px** | **0 px** | 2013 px |
| `11-held_item`    | 31 px | 0 px | **0 px** | 559 px |
| `12-bind_still_0` | 35 px | 0 px | **0 px** | 1560 px |
| `13-bind_still_1` | 29 px | 0 px | **0 px** | 257 px |
| `14-bind_still_2` | 29 px | 0 px | **0 px** | 970 px |
| `19-drops`        |  0 px | 0 px | **0 px** | 140 px |
| **total** | **153 px** | **1 px** | **0 px** | — |

At **3+3** the residue was 29-35 px on five of six shots — and it was already visibly *not* a finding, because
against the exact null over the 10 balanced relabelings of those same six runs the real split ranked
**4th to 7th of 10** (p = 0.4-0.7), with arbitrary relabelings reaching **140-7659 px**, two orders of
magnitude above the real value. Per D1's standing lesson — *a nonzero residue at any fixed run count is a
hypothesis, not a finding, until more runs fail to remove it* — two more runs per tree were added. At **5+5**
the total collapsed to a single pixel (`07-held_block`, x=93 y=202), a **single-LSB difference on one
channel** (work R=17, head R=16; G, B and A identical) sitting on a **strong local luma edge** (3x3 luma range
97.0, the boundary between a ~13-luma shadow and a ~110-luma lit face), ranking 28/126 against null maxima up
to 2013 px. Two further runs per tree took it to **7+7**, where the residue is **exactly 0 px on all six
shots**. 153 -> 1 -> 0: the accidental-agreement collapse, reproduced at two successive run counts.

The mechanism is visible in the data and is worth carrying forward: `entity-binds` is **pose**-stable but not
**pixel**-stable. Only 91-98 % of each frame is constant across a tree's own runs, and two runs of the *same*
tree differ on 945-75,155 raw pixels from animated content (foliage sway, drifting clouds, particle phase).
That is precisely why the raw cross-tree diff is useless here and the within-tree-constant instrument is
necessary — and why the "constant" shrinks as runs are added, so an accidental agreement collapses while a
real effect could not.

**Eyeball-only, entering no statistic: `particles` and `lod-horizon`, one run per tree.** Both were opened and
compared by eye and are structurally identical — same terrain, same LOD horizon and shell boundaries, same
cloud layout, same canopy; the only visible differences are animation phase (a falling leaf present in one
shot and not the other) and, on `lod-horizon`, a small camera-height offset from the known free-fall. Their
raw single-run cross-tree diffs are large (`particles` 8.3 %, 10.9 % and 83.5 % of the frame; `lod-horizon`
60-78 %) and mean **nothing**: a single run of an unstable instrument has no within-tree constant to subtract,
which is the whole reason these two are eyeball-only.

**Instrument defects re-confirmed, not merely inherited.** `lod-horizon` logged **exactly 44 respawns per
run, identically on both trees** — reproducing the figure this file already records for its stock 40/35/35/35
waits, on a third independent occasion. It remains unusable as a scored instrument and the reason is not
tree-dependent. `particles` logged one respawn per run on both trees (the scripted descent, not a defect).
`entity-binds` remains the only checked-in scenario that is pose-stable on this machine, and this round
sharpens that record: it is pose-stable and *pixel-noisy*, usable only with the within-tree-constant
instrument and only at a run count high enough that the constant has stopped shrinking — **7+7 here, where
3+3 would have reported 153 spurious pixels.**

### Terminal hand-off — nothing is open

Start here. The accumulated gate machinery — every trap a scenario-based comparison has fallen into
in this series — is the **"Gate traps that still apply to any scenario-based comparison"** list
immediately above; read it before designing any capture, and read the entry of whatever item you are
about to touch before touching its code. What follows is the **terminal** hand-off: what landed across
the whole series, what was ruled out and why, what was relocated and where, and what debt is carried
forward. The backlog itself is **empty** — see `## Open (next round)` at the top of this file.

**This round closed the file.** It took the last entry with code in it — **R6** — and disposed of the
other three by decision rather than by code: **R3** to **Ruled out** carrying its findings intact,
**C5** split between **What landed** (the mechanism) and **Ruled out** (the pool-size lever alone),
**D2b** relocated to `TODO.md` `M-11` because it is a correctness item entangled with save/load and
never belonged in a perf file. R6 shipped a **per-frame-latched gate with an idle keep-alive**
(`GpuTimingGate.ShouldTime`, `WorldGenerationConfig.GpuTimingIdleInterval = 8`), measured at
**37.8 -> 4.3 GL query calls a frame** on the same scenario — counted, never timed — with a
zero-residue cross-tree pixel gate that, as its own entry says plainly, **could not have seen the only
surface this change can touch**. The full account is the R6 bullets under **What landed**. The rounds
below are the earlier ones, newest first.

**This round took D5, alone, as the closing paragraph then said to — and D5 turned out to be an
implementation after all.** The measurement its bullet demanded was taken first, the pre-registered
rule fired on it, and `GeometryAllocator`'s flat free list became an offset-keyed treap augmented
with a subtree maximum, preserving exact first fit at O(log n). The rule, both captures, the
confirmation microbenchmarks, the before/after table and the cross-tree gate are in
**"Measurement: D5"**; what changed, what it bought, the premise it disproved and the remedy it left
unused are the D5 bullets under **"What landed"**. **The round before it took D1, entirely on its own
as the round before that said to.** `ChunkManager.Chunks` is
now a `ChunkMap` over a `ConcurrentDictionary<long, Chunk>`, `_dirtyChunks`, `_queuedChunkPositions`,
`_generatingMeshes` and C6's `_playerPlacedByChunk` hold packed `long` keys, and **`Vector3` stayed
the outward key**, so zero call sites were edited to keep the tree compiling. The isolated per-shape
probe table, the pre-registered decision rule and the cross-tree gate are in **"Measurement: D1"**;
the design decision, the C3 obligation, the enumeration-order consequence, the rejected alternatives
and the sites deliberately left alone are the D1 bullets under **"What landed"**. Read the
enumeration-order bullet before designing any gate on this path. The round before it took C6 and D6,
both counted rather than timed. **Six things now govern what a future round should believe — D1
confirmed the first four and added the fifth, and D5 added the sixth:**

1. **The `perf-fly` 60 s window is saturated for LOD as well as chunks.** All ten counterbalanced
   runs ended at the identical `chunks 17077` and `regions 907-913/645`, so every end-state
   throughput field measures the scenario's ceiling and **nothing isolated; nothing was quoted**.
   Any future frame-time claim on this family needs a **time-to-N rule on a non-saturating window,
   pre-registered before the capture**. Do not try to rescue a number out of the existing logs.
   C6 + D6 ran **no** `perf-fly` capture at all for this reason, and quoted no wall clock from any
   run — not from the eviction gate's own `[elapsed]` line, not from the visual runs.
2. **R10's within-tree-constant pixel instrument needs an exact permutation null** — with 3 runs per
   tree, `p = rank/10` over the 10 distinct 3-vs-3 relabelings. Without it the raw residue on
   `lod-horizon` looked like a finding and was not (the real split ranked 6th/2nd/4th/10th). C6 + D6's
   one converged `lod-horizon` shot separated perfectly at `p = 0.1` and still turned out to be
   bubble sprites plus a smooth sunset dimming; after the high-pass normalization that instrument's
   own header mandates, `p = 0.5`. **D1 added the other half: the run count is part of the
   instrument.** Its gate saw the same accidental-agreement collapse twice, one level apart — a
   1094 px "finding" at 3+3 that is 0 px at 5+5, and the 10 px surviving at 5+5 that are 0 px at
   7+7 across all 23 scored shots. A nonzero residue at any fixed run count is a hypothesis until
   more runs fail to remove it, because a tree's "constant" pixels shrink as runs are added.
3. **`ColumnCaps` already costs something today.** Two output-determinism facts
   (`LodBuildPoolTests.RegionBuildsAreIdenticalWhicheverThreadRunsThem`,
   `LodHeightSamplingTests.SeparableMinMatchesTheNestedWindowAtEveryLevel`) are intermittent in
   full-suite runs because it is process-global with no reset hook — **reproduced on pristine HEAD**,
   and observed again this round on an unmodified tree before a line was edited, so expect them and
   do not chase them as someone else's bug. Never "fix" them by touching `ColumnCaps`.
4. **`Generations` is a one-token re-pricing lever with a measured lower edge**, and it may only be
   moved **against a real capture, never blind**: `ColumnCacheGenerations` at G = 2 is *worse* than
   the old wholesale clear, and G = 8 wins on totals while losing on chunk-gen's own misses. D6 took
   that rule literally — it ran its capture **before** the conversions, with the decision rule written
   in final wording before a number was read, prepared either to re-price one cache's G against that
   table or to leave that cache unconverted on the R3/C5 no-go precedent. The rule did not fire.
   **All five `GenerationalCache` sites now ship at G = 4** — `Chunk.ColumnCache`,
   `WormCarver.CellCache`, `TreeGenerator.ShapeCache`, `TreeGenerator.CanopyCache`,
   `CactusGenerator.ShapeCache` — and **none of them ships at a G derived from a capture**; four is
   simply the value every capture so far has failed to beat. The next editor who wants a different G
   owes a capture of their own, per cache.
5. **A rekeying's win is per-call-site SHAPE, and a rekeying changes enumeration order.** Both
   halves are D1's, and both generalize. *Shape*: the win is decided by how many dictionary
   operations reuse one packed key, so a single aggregate probe number would have misled — D1's
   capture had to price `NeedsChunk` (one key, up to two probes) separately from the
   distinct-key-per-probe sites, and the two came out at 0.52-0.58x and 0.39-0.44x of HEAD
   respectively. Price shapes, not implementations, and never quote the better arm as if it were the
   other one's result. *Order*: repacking a key changes hash-bucket layout and therefore dictionary
   enumeration order, which three sites in this engine observe, so **a per-frame output comparison
   is the wrong gate** and mid-stream pixel differences are expected rather than evidence. Only an
   **order-independent converged hash** can settle output preservation for a change of this shape —
   which is why D1's authority was a sorted-state cross-tree hash written before the first
   production edit, with pixels demoted to a settled-state smoke check.
6. **A count is only a time once you have priced its step, and two data structures do not share a
   price.** D5's rewrite cut worst-frame scan steps **21.4x**, and quoting that as a 21.4x time win
   would have been false: a flat-list step is one 8-byte tuple read out of a prefetched array
   (**1.1468 ns** measured) and a treap step is a pointer chase through a 48-byte instance
   (**5.12 ns** measured at the same list length), so the honest figure is **4.8x**. The same
   discipline cuts the other way and did: D5's rule fixed a deliberately conservative **2.0 ns/step**
   before any capture so that a *decline* could only be conservative, and made every *fire* carry a
   confirmation obligation — which duly **withdrew** one of the three triggers when the real price
   came in 1.74x cheaper. **D5's other half is a premise correction worth carrying: high buffer
   utilization does not imply a short free list.** The pool's 93 % steady-state utilization was cited
   in D5's own bullet as evidence the scan was cheap and probably not worth touching; the opaque
   pool's free list peaks at **2,264-2,295 blocks** at that very utilization, because utilization says how full the buffer is, not how many holes
   the occupancy is threaded through.

**The backlog is closed, not merely exhausted.** D5 was the last item on this list takeable without a
human and the round before this one took it. This round was authorized by a human to finish or dispose
of everything that was left, and did: **R6 implemented, R3 ruled out, C5 filed as closed, D2b
relocated to `TODO.md` `M-11`.** `## Open (next round)` is now **empty and says so in words**. **This
is a terminal hand-off, not a suggestion for a next round.** There is nothing here to pick up. What
follows is what a cold reader needs in order to measure anything in this engine without repeating four
rounds of instrument mistakes, the disposition of each of the four entries that used to stand open,
and the two pieces of carried debt that outlive the series.

**R3 — closed: moved to Ruled out, not unparked.** It was resolved against the code in the R2 round
and parked, never disproven — the depth copy cannot move, the shader claim holds, and the ceiling is an
**already-gated 0.09 ms pass**. This hand-off used to say only a human could unpark it, and that was
right: what was left was never a measurement but a perceptual judgment no capture can answer. The
human closing this round chose to **stop carrying it** rather than unpark it, so it now sits under
**Ruled out** with findings (a)/(b)/(c) intact. Retry only on new evidence — a capture that isolates
the color copy as its own row.

**R6 — closed: implemented, under a reservation that was lifted on purpose.** This hand-off used to
say R6 "must not be taken autonomously", and that sentence is kept here rather than deleted because it
was correct: the number was never the obstacle, the trade was. It was lifted on **2026-09-20** by an
explicit human instruction to finish or dispose of every remaining open bullet, and the trade was then
**decided rather than deferred** — and turned out not to be the either/or the bullet framed. A
per-frame latched gate with an idle keep-alive keeps the property the bullet said would be sacrificed
(the once-a-second snapshot standing when `X` is pressed already carries GPU figures) and costs
**37.8 -> 4.3 GL query calls a frame**, measured on the same scenario, counted and never timed. What is
genuinely given up — including the one part that bites, a conditionally-bracketed pass reading `--` —
is in the R6 bullets under **What landed**, along with the finding that the always-on snapshot on HEAD
is *already* only about half reliable for passes after `lodPass` on this machine.

**C5 — closed, and now filed as closed rather than listed as open.**
`WorldGenerationConfig.MaxConcurrentLodGenerations` ships at **1**. The mechanism landed and stays —
the TryAdd-gated claim protocol, the in-flight cap, the Idle/Throttled split, `CompletedBuildCount` and
the raised retention ceilings, all correct at any N — and it is under **What landed**; the **pool-size
lever alone** is under **Ruled out**, with its capture under **"Measurement: C5"**. It sat in the open
list only so a cold reader would not re-propose a bigger pool, and the Ruled-out entry now does that
job properly. (D2 was the lever that capture pointed at and has been taken; D6 extended it to three
more caches. Neither reopens the pool question.)

**D2b — relocated, not fixed: it is `TODO.md` `M-11` now.** `LodRegion.ColumnCaps` grows unbounded,
but its entries are **irreproducible player edits**, so it must not take D2's or D6's cache/eviction
scheme — evicting a cap is **data loss, not a recompute**, and `GenerationalCache` invariant 10 names
this exact site — and it cannot ride chunk unload either, because LOD regions cover terrain far outside
the loaded chunk sphere so a cap must **outlive** its chunk. A real fix needs caps **re-derivable from
stored block data**, i.e. it is entangled with save/load (`TODO.md` `M-1`): a **correctness item
waiting on a design**, which is why it left this file entirely. Its four sub-points, its
~1 KB-per-edited-column arithmetic and `LowerColumnBelow`'s load-bearing publish ordering traveled
with it to `M-11`, beside `M-10`, its sibling data-loss bug. What stays here is the cost it imposes on
this file's own instruments — see the carried-debt bullet below.

**What a cold reader needs.**

- **Build and test.** The system `dotnet` has **no SDK**; use `~/.dotnet/dotnet.exe`.
  Build: `~/.dotnet/dotnet.exe build OpenGLTest.sln -c Release` (Release only — it is the sole
  configuration measured or shipped here). Tests:
  `DOTNET_ROLL_FORWARD=LatestMajor ~/.dotnet/dotnet.exe test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release`.
  All code lives under the `VoxelEngine` root namespace, one sub-namespace per folder (AGENTS.md, "Code organization").
- **The suite baseline, and how the series moved it.** **790 total / 786 passed / 4 failed** at
  9b7a244, rising to **797 / 793 / 4** by the D5 round's HEAD (8b743c7) — which is exactly what this
  round re-measured before touching anything. R6's three `GpuTimingGateTests` facts take it to
  **800 / 796 / 4**, with the same four failures and no others. Those four are **standing failures that
  predate this series**; they and the two `ColumnCaps` intermittents are the carried debt named in the
  next bullet. **Expect them; do not chase them.**
- **Two pieces of carried debt outlive this work, and neither is anybody's regression.**
  **(i) Four standing test failures.** They were failing at 9b7a244 before a line of this series was
  written, they are unrelated to everything in it, and no round here has touched the code they cover:
  `ContentTests.DisplayMatrixPosesSpritesAsConfigured`,
  `BlockMovementTests.FrictionDefaultsToPointNineNineAndIceIsLower`,
  `BlockMovementTests.LeavesDeclareThreeQuarterMovementSpeed` and
  `FallingBlockTests.SevenTowerChainRebuildsFullyWithNoDrops`. They are real failures and somebody
  should fix them — it is simply not this file's work, and a round that sees exactly these four has
  seen a clean run. **(ii) `LodRegion.ColumnCaps` is process-global with no reset hook**, so any test
  class that mines near the origin flips a *later* build of the same region, which makes
  `LodBuildPoolTests.RegionBuildsAreIdenticalWhicheverThreadRunsThem` and
  `LodHeightSamplingTests.SeparableMinMatchesTheNestedWindowAtEveryLevel` **intermittent in full-suite
  runs**. Both reproduce on pristine HEAD; during this round one or the other fired on several runs of
  a tree whose only functional addition was a GL-free static class no test reaches, and the failing
  name **moved between runs**, which is the signature. Re-run once to confirm it moves, record it, and
  **never silence it by touching `ColumnCaps`**. The underlying unbounded growth is now tracked as
  `TODO.md` `M-11`.
- **Measurement here is counted, never timed.** The `perf-fly` 60 s window is **saturated** for both
  chunks and LOD (lesson 1 above), so every end-state throughput field measures the scenario's ceiling
  and isolates nothing. Any frame-time claim needs a **time-to-N rule on a non-saturating window,
  pre-registered before the capture**. Counted instruments have produced every usable number in the
  last four rounds (D2, C6, D1, D5); timings have produced none.
- **Decision rules are written in final wording before a number is read.** D6 set the pattern, D1 and
  D5 followed it. D5's rule is reproduced verbatim under "Measurement: D5" — with its bars, its fixed
  pricing constant, its branch-selection test and its **confirmation obligation**, which is what
  withdrew D5's SUSTAINED trigger after the real ns/step turned out to be 1.74x cheaper than the
  conservative constant the rule priced at. A rule that cannot withdraw a fire is not a rule.
- **Harness settings.** `VOXEL_DEBUG=1 VOXEL_TIME=60` for `perf-fly` — that is what this file's Method
  block pins and what every recorded number was taken at, **notwithstanding `perf-fly.json`'s own
  header comment saying 120**. `VOXEL_TIME=120` for `entity-binds`, whose sun angle is wall-clock
  driven. `VOXEL_SCRIPT` takes an absolute path (`VisualScenario.Load` uses `File.ReadAllText`), and a
  scenario's `output` key redirects its shots — which is the cheap way to run a cross-tree pixel gate
  without either tree's `visual/` being written at all.
- **Baselines come from `git archive HEAD` into a scratchpad, built there — never from a stash and
  never by mutating the working tree.** Note the trap: this repo carries **three pre-existing stashes**
  on `5124d26`, unrelated to this series. A "no stash" check must verify the count is **unchanged**,
  not that it is zero, and must never drop them to make a literal check pass.
- **The `geo` console token is the standing free-list instrument.** It is published once a second on
  `FrameProfiler`'s **console** line only (never `_lines`, never the HUD overlay — every gate
  screenshot passes `hideHud:true` and `perf-fly` bakes the HUD in, so a console-only token cannot move
  a gate pixel). Its shape is
  `geo f o/t/l pkw o/t/l pk o/t/l scan w0/w1/w2 worst SUM=d0/d1/d2 frames N`, index order fixed
  **0 = opaque, 1 = translucent, 2 = lod**: `f` is the end-of-frame `FreeBlockCount`, `pkw` the
  per-window maximum of it, `pk` the **lifetime** high-water mark (updated inside `Free` and `Grow`, so
  it is the only field that can see an intra-frame teleport storm), `scan` the per-window `ScanSteps`
  delta, `worst` the largest single-frame total with its three-way split, and `frames` the **raw**
  window frame count. Divide by `frames`, never by the line's `fps` — `fps` is
  `(int)Math.Round(frames / seconds)`, a rounded rate. The token does **not** carry `AllocatorCalls`;
  a future round wanting steps-per-call must add it to `FrameProfiler.SetGeometryCounts`.
- **The `GPU timer queries:` shutdown line is R6's standing instrument, and it is a *shutdown* line on
  purpose.** `RenderManager.Dispose` (`RenderManager.cs:2863`) logs
  `GPU timer queries: {calls} GL calls over {frames} frames, {timed} timed ({perFrame} per frame)`
  before any GL teardown, and `Program.OnClosing` disposes the render manager before `Log.Shutdown()`,
  so it is drained in **every** run — scripted or interactive, with `VOXEL_DEBUG` or without. It is
  deliberately **not** a `FrameProfiler` console token, because a console token **structurally cannot**
  report the gated configuration: `Program.LogDebugLine` (`Program.cs:194`) opens
  `if (!debugEnabled) return;` (`:196`) — the same flag the gate reads — so a token would print only
  when the gate is open on every frame, i.e. it could only ever re-measure the always-on path. Scrape
  it from the tail of a run log. It reaches no pixel surface, but a scraper that parses the end of a
  log must tolerate one extra line.
- **Instrument defects, which cost two rounds to find and will cost a third if skipped.**
  `lod-horizon` is **unusable** as a scored instrument on this machine: its camera free-falls and
  respawns **44 times a run** at stock waits (reconfirmed identically on both trees in D5's gate, a
  third independent occasion), so three of its four shots photograph the spawn point.
  `clouds-mirror` and `rays-halfres` photograph **different camera poses run to run within the same
  tree**, so at those two the same file name is not the same view. **Of the checked-in scenarios only
  `entity-binds` is pose-stable here** — and the task briefs that claim five are pose-stable are wrong
  against this file. **New in D5: `entity-binds` is pose-stable but not pixel-stable.** Only 91-98 % of
  each frame is constant across a single tree's own runs, and two runs of the *same* tree differ on
  945-75,155 raw pixels from animated content. A raw cross-tree diff on it is therefore meaningless;
  the within-tree-constant instrument is mandatory, and **3+3 runs is not enough** — D5's gate reported
  153 spurious pixels at 3+3, 1 at 5+5 and **0 at 7+7**, the same accidental-agreement collapse D1 saw
  twice.

**Before the next visual gate, read the accumulated instrument defects — the two at the end of
"Measurement: C6 + D6", the two D1 added, and the one D5 added in "The visual gate: D5" above
(`entity-binds` is pose-stable but *not* pixel-stable, so 3+3 runs reports residue that is gone by
7+7).** `lod-horizon` cannot currently resolve anything on this machine — its
camera free-falls and respawns 44 times a run at stock waits and 111 at raised waits, identically on
pristine HEAD, so three of its four shots photograph the spawn point rather than their scored
vantages — and `rays-halfres` is only usable with its settle wait raised from 6 s to 30 s, at which
point same-build control ranges collapse to exactly `[0,0]` on several shots. A round that skips this
will burn its gate budget rediscovering both. **D1 added a third and a fourth defect to that list,
recorded in "The visual gate: D1" above:** at these wait settings `clouds-mirror` and `rays-halfres`
photograph **different camera poses run to run within the same tree**, identically on pristine HEAD,
so at those two the same file name is not the same view — of the checked-in scenarios only
`entity-binds` is pose-stable here; and the raised-wait control never reports equal streaming counts
on any of them, but per **field** the two trees' ranges overlap on all **92** shot-fields (23 shots
x 4 fields) with the work tree inside HEAD's on **86**, so a tree-disjoint label on a 4-tuple of four
noisy fields is an artifact, not a separation. Compare fields, not tuples.

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
  `CopyScene` (`RenderManager.cs:1046-1056`), called with color+depth at `:1734` and
  depth-only at `:1550`. The condition R2 left on this bullet was resolved against the code
  rather than implemented, and it settles three ways.
  **(a) The depth copy cannot move.** `CopyScene(bool color)` copies depth
  *unconditionally* — only the color half is gated by the parameter — and both call sites
  write the same `_sceneDepthTex`, which R2's depth-aware upsample pins at full res. So the
  `:1734` call keeps paying a full-res `DepthComponent24` `CopyTexSubImage2D` whatever R3
  does, and a second, half-res depth used only by the water pass is the same *additive* cost
  (an extra texture plus an extra reduction, on top of a full-res copy that still has to
  happen) that R2 already rejected on the rays side. Only the RGBA8 color copy is left.
  **(b) The shader claim holds.** `test-fragment.glsl`'s refraction really is
  resolution-independent: `uv = gl_FragCoord.xy * uInvViewport` (`:267`) against
  `uInvViewport = 1/_viewportSize` (`RenderManager.cs:1483-1484`), bilinear `texture()` fetches
  (`:270-272`), a `uInvViewProj` unprojection (`:276`) — no `texelFetch` and no pixel indices
  anywhere in the file. The shader would need zero changes.
  **(c) But the ceiling is tiny and the change is not "sample less".** The color copy is
  gated behind `if (_nearWaterVisible)` and sits inside a `waterPass` measured at **0.09 ms
  GPU total** (the compute-frustum-cull capture in this same section, above) — a figure that
  also contains every translucent chunk draw and every LOD water sheet. That capture
  demonstrably *included* the color copy: the same line reports `reflect 0.83 ms`, and
  `RenderReflection` early-returns unless `_nearWaterVisible`
  (`RenderManager.cs:1081-1082`) — the very flag that gates the copy — so near water was on
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
  threads for the one the burst is already saturating. `WorldGenerationConfig.MaxConcurrentLodGenerations`
  ships at **1**. The capture is under **"Measurement: C5"** and the argument under **"The C5
  concurrency argument"**; **everything except the extra threads landed and stays** (see **What
  landed**). Only the pool size is ruled out, and re-proposing it needs a capture that answers this one.
