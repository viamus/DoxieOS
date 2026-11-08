const assert = require('node:assert/strict');
const { mkdirSync, mkdtempSync, rmSync, writeFileSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { dirname, join } = require('node:path');
const { test } = require('node:test');
const { createMemoryImportJobs } = require('../../src/desktop/memory-import-jobs.cjs');
const { createMemoryStore } = require('../../src/desktop/memory-store.cjs');

function setup(t) {
  const home = mkdtempSync(join(tmpdir(), 'doxie-memory-jobs-'));
  const root = join(home, 'app');
  const events = [];
  const store = createMemoryStore({ root });
  const jobs = createMemoryImportJobs({ root, store, emit: event => events.push(event) });
  const write = (path, content) => { mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, content); };
  t.after(async () => { await jobs.close(); store.close(); rmSync(home, { recursive: true, force: true }); });
  return { home, root, store, jobs, events, write };
}

test('explicit background import prepares both providers and commits idempotently through the Brain owner', async t => {
  const { home, store, jobs, events, write } = setup(t);
  write(join(home, '.codex', 'memories_v2', 'memory_summary.md'), '# Review policy\nRequire independent review.');
  write(join(home, '.claude', 'projects', 'workspace', 'memory', 'MEMORY.md'), '# Customer terms\nPreserve Portuguese terminology.');
  write(join(home, '.claude', 'CLAUDE.md'), '# Manual root instructions\nDo not import.');
  write(join(home, '.codex', 'MEMORY.md'), '# Manual root notes\nDo not import.');
  const discovered = await jobs.discover({ home });
  assert.equal(discovered.sources.length, 2);
  const preview = await jobs.preview({ sources: discovered.sources });
  assert.equal(preview.counts.files, 2);
  assert.equal(store.stats().total, 0);
  const result = await jobs.import({ sources: preview.sources, expectedFiles: preview.files });
  assert.equal(result.imported, 2);
  assert.equal(store.search({ query: 'review' }).results.length, 1);
  const repeated = await jobs.import({ sources: preview.sources, expectedFiles: preview.files });
  assert.equal(repeated.duplicates, 2);
  assert.equal(store.stats().total, 2);
  assert(events.some(event => event.type === 'memory:importProgress' && event.phase === 'scan'));
  assert(events.some(event => event.phase === 'import' && event.completed === 2 && event.total === 2));
  assert(events.every(event => typeof event.jobId === 'string'));
});

test('background validation rejects changed snapshots without persisting and the queue remains usable', async t => {
  const { home, store, jobs, write } = setup(t);
  const path = join(home, '.codex', 'memories', 'MEMORY.md');
  write(path, '# Reviewed memory\nOriginal policy.');
  const sources = [{ provider: 'codex', path: dirname(path) }];
  const preview = await jobs.preview({ sources });
  write(path, '# Reviewed memory\nChanged policy.');
  await assert.rejects(jobs.import({ sources, expectedFiles: preview.files }), /Memory sources changed\. Preview again/);
  assert.equal(store.stats().total, 0);
  const refreshed = await jobs.preview({ sources });
  assert.equal((await jobs.import({ sources, expectedFiles: refreshed.files })).imported, 1);
  await assert.rejects(jobs.preview({ sources, unsupported: () => {} }), /serializable/);
});

test('close cancels an active worker and queued operations, is idempotent, and rejects future work', async t => {
  const { home, jobs, write } = setup(t);
  write(join(home, '.codex', 'memories', 'MEMORY.md'), '# Closing\nA memory source.');
  const active = jobs.preview({ sources: [{ provider: 'codex', path: join(home, '.codex', 'memories') }] });
  const queued = jobs.discover({ home });
  const results = Promise.allSettled([active, queued]);
  await jobs.close();
  await jobs.close();
  assert((await results).every(item => item.status === 'rejected'));
  await assert.rejects(jobs.preview({ sources: [] }), /closed/);
});

test('reset cancellation quiesces imports, prevents late writes and keeps the service reusable', async t => {
  const { home, store, jobs, write } = setup(t);
  const source = join(home, '.codex', 'memories');
  write(join(source, 'fixture.md'), '# Extraction fixture\nOnly explicitly imported knowledge.');
  const options = { sources: [{ provider: 'codex', path: source }] };
  const active = jobs.import(options);
  const queued = jobs.import(options);
  const done = Promise.allSettled([active, queued]);
  await jobs.cancel();
  store.reset();
  assert((await done).every(result => result.status === 'rejected'));
  await new Promise(resolve => setTimeout(resolve, 15));
  assert.equal(store.stats().total, 0);
  assert.equal((await jobs.import(options)).imported, 1);
});
