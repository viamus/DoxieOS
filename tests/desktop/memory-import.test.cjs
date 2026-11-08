const assert = require('node:assert/strict');
const { mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { test } = require('node:test');
const { createMemoryStore } = require('../../src/desktop/memory-store.cjs');
const { discoverMemorySources, previewMemorySources, importMemorySources } = require('../../src/desktop/memory-import.cjs');

function setup(t) {
  const home = mkdtempSync(join(tmpdir(), 'doxie-memory-import-'));
  const codexHome = join(home, '.codex');
  const claudeHome = join(home, '.claude');
  const store = createMemoryStore({ root: join(home, 'app') });
  const write = (file, text) => { mkdirSync(require('node:path').dirname(file), { recursive: true }); writeFileSync(file, text); };
  t.after(() => { store.close(); rmSync(home, { force: true, recursive: true }); });
  return { home, codexHome, claudeHome, store, write };
}

test('discovers both providers and copies only previewed recognized memories, with idempotent revisions and unchanged originals', t => {
  const { home, codexHome, claudeHome, store, write } = setup(t);
  const codex = join(codexHome, 'memories_v2', 'memory_summary.md');
  const claude = join(claudeHome, 'CLAUDE.md');
  const codexInstructions = join(codexHome, 'MEMORY.md');
  const projectMemory = join(claudeHome, 'projects', 'project-one', 'memory', 'MEMORY.md');
  write(codex, '# Política de revisão\nExigir aprovação humana.\n');
  write(claude, '# Global instructions\nMANUAL_CLAUDE_MUST_NOT_IMPORT\n');
  write(codexInstructions, '# Manual profile notes\nMANUAL_CODEX_MUST_NOT_IMPORT\n');
  write(projectMemory, '# Project memory\nUse the staged delivery workflow.\n');
  write(join(codexHome, 'auth.json'), 'MUST_NOT_IMPORT_AUTH');
  write(join(codexHome, 'config.toml'), 'MUST_NOT_IMPORT_CONFIG');
  write(join(codexHome, 'memories_v2', 'transcripts', 'private.md'), 'MUST_NOT_IMPORT_TRANSCRIPT');
  write(join(claudeHome, 'projects', 'project-one', 'session.jsonl'), 'MUST_NOT_IMPORT_SESSION');
  const discovered = discoverMemorySources({ home, codexHome, claudeHome });
  assert.deepEqual(new Set(discovered.sources.map(source => source.provider)), new Set(['codex', 'claude-code']));
  const preview = previewMemorySources({ sources: discovered.sources });
  assert.equal(preview.counts.files, 2);
  assert(!JSON.stringify(preview).includes('MUST_NOT_IMPORT'));
  assert.equal(store.stats().total, 0, 'discovery and preview must not import');
  const originals = [codex, claude, codexInstructions, projectMemory].map(file => readFileSync(file, 'utf8'));
  assert.equal(importMemorySources(store, { sources: discovered.sources }).imported, 2);
  assert.equal(importMemorySources(store, { sources: discovered.sources }).duplicates, 2);
  assert.deepEqual([codex, claude, codexInstructions, projectMemory].map(file => readFileSync(file, 'utf8')), originals);
  assert.equal(store.search({ query: 'aprovacao' }).results.length, 1);
  write(codex, '# Política de revisão\nExigir duas aprovações humanas.\n');
  const updated = importMemorySources(store, { sources: discovered.sources });
  assert.equal(updated.imported, 1);
  assert.equal(updated.duplicates, 1);
  assert.equal(store.stats().total, 3);
  assert(store.graph().edges.some(edge => edge.type === 'supersedes'));
});

test('preview rejects symlinks, credential files and oversized sources instead of following or truncating them', t => {
  const { home, codexHome, store, write } = setup(t);
  const source = join(codexHome, 'memories_v2');
  write(join(source, 'allowed.md'), '# Known memory\nSmall safe note.');
  write(join(source, 'secrets.md'), '# Credentials\nDo not import this file.');
  write(join(source, 'credential-value.md'), 'sk' + '-' + 'x'.repeat(40));
  write(join(source, 'oversized.md'), 'x'.repeat(300 * 1024));
  write(join(source, 'invalid.md'), Buffer.from([0xff, 0xfe]));
  const outside = join(home, 'outside');
  write(join(outside, 'private.md'), 'OUTSIDE_MUST_NOT_BE_READ');
  symlinkSync(outside, join(source, 'linked'), process.platform === 'win32' ? 'junction' : 'dir');
  const preview = previewMemorySources({ sources: [{ provider: 'codex', path: source }], maxFileBytes: 256 * 1024 });
  assert.deepEqual(preview.files.map(file => file.name), ['allowed.md']);
  assert(preview.warnings.some(warning => /symbolic/i.test(warning.reason)));
  assert(preview.warnings.some(warning => /per-file limit/i.test(warning.reason)));
  assert(preview.warnings.some(warning => /read safely/i.test(warning.reason)));
  assert.equal(importMemorySources(store, { sources: [{ provider: 'codex', path: source }], maxFileBytes: 256 * 1024 }).imported, 1);
  assert.equal(store.stats().total, 1);
  assert(!store.list()[0].content.includes('OUTSIDE'));
  assert.equal(previewMemorySources({ sources: [{ provider: 'codex', path: join(codexHome, 'auth.json') }] }).files.length, 0);
});

test('file and byte budgets are enforced, selected file IDs are respected, and Unicode chunks retain the source', t => {
  const { codexHome, store, write } = setup(t);
  const source = join(codexHome, 'memories');
  const long = '# Histórico português\n' + 'Revisão 🐕 de São Paulo. '.repeat(1500);
  write(join(source, 'a.md'), long);
  write(join(source, 'b.md'), '# Another\nOnly import when selected.');
  const limited = previewMemorySources({ sources: [{ provider: 'codex', path: source }], maxFiles: 1 });
  assert.equal(limited.files.length, 1);
  assert(limited.warnings.some(item => /limit/i.test(item.reason)));
  const preview = previewMemorySources({ sources: [{ provider: 'codex', path: source }] });
  assert.equal(preview.files.length, 2);
  const selected = preview.files.find(file => file.name === 'a.md');
  const result = importMemorySources(store, { sources: preview.sources, selectedFileIds: [selected.id] });
  assert.equal(result.imported, selected.chunks);
  const copies = store.list({ limit: 100 }).sort((a, b) => a.source.chunk - b.source.chunk).map(item => store.get(item.id).content);
  assert.equal(copies.join(''), long);
  assert(copies.every(content => Buffer.byteLength(content) <= 16000 && !content.includes('\uFFFD')));
  assert.equal(previewMemorySources({ sources: preview.sources, maxTotalBytes: 5 }).files.length, 0);
});

test('a selected profile folder reads only recognized memory entries and never sibling markdown or configuration', t => {
  const { home, store, write } = setup(t);
  const profile = join(home, 'selected-provider-backup');
  write(join(profile, 'MEMORY.md'), '# Manual profile instructions\nDO_NOT_COPY_MANUAL_PROFILE_NOTES');
  write(join(profile, 'memories_v2', 'harness-learning.md'), '# Verified harness learning\nPrefer concise explanations.');
  write(join(profile, 'README.md'), 'DO_NOT_COPY_UNRELATED_README');
  write(join(profile, 'config.toml'), 'DO_NOT_COPY_CONFIGURATION');
  const preview = previewMemorySources({ sources: [{ provider: 'codex', path: profile }] });
  assert.deepEqual(preview.files.map(file => file.name), ['harness-learning.md']);
  assert.equal(importMemorySources(store, { sources: preview.sources }).imported, 1);
  assert(!store.list()[0].content.includes('DO_NOT_COPY'));
  assert.equal(previewMemorySources({ sources: [{ provider: 'codex', path: join(profile, 'MEMORY.md') }] }).files.length, 0);
});

test('selected Claude profiles include only global and project harness memory folders, never manual root instructions or project files', t => {
  const { claudeHome, store, write } = setup(t);
  const manual = join(claudeHome, 'CLAUDE.md');
  write(manual, '# Manual instructions\nROOT_MANUAL_NOTES');
  write(join(claudeHome, 'memory', 'MEMORY.md'), '# Harness memory\nGlobal harness learning.');
  write(join(claudeHome, 'projects', 'project-one', 'memory', 'MEMORY.md'), '# Project learning\nVerified project learning.');
  write(join(claudeHome, 'projects', 'project-one', 'CLAUDE.md'), 'PROJECT_MANUAL_NOTES');
  write(join(claudeHome, 'projects', 'README.md'), 'PROJECTS_MANUAL_NOTES');
  const preview = previewMemorySources({ sources: [{ provider: 'claude-code', path: claudeHome }] });
  assert.equal(preview.files.length, 2);
  assert(preview.files.every(file => file.relativePath === 'memory/MEMORY.md' || file.relativePath === 'projects/project-one/memory/MEMORY.md'));
  assert(!JSON.stringify(preview).includes('MANUAL_NOTES'));
  assert.equal(importMemorySources(store, { sources: preview.sources, expectedFiles: preview.files }).imported, 2);
  assert.equal(readFileSync(manual, 'utf8'), '# Manual instructions\nROOT_MANUAL_NOTES');
  assert.equal(previewMemorySources({ sources: [{ provider: 'claude-code', path: manual }] }).files.length, 0);
});

test('import verifies the entire preview snapshot before persisting any added, removed or changed source', t => {
  const { codexHome, store, write } = setup(t);
  const source = join(codexHome, 'memories_v2');
  const first = join(source, 'a.md');
  const second = join(source, 'b.md');
  write(first, '# First\nReviewed content.');
  write(second, '# Second\nReviewed context.');
  const sources = [{ provider: 'codex', path: source }];
  const preview = previewMemorySources({ sources });
  assert(preview.files.every(file => /^[a-f0-9]{64}$/.test(file.contentHash)));
  write(second, '# Second\nChanged after preview.');
  assert.throws(() => importMemorySources(store, { sources, expectedFiles: preview.files, selectedFileIds: [preview.files[0].id] }), /Memory sources changed\. Preview again/);
  assert.equal(store.stats().total, 0);
  write(second, '# Second\nReviewed context.');
  write(join(source, 'new.md'), '# New\nNot reviewed.');
  assert.throws(() => importMemorySources(store, { sources, expectedFiles: preview.files }), /Memory sources changed/);
  assert.equal(store.stats().total, 0);
  rmSync(join(source, 'new.md'));
  rmSync(second);
  assert.throws(() => importMemorySources(store, { sources, expectedFiles: preview.files }), /Memory sources changed/);
  assert.equal(store.stats().total, 0);
  write(second, '# Second\nReviewed context.');
  assert.equal(importMemorySources(store, { sources, expectedFiles: preview.files }).imported, 2);
});

test('manual imports accept larger memory files and preserve paragraph boundaries and every UTF-8 byte', t => {
  const { codexHome, store, write } = setup(t);
  const source = join(codexHome, 'memories_v2');
  const content = '# Long memory\r\n\r\n' + ('A revisão em São Paulo 🐕 keeps its source.\r\n\r\n'.repeat(7000));
  assert(Buffer.byteLength(content) > 256 * 1024);
  write(join(source, 'long.md'), content);
  const preview = previewMemorySources({ sources: [{ provider: 'codex', path: source }] });
  assert.equal(preview.files.length, 1);
  assert.equal(preview.warnings.length, 0);
  const result = importMemorySources(store, { sources: preview.sources, expectedFiles: preview.files });
  assert.equal(result.imported, preview.files[0].chunks);
  const chunks = store.list({ limit: 100 }).sort((a, b) => a.source.chunk - b.source.chunk).map(node => store.get(node.id).content);
  assert.equal(chunks.join(''), content);
  assert(chunks.slice(0, -1).every(chunk => chunk.endsWith('\r\n\r\n')));
  assert(chunks.every(chunk => Buffer.byteLength(chunk) <= 16000));
});

test('reimporting a shorter file retires removed chunks while preserving their history', t => {
  const { codexHome, store, write } = setup(t);
  const source = join(codexHome, 'memories');
  const file = join(source, 'shrinking.md');
  const options = { sources: [{ provider: 'codex', path: source }] };
  write(file, '# Evolving rules\n\n' + 'Review before release.\n\n'.repeat(1600));
  const first = importMemorySources(store, options);
  assert(first.imported > 1);
  const oldIds = store.list().map(memory => memory.id);
  write(file, '# Evolving rules\n\nThe current release rule.');
  assert.equal(importMemorySources(store, options).imported, 1);
  assert.equal(store.list().length, 1);
  assert(oldIds.every(id => { const memory = store.get(id); return memory.archivedAt || memory.supersededBy; }));
  assert.equal(store.stats().total, first.imported + 1);
});
