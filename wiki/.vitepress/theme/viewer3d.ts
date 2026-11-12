// three.js scene builders for the wiki's ModelViewer: blocks (resolved model elements), items (extruded
// sprite) and entities (.bbmodel with animation clips). three is imported lazily so SSR never loads it.
import { clipTime, keyValue, positionToBlocks, rotationToEuler, sample, sheetFrame, storesPreviewSigns, type Key, type Sheet, type Vec3 } from './animation.ts'

type THREE = typeof import('three')

export interface EntityRig {
  clipNames: string[]
  play(name: string, speed: number): void
  setSpeed(speed: number): void
  setPaused(paused: boolean): void
  /** true wraps the clip, false plays it once and holds the last frame; the clip's own mode until first set. */
  setLoop(loop: boolean): void
  /** The active clip's length in seconds (0 without one). */
  length(): number
  /** Seconds into the active clip, after the loop rule. */
  time(): number
  /** Jumps to a time in the active clip (the pose follows even while paused). */
  seek(seconds: number): void
  /** Whether the active clip loops by its own authoring, for the switch's starting state. */
  clipLoops(): boolean
}
export interface Built { dispose: () => void; note?: string; rig?: EntityRig }

// Face corners per Minecraft convention: [x, y, z] picks from (min=0 / max=1) per axis, with uv corner index.
// Each corner is [axisX(0|1), axisY(0|1), axisZ(0|1), uCorner(0|1), vCorner(0|1)] over from/to.
const FACE_CORNERS: Record<string, number[][]> = {
  up: [[0, 1, 0, 0, 0], [1, 1, 0, 1, 0], [1, 1, 1, 1, 1], [0, 1, 1, 0, 1]],
  down: [[0, 0, 1, 0, 0], [1, 0, 1, 1, 0], [1, 0, 0, 1, 1], [0, 0, 0, 0, 1]],
  north: [[1, 1, 0, 0, 0], [0, 1, 0, 1, 0], [0, 0, 0, 1, 1], [1, 0, 0, 0, 1]],
  south: [[0, 1, 1, 0, 0], [1, 1, 1, 1, 0], [1, 0, 1, 1, 1], [0, 0, 1, 0, 1]],
  west: [[0, 1, 0, 0, 0], [0, 1, 1, 1, 0], [0, 0, 1, 1, 1], [0, 0, 0, 0, 1]],
  east: [[1, 1, 1, 0, 0], [1, 1, 0, 1, 0], [1, 0, 0, 1, 1], [1, 0, 1, 0, 1]]
}

/** Pushes one face quad. Positions are in model pixels; uv is [u1, v1, u2, v2] over a w x h texture. */
function pushFace(pos: number[], uvs: number[], idx: number[], face: string, from: Vec3, to: Vec3, uv: number[], w: number, h: number, rot = 0) {
  const base = pos.length / 3
  for (const [ax, ay, az, uc, vc] of FACE_CORNERS[face]) {
    pos.push(ax ? to[0] : from[0], ay ? to[1] : from[1], az ? to[2] : from[2])
    // Optional quarter-turn of the face texture.
    let u = uc
    let v = vc
    for (let r = 0; r < ((rot / 90) | 0) % 4; r++) [u, v] = [1 - v, u]
    uvs.push((u ? uv[2] : uv[0]) / w, 1 - (v ? uv[3] : uv[1]) / h)
  }
  idx.push(base, base + 1, base + 2, base, base + 2, base + 3)
}

export async function loadTexture(T: THREE, url: string) {
  const tex = await new T.TextureLoader().loadAsync(url)
  tex.magFilter = T.NearestFilter
  tex.minFilter = T.NearestFilter
  tex.colorSpace = T.SRGBColorSpace
  tex.generateMipmaps = false
  // Quads merged over several cells (multiblock models) carry uvs past 0..1 and rely on tiling.
  tex.wrapS = T.RepeatWrapping
  tex.wrapT = T.RepeatWrapping
  return tex
}

export function lit(T: THREE, tex: InstanceType<THREE['Texture']>) {
  return new T.MeshLambertMaterial({ map: tex, transparent: true, alphaTest: 0.5, side: T.DoubleSide })
}

/** Shared scene shell: renderer, orbit controls, lights, resize and offscreen/hidden pausing. */
export async function createStage(host: HTMLElement, onFrame?: (dt: number) => void) {
  const T = await import('three')
  const { OrbitControls } = await import('three/examples/jsm/controls/OrbitControls.js')
  const renderer = new T.WebGLRenderer({ antialias: false, alpha: true })
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2))
  host.replaceChildren(renderer.domElement)
  const scene = new T.Scene()
  scene.add(new T.AmbientLight(0xffffff, 1.4))
  const sun = new T.DirectionalLight(0xffffff, 1.6)
  sun.position.set(3, 5, 4)
  scene.add(sun)
  const camera = new T.PerspectiveCamera(35, 1, 0.01, 100)
  const controls = new OrbitControls(camera, renderer.domElement)
  controls.enableDamping = true
  controls.autoRotate = !window.matchMedia('(prefers-reduced-motion: reduce)').matches
  controls.autoRotateSpeed = 2
  controls.addEventListener('start', () => { controls.autoRotate = false })

  const resize = () => {
    const w = host.clientWidth
    const h = host.clientHeight
    renderer.setSize(w, h, false)
    camera.aspect = w / h
    camera.updateProjectionMatrix()
  }
  const ro = new ResizeObserver(resize)
  ro.observe(host)
  resize()

  let visible = true
  const io = new IntersectionObserver((e) => { visible = e[0].isIntersecting })
  io.observe(host)
  const clock = new T.Clock()
  let raf = 0
  const loop = () => {
    raf = requestAnimationFrame(loop)
    const dt = Math.min(clock.getDelta(), 0.1)
    if (!visible || document.hidden) return
    onFrame?.(dt)
    controls.update()
    renderer.render(scene, camera)
  }
  loop()

  /** Frames a bounding box: camera looks at its center from a 3/4 angle. */
  const frame = (box: InstanceType<THREE['Box3']>) => {
    const size = box.getSize(new T.Vector3())
    const center = box.getCenter(new T.Vector3())
    const r = Math.max(size.x, size.y, size.z) * 0.5 || 0.5
    const dist = r / Math.tan((camera.fov * Math.PI) / 360) * 1.6
    controls.target.copy(center)
    camera.position.set(center.x + dist * 0.6, center.y + dist * 0.45, center.z + dist * 0.8)
    controls.update()
  }

  const dispose = () => {
    cancelAnimationFrame(raf)
    ro.disconnect()
    io.disconnect()
    controls.dispose()
    scene.traverse((o) => {
      const m = o as InstanceType<THREE['Mesh']>
      m.geometry?.dispose?.()
      const mat = m.material as InstanceType<THREE['Material']> | InstanceType<THREE['Material']>[] | undefined
      for (const x of Array.isArray(mat) ? mat : mat ? [mat] : []) {
        (x as InstanceType<THREE['MeshLambertMaterial']>).map?.dispose()
        x.dispose()
      }
    })
    renderer.dispose()
    renderer.domElement.remove()
  }
  return { T, scene, frame, dispose }
}

// ---------------------------------------------------------------- blocks
/** The block's model as a mesh group in block units (cell centered on the origin), or null without a static model. */
export async function blockGroup(T: THREE, block: any, base: string, size = 1): Promise<InstanceType<THREE['Group']> | null> {
  const lowerElements: any[] = block?.model?.elements ?? []
  if (!lowerElements.length) return null
  // A block two cells tall stacks its upper half's model one cell (16 model units) above the lower half's
  const raise = (v: Vec3): Vec3 => [v[0], v[1] + 16, v[2]]
  const upperElements: any[] = (block?.upperModel?.elements ?? []).map((el: any) => ({
    ...el, from: raise(el.from), to: raise(el.to),
    rotation: el.rotation ? { ...el.rotation, origin: raise(el.rotation.origin) } : el.rotation
  }))
  const elements = [...lowerElements, ...upperElements]
  const tiles = new Set<string>()
  for (const el of elements) for (const f of Object.values<any>(el.faces)) tiles.add(f.tile)
  const mats = new Map<string, InstanceType<THREE['MeshLambertMaterial']>>()
  await Promise.all([...tiles].map(async (t) => {
    try { mats.set(t, lit(T, await loadTexture(T, `${base}/textures/blocks/${t}.png`))) } catch { /* missing tile: face skipped */ }
  }))

  const group = new T.Group()
  for (const el of elements) {
    for (const [face, f] of Object.entries<any>(el.faces)) {
      const mat = mats.get(f.tile)
      if (!mat) continue
      const pos: number[] = []
      const uvs: number[] = []
      const idx: number[] = []
      pushFace(pos, uvs, idx, face, el.from, el.to, f.uv, 16, 16, f.rotation)
      const g = new T.BufferGeometry()
      g.setAttribute('position', new T.Float32BufferAttribute(pos, 3))
      g.setAttribute('uv', new T.Float32BufferAttribute(uvs, 2))
      g.setIndex(idx)
      g.computeVertexNormals()
      if (el.rotation) {
        const o = el.rotation.origin as Vec3
        const axis = new T.Vector3(el.rotation.axis === 'x' ? 1 : 0, el.rotation.axis === 'y' ? 1 : 0, el.rotation.axis === 'z' ? 1 : 0)
        g.translate(-o[0], -o[1], -o[2])
        g.applyMatrix4(new T.Matrix4().makeRotationAxis(axis, (el.rotation.angle * Math.PI) / 180))
        g.translate(o[0], o[1], o[2])
      }
      group.add(new T.Mesh(g, mat))
    }
  }
  group.scale.setScalar(1 / 16)
  group.position.set(-size / 2, -(upperElements.length ? 2 : size) / 2, -size / 2)
  return group
}

export async function buildBlock(host: HTMLElement, block: any, base: string): Promise<Built> {
  const stage = await createStage(host)
  const { T, scene } = stage
  const size: number = block?.size ?? 1
  const group = await blockGroup(T, block, base, size)
  if (!group) {
    stage.dispose()
    throw new Error('this block has no static model')
  }
  scene.add(group)
  const h = size / 2
  const hy = block.upperModel ? 1 : h
  stage.frame(new T.Box3(new T.Vector3(-h, -hy, -h), new T.Vector3(h, hy, h)))
  const note = block.renderer !== 'model' ? 'Representative shape; the in-game form depends on placement.' : ''
  return { dispose: stage.dispose, note }
}

/** A size^3 volume of one block, one cell per brick; `setLayers(n)` shows only the lowest n layers. */
export async function buildStructure(host: HTMLElement, source: any, layers: string[][][], base: string): Promise<Built & { setLayers(n: number): void }> {
  const stage = await createStage(host)
  const { T, scene } = stage
  const cell = await blockGroup(T, source, base)
  if (!cell) {
    stage.dispose()
    throw new Error('the building block has no static model')
  }
  const size = layers.length
  const byLayer: InstanceType<THREE['Group']>[][] = []
  layers.forEach((rows, y) => {
    byLayer[y] = []
    rows.forEach((row, z) => row.forEach((c, x) => {
      if (c !== 'source') return
      const g = cell.clone()
      g.position.set(x - size / 2, y - size / 2, z - size / 2)
      scene.add(g)
      byLayer[y].push(g)
    }))
  })
  const h = size / 2
  stage.frame(new T.Box3(new T.Vector3(-h, -h, -h), new T.Vector3(h, h, h)))
  const setLayers = (n: number) => byLayer.forEach((gs, y) => gs.forEach((g) => { g.visible = y < n }))
  return { dispose: stage.dispose, setLayers }
}

// ---------------------------------------------------------------- items
export async function buildItem(host: HTMLElement, item: any, base: string): Promise<Built> {
  if (!item?.texture) throw new Error('no sprite')
  // An animated item is a grid of equally sized frames in one image; a plain sprite is a 1x1 grid.
  const sheet: Sheet = item.sheet ?? { cols: 1, rows: 1, frames: 1, mode: 'loop', fps: 8 }
  let meshes: any[] = []
  let started = 0
  const stage = await createStage(host, () => {
    if (meshes.length < 2) return
    const shown = sheetFrame(sheet, (performance.now() - started) / 1000)
    meshes.forEach((m, i) => { m.visible = i === shown })
  })
  const { T, scene } = stage
  const url = `${base}/textures/items/${item.texture}`
  const img = await new Promise<HTMLImageElement>((res, rej) => {
    const i = new Image()
    i.onload = () => res(i)
    i.onerror = () => rej(new Error('missing sprite'))
    i.src = url
  })
  const w = img.width
  const h = img.height
  const cv = document.createElement('canvas')
  cv.width = w
  cv.height = h
  const ctx = cv.getContext('2d')!
  ctx.drawImage(img, 0, 0)
  const data = ctx.getImageData(0, 0, w, h).data
  const cw = Math.floor(w / sheet.cols)
  const ch = Math.floor(h / sheet.rows)
  const px = 1 / Math.max(cw, ch)
  const depth = 1 / 16
  const geometry = new T.BoxGeometry(px, px, depth)
  const m = new T.Matrix4()
  const col = new T.Color()
  for (let f = 0; f < sheet.frames; f++) {
    const ox = (f % sheet.cols) * cw
    const oy = Math.floor(f / sheet.cols) * ch
    const solid: number[] = []
    for (let y = 0; y < ch; y++) {
      for (let x = 0; x < cw; x++) if (data[((oy + y) * w + ox + x) * 4 + 3] > 127) solid.push(x, y)
    }
    const mesh = new T.InstancedMesh(geometry, new T.MeshLambertMaterial(), solid.length / 2)
    for (let n = 0; n < solid.length / 2; n++) {
      const x = solid[n * 2]
      const y = solid[n * 2 + 1]
      const o = ((oy + y) * w + ox + x) * 4
      m.makeTranslation((x - cw / 2 + 0.5) * px, (ch / 2 - y - 0.5) * px, 0)
      mesh.setMatrixAt(n, m)
      mesh.setColorAt(n, col.setRGB(data[o] / 255, data[o + 1] / 255, data[o + 2] / 255, T.SRGBColorSpace))
    }
    mesh.visible = f === 0
    scene.add(mesh)
    meshes.push(mesh)
  }
  started = performance.now()
  // Frame the first frame: every frame shares the cell size, so the view never shifts as it animates.
  stage.frame(new T.Box3().setFromObject(meshes[0]))
  return { dispose: stage.dispose }
}

// ---------------------------------------------------------------- entities
interface Clip { name: string; loop: string; length: number; tracks: { bone: string; channel: string; keys: Key[] }[] }

/** The entity model as a bone hierarchy in block units (feet at the origin); bones are recorded by name in <bones>. */
export async function entityRoot(T: THREE, model: any, modelFile: string, base: string, bones: Map<string, { node: any; rest: Vec3 }>) {
  // A file saved by Blockbench embeds its skin as a data URL; a generated one names the PNG beside it.
  const source: string = model.textures?.[0]?.source ?? modelFile.replace('.bbmodel', '.png')
  const tex = await loadTexture(T, source.startsWith('data:') ? source : `${base}/models/entity/${source}`)
  // Blockbench 5 keeps a group's name and origin in the root 'groups' array, an older file in the outliner node.
  const groups = new Map<string, any>((model.groups ?? []).map((g: any) => [g.uuid, g]))
  const material = lit(T, tex)
  const rw = model.resolution?.width ?? 64
  const rh = model.resolution?.height ?? 64
  const cubes = new Map<string, any>((model.elements ?? []).map((e: any) => [e.uuid, e]))

  const root = new T.Group()
  const build = (nodes: any[], parent: any, parentOrigin: Vec3) => {
    for (const n of nodes) {
      if (typeof n === 'string') {
        const el = cubes.get(n)
        if (!el || el.type !== 'cube') continue
        const pos: number[] = []
        const uvs: number[] = []
        const idx: number[] = []
        for (const [face, f] of Object.entries<any>(el.faces)) {
          if (f.texture == null || !f.uv) continue
          pushFace(pos, uvs, idx, face, el.from, el.to, f.uv, rw, rh)
        }
        const g = new T.BufferGeometry()
        g.setAttribute('position', new T.Float32BufferAttribute(pos, 3))
        g.setAttribute('uv', new T.Float32BufferAttribute(uvs, 2))
        g.setIndex(idx)
        g.computeVertexNormals()
        g.translate(-parentOrigin[0], -parentOrigin[1], -parentOrigin[2])
        g.scale(1 / 16, 1 / 16, 1 / 16)
        parent.add(new T.Mesh(g, material))
        continue
      }
      const info = n.origin ? n : groups.get(n.uuid) ?? n
      const origin = info.origin as Vec3
      const node = new T.Group()
      const rest: Vec3 = [(origin[0] - parentOrigin[0]) / 16, (origin[1] - parentOrigin[1]) / 16, (origin[2] - parentOrigin[2]) / 16]
      node.position.set(...rest)
      parent.add(node)
      bones.set(info.name, { node, rest })
      build(n.children ?? [], node, origin)
    }
  }
  build(model.outliner ?? [], root, [0, 0, 0])
  return root
}

export async function buildEntity(host: HTMLElement, modelFile: string, base: string): Promise<Built> {
  const model = await (await fetch(`${base}/models/entity/${modelFile}`)).json()
  let active: Clip | null = null
  let elapsed = 0
  let speed = 1
  let paused = false
  let loopOverride: boolean | null = null
  const bones = new Map<string, { node: any; rest: Vec3 }>()
  const previewSigns = storesPreviewSigns(model)
  const clips: Clip[] = (model.animations ?? []).map((a: any) => ({
    name: a.name, loop: a.loop ?? 'once', length: a.length ?? 0,
    tracks: Object.values<any>(a.animators ?? {}).filter((an) => an.type === 'bone' || an.keyframes).flatMap((an) =>
      (['rotation', 'position', 'scale'] as const).map((channel) => ({
        bone: an.name, channel,
        keys: (an.keyframes ?? []).filter((k: any) => k.channel === channel)
          .map((k: any) => ({ time: k.time, value: keyValue(channel, k.data_points[0], previewSigns), interpolation: k.interpolation ?? 'linear' }))
          .sort((p: Key, q: Key) => p.time - q.time)
      })).filter((t) => t.keys.length)
    )
  }))

  const modeOf = (c: Clip) => (loopOverride == null ? c.loop : loopOverride ? 'loop' : 'once')
  const pose = (c: Clip) => {
    const t = clipTime(elapsed, c.length, modeOf(c))
    for (const tr of c.tracks) {
      const b = bones.get(tr.bone)
      if (!b) continue
      const v = sample(tr.keys, t)
      if (tr.channel === 'rotation') b.node.rotation.set(...rotationToEuler(v), 'ZYX')
      else if (tr.channel === 'position') {
        const p = positionToBlocks(v)
        b.node.position.set(b.rest[0] + p[0], b.rest[1] + p[1], b.rest[2] + p[2])
      } else b.node.scale.set(...v)
    }
  }
  const stage = await createStage(host, (dt) => {
    if (!active) return
    if (!paused) elapsed += dt * speed
    pose(active)
  })
  const { T, scene } = stage

  const root = await entityRoot(T, model, modelFile, base, bones)
  scene.add(root)
  stage.frame(new T.Box3().setFromObject(root))

  const rig: EntityRig = {
    clipNames: clips.map((c) => c.name),
    play(name, s) {
      speed = s
      elapsed = 0
      active = clips.find((c) => c.name === name) ?? null
      if (!active) for (const b of bones.values()) { b.node.rotation.set(0, 0, 0); b.node.position.set(...b.rest); b.node.scale.set(1, 1, 1) }
    },
    setSpeed(s) { speed = s },
    setPaused(p) { paused = p },
    setLoop(l) {
      // Switching a finished play back to looping must not jump: wrap the clock into the clip first.
      if (active && l && !(loopOverride ?? active.loop === 'loop')) elapsed = clipTime(elapsed, active.length, 'once')
      loopOverride = l
    },
    length() { return active?.length ?? 0 },
    time() { return active ? clipTime(elapsed, active.length, modeOf(active)) : 0 },
    seek(sec) { if (active) { elapsed = Math.max(0, sec); pose(active) } },
    clipLoops() { return active ? modeOf(active) === 'loop' : false }
  }
  return { dispose: stage.dispose, rig }
}
