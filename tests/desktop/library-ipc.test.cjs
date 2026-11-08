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
  storage.memoryImports = createMemoryImportJobs({ root });
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

test('source feed inspection and refresh use only the harness service, without renderer paths', async t => {
  const { actions, storage, picks } = await fixture(t);
  assert.equal(actions['memory:feedStatus']().state, 'disabled');
  let refreshes = 0;
  const status = { state: 'ready', sources: [{ provider: 'codex', path: 'known-harness-memory' }], lastResult: { imported: 2 } };
  storage.memoryFeed = { status: () => status, refresh() { assert.equal(arguments.length, 0); refreshes++; return Promise.resolve(status); } };
  assert.equal(actions['memory:feedStatus'](), status);
  assert.equal(await actions['memory:refreshSources']({ path: 'untrusted-renderer-path', provider: 'manual' }), status);
  assert.equal(refreshes, 1);
  assert.equal(picks(), 0);
  for (const action of ['memory:importPreview', 'memory:import']) assert.throws(() => actions[action]({ path: 'manual-document.md' }), /agent harness/);
});
