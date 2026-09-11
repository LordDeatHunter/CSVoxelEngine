# Fluid tick performance (`fluidTick` ~100ms max)

Status: investigation complete, fixes not yet implemented. Work the checklist top to
bottom and record a before/after row in [Measurements](#measurements). See `PERF_TODO.md`
for the repository's general measurement protocol.

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

### [ ] 1. Cache chunk lookups within a tick (biggest win, low risk)

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

### [ ] 2. Early-out the slope search (free, do with item 1)

- In `ChooseSpreadDirections`, `break` once `best == 0` — no other direction can beat a
  hole directly under a neighbour.
- In `SlopeDistance`, a hit at `depth == 1` is the global minimum; stop the outer loop
  immediately rather than continuing to scan the remaining directions.

Zero semantic change; trims the worst case and lets item 1's win land on the remaining CPU.

### [ ] 3. Gate the mud scan (big win, low risk)

`TryConvertMudAround` (`:688-707`) scans 26 cells for **every** ticked water cell, then
`IsMudWet` scans up to 26 more. Add a cheap gate that preserves the outcome exactly: first
test only the **6 face neighbours** for the dirt id; only if one is dirt run the full
26-scan. The mud condition (dirt with water directly above and >=2 water in the
26-neighbourhood) can only be met adjacent to the ticked water, so nothing is missed. Open
ocean water stops paying ~26 lookups/tick.

With item 1 in place these lookups are already cheap, so measure item 1 first and only
reach for this if the mud scan still shows up.

### [ ] 4. Bound the per-frame work, not just the per-tick budget

`MaxFluidTicksPerFrame (4) x MaxFluidCellsPerTick (2048)` allows 8192 cells in one frame. A
shared per-frame cell counter that stops `Update`'s tick loop and carries the remainder into
`_next` (reusing the existing over-budget carry at `:296-305`) flattens the spike the same
way item 7 flattened `fluidWake`. Trades a longer settling tail for a flat frame cost.

Tuning-only alternative if you would rather not add carry logic (instant relief):
`MaxFluidTicksPerFrame 4 -> 2` and/or `MaxFluidCellsPerTick 2048 -> 1024`.

### [ ] 5. Cheapen the over-budget selection

While a storm drains, `Tick` does a full `List.Sort` of `_pending` **every** tick
(`:300-303`) — 50k, 48k, ... elements on consecutive ticks. Only the lowest `budget` cells
are needed; a quickselect or Y-bucket selection avoids O(n log n) on the whole set. This
becomes the visible cost once items 1-3 shrink per-cell work.

### [ ] 6. Hygiene (small, do last)

- Move `Inventory.Update()` out of the `FluidTick` span (`World.cs:284`) so the row is pure
  simulation.
- Replace `HashSet<Vector3>` dirty tracking (`_dirtyChunks`, `_borderScratch`) with packed
  `long` chunk keys — same float-hashing tax, just at a lower volume.
- Note: `Flow`'s extra `ComputeState` is for the **target** cell, not a strict duplicate of
  `TickCell`'s. The real redundancy is re-reading the same neighbours across helpers;
  folding state through would need an API change, so treat it as optional.

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
