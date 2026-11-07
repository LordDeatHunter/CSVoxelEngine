# This wiki

The site is VitePress, in the `wiki/` folder, deployed to GitHub Pages by `.github/workflows/wiki.yml` on every push to master that touches the wiki or the game assets.

## How the pages are made

`wiki/scripts/build-data.mjs` runs before every build. It reads `OpenGLTest/assets` and writes:

- `public/data/*.json`: resolved block models, items, entities, recipes (with made-from and used-in indexes), loot, trees, spells and effects.
- `public/assets/`: the block and item textures and the entity `.bbmodel` files with their skins.
- One page per block, item and mob under `game/blocks`, `game/items` and `game/mobs`, plus the generated overview pages in `game/reference/`.

All of that is gitignored and rebuilt in CI. The hand-written pages (controls, biomes, layers and everything under `dev/`) are committed.

## 3D viewers

`<ModelViewer kind="block|item|entity" id="..."/>` lazily loads three.js when it scrolls into view.

- **Blocks** are built from the resolved model elements with nearest-neighbor textures.
- **Items** are the sprite extruded into a thin slab of voxels.
- **Mobs** load the real `.bbmodel` (cubes, nested groups, per-face UVs) and play its clips with the engine's sign convention: rotation (x, y, z) in degrees becomes Euler (-x, -y, z) applied X first, and position (x, y, z) in pixels becomes (-x, y, z) / 16 blocks.

Blocks whose shape depends on placement (panes, fluids, tree wood and others) show a representative shape.

## Working on it

```powershell
cd wiki
npm install
npm run dev      # regenerates the data, then serves with hot reload
npm test         # generator and animation tests
npm run build    # the full production build
```

To keep a file out of the published site, add its path (relative to `OpenGLTest/assets`) to `wiki/exclude.txt`.
