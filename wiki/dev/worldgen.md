# Worldgen

Worldgen is a **pure function of world position and the seed**: each chunk generates its own blocks with no cross-chunk writes, so neighbors agree at their borders without shared state. Only deltas and the seed are saved.

## Invariants

- Nothing generated is ever solid above `height(x, z)`. Heights come from the column cache and already include ravines.
- Carvers (noise caves, worm tunnels) only remove blocks.
- A per-chunk cave biome map is classified once, right after the column fill and before the carvers, and dresses them: the cave shape, the ore richness, the exclusive cave ores and the palette pass all read it, and its point query equals it across every seam (`plans/BIOMEGEN.md` §12).
- Water fills air up to the column's own level in a wet column. There is no single water plane: oceans sit at sea level, rivers at their own level and ponds at their own surface.
- Generated water and lava are always sources. Cold columns freeze their top water cell into ice.
- Noise seeds derive from the world seed plus fixed per-channel offsets. A new noise takes the next free offset (the table is kept in the biome plan).
- Every feature stamp declares a conservative world-Y band, so chunks outside it skip the stamp with bit-identical output.

## Climate and biomes

Temperature and humidity are read only through one domain-warped reader, so biome, snow, tint, color grade and aurora all agree. The classification is the 3×3 grid on the [biomes page](/game/biomes); borders are smooth weights, and a coherent dither noise breaks material lines into ragged patches.

## Caches

Deterministic worldgen caches (columns, worm cells, tree batches and shapes, canopy tops, site verdicts, hell and sky trees) all use one generational cache. **Only values that are a pure function of the key may live there**: state carrying player edits must never be put in it, because eviction there would be data loss rather than a recompute.

## Layers

The overworld is stacked between a barrier band and the sky. Hell below it and the sky islands above are each a pure function of position, with their own stamps, biomes, flora and trees. A chunk wholly inside a realm skips the overworld pipeline. See the [layers page](/game/layers) for the bands.

## Far-terrain LOD

Full-detail chunks load to 16 chunks (256 blocks). Rings of reduced-resolution regions (2-, 4- and 8-block cells) extend the view to about 2048 blocks. Each ring is eroded and biased so that finer geometry always buries coarser geometry through the depth buffer, and regions fully covered by finer ones are skipped. Mining a block caps its column's LOD height so a dug hole never exposes the buried shell.
