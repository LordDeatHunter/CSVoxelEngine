import DefaultTheme from 'vitepress/theme'
import type { Theme } from 'vitepress'
import ModelViewer from './ModelViewer.vue'

export default {
  extends: DefaultTheme,
  enhanceApp({ app }) {
    app.component('ModelViewer', ModelViewer)
  }
} satisfies Theme
