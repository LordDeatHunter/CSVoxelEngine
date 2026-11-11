// Resolves Minecraft-style block models (parent chains, #slot textures, default UVs) into flat elements.
const FACES = ['north', 'south', 'west', 'east', 'up', 'down']

export function normalizeTexture(ref) {
  return ref.replace(/^minecraft:/, '').replace(/^blocks?\//, '')
}

/** Minecraft's default per-face UV: the crop of the tile the box face covers (the game's ModelBaker.DefaultUv). */
export function defaultUv(face, from, to) {
  switch (face) {
    case 'up': return [from[0], from[2], to[0], to[2]]
    case 'down': return [from[0], 16 - to[2], to[0], 16 - from[2]]
    case 'north': return [16 - to[0], 16 - to[1], 16 - from[0], 16 - from[1]]
    case 'south': return [from[0], 16 - to[1], to[0], 16 - from[1]]
    case 'west': return [from[2], 16 - to[1], to[2], 16 - from[1]]
    default: return [16 - to[2], 16 - to[1], 16 - from[2], 16 - from[1]]
  }
}

function resolveSlot(ref, textures, depth = 0) {
  if (typeof ref !== 'string' || depth > 16) return null
  if (!ref.startsWith('#')) return normalizeTexture(ref)
  return resolveSlot(textures[ref.slice(1)], textures, depth + 1)
}

/**
 * @param model a model object (inline) or a "block/name" path
 * @param loadModel (path) => model object, throws when missing
 * @returns {{elements: object[], textures: Record<string,string>}}
 */
export function resolveModel(model, loadModel) {
  const chain = []
  let cur = typeof model === 'string' ? loadModel(model) : model
  const seen = new Set()
  while (cur) {
    chain.push(cur)
    if (!cur.parent) break
    if (seen.has(cur.parent)) throw new Error(`model parent cycle at ${cur.parent}`)
    seen.add(cur.parent)
    cur = loadModel(cur.parent)
  }
  const textures = {}
  for (const m of [...chain].reverse()) Object.assign(textures, m.textures ?? {})
  const withElements = chain.find((m) => m.elements)
  const elements = (withElements?.elements ?? []).map((el) => {
    const faces = {}
    for (const f of FACES) {
      const face = el.faces?.[f]
      if (!face) continue
      const tile = resolveSlot(face.texture, textures)
      if (!tile) continue
      faces[f] = { tile, uv: face.uv ?? defaultUv(f, el.from, el.to), rotation: face.rotation ?? 0 }
    }
    return { from: el.from, to: el.to, rotation: el.rotation ?? null, faces }
  })
  const resolved = {}
  for (const k of Object.keys(textures)) {
    const t = resolveSlot('#' + k, textures)
    if (t) resolved[k] = t
  }
  return { elements, textures: resolved }
}
