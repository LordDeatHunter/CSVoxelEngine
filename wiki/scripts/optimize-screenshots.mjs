// Converts the game's raw scenario screenshots (visual/wiki-<scenario>/NN-<slug>.png) into the WebP files the
// wiki ships (public/screenshots/<slug>.webp). Local only: CI never runs it, it publishes the committed WebP.
//   npm run shots                     reads ../OpenGLTest/visual
//   npm run shots -- <visual dir>
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import sharp from 'sharp'

const wikiRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const source = path.resolve(process.argv[2] ?? path.join(wikiRoot, '../OpenGLTest/visual'))
const outDir = path.join(wikiRoot, 'public/screenshots')
const manifest = JSON.parse(fs.readFileSync(path.join(wikiRoot, 'data/screenshots.json'), 'utf8'))

const WIDTH = 1280
const QUALITY = 78

fs.mkdirSync(outDir, { recursive: true })
let total = 0
let count = 0
const seen = new Set()
// A scenario folder is visual/wiki-<name>; earlier runs are kept aside as wiki-<name>-old-<time>.
const folders = fs.readdirSync(source).filter((d) => /^wiki-[a-z-]+$/.test(d) && !d.includes('-old-'))
for (const folder of folders) {
  for (const file of fs.readdirSync(path.join(source, folder)).filter((f) => f.endsWith('.png')).sort()) {
    const slug = file.replace(/^\d+-/, '').replace(/\.png$/, '')
    if (!(slug in manifest)) { console.warn(`skip ${folder}/${file}: '${slug}' is not in data/screenshots.json`); continue }
    const out = path.join(outDir, slug + '.webp')
    await sharp(path.join(source, folder, file)).resize({ width: WIDTH, withoutEnlargement: true }).webp({ quality: QUALITY, effort: 5 }).toFile(out)
    total += fs.statSync(out).size
    count++
    seen.add(slug)
  }
}

const missing = Object.keys(manifest).filter((s) => !seen.has(s) && !fs.existsSync(path.join(outDir, s + '.webp')))
console.log(`${count} screenshots, ${(total / 1024).toFixed(0)} KB (${count ? (total / 1024 / count).toFixed(0) : 0} KB each) -> ${path.relative(wikiRoot, outDir)}`)
if (missing.length) console.warn(`no image yet for: ${missing.join(', ')}`)
