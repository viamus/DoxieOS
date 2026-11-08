const { createHash } = require('node:crypto');
const { closeSync, constants, fstatSync, lstatSync, openSync, readSync, readdirSync } = require('node:fs');
const { homedir } = require('node:os');
const { basename, extname, join, parse, relative, resolve, sep } = require('node:path');

const defaultLimits = { maxFiles: 500, maxFileBytes: 256 * 1024, maxTotalBytes: 16 * 1024 * 1024, maxDepth: 8 };
const deniedPart = /^(?:auth|credentials?|secrets?|passwords?|tokens|sessions?|transcripts?|logs?|shell_snapshots|config)(?:[._-]|$)/i;
const credentials = /-----BEGIN [\w ]*PRIVATE KEY-----|\b(?:sk-[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9]{24,}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,})/;
const fingerprint = text => createHash('sha256').update(text).digest('hex');
const providerId = value => value === 'claude' ? 'claude-code' : value;
const positive = (value, fallback, cap) => Number.isSafeInteger(value) && value > 0 ? Math.min(value, cap) : fallback;

function limits(options) {
  return { maxFiles: positive(options.maxFiles, defaultLimits.maxFiles, 4096),
    maxFileBytes: positive(options.maxFileBytes, defaultLimits.maxFileBytes, 256 * 1024),
    maxTotalBytes: positive(options.maxTotalBytes, defaultLimits.maxTotalBytes, 64 * 1024 * 1024),
    maxDepth: positive(options.maxDepth, defaultLimits.maxDepth, 12) };
}

function inspectPath(file) {
  const absolute = resolve(file);
  const root = parse(absolute).root;
  let current = root;
  for (const part of absolute.slice(root.length).split(sep).filter(Boolean)) {
    current = join(current, part);
    const info = lstatSync(current, { throwIfNoEntry: false });
    if (!info) return null;
    if (info.isSymbolicLink()) throw new Error('Symbolic links are not eligible memory sources.');
  }
  return lstatSync(absolute, { throwIfNoEntry: false });
}

function normalizeSource(source) {
  if (!source || typeof source !== 'object' || typeof source.path !== 'string' || !source.path.trim()) throw new Error('Choose a recognized memory source.');
  const provider = providerId(source.provider);
  if (!['codex', 'claude-code'].includes(provider)) throw new Error('Memory imports support Codex and Claude Code.');
  const path = resolve(source.path);
  const info = inspectPath(path);
  if (!info) throw new Error('The memory source no longer exists.');
  const name = basename(path).toLowerCase();
  const eligible = provider === 'codex'
    ? info.isDirectory() && ['memories', 'memories_v2'].includes(name)
    : info.isDirectory() && name === 'memory';
  const profileEntries = provider === 'codex' ? ['memories', 'memories_v2'] : ['memory', 'projects'];
  const profile = !eligible && info.isDirectory() && profileEntries.some(entry => inspectPath(join(path, entry))?.isDirectory());
  if (!eligible && !profile) throw new Error('Only recognized harness memory directories can be imported. Manual Markdown belongs in workspace inputs.');
  return { id: fingerprint(`${provider}\0${process.platform === 'win32' ? path.toLowerCase() : path}`), provider, path,
    name: source.name || basename(path), ...(profile ? { profileEntries } : {}) };
}

function eligibleMarkdown(path) {
  return extname(path).toLowerCase() === '.md' && !path.split(/[\\/]/).some(part => deniedPart.test(part));
}

function boundedRead(path, maximum) {
  const handle = openSync(path, constants.O_RDONLY | (constants.O_NOFOLLOW || 0));
  try {
    const info = fstatSync(handle);
    if (!info.isFile() || info.size > maximum) throw new Error('The memory file exceeds the file-size limit.');
    const buffer = Buffer.alloc(maximum + 1);
    let size = 0;
    while (size < buffer.length) {
      const count = readSync(handle, buffer, size, buffer.length - size, null);
      if (!count) break;
      size += count;
    }
    if (size > maximum) throw new Error('The memory file grew beyond the file-size limit.');
    const contents = buffer.subarray(0, size);
    return { text: new TextDecoder('utf-8', { fatal: true }).decode(contents), contentHash: fingerprint(contents) };
  } finally { closeSync(handle); }
}

function chunksFor(text, maxBytes = 16000) {
  const chunks = [];
  let content = '';
  let size = 0;
  for (const character of text) {
    const bytes = Buffer.byteLength(character);
    if (size + bytes > maxBytes && content) { chunks.push(content); content = ''; size = 0; }
    content += character;
    size += bytes;
  }
  if (content.trim()) chunks.push(content);
  return chunks;
}

function scan(options = {}, readContents = true) {
  const budget = limits(options);
  const warnings = [];
  const files = [];
  const sources = [];
  const seen = new Set();
  let bytes = 0;
  let stop = false;
  options.onProgress?.({ phase: 'scan', completed: 0, total: null });
  for (const candidate of options.sources || []) {
    let source;
    try { source = normalizeSource(candidate); }
    catch (error) { warnings.push({ path: candidate?.path || '', reason: error.message }); continue; }
    if (sources.some(item => item.id === source.id)) continue;
    const accepted = { ...source, fileCount: 0, bytes: 0 };
    sources.push(accepted);
    function visit(path, depth) {
      if (stop) return;
      if (depth > budget.maxDepth) { warnings.push({ path, reason: 'Memory source exceeds the directory-depth limit.' }); return; }
      let info;
      try { info = inspectPath(path); }
      catch (error) { warnings.push({ path, reason: error.message }); return; }
      if (!info) return;
      if (info.isDirectory()) {
        const profileParts = source.profileEntries ? relative(source.path, path).split(sep).filter(Boolean) : [];
        for (const entry of readdirSync(path, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
          if (depth === 0 && source.profileEntries && !source.profileEntries.some(name => name.toLowerCase() === entry.name.toLowerCase())) continue;
          if (source.provider === 'claude-code' && profileParts[0] === 'projects') {
            if (profileParts.length === 1 && !entry.isDirectory() && !entry.isSymbolicLink()) continue;
            if (profileParts.length === 2 && entry.name.toLowerCase() !== 'memory') continue;
          }
          if (entry.name.startsWith('.') || deniedPart.test(entry.name)) continue;
          visit(join(path, entry.name), depth + 1);
          if (stop) break;
        }
        return;
      }
      if (!info.isFile() || !eligibleMarkdown(path === source.path ? basename(path) : relative(source.path, path))) return;
      const identity = process.platform === 'win32' ? path.toLowerCase() : path;
      if (seen.has(identity)) return;
      seen.add(identity);
      if (info.size > budget.maxFileBytes) { warnings.push({ path, reason: 'Memory file exceeds the file-size limit.' }); return; }
      if (files.length >= budget.maxFiles || bytes + info.size > budget.maxTotalBytes) {
        warnings.push({ path, reason: 'Memory preview reached its file-count or total-size limit.' }); stop = true; return;
      }
      let text;
      let contentHash;
      try {
        if (readContents) {
          ({ text, contentHash } = boundedRead(path, budget.maxFileBytes));
          if (credentials.test(text)) { warnings.push({ path, reason: 'Credential material was detected; this file was excluded.' }); return; }
          if (!text.trim()) return;
        }
      } catch (error) { warnings.push({ path, reason: `Memory file could not be read safely: ${error.message}` }); return; }
      const title = text?.replace(/^---\r?\n[\s\S]*?\r?\n---\r?\n/, '').match(/^#{1,4}\s+(.+)$/m)?.[1]?.trim() || basename(path, extname(path));
      files.push({ id: fingerprint(`${source.provider}\0${identity}`), sourceId: source.id, provider: source.provider, path,
        relativePath: info.isFile() && path === source.path ? basename(path) : relative(source.path, path).split(sep).join('/'),
        name: basename(path), title: title.slice(0, 220), bytes: info.size, ...(readContents ? { contentHash, contentPreview: Array.from(text).slice(0, 1200).join(''), text } : {}) });
      accepted.fileCount++;
      accepted.bytes += info.size;
      bytes += info.size;
      options.onProgress?.({ phase: 'scan', completed: files.length, total: null, bytes });
    }
    visit(source.path, 0);
  }
  options.onProgress?.({ phase: 'scan', completed: files.length, total: files.length, bytes });
  return { sources, files, counts: { sources: sources.length, files: files.length, bytes }, warnings };
}

function discoverMemorySources({ home = homedir(), codexHome = join(home, '.codex'), claudeHome = join(home, '.claude'), ...options } = {}) {
  const sources = [];
  const warnings = [];
  function add(provider, path, name) {
    try { if (inspectPath(path)) sources.push(normalizeSource({ provider, path, name })); }
    catch (error) { warnings.push({ path, reason: error.message }); }
  }
  for (const name of ['memories_v2', 'memories']) add('codex', join(codexHome, name), `Codex / ${name}`);
  add('claude-code', join(claudeHome, 'memory'), 'Claude Code / global memory');
  const projects = join(claudeHome, 'projects');
  try {
    if (inspectPath(projects)?.isDirectory()) for (const project of readdirSync(projects, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
      if (project.isSymbolicLink()) { warnings.push({ path: join(projects, project.name), reason: 'Symbolic links are not eligible memory sources.' }); continue; }
      if (project.isDirectory()) add('claude-code', join(projects, project.name, 'memory'), `Claude Code / ${project.name}`);
    }
  } catch (error) { warnings.push({ path: projects, reason: error.message }); }
  const result = scan({ ...options, sources }, false);
  return { sources: result.sources, warnings: [...warnings, ...result.warnings] };
}

function previewMemorySources(options = {}) {
  const result = scan(options);
  return { ...result, files: result.files.map(({ text, ...file }) => ({ ...file, chunks: chunksFor(text).length })) };
}

function importMemorySources(store, options = {}) {
  if (!store || typeof store.remember !== 'function') throw new Error('An active central memory store is required.');
  const result = scan(options);
  if (options.expectedFiles !== undefined) {
    const expected = options.expectedFiles;
    const changed = !Array.isArray(expected) || expected.length !== result.files.length
      || new Set(expected.map(item => item?.id)).size !== expected.length
      || expected.some(item => typeof item?.id !== 'string' || !/^[a-f0-9]{64}$/.test(item.contentHash || ''))
      || result.files.some(file => !expected.some(item => item.id === file.id && item.contentHash === file.contentHash));
    if (changed) throw new Error('Memory sources changed. Preview again before importing.');
  }
  const selected = options.selectedFileIds === undefined ? null : new Set(options.selectedFileIds);
  let imported = 0;
  let duplicates = 0;
  let skipped = result.warnings.length;
  const total = result.files.filter(file => !selected || selected.has(file.id)).length;
  let completed = 0;
  options.onProgress?.({ phase: 'import', completed, total });
  for (const file of result.files) {
    if (selected && !selected.has(file.id)) { skipped++; continue; }
    const chunks = chunksFor(file.text);
    for (let chunk = 0; chunk < chunks.length; chunk++) {
      const before = store.stats().total;
      store.remember({ title: `${file.title}${chunks.length > 1 ? ` (${chunk + 1}/${chunks.length})` : ''}`.slice(0, 240),
        content: chunks[chunk], kind: 'memory', libraryId: options.libraryId, catalogId: options.catalogId,
        tags: [file.provider, 'imported'], source: { provider: file.provider, path: file.path, chunk, label: file.title } });
      if (store.stats().total === before) duplicates++; else imported++;
    }
    completed++;
    options.onProgress?.({ phase: 'import', completed, total });
  }
  return { imported, duplicates, skipped, files: result.files.length, sources: result.sources, warnings: result.warnings };
}

module.exports = { discoverMemorySources, previewMemorySources, importMemorySources };
