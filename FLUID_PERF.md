# Fluid tick performance (`fluidTick` ~100ms max)

Status: items 1, 2, 4, 5 and 6 are implemented; item 3 is deferred. A later regression
(`d1fa6db`'s per-tick cap made fluid appear frozen) forced the item-4 bound to be redesigned
as a per-frame spread. Work the checklist top to bottom and record a before/after row in
[Measurements](#measurements). See `PERF_TODO.md` for the repository's general measurement
protocol.

## Symptom

`fluidTick` in the `[X]` debug overlay shows a **max near 100 ms**, while the average is
typically far lower. `FrameProfiler` (`OpenGLTest/src/FrameProfiler.cs`) tracks both an
average and a max per section over the 1-second publish window, so the number is the
worst single frame, not a steady cost. One fluid tick runs every
`FluidTickSeconds = 0.25 s` (~every 15 frames at 60 FPS), so a single expensive tick
lands entirely in one frame and dominates its max.

## What the row actually measures

`OpenGLTest/src/World.cs:278-285` (`World.Update`):

```csharp
FrameProfiler.Begin(ProfileSection.FluidTick);
_fluidManager.Update((float)deltaTime, _chunkManager, _dropManager);
Inventory.Update((float)deltaTime);          // unrelated cost attributed to fluids
FrameProfiler.End(ProfileSection.FluidTick);
```

- The row includes `Inventory.Update()`. Split it out (checklist item 6) before trusting
  the number to the tenth of a millisecond; it is small, but it is not fluid simulation.
- `FluidWake` (`WakeLoadedChunks`) is a **separate** row. It was measured at <=0.01 ms in
  `PERF_TODO.md` item 7, so the scan itself is not the spike — but it seeds the tick with a
  whole sea chunk's frontier at once, which is what creates the expensive ticks below.

## Why the spike happens

### Tick budget: one frame can do 4x work

`FluidManager.cs:236-251` (`Update`):

- One tick every `FluidTickSeconds = 0.25 s`, up to `MaxFluidTicksPerFrame = 4` ticks per
  frame after a hitch (leftover time is dropped, but the burst still runs 4 ticks).
- Each `Tick` (`:288-315`) processes up to `MaxFluidCellsPerTick = 2048` cells; an
  over-budget tick copies and `Sort()`s `_tickOrder` lowest-Y-first (`:300-303`).

Worst single frame: **4 ticks x 2048 cells = 8192 `TickCell` calls**, plus a large sort.
The cap's own comment (`WorldGenerationConfig.cs:184-187`) says it bounds a wake-up storm
"to a flat per-tick cost rather than one 100ms+ spike" — the observed 100 ms says that
flat cost is still too high.

### Per-cell cost: dictionary lookups dominate

Every cell read goes through `ChunkManager.TryGetCell` (`ChunkManager.cs:508-524`), which
is `ChunkAt` + two array reads. `ChunkAt` (`:497-501`) builds a
`new Vector3(x >> 4, y >> 4, z >> 4)` and does a
`ConcurrentDictionary<Vector3, Chunk>.TryGetValue` — float-vector hashing plus
concurrency overhead, **per cell read**.

Approximate `ConcurrentDictionary` lookups per active water cell
(`FluidManager.TickCell`, `:318-371`):

| Step | Lookups |
|---|---|
| Initial `TryGetCell` | 1 |
| `TryConvertMudAround` (`:688-707`) runs on **every** ticked water cell: full 26-neighbourhood scan, and `IsMudWet` (`:713-729`) scans up to 26 more per dirt neighbour | ~27-50+ |
| `ComputeState` for non-sources (`:380-414`): 4 horizontal + below + above | ~6 |
| `Flow` (`:534-576`) re-runs `ComputeState` on the target cell | ~6 |
| `TouchesFluid` (`:626-635`), `HardenedByNeighbour` (`:642-666`), `SourceNeighbourCount` (`:669-680`), `IsWaterHole`, `CanPassThrough` — ~6 lookups each, several fire per cell | ~12-24 |
| `Spread` -> `SpreadSides` -> `ChooseSpreadDirections` -> recursive `SlopeDistance` (`:505-528`) | up to ~900-1000 |
| `Write` (`:584-591`) `ChunkAt` + `HashSet<Vector3>` touch tracking (`AddTouchedChunks`, `:759-778`); `ScheduleAround` (`:86-95`) adds 7 entries to `_next` per write | dict + hash churn |

The slope term is the worst and was previously under-counted:

- `SlopeDistance(x, y, z, 1, exclude)` iterates the 4 horizontals but skips the direction
  it came from, so each node branches **3 ways**, recursing while `depth < 4` (depths
  1..4). Node count = `1 + 3 + 9 + 27 = 40`. Each node iterates 3 directions and does
  `CanPassThrough` (1 lookup) plus, when passable, `IsWaterHole` on the cell below (1
  more) = up to **240 lookups per `SlopeDistance` call**.
- `ChooseSpreadDirections` calls it for **each of the 4 horizontal directions** (unless
  the neighbour is a hole directly), so one spreading cell can hit **~900-1000 lookups**.
- The "no flowable neighbour" fast path at `:448-452` only spares settled cells.

Back of envelope: `4 ticks x 2048 cells x ~1000 lookups` is ~8M concurrent-dict lookups.
At ~10-15 ns each that is tens of ms before the depth-4 CPU recursion; at ~30-40 ns it is
~100 ms. `_dirtyChunks` / `_borderScratch` are `HashSet<Vector3>` and pay the same
float-hashing tax, and `DropManager.SpawnLoot` + `PileCount` + `UnmarkPlayerPlaced` per
washed-away fragile block add more.

### When the expensive path actually fires

The deep search does **not** fire in a full pool. `IsWaterHole(nx, ny-1, nz)` returns true
as soon as the cell under a neighbour is the same fluid, so inside a body of water each
direction short-circuits to distance 0. The full ~1000-lookup search fires only where
`CanPassThrough` succeeds but there is no hole below — i.e. **the spreading leading edge
over flat/solid ground**: a draining pond, a breached sea fanning across a flat cave floor,
a player-placed source sheet. That is why the symptom is a max spike with a low average.

Burst sources that fill `_pending` fast: chunk-load wake frontier (`WakeLoadedChunks` +
`Chunk.FluidFrontier` + `WakeBorders`), TNT `ScheduleBox` over a blast volume, bucket
`SetCellImmediate` + `ScheduleAround`, and any edit path calling `ScheduleAround` (each
adds 7 cells, each write reschedules 7 more).

## Why Minecraft / Vintage Story feel smooth

- **No dictionary per cell.** Both index chunk sections/arrays directly. The rules here
  mirror MC (`getNewLiquid`, `getSpread`, slope distance 4) but MC's reads are ~ns array
  accesses; ours are concurrent hash lookups on `Vector3`.
- **No piggyback simulation in the hot loop.** The mud conversion on every water tick has
  no MC equivalent in the fluid path — settled water keeps paying ~30 lookups/tick.
- **Smaller scheduling quanta.** MC spreads scheduled block ticks across game ticks; here
  4 full 2048-cell ticks can land in one frame, so streaming bursts, blasts, or a draining
  pond show up as max-frame spikes while avg looks fine.

The algorithm is fine — the per-read and per-frame costs are the problem.

## Fix plan

Work top to bottom. Items 1-3 are safe and independent; item 4 is a bounded-work safety
valve; items 5-6 are cleanup. Re-measure after each (see [Measurements](#measurements)).

Implemented state: **1, 2, 4 (redesigned), 5 (obsolete) and 6 are done; 3 is deferred.**
The line numbers quoted below are from the original investigation and have since moved.

### [x] 1. Cache chunk lookups within a tick (biggest win, low risk)

Cut the dictionary tax at the source instead of chasing each call site. Add to
`FluidManager`:

- `private Chunk?[] _chunkCache = new Chunk?[16];` plus a parallel key/valid array, cleared
  at the top of `Tick`.
- A private `bool TryGetCell(int x, int y, int z, out byte id, out byte state)` that packs
  the **chunk** coords (`x >> Chunk.SizeShift` etc.; reuse `Pack`), probes a direct-mapped
  slot (e.g. `(int)(key ^ (key >> 32)) & 15`), and on a miss falls back to
  `Chunks.TryGetValue(new Vector3(cx, cy, cz), ...) && chunk.IsBlockDataReady`, storing the
  `Chunk` reference.
- Route the fluid helpers through it: make the `chunks`-taking helpers instance methods (or
  pass a tiny reader struct) and replace `chunks.TryGetCell` with the cache-aware call. Set
  a `ChunkManager _chunks` field at the top of `Update`. Leave `WakeLoadedChunks` on its
  direct `chunk.GetLocalBlock` reads (it already avoids the dictionary).

A slope search spans at most ~2 chunks horizontally, so 16 slots is ample; most reads hit
the same chunk and become an array index + key compare.

**Correctness traps:**

- Cache the **`Chunk` reference, never its `_blocks`/`_state` arrays** — `SetBlockRaw`
  allocates and swaps in `_state` on the first non-zero write (`Chunk.cs:662-689`), so a
  cached array pointer would miss later fluid state writes.
- Reset the cache every `Tick`; chunk unload happens earlier the same frame in
  `ChunkManager.Update`, so one-tick lifetime keeps that assumption local.
- Preserve the `IsBlockDataReady` check on the miss path (tests insert a chunk with the
  flag set directly, e.g. `FluidTests.FullFloorBasin`).

Expected: the ~1000 lookups/cell collapse to cache probes plus a few misses — the spike
should drop several-fold on its own.

**Done** (`FluidManager.TryGetChunk`/`TryGetCell`, 16-slot direct-mapped cache, cleared per
frame). Measured: it did **not** reduce the spike on its own, and cache size made no
difference (16 vs 256 slots) — so the "biggest win" premise here was wrong and the per-cell
cost is not dictionary lookups.

### [x] 2. Early-out the slope search (free, do with item 1)

- In `ChooseSpreadDirections`, `break` once `best == 0` — no other direction can beat a
  hole directly under a neighbour.
- In `SlopeDistance`, a hit at `depth == 1` is the global minimum; stop the outer loop
  immediately rather than continuing to scan the remaining directions.

Zero semantic change; trims the worst case and lets item 1's win land on the remaining CPU.

**Done**, but not as literally worded: `break` on `best == 0` would drop *tied* directions
that also have a hole directly beneath (Minecraft's `getSpread` spreads to every minimal
direction), and a `depth == 1` hit already returned immediately. Implemented
result-preserving early-outs instead — skip the deep search once `best == 0` while still
adding other direct holes, and truncate `SlopeDistance` once `best == depth + 1`.

### [ ] 3. Gate the mud scan (big win, low risk)

`TryConvertMudAround` (`:688-707`) scans 26 cells for **every** ticked water cell, then
`IsMudWet` scans up to 26 more. Add a cheap gate that preserves the outcome exactly: first
test only the **6 face neighbours** for the dirt id; only if one is dirt run the full
26-scan. The mud condition (dirt with water directly above and >=2 water in the
26-neighbourhood) can only be met adjacent to the ticked water, so nothing is missed. Open
ocean water stops paying ~26 lookups/tick.

With item 1 in place these lookups are already cheap, so measure item 1 first and only
reach for this if the mud scan still shows up.

**Deferred.** The 6-face gate is not exact as written: a dirt cell diagonally adjacent to
the ticked water can be wet (water directly above it plus two water neighbours) with no
face-adjacent dirt, so the gate would skip a conversion the 26-scan performs. It is only
outcome-equivalent under a load-bearing invariant that the water directly above any wet dirt
is always scheduled (every edit that creates it calls `ScheduleAround`). Not implemented.

### [x] 4. Bound the per-frame work, not just the per-tick budget

`MaxFluidTicksPerFrame (4) x MaxFluidCellsPerTick (2048)` allows 8192 cells in one frame. A
shared per-frame cell counter that stops `Update`'s tick loop and carries the remainder into
`_next` (reusing the existing over-budget carry at `:296-305`) flattens the spike the same
way item 7 flattened `fluidWake`. Trades a longer settling tail for a flat frame cost.

Tuning-only alternative if you would rather not add carry logic (instant relief):
`MaxFluidTicksPerFrame 4 -> 2` and/or `MaxFluidCellsPerTick 2048 -> 1024`.

**Done (redesigned).** A per-tick cap alone cannot work: `WakeLoadedChunks` schedules every
frame (~60/s) while a tick runs every `FluidTickSeconds` (~4/s), so a per-tick cap leaves a
runaway queue and the fluid appears frozen. `Update` now starts a tick at most every
`FluidTickSeconds` and processes its cells in slices of `MaxFluidCellsPerFrame` under a
shared per-frame cell counter, while `WakeLoadedChunks` stops scheduling once `_next` reaches
`MaxFluidBacklogCells`. Per-frame work is bounded *and* the queue drains.
`MaxFluidCellsPerTick` was renamed `MaxFluidCellsPerFrame` (no per-tick cap remains).

### [x] 5. Cheapen the over-budget selection

While a storm drains, `Tick` does a full `List.Sort` of `_pending` **every** tick
(`:300-303`) — 50k, 48k, ... elements on consecutive ticks. Only the lowest `budget` cells
are needed; a quickselect or Y-bucket selection avoids O(n log n) on the whole set. This
becomes the visible cost once items 1-3 shrink per-cell work.

**Obsolete, resolved by item 4.** The per-tick `_tickOrder` sort and over-budget carry are
gone: a tick now snapshots its scheduled set and walks it across frames, so there is no
whole-set sort to cheapen. The `d1fa6db` lowest-Y sort is removed too (pre-`d1fa6db` iterated
the scheduled set directly); measuring confirmed it was not the spike.

### [x] 6. Hygiene (small, do last)

- Move `Inventory.Update()` out of the `FluidTick` span (`World.cs:284`) so the row is pure
  simulation.
- Replace `HashSet<Vector3>` dirty tracking (`_dirtyChunks`, `_borderScratch`) with packed
  `long` chunk keys — same float-hashing tax, just at a lower volume.
- Note: `Flow`'s extra `ComputeState` is for the **target** cell, not a strict duplicate of
  `TickCell`'s. The real redundancy is re-reading the same neighbours across helpers;
  folding state through would need an API change, so treat it as optional.

**Done.** `Inventory.Update()` moved out of the `FluidTick` span in `World.Update`, and
`_dirtyChunks`/`_borderScratch` are now `HashSet<long>` of packed chunk keys (unpacked only
when handing `MarkDirty` a `Vector3`).

## Diagnosing your spike

- Run with `VOXEL_DEBUG=1`: the console logs per-section avg + `gc` collections/s once a
  second. Check whether `fluidTick` **avg** is high (steady-state: a large active ocean/pond
  nearby) or only **max** is high (burst).
- Note what you were doing when max spikes: fresh spawn / fast flight over sea (wake
  frontier), an explosion (`ScheduleBox`), or standing by water (settled-water mud + slope
  cost). Bursts favor item 4; steady-state favors items 1-3.

## Verification

- `dotnet test OpenGLTest.Tests/silkgltriangle.Tests.csproj -c Release` — `FluidTests`
  (source spread/fall/wake cap), `LavaTests` (hardening + tick divisor) and `MudTests`
  (conversion) are the regression net; add a case if a change alters scheduling.
- Scripted Release run over the shallows (`VOXEL_SPAWN` in a sea, `VOXEL_TIME=70`,
  `VOXEL_DEBUG=1`), comparing `fluidTick` **max** and avg in the `perf` lines before/after.
  Run the existing `visual-examples/water.json` and diff screenshots for no visual change.
- This codebase has no CI, so interleave a HEAD worktree control (`git worktree`) exactly as
  `PERF_TODO.md` items 5/7/11 do — fluid timing swings with GPU clocks and streaming state.

## Measurements

Add a row per attempt, before/after, following `PERF_TODO.md` conventions.

| Item | Build | View/scenario | fluidTick avg | fluidTick max | Notes |
|---|---|---|---|---|---|
| baseline | Release | shallows, `VOXEL_TIME=70` | | ~100 ms (reported) | user report, 2026-09-11 |
| 1+2 (cache + early-outs) | Release | bay, `VOXEL_TIME=70`, ~35 s | 0.62 ms | 29.2 ms | vs HEAD worktree 0.58 / 34.1 ms — within noise; cache 16 vs 256 slots identical |
| 1+2, over-budget sort removed (diagnostic only) | Release | bay, same | 0.34 ms | 16.3 ms | sort was the dominant cost, not the dictionary; never shipped as-is |
| 1+2+4 (per-frame spread) | Release | bay, same | 0.55–0.66 ms | typical 11–16 ms, one ~41–48 ms loading outlier | queue 100–196k → 8–12k; total fluid writes 51k → 202k (4x). Item 6 is hygiene-only, not separately measured |

Environment: Release, Quadro T1000 (GL 4.6), 12 cpus, `SustainedLowLatency` GC. Values are
one run each on a fast machine and fluid timing swings with GPU clocks and streaming state;
they are indicative, not a benchmark. The max column is the worst frame in a 1 s window
(console line temporarily extended with `~max` to read it).
