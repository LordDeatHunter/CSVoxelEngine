# QUICK_WINS — one-thing features

> Plug-n-play ideas: each entry is **one self-contained feature** you can start and finish in a
> sitting. Not a category, not a system, and nothing that drags in sub-features.
> The big roadmap lives in **TODO.md**, performance in **PERF_TODO.md**.

## What qualifies

- One sentence says what it is. If it needs "and also", split it or drop it.
- It reuses what exists (a block flag, an existing manager, an existing animation hook).
- No new station, no new protocol message, no new save file, no new shader pass unless that
  *is* the feature.
- It can be verified with one scenario or one small test.
- A new block, item or enemy is fine, as long as it is a single one that needs no machinery
  around it. (The kiln was not: multiblock, fuel, crucible, molds and casting.)

Mark done with `[x]` and a short note, or delete the line. Add a new idea as one line plus a
**Hook** (where it plugs in) so the next person can start at once.

Art a feature needs is generated as a placeholder (a deterministic script under `tools/`) and
listed in `USER_TODO.md` (local, never committed) for replacement. A feature that needs sound
files waits in TODO.md instead, since there are none to use yet.

## Ideas

_None open. Every earlier entry shipped and was removed; `git log -- QUICK_WINS.md` has the list._
