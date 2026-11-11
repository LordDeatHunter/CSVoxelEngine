<script setup lang="ts">
// One icon for a content reference: block/<id> (rendered isometric cube), item/<id> (its sprite),
// fluid/<id> (a molten-metal drop) or tag/<name> (cycles through the tag's members).
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { blockIcon, itemSprite, loadData, whenVisible } from './icons.ts'
import { sheetFrame, sheetStyle, type Sheet } from './animation.ts'

const props = withDefaults(defineProps<{ target: string; size?: number }>(), { size: 40 })

const root = ref<HTMLElement>()
const src = ref<string | null>(null)
const color = ref('#e0813c')
const sprites = ref<string[]>([])
const cycle = ref(0)
const missing = ref(false)
// An animated item (a spritesheet) shows its frames in turn instead of the whole sheet image.
const sheet = ref<Sheet | null>(null)
const frame = ref(0)

const kind = computed(() => props.target.split('/')[0])
const id = computed(() => props.target.slice(props.target.indexOf('/') + 1))

let stopWatch: (() => void) | null = null
let timer: ReturnType<typeof setInterval> | null = null
let sheetTimer: ReturnType<typeof setInterval> | null = null

function startSheet(s: Sheet) {
  const t0 = performance.now()
  frame.value = 0
  if (s.frames > 1) sheetTimer = setInterval(() => { frame.value = sheetFrame(s, (performance.now() - t0) / 1000) }, 60)
}

async function load() {
  try {
    if (kind.value === 'block') {
      src.value = await blockIcon(id.value)
      missing.value = !src.value
    } else if (kind.value === 'item') {
      const item = (await loadData('items'))[id.value]
      src.value = itemSprite(item)
      sheet.value = item?.sheet ?? null
      if (sheet.value) startSheet(sheet.value)
      missing.value = !src.value
    } else if (kind.value === 'fluid') {
      const m = (await loadData('moltens'))[id.value]
      if (m) color.value = `rgb(${m.color.join(',')})`
    } else if (kind.value === 'tag') {
      const items = await loadData('items')
      const members: string[] = (await loadData('tags'))[id.value] ?? []
      sprites.value = members.map((m) => itemSprite(items[m.replace(/^item\//, '')])).filter((s): s is string => !!s)
      if (sprites.value.length > 1) timer = setInterval(() => { cycle.value = (cycle.value + 1) % sprites.value.length }, 1100)
      missing.value = sprites.value.length === 0
    }
  } catch {
    missing.value = true
  }
}

onMounted(() => { if (root.value) stopWatch = whenVisible(root.value, load) })
onBeforeUnmount(() => { stopWatch?.(); if (timer) clearInterval(timer); if (sheetTimer) clearInterval(sheetTimer) })
</script>

<template>
  <span ref="root" class="gi" :style="{ width: size + 'px', height: size + 'px' }">
    <img v-if="kind === 'block' && src" :src="src" alt="" class="gi-block" draggable="false" />
    <span v-else-if="kind === 'item' && src && sheet" class="gi-item gi-sheet" :style="sheetStyle(src, sheet, frame)" role="img" />
    <img v-else-if="kind === 'item' && src" :src="src" alt="" class="gi-item" draggable="false" />
    <img v-else-if="kind === 'tag' && sprites.length" :src="sprites[cycle]" alt="" class="gi-item" draggable="false" />
    <svg v-else-if="kind === 'fluid'" viewBox="0 0 24 24" class="gi-fluid" aria-hidden="true">
      <path d="M12 2.5c3.2 4.3 6.5 7.6 6.5 11.6A6.5 6.5 0 0 1 12 20.6a6.5 6.5 0 0 1-6.5-6.5C5.5 10.1 8.8 6.8 12 2.5z" :fill="color" stroke="rgba(0,0,0,.55)" stroke-width="1.3" />
      <path d="M9 13.5a3 3 0 0 0 2 2.8" fill="none" stroke="rgba(255,255,255,.55)" stroke-width="1.4" stroke-linecap="round" />
    </svg>
    <span v-else-if="missing" class="gi-missing">?</span>
  </span>
</template>

<style scoped>
.gi { display: inline-flex; align-items: center; justify-content: center; flex: none; }
.gi img, .gi svg { width: 100%; height: 100%; object-fit: contain; display: block; }
.gi-item { image-rendering: pixelated; }
.gi-sheet { display: block; width: 100%; height: 100%; }
.gi-missing { font-weight: 700; color: var(--vp-c-text-3); }
</style>
