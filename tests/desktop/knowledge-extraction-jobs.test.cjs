const assert = require('node:assert/strict');
const { test } = require('node:test');
const { tmpdir } = require('node:os');
const { createMemoryStore } = require('../../src/desktop/memory-store.cjs');
const { createKnowledgeExtraction } = require('../../src/desktop/knowledge-extraction-jobs.cjs');

const turn = () => new Promise(resolve => setImmediate(resolve));
const everyRecord = { includeStale: true, includeArchived: true, includeSuperseded: true };
function fixture(t, options = {}) {
  const store = createMemoryStore({ root: tmpdir(), databasePath: ':memory:' });
  const events = [];
  const jobs = createKnowledgeExtraction({ store, extract: literalFixture, emit: event => events.push(event), ...options });
  t.after(async () => { await jobs.close(); store.close(); });
  return { store, jobs, events };
}
function source(store, path, content = 'Repository DoxieOS.\nregion: br') {
  return store.remember({ title: 'Project configuration', content, source: { provider: 'codex', path } });
}
function literalFixture(memory) {
  function evidence(start, excerpt) {
    const end = start + excerpt.length;
    return { memoryId: memory.id, start, end, startLine: memory.content.slice(0, start).split('\n').length,
      endLine: memory.content.slice(0, end).split('\n').length, excerpt };
  }
  const start = memory.content.indexOf('DoxieOS');
  const entities = start < 0 ? [] : [{ key: 'repository:doxieos', entityType: 'repository', value: 'DoxieOS',
    title: 'DoxieOS', content: 'repository: DoxieOS', tags: ['repository'], evidence: evidence(start, 'DoxieOS') }];
  const parameter = /region: ([a-z-]+)/.exec(memory.content);
  return { entities, parameters: parameter ? [{ key: `parameter:region:${parameter[1]}`, title: `region: ${parameter[1]}`,
    content: `region: ${parameter[1]}`, parameters: { region: parameter[1] }, tags: ['configuration'],
    evidence: evidence(parameter.index, parameter[0]) }] : [], truncated: false };
}

test('local extraction builds shared entities and typed parameters with per-source evidence and no writes on unchanged reruns', async t => {
  const { store, jobs } = fixture(t);
  const first = source(store, 'memory/a.md');
  const second = source(store, 'memory/b.md', 'Release references Repository DoxieOS.\nregion: br');
  assert.equal(store.stats().entities, 0, 'constructing the service does not scan or enrich anything');
  const result = await jobs.run();
  assert.equal(result.state, 'ready');
  assert.equal(result.created, 2);
  assert.equal(result.processed, 2);
  const derived = store.records(everyRecord).filter(node => node.kind !== 'memory');
  assert.equal(derived.length, 2, 'identical canonical facts share one record across sources');
  for (const fact of derived) {
    assert.equal(fact.origin, 'optimized');
    assert.equal(fact.provenance.length, 2);
    for (const origin of fact.provenance) {
      assert.equal(origin.provider, 'doxie-extractor');
      assert(origin.label.includes('not independently verified'));
      assert([`memory:${first.id}`, `memory:${second.id}`].includes(origin.path));
    }
  }
  assert.deepEqual(derived.find(node => node.kind === 'parameter').parameters, { region: 'br' });
  const edges = store.graph({ includeStale: true, maxBytes: 16000 }).edges;
  assert.equal(edges.filter(edge => edge.type === 'mentions').length, 2);
  assert.equal(edges.filter(edge => edge.type === 'supports').length, 2);
  for (const method of ['remember', 'relate', 'review', 'batch']) store[method] = () => assert.fail(`Unchanged extraction called ${method}`);
  const unchanged = await jobs.run();
  assert.equal(unchanged.created, 0);
  assert.equal(unchanged.linked, 0);
  assert.equal(unchanged.reviewed, 0);
});

test('shared derived facts stay current while one source remains active and withdraw when all anchors are archived', async t => {
  const { store, jobs } = fixture(t);
  const first = source(store, 'memory/a.md');
  const second = source(store, 'memory/b.md', 'Repository DoxieOS.\nregion: br\nIndependent source.');
  await jobs.run();
  const derived = store.records().filter(node => node.kind !== 'memory');
  store.archive(first.id);
  await jobs.run();
  assert(derived.every(node => store.get(node.id).status === 'current'));
  store.archive(second.id);
  const withdrawn = await jobs.run();
  assert.equal(withdrawn.withdrawn, 2);
  assert(derived.every(node => store.get(node.id).status === 'archived'));
  store.review({ id: second.id, status: 'current', evidence: 'User restored this source.' });
  await jobs.run();
  assert(derived.every(node => store.get(node.id).status === 'current'));
  store.review({ id: derived[0].id, status: 'archived', evidence: 'The user intentionally removed this fact from retrieval.' });
  await jobs.run();
  assert.equal(store.get(derived[0].id).status, 'archived', 'automatic enrichment preserves a manual archive');
});

test('derived lifecycle follows current and stale supporting sources without making stale assertions fresh', async t => {
  const { store, jobs } = fixture(t, { batchSize: 1 });
  const first = source(store, 'memory/stale-a.md');
  const second = source(store, 'memory/stale-b.md', 'Repository DoxieOS.\nregion: br\nOther source.');
  store.review({ id: first.id, status: 'stale', reviewAfter: '2031-01-01T00:00:00Z', evidence: 'Requires verification.' });
  store.review({ id: second.id, status: 'stale', reviewAfter: '2031-01-01T00:00:00Z', evidence: 'Requires verification.' });
  await jobs.run();
  const derived = store.records(everyRecord).filter(node => node.kind !== 'memory');
  assert(derived.every(node => node.status === 'stale'));
  assert.equal(store.search({ query: 'DoxieOS region' }).results.length, 0);
  store.review({ id: second.id, status: 'current', reviewAfter: '2030-01-01T00:00:00Z', evidence: 'The current source was revalidated.' });
  await jobs.run();
  assert(derived.every(node => store.get(node.id).status === 'current'));
  assert(derived.every(node => store.get(node.id).reviewAfter === '2030-01-01T00:00:00.000Z'));
  store.review({ id: derived[0].id, status: 'stale', evidence: 'User flags this extraction for closer review.' });
  await jobs.run();
  assert.equal(store.get(derived[0].id).status, 'stale');
});

test('a source revision withdraws the old typed parameter and preserves the shared entity', async t => {
  const { store, jobs } = fixture(t);
  const original = source(store, 'memory/config.md');
  await jobs.run();
  const entity = store.records({ kind: 'entity' })[0];
  const parameter = store.records({ kind: 'parameter' })[0];
  const revised = source(store, 'memory/config.md', 'Repository DoxieOS.\nregion: us');
  assert.notEqual(revised.id, original.id);
  await jobs.run();
  assert.equal(store.get(parameter.id).status, 'archived');
  assert.equal(store.get(entity.id).status, 'current');
  assert.equal(store.get(entity.id).provenance.length, 2);
  assert.deepEqual(store.records({ kind: 'parameter' })[0].parameters, { region: 'us' });
});

test('cancel waits for in-flight extraction and blocks every late write before a Brain reset', async t => {
  let release;
  let started;
  const entered = new Promise(resolve => { started = resolve; });
  const gate = new Promise(resolve => { release = resolve; });
  const { store, jobs } = fixture(t, { extract: async memory => { started(); await gate; return literalFixture(memory); } });
  source(store, 'memory/cancel.md');
  const running = jobs.run();
  await entered;
  let cancelled = false;
  const cancellation = jobs.cancel().then(() => { cancelled = true; });
  await turn();
  assert.equal(cancelled, false, 'reset must wait for the in-flight extraction to quiesce');
  release();
  await Promise.all([running, cancellation]);
  assert.equal(jobs.status().state, 'idle');
  assert.equal(store.stats().entities, 0);
  store.reset();
  await turn();
  assert.equal(store.stats().total, 0);
  await jobs.run();
  assert.equal(store.stats().total, 0, 'no cancelled extraction can repopulate a reset Brain');
});

test('concurrent requests coalesce and rescan a source added during an extraction', async t => {
  let release;
  let started;
  let first = true;
  const entered = new Promise(resolve => { started = resolve; });
  const gate = new Promise(resolve => { release = resolve; });
  const { store, jobs } = fixture(t, { extract: async memory => { if (first) { first = false; started(); await gate; } return literalFixture(memory); } });
  source(store, 'memory/a.md');
  const running = jobs.run();
  await entered;
  source(store, 'memory/new.md', 'Repository DoxieOS.\nregion: eu');
  assert.equal(jobs.run(), running);
  release();
  await running;
  assert.equal(store.stats().entities, 1);
  assert.equal(store.stats().parameters, 2);
});

test('invalid literal evidence aborts the source transaction without saving misleading knowledge', async t => {
  const { store, jobs } = fixture(t, { extract: memory => {
    const result = literalFixture(memory);
    result.parameters[0].evidence.excerpt = 'Invented evidence';
    return result;
  } });
  source(store, 'memory/source.md');
  await assert.rejects(jobs.run(), /exact literal source evidence/);
  assert.equal(jobs.status().state, 'error');
  assert.equal(store.stats().entities, 0);
  assert.equal(store.stats().parameters, 0);
});

test('the production literal extractor enriches existing Brain text with repository and typed JSON evidence', async t => {
  const { store, jobs } = fixture(t, { extract: undefined });
  const content = 'Repository: https://github.com/example/brain-workbench\n\n```json\n{"max_results":8,"enabled":true,"language":"pt-BR"}\n```';
  const memory = source(store, 'memory/real-extractor.md', content);
  const result = await jobs.run();
  assert.equal(result.created, 2);
  const entity = store.records({ kind: 'entity' })[0];
  const parameter = store.records({ kind: 'parameter' })[0];
  assert.equal(entity.content, 'repository: https://github.com/example/brain-workbench');
  assert.deepEqual(parameter.parameters, { enabled: true, language: 'pt-BR', max_results: 8 });
  for (const node of [entity, parameter]) {
    const evidence = node.source.evidence;
    assert.equal(evidence.memoryId, memory.id);
    assert.equal(content.slice(evidence.start, evidence.end), evidence.excerpt);
    assert(node.source.label.includes(evidence.excerpt));
    assert.equal(node.source.path, `memory:${memory.id}`);
  }
  assert.equal((await jobs.run()).created, 0);
});

test('production extraction preserves safe typed settings beside credential fields without aborting the job', async t => {
  const { store, jobs } = fixture(t, { extract: undefined });
  source(store, 'memory/mixed-config.md', '```json\n{"accessKey":"dummy","retryLimit":3}\n```');
  await jobs.run();
  const parameters = store.records({ kind: 'parameter' });
  assert.equal(parameters.length, 1);
  assert.deepEqual(parameters[0].parameters, { retryLimit: 3 });
  assert(!JSON.stringify(parameters[0].source.evidence).includes('accessKey'));
  assert(!JSON.stringify(parameters[0].source.evidence).includes('dummy'));
});
