import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],

  server: {
    proxy: {
      '/api': 'http://localhost:3000',
    },
  },

  build: {
    // esbuild 기반 minify (기본값, terser보다 5~10배 빠름)
    minify: 'esbuild',

    // 청크 크기 경고 임계값 (DevExtreme이 커서 올려줌)
    chunkSizeWarningLimit: 2000,

    rollupOptions: {
      output: {
        // 벤더 코드를 청크로 분리 → 브라우저 캐시 재활용
        manualChunks: {
          'vue':        ['vue'],
          'devextreme': ['devextreme', 'devextreme-vue'],
          'axios':      ['axios'],
        },
      },
    },
  },
})
