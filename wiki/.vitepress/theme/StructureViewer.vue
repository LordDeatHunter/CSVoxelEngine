<script setup lang="ts">
// A multiblock's page body: the full 3D model, a layer-by-layer slice of its building blocks and the materials.
import { onMounted, ref } from 'vue'
import { withBase } from 'vitepress'
import GameIcon from './GameIcon.vue'
import ModelViewer from './ModelViewer.vue'
import RecipeCard from './RecipeCard.vue'

const props = defineProps<{ id: string }>()
const st = ref<any>(null)
const sourceName = ref('')

onMounted(async () => {
  const get = async (n: string) => (await fetch(withBase(`/data/${n}.json`))).json()
  st.value = (await get('structures'))[props.id]
  if (st.value) sourceName.value = (await get('blocks'))[st.value.source]?.name ?? st.value.source
})
</script>

<template>
  <ModelViewer kind="block" :id="id" />
  <template v-if="st">
    <h2 id="layer-by-layer">Layer by layer</h2>
    <p class="sv-hint">Bottom layer first, seen from above. The front edge is the opening.</p>
    <div class="sv-layers">
      <figure v-for="(layer, y) in st.layers" :key="y" class="sv-layer">
        <figcaption>Layer {{ y + 1 }}{{ y === 0 ? ' (bottom)' : y === st.size - 1 ? ' (top)' : '' }}</figcaption>
        <div class="sv-grid" :style="{ gridTemplateColumns: `repeat(${st.size}, 44px)` }">
          <div v-for="(cell, i) in layer.flat()" :key="i" class="sv-cell" :class="{ 'sv-interior': cell === 'interior' }"
            :title="cell === 'interior' ? `${sourceName} or empty` : sourceName">
            <GameIcon v-if="cell === 'source'" :target="`block/${st.source}`" :size="38" />
            <span v-else>optional</span>
          </div>
        </div>
        <div class="sv-front">▲ front (opening)</div>
      </figure>
    </div>
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
    <div class="recipe-list"><RecipeCard kind="craft" :id="st.source" /></div>
  </template>
</template>

<style scoped>
.sv-hint { color: var(--vp-c-text-2); font-size: 13px; margin: 0 0 8px; }
.sv-layers { display: flex; flex-wrap: wrap; gap: 16px; }
.sv-layer { margin: 0; }
.sv-layer figcaption { font-size: 13px; font-weight: 600; margin-bottom: 4px; }
.sv-grid { display: grid; gap: 2px; padding: 4px; background: var(--vp-c-bg-soft); border-radius: 6px; width: max-content; }
.sv-cell { width: 44px; height: 44px; display: flex; align-items: center; justify-content: center; border: 1px solid var(--vp-c-divider); border-radius: 4px; }
.sv-interior { background: repeating-linear-gradient(45deg, transparent 0 4px, var(--vp-c-divider) 4px 5px); font-size: 9px; color: var(--vp-c-text-3); }
.sv-front { font-size: 11px; color: var(--vp-c-text-2); text-align: center; margin-top: 3px; }
</style>
