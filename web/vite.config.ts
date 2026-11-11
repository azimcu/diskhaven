import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: 'dist',
    sourcemap: false,
    // two pages: the file manager (index.html) and the gallery app (galeri.html, added to the phone's home screen)
    rollupOptions: { input: { main: 'index.html', galeri: 'galeri.html' } },
  },
  server: { proxy: { '/api': 'http://127.0.0.1:5143' } },
})
