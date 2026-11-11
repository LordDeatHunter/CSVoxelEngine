import test from 'node:test'
import assert from 'node:assert/strict'
import { applyStep, cellsAt, woodBoxes, leafCells, halfWidth } from '../.vitepress/theme/tree-shape.ts'

// state = thickness | parent << 4 | shell << 7
const wood = (thick, parent, shell = 0) => thick | (parent << 4) | (shell << 7)

test('replay applies deletes before writes and later steps override earlier ones', () => {
  const steps = [
    { set: [0, 1, 0, 0, wood(3, 6), 1, 1, 0, 1, 5], del: [] },
    { set: [0, 1, 0, 0, wood(5, 6)], del: [1, 1, 0] }
  ]
  const map = cellsAt(steps, 1)
  assert.equal(map.size, 1)
  assert.equal(map.get('0,1,0').state, wood(5, 6))
  assert.equal(cellsAt(steps, 0).size, 2)
})

test('a full-thickness cell, a shell and detached wood are plain cubes', () => {
  for (const state of [wood(15, 6), wood(3, 6, 1), wood(3, 7)]) {
    const map = new Map([['2,3,4', { leaf: false, state }]])
    assert.deepEqual(woodBoxes(map), [{ min: [2, 3, 4], max: [3, 4, 5] }])
  }
})

test('a thin cell is a centered box of its thickness, and an unconnected one has no tubes', () => {
  const map = new Map([['0,5,0', { leaf: false, state: wood(7, 6) }]])
  const boxes = woodBoxes(map)
  assert.equal(boxes.length, 1)
  assert.ok(Math.abs(boxes[0].max[0] - boxes[0].min[0] - 2 * halfWidth(7)) < 1e-9)
})

test('a child whose parent points down connects both cells with a tube between them', () => {
  // 0 -Z, 1 +Z, 2 -X, 3 +X, 4 +Y, 5 -Y: the upper cell's parent is below it (5)
  const map = new Map([
    ['0,5,0', { leaf: false, state: wood(7, 6) }],
    ['0,6,0', { leaf: false, state: wood(7, 5) }]
  ])
  const boxes = woodBoxes(map)
  const half = halfWidth(7)
  // each cell draws its half of the tube, from its node box out to the shared cell face at y = 6
  const lowerHalf = boxes.some((b) => Math.abs(b.min[1] - (5.5 + half)) < 1e-9 && b.max[1] === 6)
  const upperHalf = boxes.some((b) => b.min[1] === 6 && Math.abs(b.max[1] - (6.5 - half)) < 1e-9)
  assert.ok(lowerHalf && upperHalf)
})

test('wood standing on the ground gets a plug down to it, wood in the air does not', () => {
  const low = woodBoxes(new Map([['0,1,0', { leaf: false, state: wood(4, 6) }]]), 0)
  assert.ok(low.some((b) => b.min[1] === 1))
  const high = woodBoxes(new Map([['0,9,0', { leaf: false, state: wood(4, 6) }]]), 0)
  assert.equal(high.length, 1)
})

test('leaves are listed but never become wood boxes', () => {
  const map = new Map([['0,1,0', { leaf: true, state: 7 }]])
  assert.deepEqual(leafCells(map), [[0, 1, 0]])
  assert.equal(woodBoxes(map).length, 0)
})

test('applyStep leaves the map untouched for an empty step', () => {
  const map = new Map([['0,1,0', { leaf: false, state: 1 }]])
  applyStep(map, { set: [], del: [] })
  assert.equal(map.size, 1)
})
