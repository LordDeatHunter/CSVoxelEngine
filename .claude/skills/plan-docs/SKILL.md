---
name: plan-docs
description: Layout, legend and upkeep rules for the plan markdowns under plans/ (TODO, PERF, BIOMEGEN, CLEANUP, TREES, QUICK_WINS) in CSVoxelEngine - the standard Status / In progress / Open / Reference sections, checkbox markers, ID and priority legend, and the delete-when-done rule. Use whenever you create, edit, tick off, move or prune a plan file or add an item to one.
---

# Plan documents

Every plan lives in `plans/` (repo root). `plans/README.md` is the human-facing guide and the file
index; this skill is the procedure. Plan files never carry their own guide: each starts with one
line, `> Guide: plans/README.md`.

## Layout (every plan file, in this order)

```
# <Title>
> Guide: plans/README.md

## Status        2-4 lines: what the file tracks, last updated (absolute date, never "today")
## In progress   `- [~] **`ID` Title** - P#, effort. one-line note`   (or "Nothing.")
## Open          `- [ ] **`ID` Title** - P#, effort. what / **Hook:** where it plugs in`
                 grouped under `###` subheadings; sub-tasks are nested `- [ ]` lines
## Reference     facts that are not tasks: baselines, traps, process notes, "Not chasing",
                 "Ruled out", a handoff map (BIOMEGEN sections 1-11)
```

No "Done" section. Sections may be omitted only when empty and the file is a pure reference.

## Markers

- `- [ ]` open, `- [~]` in progress (at most one or two per file), `- [x]` finished.
- **A finished entry is deleted, not kept.** Tick it `[x]` while you work, and remove the line in
  the same change or the next pass. `git log -- plans/<file>` is the record.
- Before deleting, extract anything a future reader still needs to its durable home:
  - design or invariant -> the `docs/agents/` file that owns the feature (index in `AGENTS.md`),
  - procedure or trap -> the relevant `.claude/skills/*/SKILL.md`,
  - a measurement, a ruled-out idea or a caveat -> the plan's own `## Reference`.
  Nothing noteworthy means plain deletion. Never leave "done, see git" prose behind.
- A half-done entry stays open with its remaining work rewritten as the checklist; do not tick a
  parent while a nested `- [ ]` remains.

## Legend

- IDs are stable references (`P-4`, `U-3`, `M-10`, `F9`, `G3`): keep them when reordering, never
  reuse a deleted one. Source comments cite some of them, so do not renumber.
- Priority: **P0** foundation, do first · **P1** next · **P2** later · **P3** someday/XL.
- Effort: **S** under a session · **M** a few sessions · **L** a week+ · **XL** weeks.
- Every number names its machine and date; an unmeasured win is labeled *estimated*.

## Adding an entry

One bold title with ID, priority and effort, then a sentence or two of what and why, and a **Hook**
(file or type where it plugs in) so the next person can start at once. Perf entries also carry
**Files**, **Win**, **Verify** and **Invariant** (see `perf-capture`). A new idea that is one
self-contained sitting goes in `plans/QUICK_WINS.md`; anything larger goes in `plans/TODO.md`.

## Upkeep

- The working tree is CRLF: edit with the Edit tool, never `sed -i` (see `crlf-safe-edit`).
- Moving or renaming a plan means updating every path mention: `AGENTS.md`, the skills, the
  workflows under `.claude/workflows/` and the memory index. Grep for the old name afterward.
- `plans/CLEANUP.md` is read by the `cleanup-campaign` workflow, which expects its
  "In progress" and "Open" headings; keep them when editing it.
- American English, as everywhere in the repo.
