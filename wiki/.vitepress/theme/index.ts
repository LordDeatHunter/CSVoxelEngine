import DefaultTheme from 'vitepress/theme'
import type { Theme } from 'vitepress'
import ModelViewer from './ModelViewer.vue'
import TreeViewer from './TreeViewer.vue'
import RecipeCard from './RecipeCard.vue'
import ItemGrid from './ItemGrid.vue'
import MobGrid from './MobGrid.vue'
import ShotGallery from './ShotGallery.vue'

export default {
  extends: DefaultTheme,
  enhanceApp({ app }) {
    app.component('ModelViewer', ModelViewer)
    app.component('TreeViewer', TreeViewer)
    app.component('RecipeCard', RecipeCard)
    app.component('ItemGrid', ItemGrid)
    app.component('MobGrid', MobGrid)
    app.component('ShotGallery', ShotGallery)
  }
} satisfies Theme
