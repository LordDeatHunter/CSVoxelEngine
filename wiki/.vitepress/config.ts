import { defineConfig } from 'vitepress'

export default defineConfig({
  title: 'CSVoxelEngine Wiki',
  description: 'Blocks, items, mobs and engine docs for the CSVoxelEngine voxel game',
  base: '/CSVoxelEngine/',
  cleanUrls: true,
  themeConfig: {
    search: { provider: 'local' },
    nav: [
      { text: 'Game', link: '/game/' },
      { text: 'Developer', link: '/dev/' },
      { text: 'Credits', link: '/credits' }
    ],
    sidebar: {
      '/game/': [
        { text: 'Game', items: [{ text: 'Overview', link: '/game/' }, { text: 'Controls', link: '/game/controls' }] },
        { text: 'World', items: [
          { text: 'Biomes', link: '/game/biomes' },
          { text: 'Layers', link: '/game/layers' },
          { text: 'Trees', link: '/game/reference/trees' }
        ] },
        { text: 'Systems', items: [
          { text: 'Recipes', link: '/game/reference/crafting' },
          { text: 'Magic', link: '/game/reference/magic' }
        ] },
        { text: 'Reference', items: [
          { text: 'Blocks', link: '/game/blocks/' },
          { text: 'Items', link: '/game/items/' },
          { text: 'Mobs', link: '/game/mobs/' }
        ] }
      ],
      '/dev/': [
        { text: 'Developer', items: [{ text: 'Overview', link: '/dev/' }, { text: 'Adding content', link: '/dev/adding-content' }] }
      ]
    },
    socialLinks: [{ icon: 'github', link: 'https://github.com/LordDeatHunter/CSVoxelEngine' }]
  },
  vite: { build: { chunkSizeWarningLimit: 1500 } }
})
