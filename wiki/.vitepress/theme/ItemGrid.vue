<script setup lang="ts">
// A filterable grid of icon cards for the Blocks and Items index pages.
import { computed, onMounted, ref } from 'vue'
import { withBase } from 'vitepress'
import GameIcon from './GameIcon.vue'
import { loadData } from './icons.ts'

const props = defineProps<{ kind: 'blocks' | 'items' }>()

interface Entry { id: string; name: string }

const entries = ref<Entry[]>([])
const query = ref('')

const singular = computed(() => (props.kind === 'blocks' ? 'block' : 'item'))
const shown = computed(() => {
  const q = query.value.trim().toLowerCase()
  return q ? entries.value.filter((e) => e.name.toLowerCase().includes(q)) : entries.value
})

onMounted(async () => {
  const data = await loadData(props.kind)
  entries.value = Object.values<any>(data)
    .filter((e) => props.kind === 'items' || e.hasItem)
    .map((e) => ({ id: e.id, name: e.name }))
    .sort((a, b) => a.name.localeCompare(b.name))
})
</script>

<template>
  <div class="ig">
    <input v-model="query" class="ig-search" type="search" :placeholder="`Search ${entries.length} ${props.kind}...`" />
    <div class="ig-grid">
      <a v-for="e in shown" :key="e.id" class="ig-card" :href="withBase(`/game/${props.kind}/${e.id}`)">
        <span class="ig-icon"><GameIcon :target="`${singular}/${e.id}`" :size="48" /></span>
        <span class="ig-name">{{ e.name }}</span>
      </a>
    </div>
    <p v-if="!shown.length && entries.length" class="ig-empty">Nothing matches "{{ query }}".</p>
  </div>
</template>

<style scoped>
.ig-search {
  width: 100%; max-width: 360px; box-sizing: border-box; margin: 8px 0 16px; padding: 8px 12px;
  border: 1px solid var(--vp-c-divider); border-radius: 6px; background: var(--vp-c-bg-soft); color: var(--vp-c-text-1); font-size: 14px;
}
.ig-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(112px, 1fr)); gap: 10px; }
.ig-card {
  display: flex; flex-direction: column; align-items: center; gap: 6px; padding: 10px 6px 8px; text-align: center;
  border: 1px solid var(--vp-c-divider); border-radius: 8px; background: var(--vp-c-bg-soft);
  text-decoration: none !important; color: var(--vp-c-text-1); transition: transform .12s, border-color .12s, background .12s;
}
.ig-card:hover { transform: translateY(-2px); border-color: var(--vp-c-brand-1); background: var(--vp-c-bg-elv); }
.ig-icon {
  display: flex; align-items: center; justify-content: center; width: 64px; height: 64px; border-radius: 4px; box-sizing: border-box;
  background: #8b8b8b; border: 2px solid; border-color: #373737 #fff #fff #373737;
}
.dark .ig-icon { background: #4a4a4f; border-color: #1a1a1d #6b6b72 #6b6b72 #1a1a1d; }
.ig-name { font-size: 12.5px; font-weight: 600; line-height: 1.25; }
.ig-empty { color: var(--vp-c-text-2); }
</style>
