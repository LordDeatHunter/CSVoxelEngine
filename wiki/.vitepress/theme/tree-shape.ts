// Pure tree logic for the wiki viewer (no three.js): replaying the exporter's per-step diffs and turning
// wood cells into boxes the way the game's TreeWoodShape draws them.
//
// Wood state byte: bits 0-3 thickness code n (diameter n + 1 px, 15 is a full cube), bits 4-6 the parent
// direction in cube-face order (0 -Z, 1 +Z, 2 -X, 3 +X, 4 +Y, 5 -Y, 6 root, 7 detached), bit 7 a shell cell
// of a wide trunk (always a full cube).

export interface Cell { leaf: boolean; state: number }
export type CellMap = Map<string, Cell>
export interface TreeStep { set: number[]; del: number[] }
export interface Box { min: [number, number, number]; max: [number, number, number] }

const key = (x: number, y: number, z: number) => `${x},${y},${z}`
const unkey = (k: string) => k.split(',').map(Number) as [number, number, number]

export const FULL_CUBE = 15
export const PARENT_ROOT = 6
export const PARENT_DETACHED = 7
export const SHELL_BIT = 0x80

// Direction vectors in cube-face order; the opposite of d is d ^ 1.
const DIRS: [number, number, number][] = [[0, 0, -1], [0, 0, 1], [-1, 0, 0], [1, 0, 0], [0, 1, 0], [0, -1, 0]]

export function applyStep(map: CellMap, step: TreeStep) {
  for (let i = 0; i < step.del.length; i += 3) map.delete(key(step.del[i], step.del[i + 1], step.del[i + 2]))
  for (let i = 0; i < step.set.length; i += 5) {
    map.set(key(step.set[i], step.set[i + 1], step.set[i + 2]), { leaf: step.set[i + 3] === 1, state: step.set[i + 4] })
  }
}

/** The cells after steps 0..n, replayed from nothing. */
export function cellsAt(steps: TreeStep[], n: number): CellMap {
  const map: CellMap = new Map()
  for (let i = 0; i <= n && i < steps.length; i++) applyStep(map, steps[i])
  return map
}

export const thickness = (state: number) => state & 15
export const parentDir = (state: number) => (state >> 4) & 7
export const isShell = (state: number) => (state & SHELL_BIT) !== 0
/** Half the cell-unit width of a wood cell of this thickness code (a full cube is 0.5). */
export const halfWidth = (code: number) => (code + 1) / 32

const isFull = (state: number) => thickness(state) === FULL_CUBE || isShell(state) || parentDir(state) === PARENT_DETACHED

/** Whether wood cell A (at its position) connects toward direction d: one cell's parent points at the other. */
function connects(a: Cell, neighbor: Cell | undefined, d: number): boolean {
  if (!neighbor || neighbor.leaf) return false
  if (parentDir(a.state) === PARENT_DETACHED || parentDir(neighbor.state) === PARENT_DETACHED) return false
  return parentDir(a.state) === d || parentDir(neighbor.state) === (d ^ 1)
}

/**
 * Boxes (world coordinates, cell (x, y, z) spans x..x+1) for every wood cell: a full cube for a full,
 * shell or detached cell, else a centered node box plus a tube toward each connected neighbor at the thinner
 * width and a plug down into the ground when the cell stands on it.
 */
export function woodBoxes(map: CellMap, groundY = 0): Box[] {
  const boxes: Box[] = []
  for (const [k, cell] of map) {
    if (cell.leaf) continue
    const [x, y, z] = unkey(k)
    if (isFull(cell.state)) {
      boxes.push({ min: [x, y, z], max: [x + 1, y + 1, z + 1] })
      continue
    }
    const half = halfWidth(thickness(cell.state))
    const c = [x + 0.5, y + 0.5, z + 0.5]
    boxes.push({ min: [c[0] - half, c[1] - half, c[2] - half], max: [c[0] + half, c[1] + half, c[2] + half] })
    for (let d = 0; d < 6; d++) {
      const [dx, dy, dz] = DIRS[d]
      const neighbor = map.get(key(x + dx, y + dy, z + dz))
      const onGround = d === 5 && y - 1 === groundY
      if (!onGround && !connects(cell, neighbor, d)) continue
      const tube = onGround && !connects(cell, neighbor, d) ? half : Math.min(half, neighbor ? (isFull(neighbor.state) ? 0.5 : halfWidth(thickness(neighbor.state))) : half)
      const min: [number, number, number] = [c[0] - tube, c[1] - tube, c[2] - tube]
      const max: [number, number, number] = [c[0] + tube, c[1] + tube, c[2] + tube]
      const axis = dx !== 0 ? 0 : dy !== 0 ? 1 : 2
      const sign = dx + dy + dz
      const base = [x, y, z][axis]
      if (sign > 0) { min[axis] = c[axis] + half; max[axis] = base + 1 } else { min[axis] = base; max[axis] = c[axis] - half }
      boxes.push({ min, max })
    }
  }
  return boxes
}

export function leafCells(map: CellMap): [number, number, number][] {
  const out: [number, number, number][] = []
  for (const [k, cell] of map) if (cell.leaf) out.push(unkey(k))
  return out
}

export function bounds(map: CellMap): Box {
  const min: [number, number, number] = [Infinity, Infinity, Infinity]
  const max: [number, number, number] = [-Infinity, -Infinity, -Infinity]
  for (const k of map.keys()) {
    const p = unkey(k)
    for (let a = 0; a < 3; a++) { min[a] = Math.min(min[a], p[a]); max[a] = Math.max(max[a], p[a] + 1) }
  }
  return { min, max }
}
