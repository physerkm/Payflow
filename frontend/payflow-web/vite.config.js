import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// In development the SPA and the API share one origin through this proxy, so the refresh-token
// cookie (HttpOnly, SameSite=Strict, path /api/v1/auth) works without CORS.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: process.env.PAYFLOW_API_URL ?? 'http://localhost:5080', changeOrigin: false },
    },
  },
  build: {
    sourcemap: false,
    chunkSizeWarningLimit: 1500,
    rollupOptions: {
      output: {
        manualChunks: {
          react: ['react', 'react-dom', 'react-router'],
          mui: ['@mui/material', '@emotion/react', '@emotion/styled'],
          grid: ['@mui/x-data-grid'],
          charts: ['@mui/x-charts'],
        },
      },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.js'],
    css: false,
  },
});
