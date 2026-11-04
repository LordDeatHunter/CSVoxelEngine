# TREES_PLAN.md — tree generation & wood shape rework

## Status

All nine phases are done and removed from this file: content + codecs, the headless growth
engine, the cutover to engine-grown trees, cell batches + the LOD canopy proxy, leaf decay,
reactive growth, wide trunks + baobab/stone pine, the ItemDebugger TREES tab + docs, and shape
tuning. The design now lives in `AGENTS.md` (`### Trees`, the tree species schema); the history is
in `git log` on `tree-rework`. What is left is the residue below, tracked in `TODO.md`.

## Open work

- [ ] **`M-10` Felled-canopy markers only ever grow.** The interim fix landed (per-chunk-column
  bitsets in `TreeCanopy.CanopyDisturbed`, never cleared at a cap); what is left is that the set
  is edit state that only grows and `lod-edits.bin` lists it column by column. Real fix: derive
  the set from stored block data (entangled with save/load). Must NOT move into
  `GenerationalCache`.
- [ ] **`M-13` Far-LOD canopy for grown trees.** `PoisonCanopy` only removes canopy, so a grown
  tree shows no far canopy until reload. Needs an add-canopy override keyed by column
  (`TreeCanopy.CanopyAdded`, persisted in `lod-edits.bin`).
- [ ] **`M-14` Wide-trunk residue** (three independent items):
  - [ ] A felled tree's mesh is cut off at its one 16³ scratch chunk (`CellBatchMesher.Build` is a
    ready multi-chunk replacement).
  - [ ] Cutting a wide core while its shells stand strands the crown for reactive growth.
  - [x] A beveled shell's cap went stale when an edit landed in an edge-diagonal chunk:
    `ChunkCellCache.AddTouchedChunks` now dirties it too.

## Perf baseline

Measured with `visual-examples/tree-perf.json` (a fixed flight-sprint fly-through: forest, taiga,
desert, 12 s each after an 8 s settle) in Release:
`cd OpenGLTest && VOXEL_DEBUG=1 VOXEL_TIME=147 VOXEL_SCRIPT=visual-examples/tree-perf.json dotnet run -c Release`,
reading the console line's `wgen` token (`WorldgenStats`, published by `FrameProfiler`).

Machine: `Build: Release | debugger False | serverGC False | latency SustainedLowLatency | cpus 12`,
`GPU: Quadro T1000 with Max-Q Design/PCIe/SSE2 | NVIDIA Corporation | GL 4.6.0 NVIDIA 597.06`.

Columns: *chunks* is the lifetime `GenerateBlockData` count at exit (every chunk, the many
above-terrain early exits included, which is why ms/chunk is low); *ms/chunk* and *tree ms/chunk*
are lifetime totals over that count (tree = the tree + cactus stamp); *tree ms/s*, *grow ms/s* and
*misses/s* are medians of the once-a-second windows that generated any chunk (summed over the
generation workers, so ms/s can exceed 1000); *grows* is every shape build (tree + cactus cache
misses, on the generation workers and the LOD builder's canopy sweep alike; from phase 4 one entry
per tree grown, solo or in a cell batch); *misses/s* counts the solo tree shape cache only. From
phase 4 the token also carries `batch` (cell-batch cache misses), `dup` (batch builds started
while another thread was already building the same cell) and `brot` (batch cache generation
rotations), each per window.

| run | chunks | ms/chunk | tree ms/chunk | tree ms/s | grows | grow ms/tree | grow ms/s | misses/s |
|---|---|---|---|---|---|---|---|---|
| old 1 | 189247 | 0.66 | 0.124 | 435.8 | 133242 | 0.004 | 7.4 | 1604 |
| old 2 | 187378 | 0.70 | 0.131 | 464.7 | 123278 | 0.004 | 7.1 | 1602 |
| old 3 | 186988 | 0.71 | 0.135 | 484.2 | 117306 | 0.004 | 7.3 | 1542 |

Phase 3 result (same machine and command, after the cutover):

| run | chunks | ms/chunk | tree ms/chunk | tree ms/s | grows | grow ms/tree | grow ms/s | misses/s |
|---|---|---|---|---|---|---|---|---|
| new 1 | 189306 | 0.65 | 0.083 | 323.6 | 144773 | 0.288 | 721.2 | 2448 |
| new 2 | 189101 | 0.67 | 0.080 | 309.6 | 138187 | 0.296 | 704.7 | 2281 |
| new 3 | 188373 | 0.69 | 0.086 | 317.9 | 132462 | 0.313 | 767.5 | 2304 |

Overall ms/chunk is unchanged (0.65-0.69 vs 0.66-0.71) and the stamp itself got cheaper (tree
ms/s ~310-325 vs ~435-485), but growing a tree now costs ~0.3 ms instead of ~0.004 ms, so grow
time rose about 100x to ~700-770 ms/s with ~1.5x the shape-cache misses. The stamp's tree time
includes every grow the generation workers trigger, so at least ~400 ms/s of grow time (over half)
runs on the single LOD builder thread's far-ring canopy sweep: that is phase 4's LOD proxy target.

Phase 4 result (same machine and command, cell batches + the far-ring canopy proxy):

| run | chunks | ms/chunk | tree ms/chunk | tree ms/s | grows | grow ms/tree | grow ms/s | misses/s |
|---|---|---|---|---|---|---|---|---|
| p4 1 | 188917 | 0.61 | 0.048 | 11.0 | 115252 | 0.274 | 369.9 | 694 |
| p4 2 | 188338 | 0.68 | 0.060 | 12.1 | 113392 | 0.312 | 438.1 | 757 |
| p4 3 | 187786 | 0.70 | 0.061 | 12.8 | 111460 | 0.325 | 496.5 | 750 |

Batch cache per run: 1685 / 1673 / 1656 misses (median 24-26/s), `dup` 109 / 127 / 117
(6.5 / 7.6 / 7.1 % of the misses), `brot` 21 / 20 / 20. A measurement run before these three gave
the same picture (0.62 ms/chunk, 1694 misses, dup 6.9 %, brot 22).

Overall ms/chunk is unchanged (median 0.68 vs 0.67, range 0.61-0.70 vs 0.65-0.69), so the 25 %
rule did not trip. The expected doubling of grows did not happen. Every tree is still grown twice
on the near side, solo as a neighbor obstacle and again in its batch, but the far rings no longer
grow anything, and that more than pays for it: grows fell from 132-145k to 111-115k, solo
misses/s to about a third (694-757 vs 2281-2448) and grow ms/s from 705-768 to 370-497, while
grow ms/tree stays at ~0.3 ms (growing against more obstacles costs no more). The stamp itself is
now nearly free on the generation workers (tree ms/s 11-13 vs 310-325, tree ms/chunk 0.05-0.06 vs
0.08). The first LOD ring reads batches out to 512 blocks, so the LOD builder has usually grown a
cell's batch before a generation worker stamps it. Total grow time is 31.6-36.2 s per run against
only 9.1-11.5 s of total stamp time (which includes every grow the workers trigger), so at least
two thirds of the growing now runs on that one LOD thread. Duplicate batch builds stay
under the 10 % gate (they cluster in the cold-start windows, where all four workers open on the
same cells: the first window of each run reads 35-56 batch misses with 20-30 dup), so the per-key single-flight was **not**
added and the miss path stays lock-free. The rotations equal the misses divided by the 84-insert
generation budget (capacity 336 / G 4), which is ordinary turnover as the flight streams new
cells; nothing here separates re-misses from first misses, but the steady ~25 misses/s while
flying gives no sign of thrash. Measured batch size: forest ~21 trees and ~5250 cells (~82 KB) on
average, max ~250 KB; taiga ~79 trees and ~18400 cells (~288 KB), max ~350 KB. Capacity 336 bounds
an all-taiga cache at ~95 MB.

Phase 9 result (same machine and command, run with `dotnet run --no-build` after a Release build;
*before* is this branch at the end of phase 8, since phases 5-8 landed after the p4 table):

| run | chunks | ms/chunk | tree ms/chunk | tree ms/s | grows | grow ms/tree | grow ms/s | misses/s | batch/dup/brot |
|---|---|---|---|---|---|---|---|---|---|
| before 1 | 188362 | 0.64 | 0.058 | 10.6 | 116965 | 0.288 | 366.6 | 692 | 1711/136/22 |
| before 2 | 187923 | 0.66 | 0.052 | 14.1 | 113973 | 0.300 | 413.2 | 738 | 1682/113/21 |
| before 3 | 188898 | 0.69 | 0.062 | 12.4 | 112059 | 0.324 | 496.8 | 767 | 1661/117/20 |
| p9 1 | 189538 | 0.60 | 0.049 | 12.6 | 123075 | 0.269 | 363.3 | 687 | 1711/106/23 |
| p9 2 | 188076 | 0.64 | 0.056 | 13.6 | 118639 | 0.295 | 384.8 | 701 | 1698/127/21 |
| p9 3 | 187435 | 0.66 | 0.062 | 11.8 | 118203 | 0.314 | 425.4 | 767 | 1692/129/21 |

No measurable worldgen cost change: median ms/chunk 0.64 against 0.66 and median grow ms/tree
0.295 against 0.300, both far inside the 25 % gates (0.83 and 0.375). Mature crown radii and cell
counts are what they were (oak, spruce and stone pine reach their full `radius` on a full-cube
root), young and suppressed crowns got cheaper because `Collect` loops only over the scaled
radius, and the cactus's doubled cell count (about 19 cells a tree) and the umbrella's O(height)
trunk walks are lost in the noise; the stone pine and baobab are not worldgen species. Warm
in-process harness runs agree (oak 0.34 -> 0.33 ms/tree, spruce 0.12 -> 0.14, cactus about
0.09). Batch misses, `dup` and `brot` are unchanged; the one-step larger `ProxyReach` (4 -> 5)
widens the far rings' proxy sweep slightly and shows no cost in the totals above.
