---
name: perf-capture
description: How to measure CSVoxelEngine framerate and frame-time honestly - Release only, the X profiler overlay, interleaved before/after runs in a git worktree, temporary diagnostic counters. Use for any performance work or claim.
---

# Performance capture

Tracking lives in repo-root `PERF_TODO.md` (ranked items with their measurements) and `TODO.md`. Read the next open item before starting.

## Ground rules
- **Release only.** Debug is several times slower in the voxel loops and says nothing. `Directory.Build.props` defaults project builds to Release, but a solution build needs `-c Release`. The startup line `Build: Release | ...` confirms it.
- The knobs are in the per-domain config classes under `OpenGLTest/src/Config/` (budgets and thread counts in `StreamingConfig`, worldgen cache capacities and generation counts in `WorldGenerationConfig`). **Do not move a re-pricing lever (cache capacities, generation counts, budgets) blind**; only against a real capture.
- Wrap new work in `FrameProfiler.Begin/End` (main thread only) rather than a private stopwatch.
- Run-to-run swing at rest is ~0.4 ms and every CPU section moves together, so **compare interleaved pairs, never single runs**.

## What to read
`X` (or `VOXEL_DEBUG=1`, which also works in scripted runs, where pressing X never does) prints the profiler snapshot once a second: frame ms, FPS, CPU sum vs GPU `TIME_ELAPSED`, GC collections/s, loaded/visible chunks, one row per section (`chunk*`, `lod`, `entities`, `fluidTick`, `lighting`, `shadowNear`, `shadowFar`, `reflect`, `worldPass`, `aoPass`, `waterPass`, `postPass`, `uiPass`, ...) as avg/worst ms. Notes: `other` is unclaimed wall time; `shadowNear` + `shadowFar` is the whole shadow pass, and either one's GPU suffix can be dropped on a line independently (`shadowFar` more often), so sum them only over lines where both report; GPU passes that do not run every frame (`selectPass`) can read `--`; the overlay's own glyphs land in `uiPass`.
Worker-thread costs (meshing, worldgen) never show in the per-frame profiler.

## Before/after recipe
1. `git worktree add --detach <scratchpad>/before HEAD`, build it Release.
2. Build your change Release.
3. Run **both** exes the same way, interleaved, so they share GPU clock state:
   `Start-Process <root>/OpenGLTest/bin/Release/net9.0/VoxelEngine.exe -WorkingDirectory <root>/OpenGLTest` with `VOXEL_SPAWN=0,30,0 VOXEL_TIME=60 VOXEL_LOOK=...` (spawn high so the player lands on loaded terrain), stdout redirected to a file.
4. Wait until `chunks N/M` stops changing (~20 s), then read the last 3 `perf` lines. For a fly capture, use a scenario with `fly` (`perf-fly.json`, `tree-perf.json`, `lod-horizon.json` exist) rather than injected keys.
5. Report both numbers per pair plus GPU/CPU ms, GC/s and frame ms. Record the numbers on the item in `PERF_TODO.md`.
6. `git worktree remove --force <scratchpad>/before` afterwards.

## Allocation / worker-side numbers
Add temporary counters tagged `// TEMP-DIAG` (build ticks, `GC.GetAllocatedBytesForCurrentThread` around a build, pool misses) to the debug line, read them from the log, then strip them: `grep -rn TEMP-DIAG OpenGLTest/src` must be empty and the solution rebuilt before finishing.

## Report
State the hardware context (GPU), build config, scene (spawn/look/time), pair counts and spread. Say plainly when a change is within noise or regressed (as the reverted early-Z split did).
