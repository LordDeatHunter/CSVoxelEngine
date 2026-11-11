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
const paused = ref(false)
const looping = ref(false)
const length = ref(0)
const time = ref(0)
let scrubbing = false
let ticker: ReturnType<typeof setInterval> | null = null
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
      onClip()
    }
    if (cancelled) { result.dispose(); return }
    dispose = result.dispose
    status.value = result.note ?? ''
  } catch (e) {
    status.value = `3D preview unavailable (${(e as Error).message})`
  }
}

function onClip() {
  if (!rig) return
  rig.play(clip.value, speed.value)
  rig.setPaused(paused.value)
  length.value = rig.length()
  looping.value = rig.clipLoops()
  time.value = 0
}
function onSpeed() { rig?.setSpeed(speed.value) }
function togglePause() { paused.value = !paused.value; rig?.setPaused(paused.value) }
function toggleLoop() { looping.value = !looping.value; rig?.setLoop(looping.value) }
function onScrubStart() { scrubbing = true }
function onScrub() { rig?.seek(time.value) }
function onScrubEnd() { scrubbing = false }
function onModel() { mount() }

onMounted(() => {
  const io = new IntersectionObserver((entries) => {
    if (entries[0].isIntersecting && !dispose && !cancelled) { io.disconnect(); mount() }
  })
  if (host.value) io.observe(host.value)
  ticker = setInterval(() => { if (rig && !scrubbing && !paused.value) time.value = rig.time() }, 80)
  onBeforeUnmount(() => io.disconnect())
})
onBeforeUnmount(() => { cancelled = true; dispose?.(); if (ticker) clearInterval(ticker) })
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
      <label v-if="clips.length" class="mv-slider">Speed
        <input v-model.number="speed" type="range" min="0" max="3" step="0.1" @input="onSpeed" />
        <output>{{ speed.toFixed(1) }}x</output>
      </label>
    </div>
    <div v-if="kind === 'entity' && clips.length && clip" class="mv-controls mv-timeline">
      <button type="button" class="mv-btn mv-icon" :aria-label="paused ? 'Play' : 'Pause'" :title="paused ? 'Play' : 'Pause'" @click="togglePause">
        <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true" fill="currentColor">
          <path v-if="paused" d="M7 4.5v15l12-7.5z" />
          <path v-else d="M6 4.5h4v15H6zM14 4.5h4v15h-4z" />
        </svg>
      </button>
      <label class="mv-slider mv-grow">Progress
        <input v-model.number="time" type="range" min="0" :max="length" step="0.01"
          @pointerdown="onScrubStart" @pointerup="onScrubEnd" @input="onScrub" />
        <output>{{ time.toFixed(2) }} / {{ length.toFixed(2) }} s</output>
      </label>
      <button type="button" class="mv-btn mv-switch" role="switch" :aria-checked="looping" @click="toggleLoop">
        <span class="mv-knob" /> Loop
      </button>
    </div>
    <p class="mv-hint">Drag to rotate, scroll to zoom.</p>
  </div>
</template>

<style scoped>
.model-viewer { margin: 16px 0; }
.mv-canvas { width: 100%; height: 360px; border-radius: 8px; background: var(--vp-c-bg-soft); overflow: hidden; cursor: grab; }
.mv-canvas:active { cursor: grabbing; }
.mv-status, .mv-hint { margin: 6px 0 0; font-size: 12px; color: var(--vp-c-text-2); }
.mv-controls { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; margin-top: 8px; font-size: 13px; }
.mv-controls label { display: inline-flex; align-items: center; gap: 6px; line-height: 1; }
.mv-slider input { vertical-align: middle; margin: 0; }
.mv-slider output { font-variant-numeric: tabular-nums; color: var(--vp-c-text-2); min-width: 3.2em; }
.mv-grow { flex: 1 1 260px; }
.mv-grow input { flex: 1; }
.mv-btn {
  display: inline-flex; align-items: center; gap: 8px; padding: 4px 12px; line-height: 1.4;
  border: 1px solid var(--vp-c-divider); border-radius: 6px; background: var(--vp-c-bg-soft); cursor: pointer;
}
.mv-icon { padding: 6px 10px; }
.mv-btn:hover { border-color: var(--vp-c-brand-1); }
.mv-knob { position: relative; width: 28px; height: 16px; border-radius: 8px; background: var(--vp-c-divider); transition: background .15s; }
.mv-knob::after { content: ''; position: absolute; top: 2px; left: 2px; width: 12px; height: 12px; border-radius: 50%; background: #fff; transition: transform .15s; }
.mv-switch[aria-checked='true'] .mv-knob { background: var(--vp-c-brand-1); }
.mv-switch[aria-checked='true'] .mv-knob::after { transform: translateX(12px); }
</style>
