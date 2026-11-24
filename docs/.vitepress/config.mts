import { readdirSync, readFileSync } from 'node:fs'
import { defineConfig } from 'vitepress'

const notesDirectory = new URL('../notes/', import.meta.url)
const notes = readdirSync(notesDirectory)
  .filter(file => file.endsWith('.md'))
  .sort()
  .map(file => ({
    text: readFileSync(new URL(file, notesDirectory), 'utf8').match(/^# (.+)$/m)?.[1] ?? file,
    link: `/notes/${file.slice(0, -3)}`,
  }))

export default defineConfig({
  title: 'MIDA',
  description: 'A build and test cache for .NET monorepos on CI',
  base: '/MIDA/',
  srcExclude: ['research_notes/**'],
  ignoreDeadLinks: [/research_notes/, /AGENTS$/],
  markdown: {
    // The docs link to headings by the ids GitHub gives them, such as #3-key-computation.
    anchor: {
      slugify: heading => heading.trim().toLowerCase().replace(/[^\p{L}\p{N}\s_-]/gu, '').replace(/\s/g, '-'),
    },
    config(md) {
      // start.md includes the README, whose links are written from the repo root.
      const normalizeLink = md.normalizeLink
      md.normalizeLink = url => normalizeLink(url.replace(/^docs\//, './'))
    },
  },
  themeConfig: {
    search: { provider: 'local' },
    nav: [
      { text: 'Get started', link: '/start' },
      { text: 'Design', link: '/design' },
      { text: 'Notes', link: '/notes/cache-key' },
    ],
    sidebar: [
      {
        text: 'Guide',
        items: [
          { text: 'Get started', link: '/start' },
          { text: 'Design', link: '/design' },
          { text: 'Plan', link: '/plan' },
        ],
      },
      { text: 'Notes', items: notes },
    ],
    socialLinks: [{ icon: 'github', link: 'https://github.com/spacentropy/MIDA' }],
  },
})
