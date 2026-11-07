import DefaultTheme from 'vitepress/theme'
import type { Theme } from 'vitepress'
import ModelViewer from './ModelViewer.vue'
import TreeViewer from './TreeViewer.vue'

export default {
  extends: DefaultTheme,
  enhanceApp({ app }) {
    app.component('ModelViewer', ModelViewer)
    app.component('TreeViewer', TreeViewer)
  }
} satisfies Theme
