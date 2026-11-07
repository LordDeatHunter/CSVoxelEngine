<script setup lang="ts">
// Portrait cards for the Mobs index page: each mob rendered from its model, with its name and health, the bosses
// under a heading of their own.
import { computed, onMounted, ref } from 'vue'
import { withBase } from 'vitepress'
import MobPortrait from './MobPortrait.vue'
import { loadData } from './icons.ts'

interface Entry { id: string; name: string; hp: number | null; clips: number; boss: boolean }

const entries = ref<Entry[]>([])
const mobs = computed(() => entries.value.filter((e) => !e.boss))
const bosses = computed(() => entries.value.filter((e) => e.boss))

onMounted(async () => {
  const data = await loadData('entities')
  entries.value = Object.values<any>(data)
    .map((e) => ({ id: e.id, name: e.name, hp: e.hp, clips: e.clips.length, boss: e.boss === true }))
    .sort((a, b) => a.name.localeCompare(b.name))
})
</script>

<template>
  <div class="mg">
    <a v-for="e in mobs" :key="e.id" class="mg-card" :href="withBase(`/game/mobs/${e.id}`)">
      <span class="mg-portrait"><MobPortrait :id="e.id" :size="112" /></span>
      <span class="mg-name">{{ e.name }}</span>
      <span class="mg-meta">
        <span v-if="e.hp != null" class="mg-hp" title="Health">
          <svg viewBox="0 0 24 22" aria-hidden="true"><path d="M12 21s-9-5.6-9-12.2A5.2 5.2 0 0 1 12 6a5.2 5.2 0 0 1 9 2.8C21 15.4 12 21 12 21z" fill="#e0483a" stroke="#4a0f0a" stroke-width="1.6" /></svg>
          {{ e.hp }}
        </span>
        <span v-else class="mg-hp none">Not a mob</span>
      </span>
    </a>
  </div>
  <template v-if="bosses.length">
    <h2 id="bosses" tabindex="-1">Bosses <a class="header-anchor" href="#bosses" aria-label="Permalink to &quot;Bosses&quot;">&#8203;</a></h2>
    <p class="mg-note">Bosses never spawn on their own: each is summoned with an item.</p>
    <div class="mg">
      <a v-for="e in bosses" :key="e.id" class="mg-card mg-boss" :href="withBase(`/game/mobs/${e.id}`)">
        <span class="mg-portrait"><MobPortrait :id="e.id" :size="112" /></span>
        <span class="mg-name">{{ e.name }}</span>
        <span class="mg-meta">
          <span v-if="e.hp != null" class="mg-hp" title="Health">
            <svg viewBox="0 0 24 22" aria-hidden="true"><path d="M12 21s-9-5.6-9-12.2A5.2 5.2 0 0 1 12 6a5.2 5.2 0 0 1 9 2.8C21 15.4 12 21 12 21z" fill="#e0483a" stroke="#4a0f0a" stroke-width="1.6" /></svg>
            {{ e.hp }}
          </span>
        </span>
      </a>
    </div>
  </template>
</template>

<style scoped>
.mg { display: grid; grid-template-columns: repeat(auto-fill, minmax(150px, 1fr)); gap: 12px; margin-top: 12px; }
.mg-card {
  display: flex; flex-direction: column; align-items: center; gap: 6px; padding: 12px 8px 10px;
  border: 1px solid var(--vp-c-divider); border-radius: 8px; background: var(--vp-c-bg-soft);
  text-decoration: none !important; color: var(--vp-c-text-1); transition: transform .12s, border-color .12s, background .12s;
}
.mg-card:hover { transform: translateY(-2px); border-color: var(--vp-c-brand-1); background: var(--vp-c-bg-elv); }
.mg-portrait {
  display: flex; align-items: center; justify-content: center; width: 128px; height: 128px; box-sizing: border-box; border-radius: 4px;
  background: radial-gradient(circle at 50% 38%, #6c6c74, #3d3d44); border: 2px solid; border-color: #1a1a1d #6b6b72 #6b6b72 #1a1a1d;
}
.mg-name { font-weight: 700; font-size: 14px; }
.mg-note { margin: 4px 0 0; color: var(--vp-c-text-2); font-size: 14px; }
.mg-boss { border-color: var(--vp-c-warning-1); }
.mg-hp { display: inline-flex; align-items: center; gap: 5px; font: 700 12px/1 ui-monospace, Consolas, monospace; color: var(--vp-c-text-2); }
.mg-hp svg { width: 14px; height: 13px; }
.mg-hp.none { font-weight: 500; font-family: inherit; }
</style>
