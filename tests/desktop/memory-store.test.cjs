const assert = require('node:assert/strict');
const { mkdtempSync, rmSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { test } = require('node:test');
const { createMemoryStore } = require('../../src/desktop/memory-store.cjs');

function setup(t, options = {}) {
  const root = mkdtempSync(join(tmpdir(), 'doxie-memory-store-'));
  const store = createMemoryStore({ root, ...options });
  t.after(() => { store.close(); rmSync(root, { force: true, recursive: true }); });
  return { store, root };
}

test('central memory supports Portuguese FTS, typed parameters and catalog filters independently of workspaces', t => {
  const { store } = setup(t);
  const policy = store.remember({ title: 'Política de revisão', content: 'A revisão de segurança exige aprovação humana antes da publicação.',
    libraryId: 'engineering', catalogId: 'global', tags: ['segurança', 'revisão'], source: { provider: 'catalog', path: 'memory/policy.md' } });
  store.remember({ title: 'Outro catálogo', content: 'Revisão de segurança de outro domínio.', libraryId: 'engineering', catalogId: 'other' });
  const parameter = store.remember({ kind: 'parameter', title: 'Prazo de revisão', parameters: { days: 3, enabled: true, region: 'São Paulo' } });
  const result = store.search({ query: 'revisao seguranca', libraryId: 'engineering', catalogId: 'global' });
  assert.deepEqual(result.results.map(item => item.id), [policy.id]);
  assert(result.results[0].score > 0);
  assert.equal(store.get(parameter.id).parameters.days, 3);
  assert.equal(store.list({ kind: 'parameter' }).length, 1);
  assert.equal(store.stats().parameters, 1);
  assert.equal(store.list({ catalogId: null }).length, 1);
  assert.equal(store.search({ query: '"; DROP TABLE memories; --' }).query, '"; DROP TABLE memories; --');
  assert.equal(store.stats().memories, 2);
});

test('provenance imports are idempotent, revisions preserve originals and explicit revision links reject cycles', t => {
  const { store } = setup(t);
  const input = { title: 'Review rule', content: 'Require one reviewer.', source: { provider: 'codex', path: 'MEMORY.md', chunk: 0 } };
  const first = store.remember(input);
  assert.equal(store.remember(input).id, first.id);
  const secondOrigin = store.remember({ ...input, source: { provider: 'claude-code', path: 'CLAUDE.md' } });
  assert.equal(secondOrigin.id, first.id);
  assert.equal(store.get(first.id).provenance.length, 2);
  const revised = store.remember({ ...input, content: 'Require two reviewers.' });
  assert.notEqual(revised.id, first.id);
  assert.equal(store.get(first.id).content, input.content);
  assert.equal(store.get(first.id).supersededBy, revised.id);
  assert.deepEqual(store.search({ query: 'reviewer' }).results.map(item => item.id), [revised.id]);
  const graph = store.graph();
  assert(graph.edges.some(edge => edge.from === revised.id && edge.to === first.id && edge.type === 'supersedes'));
  assert.throws(() => store.relate({ fromId: first.id, toId: revised.id, type: 'supersedes' }), /cycle/);
  assert.equal(store.stats().total, 2);
});

test('memory graph connects explicit entities and related knowledge with bounded filtered nodes', t => {
  const { store } = setup(t);
  const company = store.remember({ kind: 'entity', title: 'Acme', content: 'A client organization.', libraryId: 'clients' });
  const decision = store.remember({ title: 'Contract approval', content: 'The legal team approves contracts for Acme.', libraryId: 'clients' });
  const unrelated = store.remember({ title: 'Deployment', content: 'Deployment requires review.', libraryId: 'engineering' });
  const edge = store.relate({ fromId: decision.id, toId: company.id, type: 'mentions' });
  assert.equal(store.relate({ fromId: decision.id, toId: company.id, type: 'mentions' }).id, edge.id);
  store.relate({ fromId: unrelated.id, toId: decision.id, type: 'supported' });
  const graph = store.graph({ query: 'Acme', libraryId: 'clients', limit: 10 });
  assert.equal(graph.nodes.length, 2);
  assert.equal(graph.edges.length, 1);
  assert.equal(graph.edges[0].type, 'mentions');
  assert(!graph.nodes.some(node => node.id === unrelated.id));
  assert(store.graph({ limit: 1 }).truncated);
  assert.throws(() => store.relate({ fromId: decision.id, toId: decision.id }), /itself/);
  assert.throws(() => store.relate({ fromId: decision.id, toId: 'missing' }), /exist/);
});

test('retrieval returns bounded UTF-8 excerpts and on-demand get preserves the entire original', t => {
  const { store } = setup(t);
  const content = 'Revisão segura em São Paulo 🐕. '.repeat(3000);
  const saved = store.remember({ title: 'Large reference', content });
  const result = store.search({ query: 'revisão', maxBytes: 2000, limit: 1 });
  assert.equal(result.results.length, 1);
  assert(result.bytes <= 2000);
  assert.equal(result.bytes, Buffer.byteLength(JSON.stringify(result.results)));
  assert(result.truncated);
  assert(result.results[0].contentTruncated);
  assert(!result.results[0].content.includes('\uFFFD'));
  assert.equal(store.get(saved.id).content, content);
  assert(store.list()[0].contentTruncated);
});

test('learned parameters reject credential keys, concrete secret values and non-JSON objects before persistence', t => {
  const { store } = setup(t);
  for (const parameters of [{ apiKey: 'redacted' }, { headers: { Authorization: 'redacted' } }, { password: 'redacted' },
    { value: 'sk' + '-' + 'x'.repeat(40) }, { value: Infinity }, { value: new Date() }]) {
    assert.throws(() => store.remember({ title: 'Unsafe parameter', kind: 'parameter', parameters }));
  }
  assert.equal(store.stats().total, 0);
  const saved = store.remember({ title: 'Runtime choices', kind: 'parameter', parameters: { maxTokens: 8000, retries: 3, flags: ['safe'], nested: { enabled: true } } });
  assert.equal(store.get(saved.id).parameters.maxTokens, 8000);
});

test('credential material in memory titles, content and tags is rejected without persisting or changing an existing revision', t => {
  const { store } = setup(t);
  const values = [
    'sk' + '-' + 'x'.repeat(24),
    'gh' + 'p_' + 'x'.repeat(24),
    'ey' + 'J' + 'x'.repeat(12) + '.' + 'x'.repeat(12) + '.signature',
    '-----BEGIN ' + 'PRIVATE KEY-----\nfixture\n-----END ' + 'PRIVATE KEY-----',
    'https://' + 'fixture-user:fixture-password@example.invalid',
  ];
  for (const value of values) {
    for (const fields of [{ title: value }, { content: `Unverified material: ${value}` }, { tags: [value] }]) {
      assert.throws(() => store.remember({ title: 'Candidate learning', content: 'A verified non-secret fact.', ...fields }), /Credentials/);
      assert.equal(store.stats().total, 0);
      assert.deepEqual(store.sources(), []);
    }
  }
  const saved = store.remember({ title: 'Credential handling guidance',
    content: 'Do not store API keys or passwords. Keep references to credentials instead.', tags: ['credential-guidance'] });
  assert.throws(() => store.remember({ ...saved, content: values[0] }), /Credentials/);
  assert.equal(store.stats().total, 1);
  assert.equal(store.get(saved.id).content, saved.content);
  assert.equal(store.get(saved.id).supersededBy, null);
  assert.equal(store.search({ query: 'credential guidance' }).results[0].id, saved.id);
});

test('catalog synchronization copies central knowledge idempotently and revisions remain searchable', t => {
  const { store } = setup(t);
  const library = { id: 'global', name: 'Global library', catalogId: 'team', path: 'catalog/global', memories: [
    { name: 'Review', relativePath: 'memory/review.md', content: 'Require a review.', priority: 'High' },
  ] };
  const storage = { list(kind) { assert.equal(kind, 'libraries'); return [library]; } };
  assert.deepEqual(store.syncCatalog(storage), { imported: 1, duplicates: 0, skipped: 0 });
  assert.deepEqual(store.syncCatalog(storage), { imported: 0, duplicates: 1, skipped: 0 });
  library.memories[0].content = 'Require independent review.';
  assert.deepEqual(store.syncCatalog(storage), { imported: 1, duplicates: 0, skipped: 0 });
  assert.equal(store.search({ query: 'independent', catalogId: 'team', libraryId: 'global' }).results.length, 1);
  assert.equal(store.sources()[0].provider, 'catalog');
});

test('catalog text larger than a direct memory is chunked without dropping bytes or provenance', t => {
  const { store } = setup(t);
  const content = 'A memória central preserva o histórico 🐕. '.repeat(8000);
  const input = { title: 'Large knowledge', content, tags: ['High'], source: { provider: 'catalog', path: 'library/memory/large.md' } };
  const nodes = store.importText(input);
  assert(nodes.length > 20);
  assert.equal(nodes.map(node => node.content).join(''), content);
  assert(nodes.every(node => node.source.path === input.source.path && Buffer.byteLength(node.content) <= 16000));
  assert.deepEqual(store.importText(input).map(node => node.id), nodes.map(node => node.id));
});

test('vector storage is model-scoped, excludes superseded nodes and exposes only validated embeddings', t => {
  const { store } = setup(t);
  const input = { title: 'Review', content: 'Review the delivery.', libraryId: 'engineering' };
  const first = store.remember(input);
  assert.deepEqual(store.embeddingStats({ model: 'local-test' }), { indexed: 0, pending: 1, total: 1 });
  assert.equal(store.unembedded({ model: 'local-test' }).length, 1);
  assert.deepEqual(store.setEmbedding(first.id, new Float32Array([0.5, 0.25, 1]), { model: 'local-test' }), { id: first.id, model: 'local-test', dimensions: 3 });
  assert.equal(store.unembedded({ model: 'local-test' }).length, 0);
  assert.deepEqual(store.embeddingStats({ model: 'local-test', libraryId: 'engineering' }), { indexed: 1, pending: 0, total: 1 });
  assert.equal(store.unembedded({ model: 'different-model' }).length, 1);
  assert.deepEqual(store.getEmbeddings({ model: 'local-test', libraryId: 'engineering' })[0].vector, [0.5, 0.25, 1]);
  for (const vector of [[], [NaN], [Infinity], [0, 0], 'not-vector']) assert.throws(() => store.setEmbedding(first.id, vector, { model: 'local-test' }));
  const revision = store.remember({ ...input, content: 'Review independently.' });
  assert.notEqual(revision.id, first.id);
  assert.equal(store.getEmbeddings({ model: 'local-test' }).length, 0);
  assert.deepEqual(store.embeddingStats({ model: 'local-test' }), { indexed: 0, pending: 1, total: 1 });
  assert.equal(store.unembedded({ model: 'local-test' })[0].id, revision.id);
});

test('reverting an origin from A to B to A creates a new active revision without reviving an old superseded node', t => {
  const { store } = setup(t);
  const input = { title: 'Release rule', content: 'Require one review.', source: { provider: 'codex', path: 'memory/release.md' } };
  const original = store.remember(input);
  const changed = store.remember({ ...input, content: 'Require two reviews.' });
  const reverted = store.remember(input);
  assert.notEqual(reverted.id, original.id);
  assert.notEqual(reverted.id, changed.id);
  assert.equal(reverted.content, original.content);
  assert.equal(reverted.supersededBy, null);
  assert.equal(store.get(changed.id).supersededBy, reverted.id);
  assert.equal(store.get(original.id).supersededBy, changed.id);
  assert.equal(store.remember(input).id, reverted.id);
  assert.deepEqual(store.search({ query: 'review' }).results.map(item => item.id), [reverted.id]);
  assert.equal(store.graph().edges.filter(edge => edge.type === 'supersedes').length, 2);
});

test('archive withdraws knowledge from retrieval and vectors while preserving provenance, history and original files', t => {
  const { store } = setup(t);
  const input = { title: 'Outdated learning', content: 'An incorrect release assumption.', source: { provider: 'claude-code', path: 'memory/original.md' } };
  const saved = store.remember(input);
  store.setEmbedding(saved.id, [0.2, 1], { model: 'local-test' });
  const archived = store.archive(saved.id);
  assert(archived.archivedAt);
  assert.equal(archived.content, input.content);
  assert.equal(archived.source.path, input.source.path);
  assert.equal(store.archive(saved.id).archivedAt, archived.archivedAt);
  assert.equal(store.get(saved.id).provenance.length, 1);
  assert.equal(store.search({ query: 'release' }).results.length, 0);
  assert.equal(store.list().length, 0);
  assert.equal(store.getEmbeddings({ model: 'local-test' }).length, 0);
  assert.equal(store.unembedded({ model: 'different-model' }).length, 0);
  assert.deepEqual(store.embeddingStats({ model: 'local-test' }), { indexed: 0, pending: 0, total: 0 });
  assert.equal(store.graph().nodes.length, 0);
  assert.equal(store.graph({ includeHistory: true }).nodes[0].id, saved.id);
  assert.equal(store.remember(input).id, saved.id, 'repeat import does not undo an explicit archive');
  assert.equal(store.list().length, 0);
  assert.equal(store.stats().total, 1);
  assert.throws(() => store.archive('missing'), /not found/);
});
