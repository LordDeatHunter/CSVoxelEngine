// Pure Blockbench animation math (no three.js): keyframe sampling and the engine's sign convention.
// Convention (see BbmodelLoader.ReadAnimations in the game): rotation degrees (x, y, z) become Euler
// (-x, -y, z) applied X first, then Y, then Z; position pixels (x, y, z) become (-x, y, z) / 16 blocks.

export type Vec3 = [number, number, number]
export interface Key { time: number; value: Vec3; interpolation: string }

export function parsePoint(p: { x: unknown; y: unknown; z: unknown }): Vec3 {
  const n = (v: unknown) => {
    const f = typeof v === 'number' ? v : parseFloat(String(v))
    return Number.isFinite(f) ? f : 0
  }
  return [n(p.x), n(p.y), n(p.z)]
}

/** Engine Euler radians (x, y, z), to be applied in three.js order 'ZYX' (X first). */
export function rotationToEuler(deg: Vec3): Vec3 {
  const r = Math.PI / 180
  return [-deg[0] * r, -deg[1] * r, deg[2] * r]
}

/** Blocks, in the parent bone's space. */
export function positionToBlocks(px: Vec3): Vec3 {
  return [-px[0] / 16, px[1] / 16, px[2] / 16]
}

function catmull(p0: number, p1: number, p2: number, p3: number, t: number) {
  const t2 = t * t
  const t3 = t2 * t
  return 0.5 * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3)
}

/**
 * Samples a channel's keys (sorted by time) at time t. A segment is catmull-rom when either end key is,
 * step when the earlier key is step, else linear (Blockbench's rule). Outside the key range it holds the end value.
 */
export function sample(keys: Key[], t: number): Vec3 {
  if (keys.length === 0) return [0, 0, 0]
  if (t <= keys[0].time) return keys[0].value
  const last = keys[keys.length - 1]
  if (t >= last.time) return last.value
  let i = 0
  while (keys[i + 1].time < t) i++
  const a = keys[i]
  const b = keys[i + 1]
  if (a.interpolation === 'step') return a.value
  const u = (t - a.time) / (b.time - a.time)
  const smooth = a.interpolation === 'catmullrom' || b.interpolation === 'catmullrom'
  const out: Vec3 = [0, 0, 0]
  for (let c = 0; c < 3; c++) {
    if (smooth) {
      const p0 = (keys[i - 1] ?? a).value[c]
      const p3 = (keys[i + 2] ?? b).value[c]
      out[c] = catmull(p0, a.value[c], b.value[c], p3, u)
    } else {
      out[c] = a.value[c] + (b.value[c] - a.value[c]) * u
    }
  }
  return out
}

/** Maps elapsed time into clip time for the loop mode (loop wraps; once and hold clamp at the end). */
export function clipTime(elapsed: number, length: number, loop: string): number {
  if (length <= 0) return 0
  return loop === 'loop' ? elapsed % length : Math.min(elapsed, length)
}
