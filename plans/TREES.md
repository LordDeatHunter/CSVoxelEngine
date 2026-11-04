# Trees: rework residue

> Guide: plans/README.md

## Status

The nine-phase tree rework is finished and its design lives in `AGENTS.md` (`### Trees`, the tree
species schema); the history is in `git log` on `tree-rework`. Only the residue below is left, and
`TODO.md` mirrors it. Last updated 2026-11-05.

## In progress

Nothing.

## Open

- [ ] **`M-10` Felled-canopy markers only ever grow.** The interim fix landed (per-chunk-column
  bitsets in `TreeCanopy.CanopyDisturbed`, never cleared at a cap); what is left is that the set
  is edit state that only grows and `lod-edits.bin` lists it column by column. Real fix: derive
  the set from stored block data (entangled with save/load). Must NOT move into
  `GenerationalCache`.
- [ ] **`M-14` Wide-trunk residue.**
  - [ ] Cutting a wide core while its shells stand strands the crown for reactive growth.

## Reference

### Worldgen cost baseline

Fixed flight-sprint fly-through `visual-examples/tree-perf.json` (forest, taiga, desert, 12 s each
after an 8 s settle), Release:
`cd OpenGLTest && VOXEL_DEBUG=1 VOXEL_TIME=147 VOXEL_SCRIPT=visual-examples/tree-perf.json dotnet run -c Release`,
reading the console line's `wgen` token (`WorldgenStats`, published by `FrameProfiler`).
Machine: 12 CPUs, Quadro T1000 Max-Q, GL 4.6 NVIDIA 597.06.

Last full capture (tree rework phase 9, median of three runs): **0.64 ms per generated chunk**
overall, **0.295 ms to grow one tree**, the tree stamp itself ~12 ms/s on the generation workers
(the first LOD ring reads cell batches out to 512 blocks, so at least two thirds of the growing
runs on the one LOD builder thread). Gate used for tree changes: no more than 25 % worse on
ms/chunk or grow ms/tree.

- Cell batches (64x64 XZ) are the cache unit: ~25 misses/s while flying, rotations equal misses
  divided by the 84-insert generation budget (capacity 336 / 4 generations), which is ordinary
  turnover. Forest batch ~21 trees, ~5250 cells (~82 KB); taiga ~79 trees, ~18400 cells (~288 KB);
  capacity 336 bounds an all-taiga cache near 95 MB.
- Duplicate batch builds (two threads on the same cell) were 6-8 % of misses, clustered in the
  cold-start windows. Under the 10 % gate, so a per-key single-flight was **not** added and the
  miss path stays lock-free; revisit only if `dup` passes 10 %.
- Every tree is grown twice on the near side (solo as a neighbor obstacle, again in its batch);
  the far rings grow nothing (crown proxy), which more than pays for it.
