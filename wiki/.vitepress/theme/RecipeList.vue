<script setup lang="ts">
// Recipes grouped by station, each group collapsible so a block used in dozens of recipes stays scannable.
import { computed } from 'vue'
import RecipeCard from './RecipeCard.vue'

// The generator passes the list as a JSON attribute string.
const props = defineProps<{ recipes: string | { kind: string; id: string }[] }>()

const LABELS: Record<string, string> = {
  craft: 'Crafting', cook: 'Campfire', crush: 'Quern', melt: 'Kiln', cast: 'Casting', alloy: 'Alloy furnace'
}
// Groups at most this big start open; a longer one starts collapsed.
const OPEN_UP_TO = 3

const groups = computed(() => {
  const list: { kind: string; id: string }[] = typeof props.recipes === 'string' ? JSON.parse(props.recipes) : props.recipes
  const byKind = new Map<string, { kind: string; id: string }[]>()
  for (const r of list) {
    const group = byKind.get(r.kind) ?? []
    group.push(r)
    byKind.set(r.kind, group)
  }
  return [...byKind].map(([kind, recipes]) => ({ kind, recipes, label: LABELS[kind] ?? kind }))
})
</script>

<template>
  <div class="recipe-groups">
    <template v-for="g in groups" :key="g.kind">
      <!-- A lone short group needs no fold: the section heading already names it. -->
      <div v-if="groups.length === 1 && g.recipes.length <= OPEN_UP_TO" class="recipe-list">
        <RecipeCard v-for="r in g.recipes" :key="r.id" :kind="r.kind" :id="r.id" />
      </div>
      <details v-else class="rg" :data-kind="g.kind" :open="g.recipes.length <= OPEN_UP_TO">
        <summary><span class="rg-label">{{ g.label }}</span><span class="rg-count">{{ g.recipes.length }}</span></summary>
        <div class="recipe-list">
          <RecipeCard v-for="r in g.recipes" :key="r.id" :kind="r.kind" :id="r.id" />
        </div>
      </details>
    </template>
  </div>
</template>

<style scoped>
.rg { margin: 8px 0; border: 1px solid var(--vp-c-divider); border-radius: 8px; background: var(--vp-c-bg-soft); }
.rg > summary {
  display: flex; align-items: center; gap: 10px; padding: 8px 14px; cursor: pointer; list-style: none;
  font-size: 13px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; user-select: none;
}
.rg > summary::-webkit-details-marker { display: none; }
.rg > summary::before {
  content: ''; width: 7px; height: 7px; border: solid var(--vp-c-text-2); border-width: 0 2px 2px 0;
  transform: rotate(-45deg); transition: transform .15s; margin-right: 2px;
}
.rg[open] > summary::before { transform: rotate(45deg); }
.rg-count {
  margin-left: auto; min-width: 22px; text-align: center; padding: 1px 8px; border-radius: 10px;
  font-size: 12px; background: var(--vp-c-default-soft); color: var(--vp-c-text-2);
}
.rg .recipe-list { padding: 0 12px 8px; }
</style>
