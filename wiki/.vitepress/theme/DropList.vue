<script setup lang="ts">
// Loot as inventory slots with their drop chance. `table` lists what one loot table can drop; `item` lists
// the mobs and blocks (and other tables) that can drop one item.
import { computed, onMounted, ref } from 'vue'
import { withBase } from 'vitepress'
import RecipeSlot from './RecipeSlot.vue'
import MobPortrait from './MobPortrait.vue'
import { loadData } from './icons.ts'

const props = defineProps<{ table?: string; item?: string }>()

interface Row {
  key: string; target: string; name: string; link: string | null; mob: string | null
  chance: number; min: number; max: number
}
const rows = ref<Row[]>([])
const title = (s: string) => s.charAt(0).toUpperCase() + s.slice(1).replace(/_/g, ' ')
const pct = (x: number) =>
  x >= 0.9995 ? '100%' : x < 0.01 ? '<1%' : x < 0.1 ? `${(x * 100).toFixed(1)}%` : `${Math.round(x * 100)}%`
const amount = (r: Row) => (r.min === r.max ? (r.max > 1 ? `×${r.max}` : '') : `×${r.min}–${r.max}`)
const tone = (x: number) => (x >= 0.75 ? 'high' : x >= 0.25 ? 'mid' : 'low')

onMounted(async () => {
  const [loot, blocks, items, entities] = await Promise.all(['loot', 'blocks', 'items', 'entities'].map(loadData))
  const out: Row[] = []
  if (props.table) {
    for (const d of loot[props.table] ?? []) {
      const kind = d.item.split('/')[0]
      const id = d.item.slice(d.item.indexOf('/') + 1)
      const src = kind === 'block' ? blocks[id] : items[id]
      out.push({
        key: d.item, target: d.item, name: src?.name ?? title(id), mob: null,
        link: kind === 'block' ? (src?.hasItem ? withBase(`/game/blocks/${id}`) : null) : src ? withBase(`/game/items/${id}`) : null,
        chance: d.chance, min: d.min, max: d.max
      })
    }
  } else if (props.item) {
    const seen = new Set<string>()
    const mine = (table: string) => (loot[table] ?? []).find((d: any) => d.item === props.item)
    for (const e of Object.values<any>(entities)) {
      const d = e.loot && mine(e.loot)
      if (!d) continue
      seen.add(e.loot)
      out.push({ key: 'e' + e.id, target: '', name: e.name, link: withBase(`/game/mobs/${e.id}`), mob: e.id, chance: d.chance, min: d.min, max: d.max })
    }
    for (const b of Object.values<any>(blocks)) {
      const d = b.loot && mine(b.loot)
      if (!d || !b.hasItem || (b.loot === b.id && d.item === 'block/' + b.id)) continue
      seen.add(b.loot)
      out.push({ key: 'b' + b.id, target: 'block/' + b.id, name: b.name, link: withBase(`/game/blocks/${b.id}`), mob: null, chance: d.chance, min: d.min, max: d.max })
    }
    // Tables nothing above owns (a block with no item, ruin chests) still say where it can be found
    for (const table of Object.keys(loot)) {
      const d = mine(table)
      if (!d || seen.has(table)) continue
      const name = table.startsWith('ruin_') ? `${title(table.slice(5))} ruin chests` : title(table)
      out.push({ key: 't' + table, target: '', name, link: null, mob: null, chance: d.chance, min: d.min, max: d.max })
    }
  }
  rows.value = out.sort((a, b) => b.chance - a.chance)
})
const empty = computed(() => rows.value.length === 0)
</script>

<template>
  <ul v-if="!empty" class="dl">
    <li v-for="r in rows" :key="r.key" class="dl-row">
      <MobPortrait v-if="r.mob" :id="r.mob" :size="52" class="dl-icon" />
      <RecipeSlot v-else-if="r.target" :target="r.target" />
      <span v-else class="dl-icon dl-none">?</span>
      <a v-if="r.link" :href="r.link" class="dl-name">{{ r.name }}</a>
      <span v-else class="dl-name">{{ r.name }}</span>
      <span class="dl-amount">{{ amount(r) }}</span>
      <span class="dl-chance" :class="tone(r.chance)">
        <span class="dl-bar"><span :style="{ width: Math.max(r.chance * 100, 3) + '%' }" /></span>{{ pct(r.chance) }}
      </span>
    </li>
  </ul>
</template>

<style scoped>
.dl { list-style: none; margin: 10px 0; padding: 0; max-width: 560px; display: flex; flex-direction: column; gap: 6px; }
.dl-row {
  display: flex; align-items: center; gap: 12px; margin: 0; padding: 6px 12px 6px 6px; border-radius: 8px;
  background: var(--vp-c-bg-soft); border: 1px solid var(--vp-c-divider);
}
.dl-icon { width: 52px; height: 52px; flex: none; display: inline-flex; align-items: center; justify-content: center; }
.dl-none { border: 2px dashed var(--vp-c-divider); border-radius: 2px; box-sizing: border-box; color: var(--vp-c-text-3); font-weight: 700; }
.dl-name { flex: 1; font-weight: 600; }
.dl-amount { font-variant-numeric: tabular-nums; color: var(--vp-c-text-2); font-size: 13px; }
.dl-chance {
  --c: #4caf6a; display: inline-flex; align-items: center; gap: 8px; min-width: 112px; justify-content: flex-end;
  font: 700 14px/1 ui-monospace, 'Cascadia Mono', Consolas, monospace; color: var(--c);
}
.dl-chance.mid { --c: #d9a93a; }
.dl-chance.low { --c: #d9663a; }
.dl-bar { width: 52px; height: 6px; border-radius: 3px; background: var(--vp-c-default-soft); overflow: hidden; }
.dl-bar > span { display: block; height: 100%; background: var(--c); }
</style>
