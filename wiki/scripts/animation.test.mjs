import test from 'node:test'
import assert from 'node:assert/strict'
import { sample, rotationToEuler, positionToBlocks, clipTime, parsePoint, sheetFrame, sheetStyle } from '../.vitepress/theme/animation.ts'

test('rotation follows the engine sign convention: x and y negate, z keeps its sign', () => {
  const [x, y, z] = rotationToEuler([90, 90, 90])
  assert.ok(x < 0 && y < 0 && z > 0)
  assert.ok(Math.abs(Math.abs(x) - Math.PI / 2) < 1e-9)
})

test('position negates x and scales pixels to blocks', () => {
  assert.deepEqual(positionToBlocks([16, 16, 16]), [-1, 1, 1])
})

test('linear sampling is between its end keys and holds outside them', () => {
  const keys = [
    { time: 0, value: [0, 0, 0], interpolation: 'linear' },
    { time: 1, value: [10, 0, 0], interpolation: 'linear' }
  ]
  assert.equal(sample(keys, 0.5)[0], 5)
  assert.equal(sample(keys, -1)[0], 0)
  assert.equal(sample(keys, 9)[0], 10)
})

test('step holds the earlier key', () => {
  const keys = [
    { time: 0, value: [1, 0, 0], interpolation: 'step' },
    { time: 1, value: [9, 0, 0], interpolation: 'linear' }
  ]
  assert.equal(sample(keys, 0.99)[0], 1)
})

test('catmull-rom passes through its keys', () => {
  const keys = [0, 1, 2, 3].map((t) => ({ time: t, value: [t * t, 0, 0], interpolation: 'catmullrom' }))
  assert.ok(Math.abs(sample(keys, 1)[0] - 1) < 1e-9)
  assert.ok(Math.abs(sample(keys, 2)[0] - 4) < 1e-9)
})

test('loop wraps, once and hold clamp', () => {
  assert.equal(clipTime(2.5, 2, 'loop'), 0.5)
  assert.equal(clipTime(2.5, 2, 'once'), 2)
  assert.equal(clipTime(2.5, 2, 'hold'), 2)
})

test('string data points parse, anything non-numeric becomes 0', () => {
  assert.deepEqual(parsePoint({ x: '1.5', y: 2, z: 'abc' }), [1.5, 2, 0])
})

const compass = { cols: 4, rows: 4, frames: 16, mode: 'heading', fps: 8 }

test('a sheet frame stays inside the sheet and advances with time', () => {
  for (const s of [compass, { ...compass, mode: 'loop', frames: 6 }]) {
    const seen = new Set()
    for (let t = 0; t < 30; t += 0.05) {
      const f = sheetFrame(s, t)
      assert.ok(f >= 0 && f < s.frames)
      seen.add(f)
    }
    assert.equal(seen.size, s.frames)
  }
})

test('a frame crop puts frame 0 at the top left and the last frame at the bottom right, row-major', () => {
  assert.equal(sheetStyle('x.png', compass, 0).backgroundPosition, '0% 0%')
  assert.equal(sheetStyle('x.png', compass, 15).backgroundPosition, '100% 100%')
  assert.equal(sheetStyle('x.png', compass, 1).backgroundPosition.split(' ')[1], '0%')
  assert.equal(sheetStyle('x.png', compass, 4).backgroundPosition.split(' ')[0], '0%')
  assert.equal(sheetStyle('x.png', compass, 0).backgroundSize, '400% 400%')
})
