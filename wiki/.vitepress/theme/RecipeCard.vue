<script setup lang="ts">
// One recipe drawn like a game wiki: input slots, an arrow through the station, and the output slot.
import { computed, onMounted, ref } from 'vue'
import GameIcon from './GameIcon.vue'
import RecipeSlot from './RecipeSlot.vue'
import { loadData } from './icons.ts'

const props = defineProps<{ kind: string; id: string }>()

interface Slot { target: string; count?: number; ml?: number }

const recipe = ref<any>(null)

const STATIONS: Record<string, { label: string; block?: string }> = {
  craft: { label: 'Crafting' },
  cook: { label: 'Campfire', block: 'branch_pile' },
  crush: { label: 'Quern', block: 'quern' },
  melt: { label: 'Kiln', block: 'mud_brick_kiln' },
  cast: { label: 'Casting', block: 'casting_basin' },
  alloy: { label: 'Alloy furnace', block: 'alloy_furnace' }
}
const station = computed(() => STATIONS[props.kind] ?? { label: props.kind })

const inputs = computed<Slot[]>(() => {
  const r = recipe.value
  if (!r) return []
  switch (props.kind) {
    case 'craft': return r.ingredients.map((i: any) => ({ target: i.ref, count: i.count }))
    case 'cast': return [{ target: r.inputs[0] }, { target: r.inputs[1], ml: r.ml }]
    case 'alloy': return r.parts.map((p: any) => ({ target: `fluid/${p.fluid}`, ml: p.ml }))
    default: return [{ target: r.inputs[0] }]
  }
})

const output = computed<Slot | null>(() => {
  const r = recipe.value
  if (!r) return null
  if (props.kind === 'melt') return { target: r.outputs[0], ml: r.ml }
  if (props.kind === 'alloy') return { target: r.outputs[0], ml: r.parts.reduce((a: number, p: any) => a + p.ml, 0) }
  return { target: r.outputs[0], count: r.count }
})

const detail = computed(() => {
  const r = recipe.value
  if (!r) return ''
  if (props.kind === 'cook') return `${r.seconds} s`
  if (props.kind === 'cast') return `${r.seconds} s cooling`
  if (props.kind === 'alloy') return 'full set'
  return ''
})

onMounted(async () => {
  const data = await loadData('recipes')
  recipe.value = data.recipes.find((r: any) => r.kind === props.kind && r.id === props.id) ?? null
})
</script>

<template>
  <div v-if="recipe && output" class="rc" :data-kind="kind">
    <div class="rc-head">
      <span class="rc-kind">{{ station.label }}</span>
      <span v-if="detail" class="rc-detail">{{ detail }}</span>
    </div>
    <div class="rc-body">
      <div class="rc-in">
        <template v-for="(s, i) in inputs" :key="i">
          <span v-if="i" class="rc-plus">+</span>
          <RecipeSlot :target="s.target" :count="s.count" :ml="s.ml" />
        </template>
      </div>
      <div class="rc-arrow" :title="station.label">
        <GameIcon v-if="station.block" :target="`block/${station.block}`" :size="30" />
        <svg v-else viewBox="0 0 24 24" class="rc-grid" aria-hidden="true">
          <g fill="none" stroke="currentColor" stroke-width="1.6"><rect x="3" y="3" width="18" height="18" rx="1.5" /><path d="M9 3v18M15 3v18M3 9h18M3 15h18" /></g>
        </svg>
        <svg viewBox="0 0 48 14" class="rc-shaft" aria-hidden="true">
          <path d="M1 7h38M32 1l9 6-9 6" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round" />
        </svg>
      </div>
      <RecipeSlot :target="output.target" :count="output.count" :ml="output.ml" big class="rc-out" />
    </div>
  </div>
</template>

<style scoped>
.rc {
  --accent: #b9894b; margin: 10px 0; max-width: 560px; border-radius: 8px; overflow: hidden;
  background: var(--vp-c-bg-soft); border: 1px solid var(--vp-c-divider); border-left: 4px solid var(--accent);
}
.rc[data-kind='cook'] { --accent: #e0742e; }
.rc[data-kind='crush'] { --accent: #8d949e; }
.rc[data-kind='melt'] { --accent: #e0483a; }
.rc[data-kind='cast'] { --accent: #4f8fe0; }
.rc[data-kind='alloy'] { --accent: #a65fd6; }
.rc-head {
  display: flex; justify-content: space-between; align-items: center; padding: 5px 12px;
  font-size: 11px; font-weight: 700; letter-spacing: .08em; text-transform: uppercase;
  background: color-mix(in srgb, var(--accent) 16%, transparent); color: var(--vp-c-text-2);
}
.rc-kind { color: var(--accent); }
.rc-detail { font-variant-numeric: tabular-nums; letter-spacing: .02em; }
.rc-body { display: flex; align-items: center; flex-wrap: wrap; gap: 14px; padding: 14px 16px; }
.rc-in { display: flex; align-items: center; flex-wrap: wrap; gap: 6px; }
.rc-plus { font-weight: 700; font-size: 18px; color: var(--vp-c-text-3); width: 14px; text-align: center; }
.rc-arrow { display: flex; flex-direction: column; align-items: center; gap: 2px; color: var(--vp-c-text-2); }
.rc-grid { width: 26px; height: 26px; }
.rc-shaft { width: 48px; height: 14px; }
.rc-out { box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 55%, transparent), 0 0 14px color-mix(in srgb, var(--accent) 35%, transparent); }
</style>
