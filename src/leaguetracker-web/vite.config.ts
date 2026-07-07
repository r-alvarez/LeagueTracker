import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      // Dev server proxies API calls to the .NET host; in production the SPA is
      // built straight into the API's wwwroot and served same-origin.
      '/api': 'http://localhost:5170',
    },
  },
  build: {
    outDir: '../LeagueTracker.Api/wwwroot',
    emptyOutDir: true,
  },
})
