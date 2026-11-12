import test from 'node:test'
import assert from 'node:assert/strict'
import { isInterior, layersFor, materialsFor, structureFrom } from './structures.mjs'

for (const size of [2, 3, 4]) {
  test(`size ${size}: layers are size x size x size and counts cover the volume`, () => {
    const layers = layersFor(size)
    assert.equal(layers.length, size)
    for (const l of layers) { assert.equal(l.length, size); for (const r of l) assert.equal(r.length, size) }
    const { required, optional } = materialsFor(size)
    assert.equal(required + optional, size ** 3)
    assert.equal(optional, Math.max(0, size - 2) ** 3)
  })
}

test('interior cells never touch a face of the volume', () => {
  for (const size of [2, 3, 4])
    for (let x = 0; x < size; x++) for (let y = 0; y < size; y++) for (let z = 0; z < size; z++)
      if (isInterior(size, x, y, z)) assert.ok([x, y, z].every((v) => v > 0 && v < size - 1))
})

test('structureFrom ignores non-multiblocks and strips block/ prefixes', () => {
  assert.equal(structureFrom('stone', { renderer: 'model' }), null)
  const s = structureFrom('k', { renderer: 'multiblock', multiblock: { part: 'block/p', source: 'block/s' }, kiln: {} })
  assert.equal(s.source, 's')
  assert.equal(s.part, 'p')
  assert.equal(s.size, 2)
  assert.ok(s.notes.length > 0)
})
