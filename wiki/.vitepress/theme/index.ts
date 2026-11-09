import DefaultTheme from 'vitepress/theme'
import type { Theme } from 'vitepress'
import ModelViewer from './ModelViewer.vue'
import TreeViewer from './TreeViewer.vue'
import StructureViewer from './StructureViewer.vue'
import RecipeCard from './RecipeCard.vue'
import RecipeList from './RecipeList.vue'
import DropList from './DropList.vue'
import SlotList from './SlotList.vue'
import ItemGrid from './ItemGrid.vue'
import MobGrid from './MobGrid.vue'
import ShotGallery from './ShotGallery.vue'
import SheetTile from './SheetTile.vue'

export default {
  extends: DefaultTheme,
  enhanceApp({ app }) {
    app.component('ModelViewer', ModelViewer)
    app.component('TreeViewer', TreeViewer)
    app.component('StructureViewer', StructureViewer)
    app.component('RecipeCard', RecipeCard)
    app.component('RecipeList', RecipeList)
    app.component('DropList', DropList)
    app.component('SlotList', SlotList)
    app.component('ItemGrid', ItemGrid)
    app.component('MobGrid', MobGrid)
    app.component('ShotGallery', ShotGallery)
    app.component('SheetTile', SheetTile)
  }
} satisfies Theme
