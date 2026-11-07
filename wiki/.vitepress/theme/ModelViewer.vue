<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { withBase } from 'vitepress'
import { buildBlock, buildItem, buildEntity, type EntityRig } from './viewer3d'

const props = defineProps<{ kind: 'block' | 'item' | 'entity'; id: string }>()

const host = ref<HTMLDivElement>()
const status = ref('Loading 3D preview...')
const clips = ref<string[]>([])
const clip = ref('')
const speed = ref(1)
const models = ref<string[]>([])
const modelIndex = ref(0)

let dispose: (() => void) | null = null
let rig: EntityRig | null = null
let cancelled = false

async function fetchJson(name: string) {
  const r = await fetch(withBase(`/data/${name}.json`))
  if (!r.ok) throw new Error(`${name}.json: ${r.status}`)
  return r.json()
}

async function mount() {
  dispose?.()
  dispose = null
  rig = null
  if (!host.value) return
  try {
    const base = withBase('/assets')
    let result
    if (props.kind === 'block') {
      const block = (await fetchJson('blocks'))[props.id]
      result = await buildBlock(host.value, block, base)
    } else if (props.kind === 'item') {
      const item = (await fetchJson('items'))[props.id]
      result = await buildItem(host.value, item, base)
    } else {
      const entity = (await fetchJson('entities'))[props.id]
      models.value = entity.models
      result = await buildEntity(host.value, entity.models[modelIndex.value], base)
      rig = result.rig ?? null
      clips.value = rig?.clipNames ?? []
      clip.value = clips.value.includes('idle') ? 'idle' : ''
      rig?.play(clip.value, speed.value)
    }
    if (cancelled) { result.dispose(); return }
    dispose = result.dispose
    status.value = result.note ?? ''
  } catch (e) {
    status.value = `3D preview unavailable (${(e as Error).message})`
  }
}

function onClip() { rig?.play(clip.value, speed.value) }
function onSpeed() { rig?.setSpeed(speed.value) }
function onModel() { mount() }

onMounted(() => {
  const io = new IntersectionObserver((entries) => {
    if (entries[0].isIntersecting && !dispose && !cancelled) { io.disconnect(); mount() }
  })
  if (host.value) io.observe(host.value)
  onBeforeUnmount(() => io.disconnect())
})
onBeforeUnmount(() => { cancelled = true; dispose?.() })
</script>

<template>
  <div class="model-viewer">
    <div ref="host" class="mv-canvas" />
    <p v-if="status" class="mv-status">{{ status }}</p>
    <div v-if="kind === 'entity'" class="mv-controls">
      <label v-if="models.length > 1">Variant
        <select v-model.number="modelIndex" @change="onModel">
          <option v-for="(m, i) in models" :key="m" :value="i">{{ m.replace('.bbmodel', '') }}</option>
        </select>
      </label>
      <label v-if="clips.length">Animation
        <select v-model="clip" @change="onClip">
          <option value="">(bind pose)</option>
          <option v-for="c in clips" :key="c" :value="c">{{ c }}</option>
        </select>
      </label>
      <label v-if="clips.length">Speed
        <input v-model.number="speed" type="range" min="0" max="3" step="0.1" @input="onSpeed" />
      </label>
    </div>
    <p class="mv-hint">Drag to rotate, scroll to zoom.</p>
  </div>
</template>

<style scoped>
.model-viewer { margin: 16px 0; }
.mv-canvas { width: 100%; height: 360px; border-radius: 8px; background: var(--vp-c-bg-soft); overflow: hidden; cursor: grab; }
.mv-canvas:active { cursor: grabbing; }
.mv-status, .mv-hint { margin: 6px 0 0; font-size: 12px; color: var(--vp-c-text-2); }
.mv-controls { display: flex; flex-wrap: wrap; gap: 12px; margin-top: 8px; font-size: 13px; }
.mv-controls select { margin-left: 6px; }
</style>
