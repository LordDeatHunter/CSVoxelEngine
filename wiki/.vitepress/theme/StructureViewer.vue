<script setup lang="ts">
// A multiblock's page body: the full 3D model, a layer-by-layer slice of its building blocks and the materials.
import { nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { withBase } from 'vitepress'
import ModelViewer from './ModelViewer.vue'
import { buildStructure } from './viewer3d'

const props = defineProps<{ id: string }>()
const st = ref<any>(null)
const sourceName = ref('')
const host = ref<HTMLDivElement>()
const shown = ref(1)
const status = ref('')
let setLayers: ((n: number) => void) | null = null
let dispose: (() => void) | null = null
let gone = false

onMounted(async () => {
  const get = async (n: string) => (await fetch(withBase(`/data/${n}.json`))).json()
  st.value = (await get('structures'))[props.id]
  if (!st.value) return
  const blocks = await get('blocks')
  sourceName.value = blocks[st.value.source]?.name ?? st.value.source
  shown.value = st.value.size
  await nextTick()
  try {
    const r = await buildStructure(host.value!, blocks[st.value.source], st.value.layers, withBase('/assets'))
    if (gone) { r.dispose(); return }
    dispose = r.dispose
    setLayers = r.setLayers
  } catch (e) {
    status.value = `3D preview unavailable (${(e as Error).message})`
  }
})
onBeforeUnmount(() => { gone = true; dispose?.() })
function onLayers() { setLayers?.(shown.value) }
</script>

<template>
  <ModelViewer kind="block" :id="id" />
  <template v-if="st">
    <h2 id="layer-by-layer">Layer by layer</h2>
    <div ref="host" class="sv-3d" />
    <label class="sv-slider">Top layer: {{ shown }} of {{ st.size }}
      <input v-model.number="shown" type="range" min="1" :max="st.size" step="1" @input="onLayers" />
    </label>
    <p v-if="status" class="sv-hint">{{ status }}</p>
    <p class="sv-hint">Drag the slider to build the structure up layer by layer.</p>
    <h2 id="materials">Materials</h2>
    <table>
      <thead><tr><th>Block</th><th>Count</th></tr></thead>
      <tbody>
        <tr>
          <td><a :href="withBase(`/game/blocks/${st.source}`)">{{ sourceName }}</a></td>
          <td>{{ st.required }}<span v-if="st.optional"> (+{{ st.optional }} optional centre)</span></td>
        </tr>
      </tbody>
    </table>
  </template>
</template>

<style scoped>
.sv-3d { width: 100%; height: 340px; border-radius: 8px; background: var(--vp-c-bg-soft); overflow: hidden; cursor: grab; }
.sv-slider { display: flex; align-items: center; gap: 12px; margin: 8px 0; font-size: 13px; }
.sv-slider input { flex: 1; max-width: 280px; }
.sv-hint { color: var(--vp-c-text-2); font-size: 13px; margin: 0 0 8px; }
</style>
