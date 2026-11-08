const assert = require('node:assert/strict');
const { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { dirname, join } = require('node:path');
const { setTimeout: delay } = require('node:timers/promises');
const { test } = require('node:test');
const { createMemoryFeed } = require('../../src/desktop/memory-feed.cjs');
const { createMemoryImportJobs } = require('../../src/desktop/memory-import-jobs.cjs');
const { createMemoryStore } = require('../../src/desktop/memory-store.cjs');

const source = { id: 'source-1', provider: 'codex', path: '/fixture/memories_v2', name: 'Codex memory', fileCount: 1, bytes: 10 };
const file = { id: 'memory-file', contentHash: 'a'.repeat(64) };

function fakeImports() {
  const calls = [];
  return { calls,
    async discover(options) { calls.push({ method: 'discover', options }); return { sources: [source], warnings: [] }; },
    async preview(options) { calls.push({ method: 'preview', options }); return { sources: [source], files: [file] }; },
    async import(options) { calls.push({ method: 'import', options }); return { imported: 1, duplicates: 0, skipped: 0, files: 1, warnings: [] }; },
  };
}

test('disabled feed performs no scans, imports, timers or emissions', async () => {
  const imports = fakeImports();
  const events = [];
  const feed = createMemoryFeed({ imports, enabled: false, emit: event => events.push(event), intervalMs: 5 });
  assert.equal(feed.start().state, 'disabled');
  assert.equal((await feed.refresh()).state, 'disabled');
  await delay(15);
  await feed.close();
  assert.deepEqual(imports.calls, []);
  assert.deepEqual(events, []);
});

test('simultaneous refreshes coalesce and transmit exact snapshot hashes and known-home options', async () => {
  const imports = fakeImports();
  const events = [];
  let resolveDiscovery;
  imports.discover = options => { imports.calls.push({ method: 'discover', options }); return new Promise(resolve => { resolveDiscovery = resolve; }); };
  const feed = createMemoryFeed({ imports, codexHome: '/fixture/codex', claudeHome: '/fixture/claude', emit: event => events.push(event) });
  const first = feed.refresh();
  const second = feed.refresh();
  assert.equal(first, second);
  assert.equal(feed.status().state, 'scanning');
  await Promise.resolve();
  resolveDiscovery({ sources: [source], warnings: [] });
  const result = await first;
  assert.equal(result.state, 'ready');
  assert.equal(result.lastResult.imported, 1);
  assert.equal(imports.calls.filter(call => call.method === 'discover').length, 1);
  assert.equal(imports.calls[0].options.codexHome, '/fixture/codex');
  assert.equal(imports.calls[0].options.claudeHome, '/fixture/claude');
  assert.deepEqual(imports.calls.find(call => call.method === 'import').options.expectedFiles, [file]);
  assert(events.some(event => event.type === 'memory:changed'));
  assert(events.some(event => event.type === 'memory:feedChanged' && event.status.state === 'importing'));
  const copy = feed.status();
  copy.sources[0].name = 'Changed outside';
  assert.equal(feed.status().sources[0].name, source.name);
  await feed.close();
});

test('refresh triggered by a status observer shares the existing job rather than recursively starting another scan', async () => {
  const imports = fakeImports();
  let observed;
  let feed;
  feed = createMemoryFeed({ imports, emit: event => {
    if (event.type === 'memory:feedChanged' && event.status.state === 'scanning') observed = feed.refresh();
  } });
  const started = feed.refresh();
  await started;
  assert.equal(observed, started);
  assert.equal(imports.calls.filter(call => call.method === 'discover').length, 1);
  await feed.close();
});

test('a changed preview is refreshed once, persistent errors are observable, and later refresh recovers', async () => {
  const imports = fakeImports();
  let attempts = 0;
  imports.import = async options => {
    imports.calls.push({ method: 'import', options });
    if (++attempts < 2) throw new Error('Memory sources changed. Preview again before importing.');
    return { imported: 0, duplicates: 1, skipped: 0 };
  };
  const events = [];
  const feed = createMemoryFeed({ imports, emit: event => events.push(event) });
  assert.equal((await feed.refresh()).state, 'ready');
  assert.equal(imports.calls.filter(call => call.method === 'preview').length, 2);
  assert(!events.some(event => event.type === 'memory:changed'), 'duplicate imports do not publish new knowledge');
  imports.discover = async () => { throw new Error('Source unavailable'); };
  const failed = await feed.refresh();
  assert.equal(failed.state, 'error');
  assert.equal(failed.error, 'Source unavailable');
  imports.discover = async () => ({ sources: [source] });
  const recovered = await feed.refresh();
  assert.equal(recovered.state, 'ready');
  assert.equal(recovered.error, undefined);
  await feed.close();
});

test('start creates one periodic refresh stream and close does not hang on an unresolved worker', async () => {
  const imports = fakeImports();
  let calls = 0;
  let release;
  imports.discover = () => { calls++; return new Promise(resolve => { release = resolve; }); };
  const events = [];
  const feed = createMemoryFeed({ imports, intervalMs: 5, emit: event => events.push(event) });
  feed.start();
  feed.start();
  await delay(20);
  assert.equal(calls, 1, 'timer ticks share the running refresh rather than starting jobs');
  await feed.close();
  const before = events.length;
  release({ sources: [source] });
  await delay(15);
  assert.equal(calls, 1);
  assert.equal(imports.calls.length, 0, 'closing prevents preview/import after the active scan finishes');
  assert.equal(events.length, before);
  assert.equal((await feed.refresh()).state, 'scanning');
});

test('automatic feed imports both harness providers through real background workers and preserves manual sources', async t => {
  const home = mkdtempSync(join(tmpdir(), 'doxie-memory-feed-'));
  const root = join(home, 'app');
  const codexHome = join(home, '.codex');
  const claudeHome = join(home, '.claude');
  const write = (path, text) => { mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, text); };
  const codex = join(codexHome, 'memories_v2', 'memory_summary.md');
  const claude = join(claudeHome, 'projects', 'fixture', 'memory', 'MEMORY.md');
  const manual = join(claudeHome, 'CLAUDE.md');
  write(codex, '# Codex harness learning\nPreserve review evidence.');
  write(claude, '# Claude harness learning\nUse the verified release channel.');
  write(manual, '# Manual root instructions\nNever ingest these directly.');
  const original = readFileSync(manual, 'utf8');
  const store = createMemoryStore({ root });
  const imports = createMemoryImportJobs({ root });
  const feed = createMemoryFeed({ imports, codexHome, claudeHome });
  t.after(async () => { await feed.close(); await imports.close(); store.close(); rmSync(home, { recursive: true, force: true }); });
  assert.equal((await feed.refresh()).lastResult.imported, 2);
  assert.equal((await feed.refresh()).lastResult.duplicates, 2);
  assert.equal(store.stats().total, 2);
  assert.equal(store.search({ query: 'ingest' }).results.length, 0);
  assert.equal(readFileSync(manual, 'utf8'), original);
  assert.deepEqual(new Set(feed.status().sources.map(item => item.provider)), new Set(['codex', 'claude-code']));
});
