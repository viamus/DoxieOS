const assert = require('node:assert/strict');
const { test } = require('node:test');
const { mkdtemp, mkdir, writeFile, readFile, rm } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { createStorage } = require('../../src/desktop/storage.cjs');
const { createMemoryStore } = require('../../src/desktop/memory-store.cjs');
const { createMemoryImportJobs } = require('../../src/desktop/memory-import-jobs.cjs');
const { createActions } = require('../../src/desktop/ipc.cjs');

async function fixture(t) {
  const root = await mkdtemp(join(tmpdir(), 'doxie-library-ipc-'));
  const storage = createStorage({ root, databasePath: ':memory:' });
  storage.memory = createMemoryStore({ root });
  storage.memoryImports = createMemoryImportJobs({ root, store: storage.memory });
  let selected;
  let picks = 0;
  const actions = createActions({ storage, emit() {}, window: () => null,
    dialog: { async showOpenDialog() { picks++; return { canceled: false, filePaths: [selected] }; } } });
  t.after(async () => { await storage.memoryImports.close(); storage.memory.close(); storage.close(); await rm(root, { recursive: true, force: true }); });
  return { root, storage, actions, choose: path => { selected = path; }, picks: () => picks };
}

test('renderer library registration is rejected while the agent harness owns the knowledge store', async t => {
  const { actions, storage, picks } = await fixture(t);
  for (const action of ['memory:remember', 'memory:revise', 'memory:relate', 'memory:archive', 'libraries:create', 'library-files:write', 'library-files:delete']) {
    assert.throws(() => actions[action]({ title: 'Manual registration', content: 'Not a harness learning.' }), /agent harness/);
  }
  assert.throws(() => actions['catalog:save']('libraries', { id: 'manual' }), /agent harness/);
  assert.throws(() => actions['catalog:remove']('libraries', 'manual'), /agent harness/);
  await assert.rejects(actions['catalog:import']('libraries'), /agent harness/);
  assert.equal(picks(), 0);
  assert.equal(storage.memory.stats().total, 0);
  storage.memory.remember({ title: 'Verified agent learning', content: 'The harness recorded this fact.', source: { provider: 'doxie-agent', runId: 'agent-fixture', section: 'fact' } });
  assert.equal(actions['memory:search']({ query: 'harness recorded' }).results.length, 1);
});

test('provider memory import requires manual discovery, preview and explicit selected files', async t => {
  const { actions, storage, picks } = await fixture(t);
  assert.equal(actions['memory:feedStatus']().state, 'disabled');
  const calls = [];
  const source = { id: 'recognized', provider: 'codex', path: 'known-harness-memory' };
  const file = { id: 'selected', contentHash: 'a'.repeat(64) };
  storage.memoryImports = {
    close() {},
    async discover(options) { calls.push(['discover', options]); return { sources: [source], warnings: [] }; },
    async preview(options) { calls.push(['preview', options]); return { files: [file], sources: [source], counts: { files: 1 } }; },
    async import(options) { calls.push(['import', options]); return { imported: 1, duplicates: 0 }; },
  };
  assert.equal(calls.length, 0, 'creating IPC never scans or imports provider memory');
  assert.throws(() => actions['memory:refreshSources'](), /manual/);
  await assert.rejects(actions['memory:importPreview']({ path: 'untrusted-renderer-path' }), /Find and select/);
  await assert.rejects(actions['memory:import']({ selectedFileIds: [file.id] }), /Preview/);
  await actions['memory:discoverSources']({ path: 'ignored-untrusted-path' });
  await assert.rejects(actions['memory:importPreview']({ sourceIds: ['unrecognized'] }), /Find and select/);
  const preview = await actions['memory:importPreview']({ sourceIds: [source.id] });
  assert.equal(calls.filter(([name]) => name === 'import').length, 0);
  await assert.rejects(actions['memory:import']({ previewId: preview.previewId, selectedFileIds: ['other-file'] }), /Select files/);
  assert.equal((await actions['memory:import']({ previewId: preview.previewId, selectedFileIds: [file.id], sources: [{ path: 'malicious' }] })).imported, 1);
  assert.deepEqual(calls.at(-1)[1].sources, [source]);
  assert.deepEqual(calls.at(-1)[1].expectedFiles, [file]);
  await assert.rejects(actions['memory:import']({ previewId: preview.previewId, selectedFileIds: [file.id] }), /Preview/);
  assert.equal(picks(), 0);
});

test('Brain clear requires explicit confirmation and uses the quiescing owner reset', async t => {
  const { storage, actions } = await fixture(t);
  storage.memory.remember({ title: 'Keep until confirmed', content: 'A deliberate reset is required.' });
  let resets = 0;
  storage.resetBrain = async () => { resets++; return storage.memory.reset(); };
  for (const input of [undefined, {}, { confirmation: 'yes' }, { confirmation: 'clear brain' }]) {
    await assert.rejects(actions['memory:clear'](input), /Confirm clearing/);
  }
  assert.equal(storage.memory.stats().total, 1);
  assert.equal(resets, 0);
  assert.equal((await actions['memory:clear']({ confirmation: 'CLEAR BRAIN' })).cleared, true);
  assert.equal(resets, 1);
  assert.equal(storage.memory.stats().total, 0);
});
