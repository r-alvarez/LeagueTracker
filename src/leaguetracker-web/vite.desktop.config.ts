import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  build: {
    license: { fileName: 'THIRD-PARTY-NOTICES.md' },
    outDir: 'dist/desktop',
    emptyOutDir: true,
    rolldownOptions: { input: 'desktop.html' },
  },
})
