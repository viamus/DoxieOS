import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vite';

const repo = fileURLToPath(new URL('.', import.meta.url));

export default defineConfig({
  root: 'src/renderer',
  base: './',
  envDir: false,
  esbuild: { jsx: 'automatic' },
  plugins: [{
    name: 'doxie-catalog-manifest',
    generateBundle() {
      const files = execFileSync('git', ['ls-files', '-z', '--', '.doxie'], { cwd: repo, encoding: 'utf8', windowsHide: true })
        .split('\0').filter(Boolean);
      this.emitFile({ type: 'asset', fileName: 'doxie-catalog.json', source: JSON.stringify(files) });
    },
  }],
  build: { outDir: '../../dist/renderer', emptyOutDir: true },
});
