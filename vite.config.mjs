import { defineConfig } from 'vite';

export default defineConfig({
  root: 'src/renderer',
  base: './',
  envDir: false,
  esbuild: { jsx: 'automatic' },
  build: { outDir: '../../dist/renderer', emptyOutDir: true },
});
