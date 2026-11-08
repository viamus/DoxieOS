const assert = require('node:assert/strict');
const { createHash } = require('node:crypto');
const { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { test } = require('node:test');
const { createMemoryStore } = require('../../src/desktop/memory-store.cjs');

function fixture(t) {
  const root = mkdtempSync(join(tmpdir(), 'doxie-brain-store-'));
  const store = createMemoryStore({ root });
  t.after(() => {
    store.close();
    rmSync(root, { recursive: true, force: true });
  });
  return { root, store };
}

test('Brain persists native graph data, typed vectors and provenance in its own file', (t) => {
  const { root, store } = fixture(t);
  const first = store.remember({ title: 'Release review', content: 'Releases require review.' });
  const second = store.remember({
    title: 'Independent approval',
    content: 'Approval follows independent review.',
    origin: 'optimized',
  });
  store.setEmbedding(first.id, [1, 0], { model: 'fixture' });
  store.relate({ fromId: second.id, toId: first.id, type: 'supports', weight: 0.85 });
  assert.equal(store.stats().storage, 'LadybugDB');
  assert.equal(store.stats().vectorSearch, 'exact-cosine');
  assert(existsSync(join(root, '.memory', 'brain.lbdb')));
  assert(!existsSync(join(root, '.memory', 'knowledge.db')));
  store.close();
  const reopened = createMemoryStore({ root });
  try {
    assert.equal(reopened.get(second.id).origin, 'optimized');
    assert.equal(reopened.graph().edges[0].weight, 0.85);
    assert.deepEqual(reopened.getEmbeddings({ model: 'fixture' })[0].vector, [1, 0]);
  } finally {
    reopened.close();
  }
});

test('native cosine retrieval filters lifecycle, dimensions, models and scopes before ranking', (t) => {
  const { store } = fixture(t);
  const make = (title, vector, options = {}) => {
    const saved = store.remember({ title, content: `${title} evidence.`, ...options });
    store.setEmbedding(saved.id, vector, { model: 'fixture' });
    return saved;
  };
  const expected = make('Semantic answer', [0.9, 0.1], { catalogId: 'one' });
  make('Other domain', [1, 0], { catalogId: 'two' });
  make('Different dimensions', [1, 0, 0], { catalogId: 'one' });
  const stale = make('Old answer', [1, 0], { catalogId: 'one', reviewAfter: '2020-01-01T00:00:00Z' });
  const archived = make('Withdrawn answer', [1, 0], { catalogId: 'one' });
  store.archive(archived.id);
  const found = store.nearest([1, 0], { model: 'fixture', catalogId: 'one' });
  assert.deepEqual(
    found.map((item) => item.id),
    [expected.id],
  );
  assert(Math.abs(found[0].similarity - 0.9938837) < 1e-6);
  assert(
    store
      .nearest([1, 0], { model: 'fixture', catalogId: 'one', includeStale: true })
      .some((item) => item.id === stale.id),
  );
  assert.deepEqual(store.nearest([1, 0], { model: 'other' }), []);
  assert.throws(() => store.nearest([0, 0], { model: 'fixture' }), /Invalid query embedding/);
});

test('weighted explicit relations update idempotently and validate repeated writes', (t) => {
  const { store } = fixture(t);
  const first = store.remember({ title: 'One', content: 'First evidence.' });
  const second = store.remember({ title: 'Two', content: 'Second evidence.' });
  const args = { fromId: first.id, toId: second.id, type: 'supports' };
  const initial = store.relate({ ...args, weight: 0.5 });
  const updated = store.relate({ ...args, weight: 0.9 });
  assert.equal(updated.id, initial.id);
  assert.equal(updated.weight, 0.9);
  assert(Math.abs(updated.distance - 0.1) < 1e-12);
  assert.equal(store.relate(args).weight, 0.9);
  for (const weight of [-1, 1.1, Infinity, NaN, '0.5'])
    assert.throws(() => store.relate({ ...args, weight }), /between 0 and 1/);
  assert.equal(store.graph({ minWeight: 0.8 }).edges.length, 1);
  assert.equal(store.graph({ minWeight: 0.95 }).edges.length, 0);
});

test('semantic correlations are weighted, inferred and model scoped; incremental indexing preserves unrelated edges', (t) => {
  const { store } = fixture(t);
  const nodes = store.batch(() =>
    ['Release review', 'Release approval', 'Invoices', 'Payments'].map((title) =>
      store.remember({ title, content: `${title} durable evidence.`, tags: ['fixture'] }),
    ),
  );
  const vectors = [
    [1, 0],
    [0.99, 0.01],
    [0, 1],
    [0.01, 0.99],
  ];
  nodes.forEach((node, index) => store.setEmbedding(node.id, vectors[index], { model: 'fixture' }));
  const explicit = store.relate({ fromId: nodes[0].id, toId: nodes[2].id, type: 'mentions' });
  store.rebuildSemanticRelations({ model: 'fixture', neighbors: 1 });
  const semantic = store.graph({ relationType: 'semantic' }).edges;
  assert.equal(semantic.length, 2);
  assert(semantic.every((edge) => edge.inferred && edge.model === 'fixture' && edge.semanticScore > 0.99));
  assert(semantic.every((edge) => Math.abs(edge.distance - (1 - edge.weight)) < 1e-12));
  const retained = semantic.find((edge) => [edge.fromId, edge.toId].includes(nodes[2].id));
  const added = store.remember({ title: 'Release delivery', content: 'New release evidence.' });
  store.setEmbedding(added.id, [1, 0], { model: 'fixture' });
  store.rebuildSemanticRelations({ model: 'fixture', ids: [added.id], neighbors: 1 });
  const graph = store.graph();
  assert(graph.edges.some((edge) => edge.id === retained.id));
  assert(graph.edges.some((edge) => edge.id === explicit.id));
  assert(graph.edges.some((edge) => edge.inferred && [edge.fromId, edge.toId].includes(added.id)));
  nodes.slice(0, 2).forEach((node, index) => store.setEmbedding(node.id, vectors[index], { model: 'other' }));
  store.rebuildSemanticRelations({ model: 'other', neighbors: 1 });
  assert(store.graph({ relationType: 'semantic' }).edges.some((edge) => edge.model === 'other'));
  assert(store.graph({ relationType: 'semantic' }).edges.some((edge) => edge.model === 'fixture'));
});

test('semantic rebuilding yields between batches, preserves union edges and can stop before shutdown', async (t) => {
  const { store } = fixture(t);
  const saved = store.batch(() =>
    Array.from({ length: 24 }, (_, index) =>
      store.remember({ title: `Release evidence ${index}`, content: `Independent release observation ${index}.` }),
    ),
  );
  saved.forEach((node) => store.setEmbedding(node.id, [1, 0], { model: 'fixture' }));
  let eventLoopRan = false;
  setImmediate(() => {
    eventLoopRan = true;
  });
  const progress = [];
  const completed = await store.rebuildSemanticRelationsAsync({
    model: 'fixture',
    neighbors: 2,
    onProgress: (update) => progress.push(update.completed),
  });
  assert.equal(completed.processed, 24);
  assert(eventLoopRan);
  assert.deepEqual(progress, [10, 20, 24]);
  const graph = store.graph({ maxBytes: 128000 });
  assert(graph.edges.some((edge) => edge.inferred && [edge.fromId, edge.toId].includes(saved[0].id)));
  let keepRunning = true;
  const stopped = await store.rebuildSemanticRelationsAsync({
    model: 'fixture',
    neighbors: 1,
    shouldContinue: () => keepRunning,
    onProgress: () => {
      keepRunning = false;
    },
  });
  assert.equal(stopped.processed, 10);
  assert.equal(stopped.aborted, true);
  assert.equal(store.get(saved[0].id).content, saved[0].content);
});

test('a failed native transaction rolls back persisted knowledge and its in-memory text projection', (t) => {
  const { root, store } = fixture(t);
  assert.throws(
    () =>
      store.batch(() => {
        store.remember({ title: 'Rolled back learning', content: 'This uncommitted fact must disappear.' });
        throw new Error('synthetic import interruption');
      }),
    /synthetic import interruption/,
  );
  assert.equal(store.stats().total, 0);
  assert.equal(store.search({ query: 'uncommitted' }).results.length, 0);
  store.close();
  const reopened = createMemoryStore({ root });
  try {
    assert.equal(reopened.stats().total, 0);
  } finally {
    reopened.close();
  }
});

test('shared imports keep independent revision tips when only one source changes', (t) => {
  const { store } = fixture(t);
  const base = { title: 'Review policy', content: 'Require one reviewer.' };
  const sourceA = { provider: 'codex', path: 'memory/A.md' };
  const sourceB = { provider: 'codex', path: 'memory/B.md' };
  const original = store.remember({ ...base, source: sourceA });
  assert.equal(store.remember({ ...base, source: sourceB }).id, original.id);
  const revisedB = store.remember({ ...base, content: 'Require two reviewers.', source: sourceB });

  const unchangedA = store.remember({ ...base, source: sourceA });
  assert.equal(unchangedA.id, original.id);
  assert.equal(store.get(revisedB.id).supersededBy, null);
  assert.equal(store.stats().total, 2);

  const revisedA = store.remember({ ...base, content: 'Require three reviewers.', source: sourceA });
  assert.notEqual(revisedA.id, revisedB.id);
  assert.equal(store.get(revisedB.id).supersededBy, null);
  assert.equal(store.remember({ ...base, content: revisedB.content, source: sourceB }).id, revisedB.id);
  assert.equal(store.remember({ ...base, content: revisedA.content, source: sourceA }).id, revisedA.id);
  const active = new Set(store.search({ query: 'reviewers' }).results.map((item) => item.id));
  assert.deepEqual(active, new Set([revisedA.id, revisedB.id]));
  const revisions = store
    .graph({ includeHistory: true, maxBytes: 128000 })
    .edges.filter((edge) => edge.type === 'supersedes');
  assert(revisions.some((edge) => edge.fromId === revisedA.id && edge.toId === original.id));
  assert(revisions.some((edge) => edge.fromId === revisedB.id && edge.toId === original.id));
  assert(!revisions.some((edge) => edge.fromId === revisedA.id && edge.toId === revisedB.id));
});

test('revision tips stay source specific when batched imports have identical timestamps', (t) => {
  const { store } = fixture(t);
  const ActualDate = global.Date;
  const timestamp = ActualDate.parse('2026-11-08T12:00:00Z');
  class FixedDate extends ActualDate {
    constructor(...args) {
      super(...(args.length ? args : [timestamp]));
    }
    static now() {
      return timestamp;
    }
  }
  global.Date = FixedDate;
  try {
    const sourceA = { provider: 'codex', path: 'memory/A.md' };
    const sourceB = { provider: 'codex', path: 'memory/B.md' };
    const input = { title: 'Batched policy', content: 'Original rule.' };
    const versions = store.batch(() => {
      const original = store.remember({ ...input, source: sourceA });
      store.remember({ ...input, source: sourceB });
      const nextA = store.remember({ ...input, content: 'Second A rule.', source: sourceA });
      const latestA = store.remember({ ...input, content: 'Third A rule.', source: sourceA });
      const nextB = store.remember({ ...input, content: 'Second B rule.', source: sourceB });
      return { original, nextA, latestA, nextB };
    });
    assert.equal(versions.original.createdAt, versions.latestA.createdAt);
    assert.equal(
      store.remember({ ...input, content: versions.latestA.content, source: sourceA }).id,
      versions.latestA.id,
    );
    assert.equal(store.remember({ ...input, content: versions.nextB.content, source: sourceB }).id, versions.nextB.id);
    assert.equal(store.get(versions.latestA.id).supersededBy, null);
    assert.equal(store.get(versions.nextB.id).supersededBy, null);
    assert.equal(store.stats().total, 4);
  } finally {
    global.Date = ActualDate;
  }
});

test('obsolescence excludes due knowledge without deleting content; review, archive and revisions retain provenance', (t) => {
  const { store } = fixture(t);
  const stale = store.remember({
    title: 'Expiring fact',
    content: 'A fact requiring revalidation.',
    reviewAfter: '2020-01-01T00:00:00Z',
    source: { provider: 'codex', path: 'memory/fixture.md' },
  });
  assert.equal(stale.origin, 'imported');
  assert.equal(stale.lifecycle.status, 'stale');
  assert.equal(store.search({ query: 'fact' }).results.length, 0);
  assert.equal(store.search({ query: 'fact', includeStale: true }).results.length, 1);
  assert.equal(store.stats().stale, 1);
  const reviewed = store.review({
    id: stale.id,
    status: 'current',
    evidence: 'Verified against current test evidence.',
  });
  assert.equal(reviewed.lifecycle.status, 'current');
  assert.equal(reviewed.lifecycle.evidence, 'Verified against current test evidence.');
  assert.equal(store.search({ query: 'fact' }).results[0].id, stale.id);
  const revised = store.remember({
    id: stale.id,
    title: stale.title,
    content: 'A revised verified fact.',
    source: stale.source,
  });
  assert.equal(store.get(stale.id).lifecycle.status, 'superseded');
  assert.throws(() => store.review({ id: stale.id, status: 'current' }), /superseded/);
  store.archive(revised.id);
  assert.equal(store.get(revised.id).lifecycle.status, 'archived');
  assert.equal(store.get(stale.id).content, stale.content);
  assert.equal(store.get(stale.id).source.path, 'memory/fixture.md');
});

test('reconciling a smaller import retires its sole-source chunks while retaining shared knowledge', (t) => {
  const { store } = fixture(t);
  const source = { provider: 'codex', path: 'memory/fixture.md' };
  const retained = store.remember({
    title: 'Retained',
    content: 'Retain this chunk.',
    source: { ...source, chunk: 0 },
  });
  const removed = store.remember({
    title: 'Removed',
    content: 'Withdraw this old chunk.',
    source: { ...source, chunk: 1 },
  });
  const shared = store.remember({
    title: 'Shared',
    content: 'Independent shared evidence.',
    source: { ...source, chunk: 2 },
  });
  store.remember({
    title: shared.title,
    content: shared.content,
    source: { provider: 'claude-code', path: 'memory/shared.md' },
  });
  assert.deepEqual(store.reconcileImport({ ...source, activeIds: [retained.id] }), { archived: 1 });
  assert.equal(store.get(removed.id).status, 'archived');
  assert.equal(store.get(shared.id).status, 'current');
});

test('legacy SQLite migration retains IDs, history, origins and vectors, leaves source bytes unchanged and runs once', (t) => {
  const { DatabaseSync } = require('node:sqlite');
  const root = mkdtempSync(join(tmpdir(), 'doxie-brain-migration-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  mkdirSync(join(root, '.memory'));
  const path = join(root, '.memory', 'knowledge.db');
  const legacy = new DatabaseSync(path);
  const timestamp = new Date().toISOString();
  legacy.exec(`CREATE TABLE memories (id TEXT,kind TEXT,title TEXT,content TEXT,library_id TEXT,catalog_id TEXT,
    tags_json TEXT,parameters_json TEXT,fingerprint TEXT,created_at TEXT,updated_at TEXT,superseded_by TEXT,archived_at TEXT);
    CREATE TABLE memory_origins (id INTEGER,memory_id TEXT,origin_key TEXT,provider TEXT,path TEXT,source_json TEXT,imported_at TEXT);
    CREATE TABLE memory_relations(id TEXT,from_id TEXT,to_id TEXT,type TEXT,created_at TEXT);
    CREATE TABLE memory_embeddings(memory_id TEXT,model TEXT,vector_json TEXT,dimensions INTEGER,created_at TEXT);`);
  const insert = legacy.prepare('INSERT INTO memories VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)');
  insert.run(
    'old',
    'memory',
    'Old rule',
    'One reviewer.',
    null,
    null,
    '[]',
    null,
    'old-fingerprint',
    timestamp,
    timestamp,
    'new',
    null,
  );
  insert.run(
    'new',
    'memory',
    'Current rule',
    'Two reviewers.',
    null,
    null,
    '[]',
    null,
    'new-fingerprint',
    timestamp,
    timestamp,
    null,
    null,
  );
  legacy
    .prepare('INSERT INTO memory_origins VALUES(?,?,?,?,?,?,?)')
    .run(
      1,
      'old',
      'origin',
      'codex',
      'memory/fixture.md',
      JSON.stringify({ provider: 'codex', path: 'memory/fixture.md' }),
      timestamp,
    );
  legacy
    .prepare('INSERT INTO memory_origins VALUES(?,?,?,?,?,?,?)')
    .run(
      2,
      'new',
      'origin',
      'codex',
      'memory/fixture.md',
      JSON.stringify({ provider: 'codex', path: 'memory/fixture.md' }),
      timestamp,
    );
  legacy
    .prepare('INSERT INTO memory_relations VALUES(?,?,?,?,?)')
    .run('revision-edge', 'new', 'old', 'supersedes', timestamp);
  legacy.prepare('INSERT INTO memory_embeddings VALUES(?,?,?,?,?)').run('new', 'fixture', '[1,0]', 2, timestamp);
  legacy.close();
  const digest = () => createHash('sha256').update(readFileSync(path)).digest('hex');
  const before = digest();
  let store = createMemoryStore({ root });
  try {
    assert.equal(store.stats().total, 2);
    assert.equal(store.get('old').supersededBy, 'new');
    assert.equal(store.get('new').source.path, 'memory/fixture.md');
    assert.equal(store.graph().edges[0].id, 'revision-edge');
    assert.deepEqual(store.getEmbeddings({ model: 'fixture' })[0].vector, [1, 0]);
    assert.equal(digest(), before);
  } finally {
    store.close();
  }
  store = createMemoryStore({ root });
  try {
    assert.equal(store.stats().total, 2);
    assert.equal(store.stats().relations, 1);
  } finally {
    store.close();
  }
});
