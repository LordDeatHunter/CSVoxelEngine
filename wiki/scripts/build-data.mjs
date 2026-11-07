// Reads OpenGLTest/assets and emits the wiki's data JSON, copied assets and reference pages.
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { parseJsonc } from './jsonc.mjs'
import { resolveModel } from './models.mjs'
import { buildRecipeIndex, refKey } from './recipes.mjs'
import { structureFrom } from './structures.mjs'

const here = path.dirname(fileURLToPath(import.meta.url))
const wikiRoot = path.resolve(here, '..')
const assets = path.resolve(wikiRoot, '../OpenGLTest/assets')
const outAssets = path.join(wikiRoot, 'public/assets')
const outData = path.join(wikiRoot, 'public/data')

const readJson = (p) => parseJsonc(fs.readFileSync(p, 'utf8'), path.relative(assets, p))
const listJson = (dir) =>
  fs.existsSync(dir)
    ? fs.readdirSync(dir).filter((f) => f.endsWith('.json')).sort().map((f) => ({ id: f.slice(0, -5), file: path.join(dir, f) }))
    : []
const warnings = []
const warn = (m) => warnings.push(m)

const excluded = new Set(
  fs.readFileSync(path.join(wikiRoot, 'exclude.txt'), 'utf8').split(/\r?\n/).map((l) => l.trim()).filter((l) => l && !l.startsWith('#'))
)

// ---- content ----
const modelCache = new Map()
const loadModel = (p) => {
  const name = p.replace(/^block\//, '')
  if (!modelCache.has(name)) {
    const f = path.join(assets, 'models/block', name + '.json')
    if (!fs.existsSync(f)) throw new Error(`missing model ${p}`)
    modelCache.set(name, readJson(f))
  }
  return modelCache.get(name)
}

const blocks = {}
const structures = {}
for (const { id, file } of listJson(path.join(assets, 'data/blocks'))) {
  const d = readJson(file)
  let model = null
  try { model = d.model ? resolveModel(d.model, loadModel) : null } catch (e) { warn(`block ${id}: ${e.message}`) }
  blocks[id] = {
    id, name: d.name ?? id, renderer: d.renderer ?? 'model', hasItem: d.hasItem !== false && !d.fluid,
    hardness: d.hardness ?? 5, requiresTool: !!d.requiresTool, tools: d.tools ?? null, solid: d.solid !== false,
    fluid: !!d.fluid, emission: d.light?.emission ?? 0, facing: d.facing ?? null, loot: d.loot ?? id,
    size: d.multiblock ? d.multiblock.size ?? 2 : 1,
    biomeTint: d.biomeTint ? Object.values(d.biomeTint.faces ?? {}).map((f) => f.tile ?? f.overlay).filter(Boolean) : null,
    model
  }
  const st = structureFrom(id, d)
  if (st) structures[id] = st
}

const items = {}
for (const { id, file } of listJson(path.join(assets, 'data/items'))) {
  const d = readJson(file)
  items[id] = {
    id, name: d.name ?? id, texture: d.texture ? path.basename(d.texture) : null,
    entityDamage: d.entityDamage ?? null, blockDamage: d.blockDamage ?? null, tool: d.tool ?? null,
    fuelSeconds: d.fuel?.burnSeconds ?? null, attack: d.attack ?? null, staff: d.staff ?? null, use: d.use?.action ?? null,
    teaches: d.teaches ?? null
  }
}
const hasBlock = (n) => n in blocks
const hasItem = (n) => n in items

const loot = {}
for (const { id, file } of listJson(path.join(assets, 'data/loot'))) {
  const d = readJson(file)
  loot[id] = (d.pools ?? []).flatMap((p) => (p.entries ?? []).filter((e) => e.item).map((e) => ({
    item: refKey(e.item, hasBlock, hasItem) ?? e.item, chance: p.chance ?? 1, count: e.count ?? 1
  })))
}

const entities = {}
for (const { id, file } of listJson(path.join(assets, 'data/entities'))) {
  const d = readJson(file)
  const modelRel = d.model ?? `assets/models/entity/${id}.bbmodel`
  const models = (d.variants ?? [modelRel]).map((m) => path.basename(m))
  const clips = []
  const mf = path.join(assets, 'models/entity', models[0])
  if (fs.existsSync(mf)) for (const a of JSON.parse(fs.readFileSync(mf, 'utf8')).animations ?? []) clips.push(a.name)
  else warn(`entity ${id}: missing model ${models[0]}`)
  entities[id] = { id, name: d.name ?? id, hp: d.hp ?? null, loot: d.loot ?? null, models, clips }
}

const trees = Object.fromEntries(listJson(path.join(assets, 'data/trees')).map(({ id, file }) => {
  const d = readJson(file)
  return [id, { id, wood: d.wood, leaves: d.leaves ?? null, biomes: d.worldgen?.biomes ?? [], maxPx: d.thickness?.maxPx ?? null, maturity: d.growth?.maturitySteps ?? null }]
}))

const spells = {}
for (const kind of ['element', 'shape', 'modifier', 'trigger']) {
  for (const { id, file } of listJson(path.join(assets, 'data/spell', kind))) {
    const d = readJson(file)
    spells[`${kind}/${id}`] = { key: `${kind}/${id}`, kind, name: d.name ?? id, costShare: d.costShare, complexity: d.complexity, tags: d.tags ?? [], effect: d.effect ?? null, impact: d.impact ?? [], damageFactor: d.damageFactor ?? null, rangeFactor: d.rangeFactor ?? null }
  }
}
const moltens = Object.fromEntries(listJson(path.join(assets, 'data/molten')).map(({ id, file }) => {
  const d = readJson(file)
  return [id, { id, name: d.name ?? id, color: d.color ?? [235, 130, 60], texture: d.texture ?? null }]
}))
const tags = Object.fromEntries(listJson(path.join(assets, 'data/tags')).map(({ id, file }) => [id, readJson(file).items ?? []]))
const effects = Object.fromEntries(listJson(path.join(assets, 'data/effects')).map(({ id, file }) => {
  const d = readJson(file)
  return [id, { id, name: d.name ?? id, durationSeconds: d.durationSeconds, tickSeconds: d.tickSeconds ?? null, damageShare: d.damageShare ?? null, moveFactor: d.moveFactor ?? 1, attackFactor: d.attackFactor ?? 1 }]
}))

// ---- recipes ----
const R = (r) => refKey(r, hasBlock, hasItem) ?? (warn(`unresolved ref '${r}'`), r)
const recipes = []
const rdir = path.join(assets, 'data/recipes')
for (const { id, file } of listJson(rdir)) {
  const d = readJson(file)
  const ingredients = d.ingredients.map((i) => ({ ref: i.tag ? 'tag/' + i.tag : R(i.item), count: i.count ?? 1 }))
  recipes.push({ kind: 'craft', id, count: d.count ?? 1, outputs: [R(d.result)], inputs: ingredients.map((i) => i.ref), ingredients })
}
for (const { id, file } of listJson(path.join(rdir, 'cook'))) {
  const d = readJson(file)
  recipes.push({ kind: 'cook', id, count: d.count ?? 1, seconds: d.cookSeconds, outputs: [R(d.result)], inputs: [R(d.input)] })
}
for (const { id, file } of listJson(path.join(rdir, 'crush'))) {
  const d = readJson(file)
  recipes.push({ kind: 'crush', id, count: d.count ?? 1, outputs: [R(d.result)], inputs: [R(d.input)] })
}
for (const { id, file } of listJson(path.join(rdir, 'melt'))) {
  const d = readJson(file)
  recipes.push({ kind: 'melt', id, ml: d.amountMl, outputs: ['fluid/' + d.fluid], inputs: [R(d.input)] })
}
for (const { id, file } of listJson(path.join(rdir, 'cast'))) {
  const d = readJson(file)
  recipes.push({ kind: 'cast', id, count: d.count ?? 1, ml: d.amountMl, seconds: d.coolSeconds, outputs: [R(d.result)], inputs: [R(d.mold), 'fluid/' + d.fluid] })
}
for (const { id, file } of listJson(path.join(rdir, 'alloy'))) {
  const d = readJson(file)
  recipes.push({ kind: 'alloy', id, parts: d.parts, outputs: ['fluid/' + d.result], inputs: d.parts.map((p) => 'fluid/' + p.fluid) })
}
const recipeIndex = buildRecipeIndex(recipes)

// ---- output ----
// Only the generated top-level files: public/data/trees is written by the .NET exporter (npm run trees).
fs.mkdirSync(outData, { recursive: true })
for (const f of fs.readdirSync(outData)) if (f.endsWith('.json')) fs.rmSync(path.join(outData, f))
const writeData = (name, obj) => fs.writeFileSync(path.join(outData, name + '.json'), JSON.stringify(obj))
writeData('blocks', blocks)
writeData('structures', structures)
writeData('items', items)
writeData('entities', entities)
writeData('recipes', { recipes, ...recipeIndex })
writeData('loot', loot)
writeData('trees', trees)
writeData('spells', spells)
writeData('effects', effects)
writeData('moltens', moltens)
writeData('tags', tags)

fs.rmSync(outAssets, { recursive: true, force: true })
const copyDir = (rel) => {
  const src = path.join(assets, rel)
  if (!fs.existsSync(src)) return
  fs.cpSync(src, path.join(outAssets, rel), {
    recursive: true,
    filter: (s) => !excluded.has(path.relative(assets, s).split(path.sep).join('/'))
  })
}
copyDir('textures/blocks')
copyDir('textures/items')
copyDir('models/entity')
fs.copyFileSync(path.join(wikiRoot, '../CREDITS.md'), path.join(wikiRoot, 'credits.md'))

// ---- pages ----
const ESC = { '<': '&lt;', '>': '&gt;', '{': '&#123;', '}': '&#125;', '|': '\\|' }
const esc = (s) => String(s).replace(/[<>{}|]/g, (c) => ESC[c])
const label = (key) => {
  const [ns, id] = key.split('/')
  if (ns === 'block' && blocks[id]) return { text: blocks[id].name, link: blocks[id].hasItem ? `/game/blocks/${id}` : null }
  if (ns === 'item' && items[id]) return { text: items[id].name, link: `/game/items/${id}` }
  return { text: id ?? key, link: null }
}
const ref = (key) => {
  const l = label(key)
  return l.link ? `[${esc(l.text)}](${l.link})` : esc(l.text)
}
/** Recipe cards drawn by the RecipeCard component, which looks each recipe up by kind and id. */
const recipeCards = (list) =>
  `<div class="recipe-list">\n${list.map((r) => `<RecipeCard kind="${r.kind}" id="${r.id}" />`).join('\n')}\n</div>\n`
const recipeSection = (key) => {
  const made = recipeIndex.madeFrom[key] ?? []
  const used = recipeIndex.usedIn[key] ?? []
  let s = ''
  if (made.length) s += `\n## How to get it\n\n${recipeCards(made)}\n`
  if (used.length) s += `\n## Used in\n\n${recipeCards(used)}\n`
  const drops = Object.entries(loot).filter(([, es]) => es.some((e) => e.item === key)).map(([n]) => n)
  if (drops.length) s += `\n## Dropped by\n\nLoot tables: ${drops.map((n) => '`' + n + '`').join(', ')}\n`
  return s
}
const writePage = (rel, body) => {
  const f = path.join(wikiRoot, rel)
  fs.mkdirSync(path.dirname(f), { recursive: true })
  fs.writeFileSync(f, body)
}
for (const d of ['blocks', 'items', 'mobs', 'trees', 'structures']) fs.rmSync(path.join(wikiRoot, 'game', d), { recursive: true, force: true })

const shownBlocks = Object.values(blocks).filter((b) => b.hasItem)
for (const b of shownBlocks) {
  const tool = b.tools ? Object.entries(b.tools).map(([t, p]) => `${t} (tier ${p})`).join(', ') : null
  const note = b.renderer !== 'model'
    ? `\n::: info\nThis block's shape depends on its surroundings or state (${b.renderer}); the preview shows a representative form.\n:::\n`
    : ''
  writePage(`game/blocks/${b.id}.md`,
    `---\ntitle: ${JSON.stringify(b.name)}\n---\n\n# ${esc(b.name)}\n\n<ModelViewer kind="block" id="${b.id}" />\n\n` +
    `| Property | Value |\n|---|---|\n| Hardness | ${b.hardness} |\n| Needs tool | ${b.requiresTool ? tool ?? 'yes' : 'no'} |\n| Light | ${b.emission || 'none'} |\n` +
    note + recipeSection('block/' + b.id))
}
writePage('game/blocks/index.md', '# Blocks\n\n<ItemGrid kind="blocks" />\n')

for (const i of Object.values(items)) {
  const stats = [
    i.blockDamage != null && `| Block damage | ${i.blockDamage} |`,
    i.entityDamage != null && `| Mob damage | ${i.entityDamage} |`,
    i.tool && `| Mining tier | ${i.tool.power ?? 0.5} |`,
    i.fuelSeconds && `| Fuel | ${i.fuelSeconds}s |`,
    i.attack && `| Attack | ${i.attack} |`
  ].filter(Boolean)
  writePage(`game/items/${i.id}.md`,
    `---\ntitle: ${JSON.stringify(i.name)}\n---\n\n# ${esc(i.name)}\n\n<ModelViewer kind="item" id="${i.id}" />\n\n` +
    (stats.length ? `| Property | Value |\n|---|---|\n${stats.join('\n')}\n` : '') + recipeSection('item/' + i.id))
}
writePage('game/items/index.md', '# Items\n\n<ItemGrid kind="items" />\n')

for (const e of Object.values(entities)) {
  const drops = e.loot && loot[e.loot] ? loot[e.loot].map((d) => ref(d.item)).join(', ') : null
  writePage(`game/mobs/${e.id}.md`,
    `---\ntitle: ${JSON.stringify(e.name)}\n---\n\n# ${esc(e.name)}\n\n<ModelViewer kind="entity" id="${e.id}" />\n\n` +
    `| Property | Value |\n|---|---|\n${e.hp != null ? `| Health | ${e.hp} |\n` : ''}${e.clips.length ? `| Animations | ${e.clips.join(', ')} |\n` : ''}` +
    (drops ? `\n## Drops\n\n${drops}\n` : ''))
}
writePage('game/mobs/index.md', '# Mobs\n\n<MobGrid />\n')

// ---- generated overview pages (game/reference/) ----
fs.rmSync(path.join(wikiRoot, 'game/reference'), { recursive: true, force: true })
const table = (head, rows) => `| ${head.join(' | ')} |\n|${head.map(() => '---').join('|')}|\n${rows.map((r) => `| ${r.join(' | ')} |`).join('\n')}\n`
const pct = (x) => `${Math.round(x * 100)}%`
const title = (s) => s.charAt(0).toUpperCase() + s.slice(1)

for (const st of Object.values(structures)) {
  const rows = [['Size', `${st.size}×${st.size}×${st.size}`], ...st.notes]
  writePage(`game/structures/${st.id}.md`,
    `---
title: ${JSON.stringify(st.name)}
---

# ${esc(st.name)}

<StructureViewer id="${st.id}" />

` +
    `## Properties

${table(['Property', 'Value'], rows)}
## How to build

` +
    `Craft the bricks, stack them into a ${st.size}×${st.size}×${st.size} cube and right-click a front face with the multiblock converter. The opening ends up facing you.
`)
}
writePage('game/structures/index.md', `# Structures

Multiblocks are built from ordinary blocks and converted in place.

` +
  table(['Structure', 'Size', 'Built from'], Object.values(structures).map((st) => [`[${esc(st.name)}](/game/structures/${st.id})`, `${st.size}×${st.size}×${st.size}`, ref('block/' + st.source)])))


writePage('game/reference/trees.md',
  `# Trees\n\nTrees are grown, not placed from templates: each species is a data file describing how its trunk and branches grow, and every tree in the world is the deterministic result for its position and the world seed.\n\n` +
  table(['Species', 'Wood', 'Leaves', 'Grows in', 'Max trunk (px)', 'Steps to mature'],
    Object.values(trees).map((t) => [`[${title(t.id.replace(/_/g, ' '))}](/game/trees/${t.id})`, ref('block/' + String(t.wood).replace(/^block\//, '')),
      t.leaves ? ref('block/' + String(t.leaves).replace(/^block\//, '')) : '—',
      t.biomes.length ? t.biomes.map(title).join(', ') : 'special placement', t.maxPx ?? '—', t.maturity ? `${t.maturity[0]}–${t.maturity[1]}` : '—'])) +
  `\nOpen a species to watch a sample tree grow. Species with no biome are never planted by ordinary worldgen: the baobab can only be grown, the palm appears at oases, and the hell and sky trees come from their realms' biomes.\n`)

const blockRef = (b) => ref('block/' + String(b).replace(/^block\//, ''))
for (const t of Object.values(trees)) {
  const name = title(t.id.replace(/_/g, ' '))
  writePage(`game/trees/${t.id}.md`,
    `---\ntitle: ${JSON.stringify(name)}\n---\n\n# ${esc(name)}\n\n<TreeViewer id="${t.id}" />\n\n` +
    table(['Property', 'Value'], [
      ['Wood', blockRef(t.wood)],
      ['Leaves', t.leaves ? blockRef(t.leaves) : 'none'],
      ['Grows in', t.biomes.length ? t.biomes.map(title).join(', ') : 'special placement'],
      ['Largest trunk', t.maxPx ? `${t.maxPx} px across` : '—'],
      ['Steps to mature', t.maturity ? `${t.maturity[0]}–${t.maturity[1]} (depends on the tree)` : '—']
    ]) +
    `\n::: info\nThe sample is the tree the real growth engine produces for one fixed seed, standing on flat ground, and the slider replays its growth step by step. Wide-trunk bevels are drawn as plain cubes here, and every tree in the world is a little different.\n:::\n`)
}
const partRows = (kind) => Object.values(spells).filter((s) => s.kind === kind).map((s) => [
  esc(s.name), pct(s.costShare), s.complexity, s.tags.join(', '),
  [s.effect && `applies ${s.effect}`, s.impact.length && `impact: ${s.impact.join(', ')}`, s.damageFactor && `damage ×${s.damageFactor}`, s.rangeFactor && `range ×${s.rangeFactor}`].filter(Boolean).join('; ') || '—'])
writePage('game/reference/magic.md',
  `# Magic\n\nA spell is built at a **spell table** with a staff in hand: one element, one shape, any modifiers and optionally a trigger. Each part adds to the mana cost (a share of the starting mana pool) and to the spell's complexity; past a threshold, complex spells can fizzle and backfire. Parts are learned from scrolls.\n\n` +
  ['element', 'shape', 'modifier', 'trigger'].map((k) => {
    const rows = partRows(k)
    return rows.length ? `## ${title(k)}s\n\n${table(['Part', 'Cost share', 'Complexity', 'Staff tags', 'Effect'], rows)}` : ''
  }).join('\n') +
  `\n## Status effects\n\n${table(['Effect', 'Duration', 'Damage', 'Move speed', 'Attack speed'], Object.values(effects).map((e) => [
    esc(e.name), `${e.durationSeconds}s`, e.damageShare ? `${pct(e.damageShare)} of max health every ${e.tickSeconds}s` : '—', pct(e.moveFactor), pct(e.attackFactor)]))}\n` +
  `\n## Staffs\n\n${Object.values(items).filter((i) => i.staff).map((i) => `- ${ref('item/' + i.id)}: ${i.staff.slots} spell slots, tier ${i.staff.tier}, allows ${i.staff.tags.join(', ')} parts`).join('\n')}\n`)

const byKind = (k) => recipes.filter((r) => r.kind === k)
const kindText = {
  craft: ['Crafting', 'Shapeless recipes made in the inventory.'],
  cook: ['Campfire cooking', 'Right-click a lit branch pile with the item.'],
  crush: ['Quern', 'Crushing in a quern.'],
  melt: ['Melting', 'Put the item in a crucible inside a lit kiln.'],
  cast: ['Casting', 'Pour molten metal from a crucible into a fired mold.'],
  alloy: ['Alloys', 'Only made in the alloy furnace: a full set of the parts becomes the same total amount of the result.']
}
writePage('game/reference/crafting.md',
  `# Recipes\n\n` + Object.keys(kindText).map((k) => {
    const rs = byKind(k)
    if (!rs.length) return ''
    return `## ${kindText[k][0]}\n\n${kindText[k][1]}\n\n${recipeCards(rs)}`
  }).join('\n'))

for (const w of warnings) console.warn('warn:', w)
console.log(`wiki data: ${shownBlocks.length} blocks, ${Object.keys(items).length} items, ${Object.keys(entities).length} entities, ${recipes.length} recipes, ${warnings.length} warnings`)
