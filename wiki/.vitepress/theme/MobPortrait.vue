<script setup lang="ts">
// A mob's portrait image, rendered from its model when it scrolls into view.
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { mobIcon, whenVisible } from './icons.ts'

const props = withDefaults(defineProps<{ id: string; size?: number }>(), { size: 96 })

const root = ref<HTMLElement>()
const src = ref<string | null>(null)
const missing = ref(false)
let stop: (() => void) | null = null

onMounted(() => {
  if (!root.value) return
  stop = whenVisible(root.value, async () => {
    src.value = await mobIcon(props.id)
    missing.value = !src.value
  })
})
onBeforeUnmount(() => stop?.())
</script>

<template>
  <span ref="root" class="mp" :style="{ width: size + 'px', height: size + 'px' }">
    <img v-if="src" :src="src" alt="" draggable="false" />
    <span v-else-if="missing" class="mp-missing">?</span>
  </span>
</template>

<style scoped>
.mp { display: inline-flex; align-items: center; justify-content: center; }
.mp img { width: 100%; height: 100%; object-fit: contain; image-rendering: pixelated; }
.mp-missing { font-weight: 700; color: var(--vp-c-text-3); }
</style>
