import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// Dev: Vite serves the UI and proxies /api to the .NET backend.
// Build: output goes into the API's wwwroot so `dotnet run` serves everything on one port.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5040',
    },
  },
  build: {
    outDir: '../src/DeadlockClipper.Api/wwwroot',
    emptyOutDir: true,
  },
})
