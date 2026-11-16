import test from 'node:test'
import assert from 'node:assert/strict'
import { pivotBox } from '../.vitepress/theme/pivot.ts'

const cell = { min: [-0.5, -1, -0.5], max: [0.5, 1, 0.5] }
const middle = (b, axis) => (b.min[axis] + b.max[axis]) / 2

test('a block that fills its cell turns about the cell center', () => {
  assert.deepEqual(pivotBox(cell, cell), cell)
})

test('a thin panel at the edge of its cell turns about itself, at the same zoom and height', () => {
  const panel = { min: [-0.5, -1, 0.3125], max: [0.5, 1, 0.5] }
  const box = pivotBox(cell, panel)
  for (const axis of [0, 2]) {
    assert.ok(Math.abs(middle(box, axis) - middle(panel, axis)) < 1e-9)
    assert.ok(Math.abs((box.max[axis] - box.min[axis]) - (cell.max[axis] - cell.min[axis])) < 1e-9)
  }
  assert.deepEqual([box.min[1], box.max[1]], [cell.min[1], cell.max[1]])
})
