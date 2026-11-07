# Adding content

Everything about a block, item, recipe, entity or tree species comes from `OpenGLTest/assets/data`. Ids come from file names; blocks and items share 255 byte ids.

- **Block**: `assets/data/blocks/<name>.json`, only `model` is required (a `block/cube_all`-style path or an inline model).
- **Item**: `assets/data/items/<name>.json` with `name` and `texture`.
- **Recipe**: `assets/data/recipes/<result>.json`, `{ "result", "count", "ingredients": [{ "item", "count" }] }`.
- **Entity**: `assets/data/entities/<name>.json` plus a Blockbench `.bbmodel` in `assets/models/entity`.

This wiki regenerates from those files on every build, so a new block gets its page and 3D preview automatically.
