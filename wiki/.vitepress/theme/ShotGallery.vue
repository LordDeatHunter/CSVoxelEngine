<script setup lang="ts">
// In-game screenshots from data/screenshots.json: a grid of captioned figures that open in a lightbox.
// <ShotGallery group="biome" /> shows a whole group, <ShotGallery slugs="forest,steppe" /> picks shots, and
// <ShotGallery slugs="forest" hero /> shows one wide.
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { withBase } from 'vitepress'
import manifest from '../../data/screenshots.json'

const props = defineProps<{ group?: string; slugs?: string; hero?: boolean }>()

interface Shot { slug: string; title: string; caption: string }

const shots = computed<Shot[]>(() => {
  const all = manifest as Record<string, { title: string; caption: string; group: string }>
  const picked = props.slugs
    ? props.slugs.split(',').map((s) => s.trim()).filter((s) => s in all)
    : Object.keys(all).filter((s) => all[s].group === props.group)
  return picked.map((slug) => ({ slug, title: all[slug].title, caption: all[slug].caption }))
})

const src = (slug: string) => withBase(`/screenshots/${slug}.webp`)
const open = ref(-1)
const current = computed(() => shots.value[open.value])

function show(i: number) { open.value = (i + shots.value.length) % shots.value.length }
function onKey(e: KeyboardEvent) {
  if (open.value < 0) return
  if (e.key === 'Escape') open.value = -1
  else if (e.key === 'ArrowRight') show(open.value + 1)
  else if (e.key === 'ArrowLeft') show(open.value - 1)
}

onMounted(() => window.addEventListener('keydown', onKey))
onBeforeUnmount(() => window.removeEventListener('keydown', onKey))
</script>

<template>
  <div class="sg" :class="{ hero }">
    <figure v-for="(s, i) in shots" :key="s.slug" class="sg-fig">
      <button type="button" class="sg-open" :aria-label="`Enlarge: ${s.title}`" @click="show(i)">
        <img :src="src(s.slug)" :alt="s.title" loading="lazy" decoding="async" width="1280" height="720" />
      </button>
      <figcaption><strong>{{ s.title }}</strong><span>{{ s.caption }}</span></figcaption>
    </figure>

    <Teleport to="body">
      <div v-if="current" class="sg-box" role="dialog" aria-modal="true" :aria-label="current.title" @click.self="open = -1">
        <button type="button" class="sg-nav prev" aria-label="Previous" @click="show(open - 1)">‹</button>
        <figure class="sg-big">
          <img :src="src(current.slug)" :alt="current.title" />
          <figcaption><strong>{{ current.title }}</strong><span>{{ current.caption }}</span></figcaption>
        </figure>
        <button type="button" class="sg-nav next" aria-label="Next" @click="show(open + 1)">›</button>
        <button type="button" class="sg-close" aria-label="Close" @click="open = -1">×</button>
      </div>
    </Teleport>
  </div>
</template>

<style scoped>
.sg { display: grid; grid-template-columns: repeat(auto-fill, minmax(300px, 1fr)); gap: 16px; margin: 16px 0; }
.sg.hero { grid-template-columns: 1fr; }
.sg-fig { margin: 0; border: 1px solid var(--vp-c-divider); border-radius: 8px; overflow: hidden; background: var(--vp-c-bg-soft); }
.sg-open { display: block; width: 100%; padding: 0; border: 0; background: #111; cursor: zoom-in; }
.sg-open img { display: block; width: 100%; height: auto; aspect-ratio: 16 / 9; object-fit: cover; transition: transform .25s, filter .25s; }
.sg-open:hover img { transform: scale(1.025); filter: brightness(1.08); }
.sg-open { overflow: hidden; }
figcaption { display: flex; flex-direction: column; gap: 2px; padding: 8px 12px 10px; font-size: 13px; line-height: 1.45; color: var(--vp-c-text-2); }
figcaption strong { font-size: 14px; color: var(--vp-c-text-1); }

.sg-box { position: fixed; inset: 0; z-index: 100; display: flex; align-items: center; justify-content: center; padding: 24px; background: rgba(0, 0, 0, .86); }
.sg-big { margin: 0; max-width: min(1280px, 100%); max-height: 100%; display: flex; flex-direction: column; }
.sg-big img { display: block; max-width: 100%; max-height: calc(100vh - 140px); margin: 0 auto; border-radius: 6px; box-shadow: 0 8px 40px #000a; }
.sg-big figcaption { align-items: center; text-align: center; color: #cfcfd6; padding-top: 12px; }
.sg-big figcaption strong { color: #fff; font-size: 16px; }
.sg-nav, .sg-close { position: absolute; border: 0; background: rgba(255, 255, 255, .12); color: #fff; cursor: pointer; border-radius: 999px; line-height: 1; transition: background .15s; }
.sg-nav:hover, .sg-close:hover { background: rgba(255, 255, 255, .28); }
.sg-nav { top: 50%; width: 48px; height: 48px; margin-top: -24px; font-size: 32px; padding-bottom: 4px; }
.sg-nav.prev { left: 16px; }
.sg-nav.next { right: 16px; }
.sg-close { top: 16px; right: 16px; width: 40px; height: 40px; font-size: 26px; }
</style>
