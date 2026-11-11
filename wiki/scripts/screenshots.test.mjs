import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const manifest = JSON.parse(fs.readFileSync(path.join(root, 'data/screenshots.json'), 'utf8'))

/** Every hand-written page under the wiki, and its <ShotGallery> uses. */
function galleryUses() {
  const uses = []
  const walk = (dir) => {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      if (['node_modules', '.vitepress', 'public'].includes(entry.name)) continue
      const p = path.join(dir, entry.name)
      if (entry.isDirectory()) walk(p)
      else if (entry.name.endsWith('.md')) {
        for (const m of fs.readFileSync(p, 'utf8').matchAll(/<ShotGallery\b([^>]*)\/>/g)) {
          uses.push({ page: path.relative(root, p), slugs: /slugs="([^"]*)"/.exec(m[1])?.[1], group: /group="([^"]*)"/.exec(m[1])?.[1] })
        }
      }
    }
  }
  walk(root)
  return uses
}

test('every screenshot in the manifest has its image file', () => {
  const missing = Object.keys(manifest).filter((slug) => !fs.existsSync(path.join(root, 'public/screenshots', slug + '.webp')))
  assert.deepEqual(missing, [], `captured images missing for: ${missing.join(', ')}`)
})

test('every gallery on a page names shots and groups that exist', () => {
  const groups = new Set(Object.values(manifest).map((s) => s.group))
  for (const use of galleryUses()) {
    for (const slug of use.slugs?.split(',').map((s) => s.trim()) ?? []) {
      assert.ok(slug in manifest, `${use.page} names the unknown shot '${slug}'`)
    }
    if (use.group) assert.ok(groups.has(use.group), `${use.page} names the unknown group '${use.group}'`)
    assert.ok(use.slugs || use.group, `${use.page} has a gallery with neither slugs nor group`)
  }
})

test('every screenshot has a title and a caption', () => {
  for (const [slug, s] of Object.entries(manifest)) {
    assert.ok(s.title && s.caption && s.group, `${slug} is missing its title, caption or group`)
  }
})
