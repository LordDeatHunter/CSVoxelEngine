# This wiki

The site is VitePress, in the `wiki/` folder, deployed to GitHub Pages by `.github/workflows/wiki.yml` on every push to master that touches the wiki or the game assets.

## How the pages are made

`wiki/scripts/build-data.mjs` runs before every build. It reads `OpenGLTest/assets` and writes:

- `public/data/*.json`: resolved block models, items, entities, recipes (with made-from and used-in indexes), loot, trees, spells and effects.
- `public/assets/`: the block and item textures and the entity `.bbmodel` files with their skins.
- One page per block, item and mob under `game/blocks`, `game/items` and `game/mobs`, plus the generated overview pages in `game/reference/`.

All of that is gitignored and rebuilt in CI. The hand-written pages (controls, biomes, layers and everything under `dev/`) are committed.

## Tree growth data

The tree pages replay data from `OpenGLTest.WikiExport`, a small headless console project that runs the real growth engine (`TreeGrower` and `TreeRasterizer`) for every species with the item debugger's setup: the default seed, a root at (0, 1, 0), flat ground, features from the maturity step on. For each step it writes only the cells to set and remove, because wood thickens, shells appear and leaves come and go as a tree grows. CI installs .NET 10 and runs it before the site build; locally run `npm run trees` in `wiki/` (needs the .NET 10 SDK). `public/data/trees` is gitignored.

## 3D viewers

`<ModelViewer kind="block|item|entity" id="..."/>` lazily loads three.js when it scrolls into view.

- **Blocks** are built from the resolved model elements with nearest-neighbor textures.
- **Items** are the sprite extruded into a thin slab of voxels.
- **Mobs** load the real `.bbmodel` (cubes, nested groups, per-face UVs) and play its clips with the engine's sign convention: rotation (x, y, z) in degrees becomes Euler (-x, -y, z) applied X first, and position (x, y, z) in pixels becomes (-x, y, z) / 16 blocks.

Blocks whose shape depends on placement (panes, fluids, tree wood and others) show a representative shape.

`<TreeViewer id="oak"/>` draws a tree from the exporter data: wood as boxes sized by each cell's thickness with tubes toward connected neighbors (the game's `TreeWoodShape` rule, in `tree-shape.ts`), leaves as cubes, over a patch of grass. A slider replays the growth, and `?step=N` in the URL opens any step.

## Recipes and icons

Recipes are drawn by `<RecipeCard kind="craft" id="..."/>`: framed inventory-style slots with count badges (milliliters for molten metal), an arrow through the station (the quern, kiln, casting basin or alloy furnace block, or a crafting grid), and a glowing output slot. `RecipeSlot` links to the block or item page and shows its name on hover; a tag input ("any axe") cycles through its members. Item icons are their sprites. Block icons are rendered once each from the real block model by one shared offscreen three.js renderer (`icons.ts`) and cached, which also fits multiblock structures, and the same renderer draws the mob portraits (rest pose, from the front) for `<MobGrid>`, the Mobs index. The Blocks and Items index pages use `<ItemGrid>`, a searchable grid of the same icons.

## Working on it

```powershell
cd wiki
npm install
npm run dev      # regenerates the data, then serves with hot reload
npm run trees    # export tree growth data (needs the .NET 10 SDK)
npm test         # generator, animation and tree-shape tests
npm run build    # the full production build
```

To keep a file out of the published site, add its path (relative to `OpenGLTest/assets`) to `wiki/exclude.txt`.
