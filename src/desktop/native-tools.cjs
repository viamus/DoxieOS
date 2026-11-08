const { randomUUID } = require('node:crypto');
const { constants } = require('node:fs');
const { lstat, mkdir, open, readdir, writeFile } = require('node:fs/promises');
const { extname, isAbsolute, relative, resolve, sep, win32 } = require('node:path');
const { safePath } = require('./storage.cjs');

const slug = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const maxFiles = 256;
const defaultWorkspaceBytes = 16 * 1024;
const maxWorkspaceBytes = 64 * 1024;

async function writeReceipt(outputDir, name, data) {
  await writeFile(safePath(outputDir, name), JSON.stringify(data, null, 2) + '\n', 'utf8');
}

function workspacePaths(value) {
  let paths = value;
  if (typeof value === 'string') {
    if (value.trim().startsWith('[')) {
      try { paths = JSON.parse(value); } catch { throw new Error('Workspace memory paths must be relative paths, one per line, or a JSON list.'); }
    } else paths = value.split(/\r?\n/).map(item => item.trim()).filter(Boolean);
  }
  if (!Array.isArray(paths) || !paths.length || paths.length > maxFiles || paths.some(item => typeof item !== 'string' || !item.trim())) throw new Error('Select explicit workspace-relative Markdown paths or folders.');
  return paths.map(value => {
    const path = value.trim();
    if (path.length > 4096 || isAbsolute(path) || win32.isAbsolute(path) || path.split(/[\\/]/).includes('..')) throw new Error('Workspace memory paths must be relative and stay inside the selected workspace.');
    return path;
  });
}

async function workspaceMemory({ toolMode = 'load', inputs, workspace, outputDir, signal }) {
  if (!workspace) throw new Error('Select a workspace for Workspace Memory.');
  if (!['list', 'load'].includes(toolMode)) throw new Error(`Unknown workspace memory action: ${toolMode}`);
  const paths = workspacePaths(inputValue(inputs, 'paths'));
  const requested = inputValue(inputs, 'maxBytes');
  const budget = requested === undefined || requested === '' ? defaultWorkspaceBytes : Number(requested);
  if (!Number.isSafeInteger(budget) || budget < 1024 || budget > maxWorkspaceBytes) throw new Error('Workspace memory context must use a budget between 1024 and 65536 bytes.');
  const files = [];
  const seen = new Set();
  let scanned = 0;
  let scanTruncated = false;
  async function collect(relativePath, depth = 0, explicit = false) {
    signal?.throwIfAborted();
    if (depth > 12 || ++scanned > 4096 || files.length >= maxFiles) { scanTruncated = true; return; }
    const absolute = safePath(workspace.path, relativePath);
    const info = await lstat(absolute);
    if (info.isSymbolicLink()) throw new Error('Symbolic links cannot be loaded as workspace memory.');
    if (info.isDirectory()) {
      for (const entry of (await readdir(absolute, { withFileTypes: true })).sort((a, b) => a.name.localeCompare(b.name, 'en'))) {
        if (entry.name.startsWith('.')) continue;
        await collect([relativePath, entry.name].filter(Boolean).join('/'), depth + 1);
        if (files.length >= maxFiles || scanned >= 4096) { scanTruncated = true; break; }
      }
      return;
    }
    if (!info.isFile() || extname(absolute).toLowerCase() !== '.md') {
      if (explicit) throw new Error('Workspace Memory accepts only Markdown files or folders.');
      return;
    }
    const identity = process.platform === 'win32' ? absolute.toLowerCase() : absolute;
    if (seen.has(identity)) return;
    seen.add(identity);
    files.push({ path: relativePath, absolute, bytes: info.size });
  }
  for (const item of paths) {
    const path = relative(workspace.path, resolve(workspace.path, item)).split(sep).join('/');
    await collect(path, 0, true);
  }
  if (toolMode === 'load' && !files.length) throw new Error('The selected paths contain no Markdown files.');
  const receipt = { schema: 'doxie.workspace-memory.v1', action: toolMode,
    workspace: { id: workspace.id, path: workspace.path }, sources: [], bytesLoaded: 0, truncated: scanTruncated, omittedSources: files.length };
  const size = () => Buffer.byteLength(JSON.stringify(receipt));
  if (size() > budget) throw new Error('The selected workspace metadata exceeds this context budget.');
  for (const file of files) {
    signal?.throwIfAborted();
    const source = { path: file.path, bytes: file.bytes, loadedBytes: 0, truncated: false };
    if (toolMode === 'load') {
      const handle = await open(safePath(workspace.path, file.path), constants.O_RDONLY | (constants.O_NOFOLLOW || 0));
      try {
        const info = await handle.stat();
        if (!info.isFile()) throw new Error('Workspace memory source is not a regular file.');
        source.bytes = info.size;
        const buffer = Buffer.alloc(Math.min(info.size, budget) + 1);
        const read = await handle.read(buffer, 0, buffer.length, 0);
        const partial = read.bytesRead < info.size;
        source.content = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(buffer.subarray(0, read.bytesRead), { stream: partial });
        source.loadedBytes = Buffer.byteLength(source.content);
        source.truncated = source.loadedBytes < info.size;
      } finally { await handle.close(); }
    }
    receipt.sources.push(source);
    receipt.omittedSources--;
    receipt.bytesLoaded += source.loadedBytes;
    if (size() > budget && toolMode === 'load') {
      const original = source.content;
      const originalBytes = source.loadedBytes;
      source.truncated = true;
      receipt.bytesLoaded -= originalBytes;
      const precedingBytes = receipt.bytesLoaded;
      const characters = Array.from(original);
      let minimum = 0;
      let maximum = characters.length;
      let selected = '';
      while (minimum <= maximum) {
        const midpoint = Math.floor((minimum + maximum) / 2);
        source.content = characters.slice(0, midpoint).join('');
        source.loadedBytes = Buffer.byteLength(source.content);
        receipt.bytesLoaded = precedingBytes + source.loadedBytes;
        if (size() <= budget) { selected = source.content; minimum = midpoint + 1; }
        else maximum = midpoint - 1;
      }
      source.content = selected;
      source.loadedBytes = Buffer.byteLength(selected);
      receipt.bytesLoaded = precedingBytes + source.loadedBytes;
    }
    if (size() > budget) {
      receipt.sources.pop();
      receipt.omittedSources++;
      receipt.bytesLoaded -= source.loadedBytes;
      receipt.truncated = true;
      break;
    }
    if (source.truncated) receipt.truncated = true;
  }
  signal?.throwIfAborted();
  await writeReceipt(outputDir, 'workspace-memory.json', receipt);
  return { summary: `${toolMode === 'load' ? 'Loaded' : 'Listed'} ${receipt.sources.length} workspace Markdown source(s)${receipt.truncated ? ' (bounded context)' : ''}`, contextJson: receipt };
}

function inputValue(inputs, name) {
  const key = Object.keys(inputs).find(key => key.toLowerCase() === name.toLowerCase());
  return key === undefined ? undefined : inputs[key];
}

function jsonObject(value, label) {
  if (value === undefined || value === '') return undefined;
  let parsed = value;
  if (typeof value === 'string') {
    try { parsed = JSON.parse(value); } catch { throw new Error(`${label} must be valid JSON.`); }
  }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) throw new Error(`${label} must be a JSON object.`);
  return parsed;
}

async function memoryLibrary({ storage, toolMode, inputs, outputDir, provenance = {}, signal }) {
  if (!storage.memory) throw new Error('The global memory store is unavailable. Restart DoxieOS before using Library memory actions.');
  signal?.throwIfAborted();
  const value = name => inputValue(inputs, name);
  const mode = toolMode === 'search' ? 'query' : toolMode;
  let result;
  let summary;
  if (mode === 'remember') {
    const source = value('source');
    const suppliedSource = typeof source === 'string' && !source.trim().startsWith('{') ? { label: source } : jsonObject(source, 'Source');
    result = storage.memory.remember({
      title: String(value('title') || '').trim(), content: value('content') ?? '',
      libraryId: value('libraryId') || undefined, catalogId: value('catalogId') || undefined,
      kind: value('kind') || 'memory',
      tags: Array.isArray(value('tags')) ? value('tags') : String(value('tags') || '').split(/[,;\n]/).map(tag => tag.trim()).filter(Boolean),
      parameters: jsonObject(value('parameters') ?? value('parameters_json'), 'Parameters'),
      source: { provider: 'doxie', kind: 'native-library', ...suppliedSource, ...provenance,
        section: suppliedSource?.section || String(value('title') || '').trim() },
    });
    summary = `Remembered ${result.title || result.id} in the global library`;
  } else if (mode === 'query') {
    const searchOptions = { query: String(value('query') || '').trim(),
      libraryId: value('libraryId') || undefined, catalogId: value('catalogId') || undefined,
      kind: value('kind') || undefined,
      ...(value('limit') ? { limit: Number(value('limit')) } : {}),
      ...(value('maxBytes') ? { maxBytes: Number(value('maxBytes')) } : {}),
    };
    result = storage.memory.semantic?.search ? await storage.memory.semantic.search(searchOptions) : storage.memory.search(searchOptions);
    summary = `${result.results.length} global memory result(s)`;
  } else if (mode === 'relate') {
    result = storage.memory.relate({ fromId: value('fromId'), toId: value('toId'), type: value('type') || 'related' });
    summary = `Related global memories (${result.type})`;
  } else {
    throw new Error(`Unknown library memory action: ${toolMode}`);
  }
  signal?.throwIfAborted();
  const receipt = { schema: 'doxie.native-memory.v1', action: mode, result };
  await writeReceipt(outputDir, 'library.json', receipt);
  return { summary, contextJson: receipt };
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
  if (toolId === 'library' && options.toolMode === 'mount') throw new Error('Library mount has been retired. Libraries are global; use Library query to retrieve knowledge or remember to save it. Workspaces hold inputs and outputs only.');
  if (toolId === 'library' && ['remember', 'query', 'search', 'relate'].includes(options.toolMode)) return memoryLibrary(options);
  if (toolId === 'library') throw new Error('Manual Library creation and raw-file import have been retired. Use Library remember for verified harness learnings, or Workspace Memory load for explicit Markdown inputs.');
  if (toolId === 'workspace-memory') return workspaceMemory(options);
  if (toolId === 'sandbox') return manageSandbox(options);
  throw new Error(`Unknown Doxie tool: ${toolId}`);
}

module.exports = { runNativeUtility };
