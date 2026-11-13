import test from 'node:test'
import assert from 'node:assert/strict'
import { campfireModel } from './campfire.mjs'

const tiles = { side: 'bark', end: 'rings' }

test('a pile has one stick per branch, clamped to 1-4, each one layer higher', () => {
  assert.equal(campfireModel(4, tiles).elements.length, 4)
  assert.equal(campfireModel(0, tiles).elements.length, 1)
  assert.equal(campfireModel(9, tiles).elements.length, 4)
  const ys = campfireModel(4, tiles).elements.map((e) => e.from[1])
  assert.deepEqual(ys, [...ys].sort((a, b) => a - b))
})

test('sticks show rings on their cut ends and bark along their length', () => {
  const [stick] = campfireModel(1, tiles).elements
  assert.equal(stick.faces.east.tile, 'rings')
  assert.equal(stick.faces.up.tile, 'bark')
})
