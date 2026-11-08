const assert = require('node:assert/strict');
const { createHash } = require('node:crypto');
const { EventEmitter } = require('node:events');
const { mkdtemp, readFile, readdir, rm, writeFile } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { test } = require('node:test');
const { createMemoryStore } = require('../../src/desktop/memory-store.cjs');
const { createSemanticMemory, cosine } = require('../../src/desktop/semantic-memory.cjs');

async function until(check) {
  for (let attempt = 0; attempt < 200; attempt++) {
    if (check()) return;
    await new Promise(resolve => setTimeout(resolve, 5));
  }
  throw new Error('The local semantic fixture did not reach the expected state.');
}
async function fixture(t, options = {}) {
  const root = await mkdtemp(join(tmpdir(), 'doxie-semantic-'));
  const store = createMemoryStore({ root, databasePath: ':memory:' });
  const contents = new Map([['config.json', Buffer.from('{"local":true}')], ['onnx/fixture.onnx', Buffer.from('verified fixture weights')]]);
  const model = { id: 'fixture-model', name: 'Fixture embedding model', repository: 'test/model', revision: 'immutable', dimensions: 3,
    files: [...contents].map(([path, buffer]) => ({ path, bytes: buffer.length, sha256: createHash('sha256').update(buffer).digest('hex') })) };
  const requested = [];
  const sent = [];
  const events = [];
  const workers = [];
  const vector = text => /cat|feline|gato|deployment/i.test(text) ? [1, 0, 0] : [0, 1, 0];
  class FakeWorker extends EventEmitter {
    postMessage(data) {
      sent.push(data);
      if (options.hang) return;
      queueMicrotask(() => this.emit('message', { id: data.id, type: 'result', vectors: data.texts.map(options.vector || vector) }));
    }
    async terminate() { this.emit('exit', 0); return 0; }
  }
  const semantic = createSemanticMemory({ root, store, model, emit: data => events.push(data),
    request: async (url, settings) => {
      requested.push(url.href);
      return options.request ? options.request(url, settings, contents) : new Response(contents.get(url.pathname.split('/immutable/')[1]));
    }, createWorker: () => { const worker = new FakeWorker(); workers.push(worker); return worker; }, timeoutMs: 5000,
  });
  t.after(async () => { await semantic.close(); store.close(); await rm(root, { recursive: true, force: true }); });
  return { root, store, semantic, contents, model, requested, sent, events, workers };
}

test('missing semantic models use FTS without downloading or starting a worker', async t => {
  const { store, semantic, requested, workers } = await fixture(t);
  store.remember({ title: 'Release checklist', content: 'Inspect the release.' });
  assert.equal((await semantic.status()).state, 'missing');
  const found = await semantic.search({ query: 'release' });
  assert.equal(found.mode, 'lexical');
  assert.equal(found.results.length, 1);
  assert.deepEqual(requested, []);
  assert.deepEqual(workers, []);
  await assert.rejects(semantic.reindex(), /Install the local embedding model/);
});

test('explicit installation verifies each file once, indexes in batches and persists vectors', async t => {
  const { root, store, semantic, requested, sent, events, model } = await fixture(t);
  for (let index = 0; index < 53; index++) store.remember({ title: `Memory ${index}`, content: `Cat care reference ${index}` });
  const installed = await Promise.all([semantic.install(), semantic.install()]);
  assert(installed.every(item => item.state === 'ready' && item.indexed === 53 && item.pending === 0));
  assert.equal(requested.length, 2);
  assert(sent.every(item => item.texts.length <= 25));
  assert.equal(sent.length, 3);
  const embeddings = store.getEmbeddings({ model: `${model.id}@${model.revision}` });
  assert.equal(embeddings.length, 53);
  assert.deepEqual(embeddings[0].vector, [1, 0, 0]);
  assert(events.some(event => event.type === 'memory:semanticChanged' && event.status.state === 'downloading'));
  assert(events.some(event => event.status.state === 'indexing'));
  assert.equal(await readFile(join(root, '.models', 'semantic', model.id, 'onnx', 'fixture.onnx'), 'utf8'), 'verified fixture weights');
  await semantic.install();
  assert.equal(requested.length, 2);
  assert.equal(sent.length, 3);
});

test('hybrid retrieval adds semantic matches, fuses lexical ranking and bounds payloads', async t => {
  const { store, semantic, requested } = await fixture(t);
  const cat = store.remember({ title: 'Cat care', content: 'Cats need food and water.' });
  const deployment = store.remember({ title: 'Deployment checklist', content: 'Deployment instructions. '.repeat(1800) });
  store.remember({ title: 'Cooking recipe', content: 'Prepare a warm meal.' });
  await semantic.install();
  const semanticOnly = await semantic.search({ query: 'feline companion', limit: 2 });
  assert.equal(semanticOnly.mode, 'hybrid');
  assert(semanticOnly.results.some(item => item.id === cat.id));
  const fused = await semantic.search({ query: 'deployment', limit: 2, maxBytes: 1200 });
  assert.equal(fused.results[0].id, deployment.id);
  assert.equal(fused.mode, 'hybrid');
  assert(fused.bytes <= 1200);
  assert(Buffer.byteLength(JSON.stringify(fused)) <= 1200);
  assert(Buffer.byteLength(JSON.stringify(fused.results)) <= 1200);
  const capped = await semantic.search({ query: 'deployment', maxBytes: 999999, limit: 100 });
  assert(capped.bytes <= 16000);
  assert.equal(requested.length, 2, 'query inference stays offline after the explicit install');
  assert.equal(cosine([1, 0], [2, 0]), 1);
  assert.equal(cosine([1, 0], [0, 1]), 0);
  assert.equal(cosine([1], [1, 2]), -1);
});

test('local indexing creates weighted semantic neighborhoods and respects lifecycle during retrieval', async t => {
  const { store, semantic } = await fixture(t);
  const cat = store.remember({ title: 'Cat care', content: 'Cats need food and water.' });
  const feline = store.remember({ title: 'Feline nutrition', content: 'A feline needs balanced meals.' });
  store.remember({ title: 'Cooking recipe', content: 'Prepare a warm meal.' });
  await semantic.install();
  const edge = store.graph().edges.find(edge => edge.inferred && [edge.fromId, edge.toId].includes(cat.id) && [edge.fromId, edge.toId].includes(feline.id));
  assert(edge, 'indexing must materialize a semantic relation');
  assert(Math.abs(edge.semanticScore - 1) < 1e-6);
  assert(edge.weight >= 0.62 && edge.weight <= 1);
  assert(Math.abs(edge.distance - (1 - edge.weight)) < 1e-6);
  store.review({ id: cat.id, status: 'stale', evidence: 'Needs a current veterinary reference.' });
  const current = await semantic.search({ query: 'feline' });
  assert(!current.results.some(item => item.id === cat.id));
  const withStale = await semantic.search({ query: 'feline', includeStale: true });
  assert(withStale.results.some(item => item.id === cat.id && item.lifecycle.status === 'stale'));
});

test('hybrid retrieval keeps large parameter and CJK provenance hits inside a 1 KiB total response', async t => {
  const { store, semantic } = await fixture(t);
  const parameters = Object.fromEntries(Array.from({ length: 100 }, (_, index) => [`項目_${index}`, '🐕'.repeat(25)]));
  const input = { title: 'Deployment parameters', kind: 'parameter', content: 'Deployment uses these typed parameters.', parameters };
  const saved = store.remember({ ...input, source: { provider: 'fixture', path: `/sources/0/${'資料'.repeat(300)}` } });
  for (let index = 1; index < 30; index++) store.remember({ ...input, source: { provider: 'fixture', path: `/sources/${index}/${'資料'.repeat(300)}` } });
  const offline = await semantic.search({ query: 'deployment', limit: 1, maxBytes: 1024 });
  assert.equal(offline.mode, 'lexical');
  assert.equal(offline.results[0].id, saved.id);
  assert(Buffer.byteLength(JSON.stringify(offline)) <= 1024);
  await semantic.install();
  const hybrid = await semantic.search({ query: 'deployment', limit: 1, maxBytes: 1024 });
  assert.equal(hybrid.mode, 'hybrid');
  assert.equal(hybrid.results[0].id, saved.id);
  assert(hybrid.results[0].parametersTruncated);
  assert(hybrid.results[0].provenanceTruncated);
  assert(Buffer.byteLength(JSON.stringify(hybrid)) <= 1024);
  assert.deepEqual(store.get(saved.id).parameters, parameters);
  assert.equal(store.get(saved.id).provenance.length, 30);
});

test('failed integrity, truncation and redirects never publish an incomplete model', async t => {
  for (const scenario of ['checksum', 'truncated', 'redirect']) {
    const { semantic, root, requested } = await fixture(t, { request: (url, settings, contents) => {
      const content = contents.get(url.pathname.split('/immutable/')[1]);
      if (scenario === 'redirect') return new Response(null, { status: 302, headers: { location: 'https://untrusted.example/model' } });
      return new Response(scenario === 'checksum' ? Buffer.alloc(content.length, 120) : content.subarray(0, content.length - 1));
    } });
    await assert.rejects(semantic.install(), scenario === 'redirect' ? /trusted source/ : /integrity verification/);
    assert.equal((await semantic.status()).state, 'missing');
    assert.deepEqual(await readdir(join(root, '.models', 'semantic')), []);
    assert.equal(requested.length, 1);
  }
});

test('changed model files are refused before worker inference and FTS remains available', async t => {
  const { root, store, semantic, model, workers } = await fixture(t);
  await semantic.install();
  await writeFile(join(root, '.models', 'semantic', model.id, 'config.json'), '{"local":null}');
  store.remember({ title: 'Cat care', content: 'Give the cat water.' });
  await assert.rejects(semantic.reindex(), /downloaded again/);
  assert.equal((await semantic.search({ query: 'cat' })).mode, 'lexical');
  assert.equal(workers.length, 0);
});

test('closing aborts a model download and removes its temporary files', async t => {
  let entered;
  const { semantic, root } = await fixture(t, { request: (url, { signal }) => new Promise((resolve, reject) => {
    entered = true;
    signal.addEventListener('abort', () => reject(signal.reason), { once: true });
  }) });
  const installation = semantic.install();
  const rejected = assert.rejects(installation);
  await until(() => entered);
  await semantic.close();
  await rejected;
  assert.deepEqual(await readdir(join(root, '.models', 'semantic')), []);
});

test('closing an embedding job rejects the wait without persisting partial vectors', async t => {
  const { store, semantic, sent, model } = await fixture(t, { hang: true });
  store.remember({ title: 'Cat care', content: 'Cats need water.' });
  const installation = semantic.install();
  const rejected = assert.rejects(installation, /closing/);
  await until(() => sent.length === 1);
  await semantic.close();
  await rejected;
  assert.equal(store.getEmbeddings({ model: `${model.id}@${model.revision}` }).length, 0);
});
