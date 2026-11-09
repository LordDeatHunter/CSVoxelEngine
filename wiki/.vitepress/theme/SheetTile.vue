<script setup lang="ts">
// An animated block with no item (the lava): one square tile that cycles through its spritesheet's frames
// at the block's own rate, the way the game's fluid shader does.
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { withBase } from 'vitepress'
import { loadData, whenVisible } from './icons.ts'
import { sheetFrame, sheetStyle, type Sheet } from './animation.ts'

const props = withDefaults(defineProps<{ block: string; size?: number }>(), { size: 128 })

const root = ref<HTMLElement>()
const style = ref<Record<string, string>>({})
let stopWatch: (() => void) | null = null
let timer: ReturnType<typeof setInterval> | null = null

async function load() {
  const b = (await loadData('blocks'))[props.block]
  if (!b?.sheet || !b.sheetTexture) return
  const sheet: Sheet = b.sheet
  const url = withBase(`/assets/textures/blocks/${b.sheetTexture}.png`)
  const t0 = performance.now()
  const show = () => { style.value = sheetStyle(url, sheet, sheetFrame(sheet, (performance.now() - t0) / 1000)) }
  show()
  if (sheet.frames > 1) timer = setInterval(show, 60)
}

onMounted(() => { if (root.value) stopWatch = whenVisible(root.value, load) })
onBeforeUnmount(() => { stopWatch?.(); if (timer) clearInterval(timer) })
</script>

<template>
  <div ref="root" class="sheet-tile" role="img" :aria-label="block" :style="{ width: `${size}px`, height: `${size}px`, ...style }" />
</template>

<style scoped>
.sheet-tile { image-rendering: pixelated; border-radius: 6px; margin: 0 auto; }
</style>
