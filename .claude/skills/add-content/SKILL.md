---
name: add-content
description: Recipe for adding data-driven content to CSVoxelEngine - a block, item, loot table, model, entity, tree species, molten material, recipe, spell part or status effect - as JSON rather than C#. Includes the id rules, known traps and which tests to extend.
---

# Adding content (JSON, not C#)

`docs/agents/content-blocks-items.md` holds the full schemas. This skill is the procedure and the traps. Read the relevant schema table there before writing a file. Other schemas: trees in `docs/agents/trees.md`, recipes and molten materials in `docs/agents/recipes-and-metalworks.md`, spell parts and status effects in `docs/agents/spells-and-effects.md`.

## Where files go
| Thing | Path |
|---|---|
| block | `OpenGLTest/assets/data/blocks/<name>.json` (file name = id) |
| item | `assets/data/items/<name>.json` (a spell staff is an item with a `staff` object, a teaching scroll one with `use.action: "learn_parts"` and `teaches`) |
| loot | `assets/data/loot/<name>.json` (a block uses the table sharing its name unless `loot` says otherwise) |
| model | `assets/models/block/<name>.json` (Minecraft Java format; 4 built-in parents: cube, cube_all, cube_column, cube_bottom_top) |
| entity | `assets/data/entities/<name>.json` + `assets/models/entity/<name>.bbmodel` + skin png |
| tree species | `assets/data/trees/<species>.json` |
| molten / melt / cast / craft / cook / crush | `assets/data/molten/`, `recipes/melt/`, `recipes/cast/`, `recipes/`, `recipes/cook/`, `recipes/crush/` |
| tags | `assets/data/tags/<tag>.json` |
| spell part | `assets/data/spell/<kind>/<name>.json` (kind = `element`, `shape`, `modifier`, `trigger`) |
| status effect | `assets/data/effects/<name>.json` (HUD icon in `assets/textures/ui/`) |
| block tile | `assets/textures/blocks/<name>.png` |
| normal map / material tile | `assets/textures/blocks_material/<name>.png` (**never** in `blocks/`) |

`assets/**/*.json` is copied by a wildcard rule; a new asset *extension* needs a `<None Update>` entry in the csproj.

## Placeholder art
If the tile, sprite, model or skin you add is a stand-in, record it in `USER_TODO.md` at the repo root
in the same change: one checkbox with the file path(s), what generated it (a `tools/` script, if any),
and what a replacement must keep (canvas size, alpha/cutout, the UV regions its model crops, palette
rules). Never leave a placeholder unrecorded. If a `tools/` generator wrote the file, remove or repoint
that generator too, or a re-run puts the placeholder back. Sound is not tracked there: a feature that
needs audio waits in `plans/TODO.md` (`M-26`).

## Procedure
1. **Prefer a data flag over code.** If the block must behave differently, add a field on `BlockDefinition`/`BlockType` and have every call site read it, instead of a `switch` on the block id. Hot paths read flat per-id mirrors (`BlockType.IsSolid`, `Emission`, ...), so new flags follow that shape.
2. **Ids are assigned, never chosen.** Blocks and items share one `ushort` id space (up to 65,535; id 0 = air, a block may not be named `air`), handed out by file name at startup, so a new file shifts the ids after it: never hardcode or persist a numeric id (saves and the wire name content or resolve it per run), and never write a test that counts ids. The registry throws on overflow at startup.
3. Write the JSON. Names in loot/recipes resolve blocks first, then items; use `block/` / `item/` prefixes whenever both could exist (`"item/branch"` vs the `branch_pile` block).
4. **New tile?** Tiles are atlas slices in sorted file-name order. Adding one renumbers later tiles, which is fine for code (it asks `TileOf(name)`) but never put non-tile PNGs in `blocks/`.
5. Build, then run a scripted visual check (`visual-verify` skill) for anything visible. A bad file fails **content load**, which fails every test through `TestContent`, and every error names its file.
6. **Stand-in art?** Record it in `USER_TODO.md` (see "Placeholder art" above) before you finish.

## Traps (all enforced by the registry, but cheaper to avoid)
- A non-full-cube model needs `light.passes: true` (default) or its cell and interior quads render black.
- A `cutout` block must not occlude. `translucent` forces occludes off.
- Partial-block textures are cropped, never stretched (`FaceTiles` crops); a new partial block needs no mining-pop work if its `FaceTiles` are sensible.
- `biomeTint` only on blocks that bake to a plain full cube, not translucent; overlay not on cutout. Textureless tint tiles must be gray (see the biome tint section).
- `multiblock` anchors/parts: `hasItem: false`, model authored in 0..32 units, no `cullface`, opening on north.
- Two blocks sharing a texture share its material (warned at startup).
- Fluids force `solid/castsAO/hasItem/needsSupport` off; `hardensTo` pairs live under `flow`.
- Tree species: `wood` must be a `tree_wood` block; two species may share a wood only if they agree on `thickness.rim`. Species with no `worldgen` block never generate.
- Entities: a new `Mob` kind's manager implements `IMobPopulation` and joins the world's `MobCaps` (the shared natural and hard caps); it gets no cap of its own.
- Recipes: cook and crush are one per input; craft files are named by result, reverse by `<result>_from_<ingredient>`; a cast must melt back into exactly what it took (`MetalworksRecipeTests`).
- Spell parts: every part needs a `costShare` in (0,1], a `complexity` >= 0 and at least one tag; `damageFactor`/`rangeFactor` only on modifiers; keep typical spells at 20-40% of `ManaConfig.StartMax` (`SpellRecipeTests` checks every shipped combination). What a spell does is data too, never a switch on a part name: an element's `impact` is a list of distinct flags (`ignite`, `freeze`, `thaw`, `prime`, `light`, `grow`) and only an element may carry one; `plant` (a loaded tree species) is element only and needs `grow`; a shape's `delivery` is `projectile` (the default) or `self` and only a shape may carry one. Every refusal names the file (`SpellPartLoaderTests`).
- Status effects: at most 8 files (`StatusEffect.MaxKinds`, every wire form is a byte mask; the id is the file-name order). `durationSeconds` is required; `tickSeconds` is required exactly when the effect has a `damageShare` or a `blocks` flag and refused otherwise; factors only slow, in (0,1]; the effect must do something; `particle` is a `FireParticleKind` name; `icon` a `.png` path. An element part's `effect` must name a loaded effect, and only an element may carry one.
- Status block flags: `flammable` never on a fluid or a multiblock cell; `freezesTo` only on a fluid and names a non-fluid; `meltsTo` only on a non-fluid and names another fluid. Flag only what a burn may safely remove (leaves, plants), never builds or stations.
- Staffs: an item with a `staff` object (`slots` 1..`SpellConfig.StaffMaxSlots`, `tier` >= 1, non-blank distinct `tags`) stores in the Tools pool and may not declare `use` or `crucible`. It must be craftable (never loot only), and a higher tier needs more slots and a superset of the lower tier's tags (`StaffContentTests`). Their sprites are placeholders recorded in `USER_TODO.md`.
- Teaching scrolls: `use.action: "learn_parts"` and a `teaches` list of spell part keys (`<kind>/<name>`) always come together, each key once; the keys are checked against the loaded spell parts after they load (`ItemLoader.ValidateLearnParts`), so a misspelled key fails content load naming the file. A scroll declares no `use.result`, `use.fluid` or `use.block`. Placeholder sprites are recorded in `USER_TODO.md` (`assets/textures/items/scroll_<name>.png`). The starter grant is `Items/StarterKit`, never a JSON flag (`ScrollSchemaTests`, `StarterKitTests`).
- Mana items: `use.action: "restore_mana"` needs a `restoresMana` share in (0, 1] (`ItemLoader.ParseRestoresMana`), and no `use.result`, `use.fluid` or `use.block`. The one item is `mana_draught`, its sprite a placeholder recorded in `USER_TODO.md` (`RestoreManaItemTests`).
- Spell stations: any block with `spellBuilder: true` opens the spell builder on right-click (a JSON flag, no code, no block entity). It must have an item and be craftable (`SpellTableTests`). Do not give it a `material`: its tile is shared with the crystal block, and two blocks sharing a texture with a material warn.

## Tests to extend
`ContentTests` (schema rules), `EntityContentTests`, `TreeSpeciesContentTests`, `KilnContentTests`, `MetalworksRecipeTests`, `BiomeTintTextureTests`, `SpellRecipeTests`, `SpellPartLoaderTests`, `StaffContentTests`, `StaffBindingTests`, `SpellTableTests`, `ScrollSchemaTests`, `StatusEffectLoaderTests` (effect schema rules), `StatusBlockFlagTests` (flammable/freezesTo/meltsTo). Assert the **rule** the new content must satisfy; do not pin its literal values (see the `functional-tests` skill).
