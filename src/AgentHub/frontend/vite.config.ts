import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vite'

export default defineConfig({
  base: '/app/',
  plugins: [vue()],
  server: {
    proxy: {
      '/api': 'http://127.0.0.1:18780',
    },
  },
  build: {
    outDir: '../wwwroot/app',
    emptyOutDir: true,
  },
})
