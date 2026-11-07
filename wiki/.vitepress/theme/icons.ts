// Shared data loading and block icons for the recipe cards and item grids. Block icons are rendered once
// each, from the block's real model, by one offscreen WebGL renderer into a data URL and then cached.
import { withBase } from 'vitepress'
import { blockGroup } from './viewer3d.ts'

const dataCache = new Map<string, Promise<any>>()

/** Fetches /data/<name>.json once per page load. */
export function loadData(name: string): Promise<any> {
  let p = dataCache.get(name)
  if (!p) {
    p = fetch(withBase(`/data/${name}.json`)).then((r) => {
      if (!r.ok) throw new Error(`${name}.json: ${r.status}`)
      return r.json()
    })
    dataCache.set(name, p)
  }
  return p
}

export const assetBase = () => withBase('/assets')

/** The sprite URL of an item, or null when it has none. */
export const itemSprite = (item: any): string | null => (item?.texture ? `${assetBase()}/textures/items/${item.texture}` : null)

type THREE = typeof import('three')

let renderer: InstanceType<THREE['WebGLRenderer']> | null = null
let queue: Promise<unknown> = Promise.resolve()
const icons = new Map<string, Promise<string | null>>()

const ICON_SIZE = 128

async function render(id: string): Promise<string | null> {
  const T = await import('three')
  const block = (await loadData('blocks'))[id]
  const group = await blockGroup(T, block, assetBase())
  if (!group) return null

  if (!renderer) {
    renderer = new T.WebGLRenderer({ alpha: true, antialias: true, preserveDrawingBuffer: true })
    renderer.setPixelRatio(1)
    renderer.setSize(ICON_SIZE, ICON_SIZE, false)
    renderer.setClearColor(0x000000, 0)
  }

  const scene = new T.Scene()
  scene.add(new T.AmbientLight(0xffffff, 1.5))
  const sun = new T.DirectionalLight(0xffffff, 1.7)
  sun.position.set(2, 5, 3)
  scene.add(sun)
  scene.add(group)

  // Fit any block, including multiblock structures several cells wide, in an isometric view.
  const box = new T.Box3().setFromObject(group)
  const center = box.getCenter(new T.Vector3())
  const radius = box.getSize(new T.Vector3()).length() / 2 || 0.5
  const camera = new T.OrthographicCamera(-radius, radius, radius, -radius, 0.1, 100)
  camera.position.copy(center).addScaledVector(new T.Vector3(1, 0.82, 1).normalize(), 20)
  camera.lookAt(center)
  renderer.render(scene, camera)
  const url = renderer.domElement.toDataURL('image/png')

  group.traverse((o) => {
    const m = o as InstanceType<THREE['Mesh']>
    m.geometry?.dispose?.()
    const mat = m.material as InstanceType<THREE['MeshLambertMaterial']> | undefined
    mat?.map?.dispose()
    mat?.dispose?.()
  })
  return url
}

/** A data URL of the block's isometric icon (rendered on first request, queued one at a time), or null. */
export function blockIcon(id: string): Promise<string | null> {
  let p = icons.get(id)
  if (!p) {
    p = queue.then(() => render(id)).catch(() => null)
    queue = p
    icons.set(id, p)
  }
  return p
}

/** Calls back once when the element is near the viewport (or at once without IntersectionObserver). */
export function whenVisible(el: Element, run: () => void): () => void {
  if (typeof IntersectionObserver === 'undefined') { run(); return () => {} }
  const io = new IntersectionObserver((entries) => {
    if (entries.some((e) => e.isIntersecting)) { io.disconnect(); run() }
  }, { rootMargin: '200px' })
  io.observe(el)
  return () => io.disconnect()
}
