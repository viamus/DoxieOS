const assert = require('node:assert/strict');
const { test } = require('node:test');
const { mkdtempSync, rmSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { boundedMemoryNode, boundedMemoryGraph } = require('../../src/desktop/memory-view.cjs');
const { createMemoryStore } = require('../../src/desktop/memory-store.cjs');

const size = value => Buffer.byteLength(JSON.stringify(value), 'utf8');
function largeNode(id = 'memory-1') {
  const parameters = Object.fromEntries(Array.from({ length: 100 }, (_, index) => [`項目_${index}_🐕`, { label: `Valor ${index}`, choices: ['São Paulo', '東京', '🐕'.repeat(8)] }]));
  assert(size(parameters) < 16384);
  const provenance = Array.from({ length: 100 }, (_, index) => ({ provider: 'fixture', path: `/notes/${index}/${'資料'.repeat(300)}`,
    label: `Registro ${index} ${'🐕'.repeat(100)}`, importedAt: '2026-11-08T00:00:00Z' }));
  return { id, title: 'Parâmetros de revisão 東京 🐕', kind: 'parameter',
    content: 'Contexto de revisão em São Paulo 🐕 指針.\n'.repeat(3000),
    parameters, tags: Array.from({ length: 40 }, (_, index) => `etiqueta-${index}-${'🐕'.repeat(20)}`),
    source: provenance[0], provenance, libraryId: 'engineering', catalogId: 'global',
    createdAt: '2026-11-08T00:00:00Z', updatedAt: '2026-11-08T00:00:00Z' };
}

test('whole memory DTOs honor 1 KiB and 4 KiB budgets with Unicode parameters and 100 provenance entries', () => {
  const original = largeNode();
  const snapshot = JSON.stringify(original);
  for (const maximum of [1024, 4096]) {
    const projected = boundedMemoryNode(original, maximum);
    assert(size(projected) <= maximum);
    assert.deepEqual(JSON.parse(JSON.stringify(projected)), projected);
    assert.equal(projected.id, original.id);
    assert.equal(projected.title, original.title);
    assert.equal(projected.kind, original.kind);
    assert.equal(projected.totalContentBytes, Buffer.byteLength(original.content));
    assert(projected.contentTruncated);
    assert(projected.parametersTruncated);
    assert(projected.provenanceTruncated);
    assert.equal(projected.totalParameterKeys, 100);
    assert.equal(projected.totalProvenance, 100);
    assert(Array.isArray(projected.availableParameterKeys));
    if (projected.availableParameterKeys.length < 100) assert(projected.availableParameterKeysTruncated);
    for (const [key, value] of Object.entries(projected.parameters || {})) assert.deepEqual(value, original.parameters[key], 'selected parameter values remain complete JSON');
    assert(!JSON.stringify(projected).includes('\uFFFD'));
  }
  assert.equal(JSON.stringify(original), snapshot, 'projection never modifies the full stored node');
});

test('a small memory retains complete parameter values when the total DTO fits', () => {
  const original = { id: 'small', kind: 'parameter', title: 'Review settings', content: 'Current settings.',
    parameters: { enabled: true, attempts: 3, region: 'São Paulo' }, tags: ['review'], source: { provider: 'fixture' }, provenance: [{ provider: 'fixture' }] };
  const projected = boundedMemoryNode(original, 4096);
  assert.deepEqual(projected.parameters, original.parameters);
  assert.equal(projected.parametersTruncated, false);
  assert.equal(projected.contentTruncated, false);
  assert.equal(projected.sourceTruncated, false);
  assert.equal(projected.provenanceTruncated, false);
  assert.deepEqual(projected.availableParameterKeys, Object.keys(original.parameters));
});

test('long Unicode titles take priority over optional metadata in a 1 KiB projection', () => {
  const original = { ...largeNode(), title: '指'.repeat(240) };
  const projected = boundedMemoryNode(original, 1024);
  assert.equal(projected.title, original.title);
  assert.equal(projected.id, original.id);
  assert(size(projected) <= 1024);
  assert(projected.parametersTruncated);
});

test('graph projections stay below 16 KiB without embedding heavy parameters or dangling edges', () => {
  const nodes = Array.from({ length: 40 }, (_, index) => ({ ...largeNode(`memory-${index}`), label: `Learning ${index}`, summary: 'Resumo da revisão 🐕.' }));
  const edges = nodes.slice(1).map((node, index) => ({ id: `edge-${index}`, from: nodes[index].id, to: node.id, type: 'supports', label: 'Apoia a decisão.' }));
  const graph = boundedMemoryGraph({ nodes, edges, truncated: false }, 16000);
  assert(size(graph) <= 16000);
  assert(graph.truncated);
  assert(graph.nodes.length > 0);
  assert(graph.nodes.length < nodes.length);
  assert.equal(graph.totalNodes, 40);
  const ids = new Set(graph.nodes.map(node => node.id));
  for (const node of graph.nodes) {
    assert.equal(node.parameters, null);
    assert(node.parametersTruncated);
    assert(node.label.startsWith('Learning'));
    assert(node.summary);
  }
  for (const edge of graph.edges) {
    assert(ids.has(edge.from) && ids.has(edge.to));
    assert.equal(edge.type, 'supports');
    assert.equal(edge.label, 'Apoia a decisão.');
  }
});

test('large parameter/provenance hits remain searchable while full get stays intact', t => {
  const root = mkdtempSync(join(tmpdir(), 'doxie-memory-view-'));
  const store = createMemoryStore({ root, databasePath: ':memory:' });
  t.after(() => { store.close(); rmSync(root, { recursive: true, force: true }); });
  const input = largeNode();
  const base = { title: input.title, kind: input.kind, content: input.content, parameters: input.parameters, tags: input.tags };
  const first = store.remember({ ...base, source: input.provenance[0] });
  for (const source of input.provenance.slice(1)) assert.equal(store.remember({ ...base, source }).id, first.id);
  for (const maximum of [1024, 4096]) {
    const result = store.search({ query: 'revisão', maxBytes: maximum, limit: 1 });
    assert.equal(result.results.length, 1, 'metadata size cannot hide a matching memory');
    assert.equal(result.results[0].id, first.id);
    assert(result.bytes <= maximum);
    assert(size(result) <= maximum, 'the search wrapper also fits the requested total budget');
    assert.equal(result.bytes, size(result.results));
    assert(result.results[0].parametersTruncated);
  }
  assert(size(store.list()[0]) <= 4000);
  assert(size(store.graph()) <= 16000);
  const full = store.get(first.id);
  assert.deepEqual(full.parameters, input.parameters);
  assert.equal(full.content, input.content);
  assert.equal(full.provenance.length, 100);
});
