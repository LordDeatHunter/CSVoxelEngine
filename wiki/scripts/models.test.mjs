import test from 'node:test'
import assert from 'node:assert/strict'
import { resolveModel, defaultUv } from './models.mjs'
import { stripJsonc, parseJsonc } from './jsonc.mjs'
import { buildRecipeIndex } from './recipes.mjs'

const models = {
  'block/cube': { elements: [{ from: [0, 0, 0], to: [16, 16, 16], faces: { up: { texture: '#top' }, north: { texture: '#side' } } }] },
  'block/child': { parent: 'block/cube', textures: { side: 'stone' } }
}
const load = (p) => models[p]

test('slots resolve through the parent chain and child textures win', () => {
  const m = resolveModel({ parent: 'block/child', textures: { top: 'blocks/dirt' } }, load)
  assert.equal(m.elements[0].faces.up.tile, 'dirt')
  assert.equal(m.elements[0].faces.north.tile, 'stone')
})

test('an unresolved slot drops its face instead of emitting a broken tile', () => {
  const m = resolveModel({ parent: 'block/cube', textures: {} }, load)
  assert.deepEqual(Object.keys(m.elements[0].faces), [])
})

test('default uv crops the tile to the covered part of the face', () => {
  assert.deepEqual(defaultUv('north', [0, 0, 0], [16, 8, 16]), [0, 8, 16, 16])
  assert.deepEqual(defaultUv('up', [2, 0, 4], [10, 8, 12]), [2, 4, 10, 12])
})

test('a parent cycle is reported', () => {
  const cyc = { a: { parent: 'b' }, b: { parent: 'a' } }
  assert.throws(() => resolveModel('a', (p) => cyc[p]), /cycle/)
})

test('comments and trailing commas are tolerated, but not inside strings', () => {
  assert.deepEqual(parseJsonc('{ // c\n "u": "http://x", /* b */ "a": [1,2,], }'), { u: 'http://x', a: [1, 2] })
  assert.equal(stripJsonc('"//"'), '"//"')
})

test('the recipe index maps outputs and inputs both ways', () => {
  const r = [{ outputs: ['item/a'], inputs: ['item/b', 'item/c'] }]
  const idx = buildRecipeIndex(r)
  assert.equal(idx.madeFrom['item/a'].length, 1)
  assert.equal(idx.usedIn['item/c'].length, 1)
})
