# Quick wins: one-thing features

> Guide: plans/README.md

## Status

Plug-n-play ideas: each entry is **one self-contained feature** you can start and finish in a
sitting. Not a category, not a system, and nothing that drags in sub-features.
The big roadmap lives in `TODO.md`, performance in `PERF.md`.

## In progress

Nothing.

## Open

None open. Add an idea as `- [ ] **`Q-n` Title** - P#, S.` plus a **Hook** (where it plugs in).

## Reference

### What qualifies

- One sentence says what it is. If it needs "and also", split it or drop it.
- It reuses what exists (a block flag, an existing manager, an existing animation hook).
- No new station, no new protocol message, no new save file, no new shader pass unless that
  *is* the feature.
- It can be verified with one scenario or one small test.
- A new block, item or enemy is fine, as long as it is a single one that needs no machinery
  around it. (The kiln was not: multiblock, fuel, crucible, molds and casting.)

Art a feature needs is generated as a placeholder (a deterministic script under `tools/`) and
listed in `USER_TODO.md` at the repo root for replacement. A feature that needs sound files waits
in TODO.md instead, since there are none to use yet.


