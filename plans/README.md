# Plans

The one guide for every file in this folder. Agents: the `plan-docs` skill
(`.claude/skills/plan-docs/SKILL.md`) is the same rules as a procedure.

## Files

| File | Tracks |
|---|---|
| [TODO.md](TODO.md) | Gameplay and content roadmap (progression, fun, UI, visuals, foundations) |
| [PERF.md](PERF.md) | Frame time, worldgen, meshing and streaming performance, with baselines |
| [BIOMEGEN.md](BIOMEGEN.md) | Biome, climate and terrain-shape generation: handoff map and remaining work |
| [CLEANUP.md](CLEANUP.md) | The code cleanup campaign the `cleanup-campaign` workflow runs |
| [MAGIC.md](MAGIC.md) | The magic system (Arc A): mana, staffs, the station-built spell builder |
| [LAYERS.md](LAYERS.md) | Hell (below) and Sky (above) layers: all shipped, the decisions and future ideas |
| [SANDWORM.md](SANDWORM.md) | The summoned desert sandworm boss: shipped, with the balance and polish follow-ups |
| [WAYSTONES.md](WAYSTONES.md) | Natural waystones next to ruins, teleported between for mana: shipped, the decisions and future ideas |
| [BATTLE_TOWER.md](BATTLE_TOWER.md) | The battle tower: skeleton floors, a roof altar, the Skeleton King boss and the collapse into a ruin: shipped, the decisions, reference and costs |
| [CAVE_BIOMES.md](CAVE_BIOMES.md) | 3D cave biomes and the barrier shift that makes room for them (barrier shift and the cave biome field built; shape, ores, atmosphere and mobs designed) |
| [TREES.md](TREES.md) | Residue of the tree rework |
| [INVENTORY.md](INVENTORY.md) | Gear, chests and ruin loot (designed; U-1 kit and tooling done) |
| [INVENTORY_UI.md](INVENTORY_UI.md) | Visual and UX cleanup of the list-based inventory UIs (designed; U-1 kit and tooling done) |
| [QUICK_WINS.md](QUICK_WINS.md) | One-sitting features |
| [VISUAL.md](VISUAL.md) | Particles, animations and other visual polish |

Architecture, content schema and threading rules live in `AGENTS.md`, not here.

## Layout of a plan file

`Status` (what it tracks, last updated) -> `In progress` -> `Open` (grouped, checkboxes) ->
`Reference` (baselines, traps, process notes, ruled-out ideas). There is no Done section.

## Markers

- `[ ]` open · `[~]` in progress · `[x]` finished.
- A finished item is deleted, not archived; git history is the record. If it taught something
  worth keeping, move that to `AGENTS.md`, a skill or the file's `Reference` first.
- A parent stays unticked while any nested `[ ]` remains.

## Legend

- **IDs** (`P-4`, `U-3`, `M-10`, `F9`, `G3`) are stable: keep them when reordering, never reuse one.
- **Priority**: **P0** foundation, do first · **P1** next · **P2** later · **P3** someday/XL.
- **Effort**: **S** under a session · **M** a few sessions · **L** a week+ · **XL** weeks.
- Numbers name their machine and date; an unmeasured win is *estimated*.

## Adding an item

`- [ ] **`ID` Title** - P#, effort.` then what and why, and a **Hook** (where it plugs in). A
self-contained one-sitting idea goes in `QUICK_WINS.md`, anything bigger in `TODO.md`. Perf items add
**Files**, **Win**, **Verify** and **Invariant**.
