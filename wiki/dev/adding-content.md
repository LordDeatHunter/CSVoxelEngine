# Adding content

Everything about a block, item, recipe, entity or tree species comes from `OpenGLTest/assets/data`. **Adding content means adding JSON, not editing C#.** The wiki regenerates from those files on every build, so a new block gets its page and 3D preview automatically.

## A new block

1. Put the tile in `assets/textures/blocks/<tile>.png` (32×32). Tiles load in sorted file-name order, so a new tile renumbers the ones after it; that is fine, nothing stores tile numbers.
2. Create `assets/data/blocks/<name>.json`:

```json
{
  "name": "Example Stone",
  "model": { "parent": "block/cube_all", "textures": { "all": "example_stone" } },
  "hardness": 5,
  "requiresTool": true,
  "tools": { "pickaxe": 1 }
}
```

3. Check the id budget: blocks and items share 255 ids.
4. Give it a recipe or a way to drop (a `loot` table by the same name, or it drops itself).

## A new item

Add `assets/data/items/<name>.json` with `name` and `texture` (`assets/textures/items/<name>.png`). Add a `use`, `tool`, `attack` or `fuel` object to give it behavior, and list it in a tool tag file if it should count as a pickaxe or axe.

## A new recipe

Add `assets/data/recipes/<result>.json`:

```json
{ "result": "block/example_stone", "count": 4, "ingredients": [{ "item": "block/stone", "count": 4 }] }
```

Cook, crush, melt, cast and alloy recipes live in their own subfolders; see [Content schemas](/dev/content-schemas).

## A new mob

Add `assets/data/entities/<name>.json` and a Blockbench `.bbmodel` with its skin PNG next to it. Entity models must be authored in Blockbench. Use the item debugger's ENTITIES tab to check the clips before writing any gameplay code.

## Checking your work

Run the tests, then build the wiki (`npm run build` in `wiki/`) and look at the new page. Visual changes are verified with a scripted scenario; see [Testing](/dev/testing).
