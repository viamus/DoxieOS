const assert = require('node:assert/strict');
const { test } = require('node:test');
const { mkdtemp, rm } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const { join, resolve } = require('node:path');
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');
const { createMemoryStore } = require('../../src/desktop/memory-store.cjs');

const execute = promisify(execFile);

test('official WASM and native Brain engines share persistent revisions, provenance, vectors and rollback behavior', { timeout: 30000 }, async t => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-brain-runtime-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  let store = createMemoryStore({ root });
  const original = store.remember({ title: 'Release review', content: 'Review releases before publication.', source: { provider: 'codex', path: 'memory/review.md' } });
  store.setEmbedding(original.id, [1, 0, 0], { model: 'runtime-fixture' });
  store.close();
  const native = require(`@ladybugdb/core-${process.platform}-${process.arch}`);
  const script = String.raw`
    const assert = require('node:assert/strict');
    const { initializeMemoryRuntime } = require('./src/desktop/brain-runtime.cjs');
    const { createMemoryStore } = require('./src/desktop/memory-store.cjs');
    (async () => {
      await Promise.all([initializeMemoryRuntime(), initializeMemoryRuntime()]);
      const wasm = require('@ladybugdb/wasm-core/nodejs/sync');
      assert.equal(String(wasm.getStorageVersion()), process.env.EXPECT_STORAGE_VERSION);
      assert.equal(wasm.getVersion(), process.env.EXPECT_ENGINE_VERSION);
      let store = createMemoryStore({root:process.env.BRAIN_FIXTURE_ROOT});
      const original = store.get(process.env.BRAIN_FIXTURE_ID);
      assert.equal(original.source.path, 'memory/review.md');
      assert.equal(store.nearest([1,0,0],{model:'runtime-fixture'})[0].id,original.id);
      const revised = store.remember({id:original.id,title:'Release review',content:'Review releases and retain approval evidence.',origin:'optimized',source:{provider:'doxie-agent',runId:'wasm-runtime',label:'Current release checklist checked'}});
      assert.equal(store.get(original.id).supersededBy,revised.id);
      assert.equal(revised.origin,'optimized');
      store.setEmbedding(revised.id,[1,0,0],{model:'runtime-fixture'});
      const other=store.remember({title:'Approval evidence',content:'Retain the release review evidence.'});
      store.setEmbedding(other.id,[0.9,0.1,0],{model:'runtime-fixture'});
      await store.rebuildSemanticRelations({model:'runtime-fixture'});
      assert(store.graph({nodeId:revised.id,relationType:'semantic'}).edges.some(edge=>edge.inferred && edge.weight>0.5));
      store.review({id:revised.id,status:'stale',evidence:'The policy needs revalidation.'});
      assert(!store.search({query:'release'}).results.some(hit=>hit.id===revised.id));
      assert(store.search({query:'release',includeStale:true}).results.some(hit=>hit.id===revised.id));
      store.review({id:revised.id,status:'current',evidence:'Revalidated against the current source.'});
      assert.throws(()=>store.batch(()=>{store.remember({title:'Rollback marker',content:'This insertion must roll back.'});throw Error('rollback fixture');}),/rollback fixture/);
      assert.equal(store.search({query:'Rollback marker'}).results.length,0);
      store.close();
      store=createMemoryStore({root:process.env.BRAIN_FIXTURE_ROOT});
      assert.equal(store.get(revised.id).lifecycle.status,'current');
      assert.equal(store.get(revised.id).source.runId,'wasm-runtime');
      store.close();
      process.stdout.write(JSON.stringify({id:revised.id,version:wasm.getVersion()}));
    })().catch(error=>{process.stderr.write(error.stack);process.exit(1)});
  `;
  const { stdout } = await execute(process.execPath, ['-e', script], { cwd: resolve(__dirname, '../..'),
    env: { ...process.env, DOXIE_BRAIN_RUNTIME: 'wasm', BRAIN_FIXTURE_ROOT: root, BRAIN_FIXTURE_ID: original.id,
      EXPECT_STORAGE_VERSION: String(native.STORAGE_VERSION), EXPECT_ENGINE_VERSION: native.VERSION },
    timeout: 25000, windowsHide: true,
  });
  const revised = JSON.parse(stdout);
  store = createMemoryStore({ root });
  try {
    assert.equal(store.get(original.id).supersededBy, revised.id);
    assert.equal(store.get(revised.id).origin, 'optimized');
    assert.equal(store.nearest([1, 0, 0], { model: 'runtime-fixture' })[0].id, revised.id);
    assert(store.search({ query: 'approval' }).results.some(hit => hit.id === revised.id));
  } finally { store.close(); }
});
