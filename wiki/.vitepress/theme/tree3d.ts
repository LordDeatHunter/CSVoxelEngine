// three.js scene for the growth slider: replays the exporter's per-step cells up to the chosen step and
// draws wood as boxes (see tree-shape.ts) and leaves as cubes over a patch of grass.
import { createStage, loadTexture, lit } from './viewer3d.ts'
import { bounds, cellsAt, leafCells, woodBoxes, type TreeStep } from './tree-shape.ts'

export interface TreeData { species: string; seed: number; maturity: number; maxStep: number; steps: TreeStep[] }
export interface TreeScene { setStep(step: number): void; dispose(): void }

const GROUND_MARGIN = 3

export async function buildTree(host: HTMLElement, data: TreeData, tiles: { wood: string; leaves: string | null; ground: string }, base: string): Promise<TreeScene> {
  const stage = await createStage(host)
  const { T, scene } = stage
  const tile = (name: string) => loadTexture(T, `${base}/textures/blocks/${name}.png`)

  const woodMat = lit(T, await tile(tiles.wood))
  const leafMat = tiles.leaves ? lit(T, await tile(tiles.leaves)) : null
  const groundTex = await tile(tiles.ground)
  groundTex.wrapS = groundTex.wrapT = T.RepeatWrapping

  // Frame the fully grown tree once, so the camera stays put while the slider moves.
  const final = cellsAt(data.steps, data.maxStep)
  const b = bounds(final)
  const x0 = Math.floor(b.min[0]) - GROUND_MARGIN
  const x1 = Math.ceil(b.max[0]) + GROUND_MARGIN
  const z0 = Math.floor(b.min[2]) - GROUND_MARGIN
  const z1 = Math.ceil(b.max[2]) + GROUND_MARGIN
  groundTex.repeat.set(x1 - x0, z1 - z0)
  const ground = new T.Mesh(new T.PlaneGeometry(x1 - x0, z1 - z0), new T.MeshLambertMaterial({ map: groundTex }))
  ground.rotation.x = -Math.PI / 2
  ground.position.set((x0 + x1) / 2, 1, (z0 + z1) / 2)
  scene.add(ground)
  stage.frame(new T.Box3(new T.Vector3(b.min[0], 1, b.min[2]), new T.Vector3(b.max[0], b.max[1], b.max[2])))

  const cube = new T.BoxGeometry(1, 1, 1)
  const m = new T.Matrix4()
  const live: InstanceType<typeof T.InstancedMesh>[] = []

  const setStep = (step: number) => {
    for (const mesh of live) { scene.remove(mesh); mesh.dispose() }
    live.length = 0
    const cells = cellsAt(data.steps, step)

    const boxes = woodBoxes(cells)
    if (boxes.length) {
      const mesh = new T.InstancedMesh(cube, woodMat, boxes.length)
      boxes.forEach((box, i) => {
        m.makeScale(box.max[0] - box.min[0], box.max[1] - box.min[1], box.max[2] - box.min[2])
        m.setPosition((box.min[0] + box.max[0]) / 2, (box.min[1] + box.max[1]) / 2, (box.min[2] + box.max[2]) / 2)
        mesh.setMatrixAt(i, m)
      })
      scene.add(mesh)
      live.push(mesh)
    }

    const leaves = leafMat ? leafCells(cells) : []
    if (leafMat && leaves.length) {
      const mesh = new T.InstancedMesh(cube, leafMat, leaves.length)
      leaves.forEach(([x, y, z], i) => {
        m.makeTranslation(x + 0.5, y + 0.5, z + 0.5)
        mesh.setMatrixAt(i, m)
      })
      scene.add(mesh)
      live.push(mesh)
    }
  }
  setStep(data.maxStep)

  return {
    setStep,
    dispose() {
      for (const mesh of live) mesh.dispose()
      cube.dispose()
      stage.dispose()
    }
  }
}
