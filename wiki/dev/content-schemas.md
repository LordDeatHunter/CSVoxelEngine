# Content schemas

Ids are `ushort`s: id 0 is air, then blocks sorted by file name, then items sorted by file name. Blocks and items share up to 65,535 ids and the registry throws on overflow. The per-block state stays a byte. Every content error names the file it came from. Files tolerate comments and trailing commas.

## Block (`assets/data/blocks/<name>.json`)

Only `model` is required. Commonly used fields:

| Field | Meaning |
|---|---|
| `model` | A `"block/cube_all"`-style path, or an inline model object |
| `name` | Display name (defaults to the file name) |
| `hardness` | Damage the block absorbs before breaking (default 5; 0 breaks on first contact) |
| `requiresTool` + `tools` | The tool tag and minimum tier needed, e.g. `{"pickaxe": 1.0}` |
| `solid`, `friction`, `movementSpeed` | Collision and movement feel |
| `light.emission`, `light.color` | Block light 0 to 15 and its color |
| `cutout`, `translucent`, `occludes`, `sway` | Rendering behavior |
| `biomeTint` | Color the block by climate (grass or foliage palette) |
| `renderer` | `model` (default), `tree_wood`, `pebbles`, `branch_pile`, `snow_layers`, `fluid`, `quern`, `multiblock`, `multiblock_part`, `pane` |
| `facing`, `open`, `upperHalf`, `wallBlock` | Rotation, openable and two-cell blocks |
| `loot`, `onBreak`, `sfx` | Drops, break action and sound group |

When a block must behave differently, add a **data flag** that call sites read, instead of another per-id branch in code.

## Models (`assets/models/block/<name>.json`)

Minecraft Java block-model format: `parent` (chained), `textures` with `#var` references, and `elements` with `from` and `to` in 0 to 16, optional `rotation`, and `faces` with `uv`, `texture`, `cullface`. The four built-in parents match Minecraft byte for byte: `cube`, `cube_all`, `cube_column`, `cube_bottom_top`. Faces name the engine's directions: north is -Z, south +Z, west -X, east +X.

## Item (`assets/data/items/<name>.json`)

`name` and `texture` are all a plain pickup needs. Optional: `use` (right-click action), `display` (held pose), `blockDamage`, `entityDamage`, `attack` (`punch`, `slice`, `sword`), `tool`, `fuel`, `crucible`, `staff`, `teaches`, `throwDamage`.

## Recipes (`assets/data/recipes/...`)

| Kind | Location | Shape |
|---|---|---|
| Craft | `recipes/<result>.json` | `{ result, count, ingredients: [{ item or tag, count }] }` |
| Cook | `recipes/cook/<input>.json` | `{ input, result, count, cookSeconds }` |
| Crush | `recipes/crush/<input>.json` | `{ input, result, count }` |
| Melt | `recipes/melt/<input>.json` | `{ input, fluid, amountMl }` |
| Cast | `recipes/cast/<result>.json` | `{ mold, fluid, amountMl, coolSeconds, result, count }` |
| Alloy | `recipes/alloy/<name>.json` | `{ result, parts: [{ fluid, ml }] }` |

Names resolve like loot entries, with `block/` and `item/` prefixes pinning the namespace. Craft files are named by result, cook files by input.

## Loot (`assets/data/loot/<name>.json`)

`pools` with `rolls`, `chance` and `entries` (`item` with `weight` and `count`, or `empty`). Every destruction path (mining, blasts, felling, decay) rolls through one place, so a block's yield never depends on how it came down.

## Entities (`assets/data/entities/<name>.json`)

`name`, `hp`, optional `loot`, and an optional `model` (or `variants`). The Blockbench `.bbmodel` carries the rig and its animation clips. Rest rotations on groups and cubes are rejected; entities animate from their clips.

## Tree species (`assets/data/trees/<species>.json`)

`wood`, `leaves`, `growth` (logic, signal energy, up probability, maturity steps), `thickness` (pipe model), `leafCluster`, `features` (roots, buttress), `lod` (far crown proxy) and an optional `worldgen` block with the placement gates. Trees are grown by a deterministic step-based engine and stored as plain blocks plus a state byte.

## Spell parts and status effects

Spell parts live in `assets/data/spell/<kind>/<name>.json` with `costShare`, `complexity`, `tags`, plus per-kind fields (`effect`, `impact`, `delivery`, `damageFactor`, `rangeFactor`). Status effects live in `assets/data/effects/<name>.json` with `durationSeconds`, damage over time, speed factors and block actions. A new part or effect is a JSON file, never a `switch` on its name.
