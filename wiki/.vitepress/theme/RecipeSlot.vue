<script setup lang="ts">
// An inventory-style slot: a framed icon with a count badge, a hover tooltip and a link to its page.
import { computed, onMounted, ref } from 'vue'
import { withBase } from 'vitepress'
import GameIcon from './GameIcon.vue'
import { loadData } from './icons.ts'

const props = defineProps<{ target: string; count?: number; ml?: number; big?: boolean }>()

const name = ref('')
const link = ref<string | null>(null)

const kind = computed(() => props.target.split('/')[0])
const id = computed(() => props.target.slice(props.target.indexOf('/') + 1))
const size = computed(() => (props.big ? 52 : 40))
const title = (s: string) => s.charAt(0).toUpperCase() + s.slice(1).replace(/_/g, ' ')

onMounted(async () => {
  name.value = title(id.value)
  try {
    if (kind.value === 'block') {
      const b = (await loadData('blocks'))[id.value]
      if (b) { name.value = b.name; link.value = b.hasItem ? withBase(`/game/blocks/${id.value}`) : null }
    } else if (kind.value === 'item') {
      const i = (await loadData('items'))[id.value]
      if (i) { name.value = i.name; link.value = withBase(`/game/items/${id.value}`) }
    } else if (kind.value === 'fluid') {
      name.value = (await loadData('moltens'))[id.value]?.name ?? `Molten ${title(id.value)}`
    } else if (kind.value === 'tag') {
      name.value = `Any ${id.value.replace(/_/g, ' ')}`
    }
  } catch { /* the id alone is a fine label */ }
})
</script>

<template>
  <component :is="link ? 'a' : 'span'" :href="link ?? undefined" class="slot" :class="{ big, tag: kind === 'tag' }" :data-name="name">
    <GameIcon :target="target" :size="size" />
    <span v-if="count && count > 1" class="badge">{{ count }}</span>
    <span v-else-if="ml" class="badge ml">{{ ml }}<small>ml</small></span>
  </component>
</template>

<style scoped>
.slot {
  --slot-bg: #8b8b8b; --slot-dark: #373737; --slot-light: #ffffff;
  position: relative; display: inline-flex; align-items: center; justify-content: center;
  width: 52px; height: 52px; box-sizing: border-box; padding: 4px; flex: none;
  background: var(--slot-bg);
  border: 2px solid; border-color: var(--slot-dark) var(--slot-light) var(--slot-light) var(--slot-dark);
  border-radius: 2px; text-decoration: none !important; cursor: default;
  transition: filter .12s, transform .12s;
}
.dark .slot { --slot-bg: #4a4a4f; --slot-dark: #1a1a1d; --slot-light: #6b6b72; }
a.slot { cursor: pointer; }
.slot:hover { filter: brightness(1.18); transform: translateY(-1px); z-index: 3; }
.slot.big { width: 64px; height: 64px; }
.slot.tag { border-style: dashed; }
.badge {
  position: absolute; right: 2px; bottom: 0; z-index: 1; color: #fff;
  font: 700 13px/1.1 ui-monospace, 'Cascadia Mono', Consolas, monospace;
  text-shadow: 1px 1px 0 #3f3f3f, -1px 0 0 #3f3f3f, 0 -1px 0 #3f3f3f;
  pointer-events: none;
}
.badge.ml { font-size: 11px; }
.badge small { font-size: 8px; margin-left: 1px; }
.slot::after {
  content: attr(data-name); position: absolute; left: 50%; bottom: calc(100% + 8px);
  transform: translateX(-50%) translateY(4px); white-space: nowrap; pointer-events: none;
  padding: 3px 8px; font-size: 12px; font-weight: 600; color: #fff; opacity: 0; transition: opacity .12s, transform .12s;
  background: #100010f2; border: 2px solid #2a0a5e; border-radius: 3px; box-shadow: 0 2px 8px #0008;
}
.slot:hover::after { opacity: 1; transform: translateX(-50%); }
</style>
