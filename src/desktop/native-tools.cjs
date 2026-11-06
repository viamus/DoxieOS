const { randomUUID } = require('node:crypto');
const { mkdir, readFile, readdir, stat, writeFile } = require('node:fs/promises');
const { basename, extname, isAbsolute, relative, resolve, sep } = require('node:path');
const { safePath } = require('./storage.cjs');

const slug = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const maxFiles = 256;
const maxBytes = 16 * 1024 * 1024;

function scopedPath(root, value) {
  const item = relative(root, resolve(root, value));
  if (item === '..' || item.startsWith(`..${sep}`) || isAbsolute(item)) throw new Error('Library sources must be inside the selected workspace.');
  return item.split(sep).join('/');
}

function sourcePaths(value) {
  if (Array.isArray(value)) return value.map(String).filter(item => item.trim());
  return String(value ?? '').split(/[\r\n;]+/).map(item => item.trim().replace(/^(['"])(.*)\1$/, '$2')).filter(Boolean);
}

async function writeReceipt(outputDir, name, data) {
  await writeFile(safePath(outputDir, name), JSON.stringify(data, null, 2) + '\n', 'utf8');
}

async function createLibrary({ storage, toolMode, inputs, workspace, sourceDirectories = [], outputDir, catalogId, signal }) {
  const mode = toolMode || 'create';
  if (!['create', 'interactive', 'from-files', 'from-current'].includes(mode)) throw new Error(`Unknown library action: ${mode}`);
  const id = String(inputs.id || inputs.libraryId || '').trim();
  if (!slug.test(id)) throw new Error('Enter a kebab-case library ID.');
  const name = String(inputs.name || id).trim();
  const description = String(inputs.description || inputs.context || '');
  const memories = [];
  let bytes = 0;

  async function collect(root, relativePath, prefix) {
    signal?.throwIfAborted();
    const candidate = safePath(root, relativePath);
    const info = await stat(candidate);
    if (info.isDirectory()) {
      const entries = await readdir(candidate, { withFileTypes: true });
      for (const entry of entries.sort((a, b) => a.name.localeCompare(b.name, 'en'))) {
        if (entry.name.startsWith('.') || (!entry.isFile() && !entry.isDirectory())) continue;
        await collect(root, [relativePath, entry.name].filter(Boolean).join('/'), prefix);
      }
      return;
    }
    const extension = extname(candidate).toLowerCase();
    if (!info.isFile() || !['.md', '.txt'].includes(extension)) return;
    if (memories.length >= maxFiles || bytes + info.size > maxBytes) throw new Error('Library sources exceed 256 text files or 16 MiB.');
    let outputName = (relativePath || basename(candidate)).replace(/\.txt$/i, '.md');
    if (/^(memory|readme)\.md$/i.test(basename(outputName))) outputName = outputName.replace(/([^/]+)$/, 'source-$1');
    let target = `memories/${prefix}/${outputName}`;
    const original = target;
    for (let suffix = 2; memories.some(file => file.relativePath.toLowerCase() === target.toLowerCase()); suffix++) {
      target = original.replace(/\.md$/i, `-${suffix}.md`);
    }
    const content = await readFile(candidate);
    if (bytes + content.length > maxBytes) throw new Error('Library sources exceed 16 MiB.');
    memories.push({ relativePath: target, content });
    bytes += content.length;
  }

  if (mode === 'from-current') {
    if (!workspace) throw new Error('Select a workspace to copy its memories.');
    for (const directory of ['memory', 'memories']) {
      try { await collect(workspace.path, directory, 'workspace'); }
      catch (error) { if (error.code !== 'ENOENT') throw error; }
    }
  } else if (mode === 'from-files') {
    const paths = sourcePaths(inputs.paths);
    if (paths.length && !workspace) throw new Error('Select a workspace to read the source paths.');
    for (const [index, path] of paths.entries()) await collect(workspace.path, scopedPath(workspace.path, path), `workspace-${index + 1}`);
    for (const [index, directory] of sourceDirectories.entries()) await collect(directory, '', `upstream-${index + 1}`);
  }
  if (['from-files', 'from-current'].includes(mode) && !memories.length) throw new Error('The selected sources contain no Markdown or text files.');
  signal?.throwIfAborted();
  const library = storage.createLibrary({ id, name, description, catalogId, memories });
  const files = memories.map(memory => memory.relativePath);
  await writeReceipt(outputDir, 'library.json', { schema: 'doxie.native-library.v1', id, name, catalogId: library.catalogId, files });
  return { summary: `Library ${id} · ${memories.length} memories` };
}

async function mountLibrary({ storage, inputs, workspace, outputDir, signal }) {
  const libraryId = String(inputs.libraryid ?? inputs.libraryId ?? '').trim();
  const library = libraryId ? storage.get('libraries', libraryId) : null;
  if (!library) throw new Error('Select an existing library to mount.');
  const workspaceId = String(inputs.workspace || workspace?.id || '').trim();
  const target = workspaceId ? storage.get('workspaces', workspaceId) : null;
  if (!target) throw new Error('Select an existing workspace to mount the library.');
  signal?.throwIfAborted();
  const mountedLibraryIds = target.mountedLibraryIds || [];
  if (!mountedLibraryIds.some(id => id.toLowerCase() === library.id.toLowerCase())) {
    storage.save('workspaces', { id: target.id, mountedLibraryIds: [...mountedLibraryIds, library.id] });
  }
  await writeReceipt(outputDir, 'library.json', {
    schema: 'doxie.native-library-mount.v1', libraryId: library.id, workspaceId: target.id,
  });
  return { summary: `Mounted library ${library.id} in ${target.id}` };
}

async function manageSandbox({ toolMode, inputs, outputDir, scratchRoot, signal }) {
  if (!scratchRoot) throw new Error('Sandbox requires a running workflow.');
  signal?.throwIfAborted();
  const mode = toolMode || 'new';
  const tag = String(inputs.tag || 'scratch').trim();
  if (!slug.test(tag)) throw new Error('Enter a kebab-case sandbox tag.');
  await mkdir(scratchRoot, { recursive: true });
  if (mode === 'new' || mode === 'create') {
    const id = `${tag}-${randomUUID()}`;
    const directory = safePath(scratchRoot, id);
    signal?.throwIfAborted();
    await mkdir(directory);
    await writeReceipt(outputDir, 'sandbox.json', { schema: 'doxie.native-sandbox.v1', id, directory, expires: 'workflow-end' });
    return { summary: `Allocated temporary sandbox ${tag}` };
  }
  if (mode === 'list' || mode === 'clean') {
    const entries = (await readdir(scratchRoot, { withFileTypes: true })).filter(item => item.isDirectory() && slug.test(item.name));
    const ids = entries.map(item => item.name).filter(id => !inputs.tag || id.startsWith(`${tag}-`)).sort();
    await writeReceipt(outputDir, 'sandbox.json', { schema: 'doxie.native-sandbox.v1', ids, expires: 'workflow-end' });
    return { summary: mode === 'list' ? `${ids.length} workflow sandboxes` : `${ids.length} sandboxes expire when the workflow ends` };
  }
  throw new Error(`Unknown sandbox action: ${mode}`);
}

function runNativeUtility(toolId, options) {
  if (toolId === 'library' && options.toolMode === 'mount') return mountLibrary(options);
  if (toolId === 'library') return createLibrary(options);
  if (toolId === 'sandbox') return manageSandbox(options);
  throw new Error(`Unknown Doxie tool: ${toolId}`);
}

module.exports = { runNativeUtility };
