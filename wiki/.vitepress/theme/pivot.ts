// Where a block's 3D view turns: the game spins a block about the middle of what is drawn, not the middle of its
// cell (BlockCubeCache.WholeModel), so a thin door standing at the edge of its cell turns about its own panel.
export type Vec3 = [number, number, number]
export interface Bounds { min: Vec3; max: Vec3 }

/**
 * The box a block's view is framed on (the camera orbits its center): the cell box, slid sideways so its middle
 * is the middle of what is drawn on X and Z. Its size and its height stay the cell's, so every block is framed
 * at the same zoom and a low slab still sits at the bottom of the frame.
 */
export function pivotBox(cell: Bounds, drawn: Bounds): Bounds {
  const slide = (axis: 0 | 2) => (drawn.min[axis] + drawn.max[axis]) / 2 - (cell.min[axis] + cell.max[axis]) / 2
  const dx = slide(0)
  const dz = slide(2)
  return {
    min: [cell.min[0] + dx, cell.min[1], cell.min[2] + dz],
    max: [cell.max[0] + dx, cell.max[1], cell.max[2] + dz]
  }
}
