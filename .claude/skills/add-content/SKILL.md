---
name: add-content
description: Recipe for adding data-driven content to CSVoxelEngine - a block, item, loot table, model, entity, tree species, molten material or recipe - as JSON rather than C#. Includes the id-cap check, known traps and which tests to extend.
---

# Adding content (JSON, not C#)

AGENTS.md ("Content: blocks, items, models and loot are data") holds the full schemas. This skill is the procedure and the traps. Read the relevant schema table there before writing a file.

## Where files go
| Thing | Path |
|---|---|
| block | `OpenGLTest/assets/data/blocks/<name>.json` (file name = id) |
| item | `assets/data/items/<name>.json` |
| loot | `assets/data/loot/<name>.json` (a block uses the table sharing its name unless `loot` says otherwise) |
| model | `assets/models/block/<name>.json` (Minecraft Java format; 4 built-in parents: cube, cube_all, cube_column, cube_bottom_top) |
| entity | `assets/data/entities/<name>.json` + `assets/models/entity/<name>.bbmodel` + skin png |
| tree species | `assets/data/trees/<species>.json` |
| molten / melt / cast / craft / cook / crush | `assets/data/molten/`, `recipes/melt/`, `recipes/cast/`, `recipes/`, `recipes/cook/`, `recipes/crush/` |
| tags | `assets/data/tags/<tag>.json` |
| block tile | `assets/textures/blocks/<name>.png` |
| normal map / material tile | `assets/textures/blocks_material/<name>.png` (**never** in `blocks/`) |

`assets/**/*.json` is copied by a wildcard rule; a new asset *extension* needs a `<None Update>` entry in the csproj.

## Procedure
1. **Prefer a data flag over code.** If the block must behave differently, add a field on `BlockDefinition`/`BlockType` and have every call site read it, instead of a `switch` on the block id. Hot paths read flat per-id mirrors (`BlockType.IsSolid`, `Emission`, ...), so new flags follow that shape.
2. **Check the id budget.** Blocks and items share 255 byte ids (id 0 = air, a block may not be named `air`). Count `assets/data/blocks/*.json` + `assets/data/items/*.json` first; the registry throws on overflow at startup.
3. Write the JSON. Names in loot/recipes resolve blocks first, then items; use `block/` / `item/` prefixes whenever both could exist (`"item/branch"` vs the `branch_pile` block).
4. **New tile?** Tiles are atlas slices in sorted file-name order. Adding one renumbers later tiles, which is fine for code (it asks `TileOf(name)`) but never put non-tile PNGs in `blocks/`.
5. Build, then run a scripted visual check (`visual-verify` skill) for anything visible. A bad file fails **content load**, which fails every test through `TestContent`, and every error names its file.

## Traps (all enforced by the registry, but cheaper to avoid)
- A non-full-cube model needs `light.passes: true` (default) or its cell and interior quads render black.
- A `cutout` block must not occlude. `translucent` forces occludes off.
- Partial-block textures are cropped, never stretched (`FaceTiles` crops); a new partial block needs no mining-pop work if its `FaceTiles` are sensible.
- `biomeTint` only on blocks that bake to a plain full cube, not translucent; overlay not on cutout. Textureless tint tiles must be grey (see the biome tint section; generator scripts in `tools/`).
- `multiblock` anchors/parts: `hasItem: false`, model authored in 0..32 units, no `cullface`, opening on north.
- Two blocks sharing a texture share its material (warned at startup).
- Fluids force `solid/castsAO/hasItem/needsSupport` off; `hardensTo` pairs live under `flow`.
- Tree species: `wood` must be a `tree_wood` block; two species may share a wood only if they agree on `thickness.rim`. Species with no `worldgen` block never generate.
- Entities: a new `Mob` kind must be added to `WorldGenerationConfig.MobsMaxAlive`.
- Recipes: cook and crush are one per input; craft files are named by result, reverse by `<result>_from_<ingredient>`; a cast must melt back into exactly what it took (`MetalworksRecipeTests`).

## Tests to extend
`ContentTests` (schema rules), `EntityContentTests`, `TreeSpeciesContentTests`, `KilnContentTests`, `MetalworksRecipeTests`, `BiomeTintTextureTests`. Assert the **rule** the new content must satisfy; do not pin its literal values (see the `functional-tests` skill).
