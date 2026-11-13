// A representative campfire for the wiki's 3D viewer: the crossed sticks of a branch pile as model elements,
// laid out like the game's BranchPileGenerator (a fixed variant and mean length, no position jitter).
import { defaultUv } from './models.mjs'

const THICKNESS = 0.13 * 16
const LAYER = 0.095 * 16
const LENGTH = 0.85 * 16
const CENTERS = [[8, 8], [8.48, 7.68], [7.52, 8.48], [8.32, 8.32]]
const YAWS = [15, 105, 60, 150]

/** The `count` (1-4) sticks of a pile, in 16-unit model space, wearing the log's bark and ring tiles. */
export function campfireModel(count, { side, end }) {
  const elements = []
  for (let i = 0; i < Math.min(Math.max(count, 1), 4); i++) {
    const [cx, cz] = CENTERS[i]
    const from = [cx - LENGTH / 2, i * LAYER, cz - THICKNESS / 2]
    const to = [cx + LENGTH / 2, i * LAYER + THICKNESS, cz + THICKNESS / 2]
    const faces = {}
    for (const f of ['north', 'south', 'west', 'east', 'up', 'down'])
      faces[f] = { tile: f === 'west' || f === 'east' ? end : side, uv: defaultUv(f, from, to), rotation: 0 }
    elements.push({ from, to, rotation: { origin: [cx, from[1], cz], axis: 'y', angle: YAWS[i] }, faces })
  }
  return { elements, textures: {} }
}
