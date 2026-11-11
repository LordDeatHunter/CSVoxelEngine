// Multiblock structure layout: the cells of a size^3 volume of one source block, layer by layer.

/** True for a cell touching no face of the volume (the game's Multiblock.IsInterior). */
export function isInterior(size, x, y, z) {
  const inner = (v) => v > 0 && v < size - 1
  return inner(x) && inner(y) && inner(z)
}

/** Layers bottom to top; each is `size` rows front (the opening) to back of `size` cells, 'source' or 'interior'. */
export function layersFor(size) {
  return Array.from({ length: size }, (_, y) =>
    Array.from({ length: size }, (_, row) =>
      Array.from({ length: size }, (_, x) => (isInterior(size, x, y, size - 1 - row) ? 'interior' : 'source'))))
}

/** Counts of the source blocks the shell needs and of the optional interior cells. */
export function materialsFor(size) {
  let required = 0
  let optional = 0
  for (const layer of layersFor(size)) for (const row of layer) for (const c of row) c === 'interior' ? optional++ : required++
  return { required, optional }
}

/** One structure entry from a multiblock anchor block's JSON; null when it is not a multiblock. */
export function structureFrom(id, d) {
  if (d.renderer !== 'multiblock' || !d.multiblock) return null
  const strip = (s) => String(s).replace(/^block\//, '')
  const size = d.multiblock.size ?? 2
  const notes = []
  if (d.kiln) {
    const k = d.kiln
    notes.push(['Fuel capacity', `${k.fuelCap ?? 4} units`], ['Melt speed', `×${k.meltSpeed ?? 1}`], ['Alloys metals', k.canAlloy ? 'yes' : 'no'])
  }
  if (d.bloomery) notes.push(['Ore capacity', `${d.bloomery.oreCap ?? 4} units`], ['Fuel capacity', `${d.bloomery.fuelCap ?? 4} units`])
  return { id, name: d.name ?? id, size, source: strip(d.multiblock.source), part: strip(d.multiblock.part), notes, layers: layersFor(size), ...materialsFor(size) }
}
