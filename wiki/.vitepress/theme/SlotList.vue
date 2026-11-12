<script setup lang="ts">
// A plain list of content references (block/<id>, item/<id>) as inventory slots with their linked names.
import { computed, onMounted, ref } from 'vue'
import { withBase } from 'vitepress'
import RecipeSlot from './RecipeSlot.vue'
import { loadData } from './icons.ts'

// The generator passes the targets as a JSON attribute string.
const props = defineProps<{ targets: string | string[] }>()

interface Row { target: string; name: string; link: string | null }
const rows = ref<Row[]>([])
const list = computed<string[]>(() => (typeof props.targets === 'string' ? JSON.parse(props.targets) : props.targets))
const title = (s: string) => s.charAt(0).toUpperCase() + s.slice(1).replace(/_/g, ' ')

onMounted(async () => {
  const [blocks, items] = await Promise.all(['blocks', 'items'].map(loadData))
  rows.value = list.value.map((target) => {
    const kind = target.split('/')[0]
    const id = target.slice(target.indexOf('/') + 1)
    const src = kind === 'block' ? blocks[id] : items[id]
    const linked = kind === 'block' ? src?.hasItem : !!src
    return { target, name: src?.name ?? title(id), link: linked ? withBase(`/game/${kind}s/${id}`) : null }
  })
})
</script>

<template>
  <ul class="sl">
    <li v-for="r in rows" :key="r.target" class="sl-row">
      <RecipeSlot :target="r.target" />
      <a v-if="r.link" :href="r.link" class="sl-name">{{ r.name }}</a>
      <span v-else class="sl-name">{{ r.name }}</span>
    </li>
  </ul>
</template>

<style scoped>
.sl { list-style: none; margin: 10px 0; padding: 0; max-width: 560px; display: flex; flex-direction: column; gap: 6px; }
.sl-row {
  display: flex; align-items: center; gap: 12px; margin: 0; padding: 6px 12px 6px 6px; border-radius: 8px;
  background: var(--vp-c-bg-soft); border: 1px solid var(--vp-c-divider);
}
.sl-name { font-weight: 600; }
</style>
