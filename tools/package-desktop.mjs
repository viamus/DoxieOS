import { spawnSync } from 'node:child_process';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { packager } from '@electron/packager';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(import.meta.url);
const { version: electronVersion } = require('electron/package.json');
const checksums = require('electron/checksums.json');
const variant = process.argv[2];
if (variant && !/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(variant)) throw new Error('Use a kebab-case package folder name.');
const output = join(root, 'release', variant || '');
const temporary = await mkdtemp(join(tmpdir(), 'doxie-package-'));

function run(command, args, cwd = root) {
  const result = spawnSync(command, args, { cwd, windowsHide: true, maxBuffer: 8 * 1024 * 1024 });
  if (result.error || result.status !== 0) throw new Error(result.error?.message || result.stderr?.toString() || `${command} failed.`);
  return result.stdout;
}

try {
  // Workbenches may have private catalog content. Copy only tracked first-party
  // files from the current commit, never the working catalog directories.
  const tracked = run('git', ['ls-tree', '-rz', '--name-only', 'HEAD', '--', '.doxie', 'docs/release-notes']).toString('utf8').split('\0').filter(Boolean);
  const seed = join(temporary, '.doxie');
  await mkdir(seed);
  const notes = join(temporary, 'release-notes');
  for (const file of tracked) {
    const destination = join(temporary, file.replace(/^docs\//, ''));
    await mkdir(dirname(destination), { recursive: true });
    await writeFile(destination, run('git', ['show', `HEAD:${file}`]));
  }
  const extras = tracked.some(file => file.startsWith('docs/release-notes/')) ? [seed, notes] : [seed];
  const desktop = new Set(['/src', '/src/desktop']);
  for (const file of run('git', ['ls-files', '-z', '--', 'src/desktop']).toString('utf8').split('\0').filter(Boolean)) {
    for (let path = `/${file}`; path !== '/src/desktop'; path = path.slice(0, path.lastIndexOf('/'))) desktop.add(path);
  }
  if (!desktop.has('/src/desktop/main.cjs')) throw new Error('The Electron sources must be tracked before packaging.');
  const allowed = /^\/(?:package\.json$|LICENSE$|NOTICE$|node_modules(?:\/|$)|dist(?:\/renderer(?:\/|$)|$))/;
  const targets = await packager({
    dir: root, name: 'DoxieOS', out: output, overwrite: true, prune: true,
    electronVersion, download: { checksums }, extraResource: extras,
    asar: { unpackDir: 'node_modules/{node-pty,onnxruntime-node,@img}' },
    icon: process.platform === 'win32' ? join(root, 'src', 'desktop', 'assets', 'icon.ico') : undefined,
    ignore: file => Boolean(file) && !allowed.test(file) && !desktop.has(file),
  });
  for (const target of targets) {
    if (process.platform !== 'win32') {
      console.log(target);
      continue;
    }
    const launcher = run('git', ['show', 'HEAD:run.bat']).toString('utf8').replace(/\r?\n/g, '\r\n');
    await writeFile(join(target, 'run.bat'), launcher);
    const folder = basename(target);
    const archive = join(output, `${folder}.zip`);
    run('tar.exe', ['-a', '-cf', archive, folder], output);
    const launchMarker = join(root, 'release', `.launch-next-${folder}`);
    if (variant === 'next') await writeFile(launchMarker, '');
    else if (!variant) await rm(launchMarker, { force: true });
    console.log(archive);
  }
} finally {
  await rm(temporary, { recursive: true, force: true });
}
