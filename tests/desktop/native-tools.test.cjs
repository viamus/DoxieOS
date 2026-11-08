const assert = require('node:assert/strict');
const { mkdir, mkdtemp, readFile, rm, stat, symlink, writeFile } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { test } = require('node:test');
const { createStorage } = require('../../src/desktop/storage.cjs');
const { createWorkflows } = require('../../src/desktop/workflows.cjs');
const { runNativeUtility } = require('../../src/desktop/native-tools.cjs');
const { createMemoryStore } = require('../../src/desktop/memory-store.cjs');

test('native workspace memory loads explicit Markdown without persisting raw knowledge and sandbox expires without a provider', async () => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-tools-'));
  const storage = createStorage({ root, databasePath: ':memory:' });
  const memory = createMemoryStore({ root, databasePath: ':memory:' });
  storage.memory = memory;
  const engine = createWorkflows({ storage, agents: { start() { assert.fail('A native tool invoked a provider'); } } });
  try {
    const workspace = storage.save('workspaces', { id: 'source', name: 'Source' });
    await mkdir(join(workspace.path, 'notes'));
    const content = Buffer.from('Exact Markdown bytes.\r\n\r\n    Preserve indentation.\n');
    await writeFile(join(workspace.path, 'notes', 'source.md'), content);
    storage.save('workflows', { id: 'native-tools', name: 'Native tools', workspaceId: workspace.id,
      nodes: [
        { id: 'allocate', kind: 'Tool', toolId: 'sandbox', toolMode: 'new', inputs: { tag: 'native' } },
        { id: 'capture', kind: 'Tool', toolId: 'workspace-memory', toolMode: 'load', inputs: { paths: 'notes/source.md' } },
      ],
      edges: [{ fromNodeId: 'allocate', toNodeId: 'capture' }],
    });
    const result = await engine.wait(engine.run('native-tools').id);
    assert.equal(result.status, 'Succeeded', JSON.stringify(result.nodeRuns));
    assert.deepEqual(await readFile(join(workspace.path, 'notes', 'source.md')), content);
    const loaded = JSON.parse(await readFile(join(storage.roots.runs, result.id, 'capture', 'workspace-memory.json'), 'utf8'));
    assert.equal(loaded.sources[0].path, 'notes/source.md');
    assert.equal(loaded.sources[0].content, content.toString('utf8'));
    assert.equal(loaded.sources[0].loadedBytes, content.length);
    assert.equal(memory.stats().total, 0, 'a loaded manual document is not a learned central memory');
    assert.equal(storage.list('libraries').length, 0);
    const receipt = JSON.parse(await readFile(join(storage.roots.runs, result.id, 'allocate', 'sandbox.json'), 'utf8'));
    assert.equal(receipt.expires, 'workflow-end');
    await assert.rejects(stat(receipt.directory), { code: 'ENOENT' });
  } finally {
    await engine.close();
    memory.close();
    storage.close();
    await rm(root, { recursive: true, force: true });
  }
});

test('native remember, query and relate share the global memory API without workspace bindings', async t => {
  const outputDir = await mkdtemp(join(tmpdir(), 'doxie-global-native-'));
  t.after(() => rm(outputDir, { recursive: true, force: true }));
  const calls = [];
  const storage = { get() { assert.fail('Global memory must not resolve a workspace'); }, memory: {
    remember(data) { calls.push({ action: 'remember', data }); return { id: 'knowledge-1', ...data }; },
    search(data) { calls.push({ action: 'query', data }); return { query: data.query, results: [{ id: 'knowledge-1', content: 'Release target: stable.' }], truncated: false, bytes: 23 }; },
    relate(data) { calls.push({ action: 'relate', data }); return { id: 'edge-1', ...data }; },
  } };
  const remembered = await runNativeUtility('library', { storage, outputDir, toolMode: 'remember',
    inputs: { title: 'Release target', content: '  Release target: stable.\n', kind: 'parameter', parameters_json: '{"release":"stable"}', tags: 'release,review', source: 'Operator decision' },
    provenance: { kind: 'workflow', runId: 'run-1', label: 'Remember release' },
  });
  assert.equal(remembered.contextJson.result.id, 'knowledge-1');
  assert.equal(calls[0].data.content, '  Release target: stable.\n');
  assert.deepEqual(calls[0].data.parameters, { release: 'stable' });
  assert.deepEqual(calls[0].data.tags, ['release', 'review']);
  assert.equal(calls[0].data.source.runId, 'run-1');
  assert.equal(calls[0].data.source.section, 'Release target');
  const queried = await runNativeUtility('library', { storage, outputDir, toolMode: 'search', inputs: { query: 'release target', limit: '2', maxBytes: '1200' } });
  assert.equal(queried.contextJson.action, 'query');
  assert.equal(calls[1].data.limit, 2);
  assert.equal(calls[1].data.maxBytes, 1200);
  const related = await runNativeUtility('library', { storage, outputDir, toolMode: 'relate', inputs: { fromId: 'knowledge-1', toId: 'knowledge-2', type: 'supports' } });
  assert.deepEqual(calls[2].data, { fromId: 'knowledge-1', toId: 'knowledge-2', type: 'supports' });
  assert.equal(related.contextJson.result.type, 'supports');
  assert.deepEqual(JSON.parse(await readFile(join(outputDir, 'library.json'), 'utf8')), related.contextJson);
  await assert.rejects(runNativeUtility('library', { storage, outputDir, toolMode: 'remember', inputs: { title: 'Invalid', content: 'content', parameters_json: '[]' } }), /JSON object/);
  assert.throws(() => runNativeUtility('library', { storage, outputDir, toolMode: 'mount', inputs: {} }), /mount has been retired/);
  for (const mode of ['from-current', 'from-files', 'create', 'interactive']) assert.throws(() => runNativeUtility('library', { storage, outputDir, toolMode: mode, inputs: {} }), /retired.*Workspace Memory/);
});

test('workspace memory list and load preserve UTF-8 sources, honor the full JSON budget and never call the central store', async t => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-workspace-memory-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const workspace = { id: 'manual', path: join(root, 'workspace') };
  const outputDir = join(root, 'output');
  await mkdir(join(workspace.path, 'notes'), { recursive: true });
  await mkdir(outputDir);
  const content = '\uFEFF# Contexto manual\r\n  Revisão 🐕.\n' + '\u0001\\" texto 🐕\n'.repeat(5000);
  await writeFile(join(workspace.path, 'notes', 'manual.md'), content, 'utf8');
  await writeFile(join(workspace.path, 'notes', 'ignored.txt'), 'Not a Markdown source.');
  const storage = { memory: { remember() { assert.fail('Raw workspace files must not be persisted'); }, importText() { assert.fail('Raw workspace files must not be imported'); } } };
  const listed = await runNativeUtility('workspace-memory', { storage, workspace, outputDir, toolMode: 'list', inputs: { paths: ['notes'] } });
  assert.equal(listed.contextJson.sources.length, 1);
  assert.equal(listed.contextJson.sources[0].content, undefined);
  assert.equal(listed.contextJson.bytesLoaded, 0);
  for (const maxBytes of [1024, 16384, 65536]) {
    const loaded = await runNativeUtility('workspace-memory', { storage, workspace, outputDir, toolMode: 'load', inputs: { paths: 'notes/manual.md', maxBytes } });
    assert(Buffer.byteLength(JSON.stringify(loaded.contextJson)) <= maxBytes);
    assert(loaded.contextJson.sources[0].loadedBytes > 0);
    assert(content.startsWith(loaded.contextJson.sources[0].content));
    assert.equal(loaded.contextJson.sources[0].loadedBytes, Buffer.byteLength(loaded.contextJson.sources[0].content));
    assert(loaded.contextJson.truncated);
    assert(!loaded.contextJson.sources[0].content.includes('\uFFFD'));
  }
  assert.equal(await readFile(join(workspace.path, 'notes', 'manual.md'), 'utf8'), content);
});

test('workspace memory rejects missing selection, parent traversal, absolute paths, symbolic links and non-Markdown sources', async t => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-workspace-paths-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const workspace = { id: 'manual', path: join(root, 'workspace') };
  const outputDir = join(root, 'output');
  await mkdir(workspace.path);
  await mkdir(outputDir);
  await writeFile(join(workspace.path, 'manual.md'), 'Manual source.');
  await writeFile(join(workspace.path, 'data.txt'), 'Other input.');
  const outside = join(root, 'outside');
  await mkdir(outside);
  await writeFile(join(outside, 'private.md'), 'Private source.');
  await symlink(outside, join(workspace.path, 'linked'), process.platform === 'win32' ? 'junction' : 'dir');
  const run = (inputs, selected = workspace) => runNativeUtility('workspace-memory', { workspace: selected, outputDir, toolMode: 'load', inputs });
  await assert.rejects(run({ paths: 'manual.md' }, null), /Select a workspace/);
  for (const path of ['../outside/private.md', 'notes/../manual.md', join(outside, 'private.md'), 'C:\\outside\\private.md']) await assert.rejects(run({ paths: path }), /relative/);
  await assert.rejects(run({ paths: 'linked/private.md' }), /symbolic/i);
  await assert.rejects(run({ paths: '.' }), /symbolic/i);
  await assert.rejects(run({ paths: '' }), /explicit/);
  await assert.rejects(run({ paths: 'data.txt' }), /Markdown/);
  await assert.rejects(run({ paths: 'manual.md', maxBytes: 65537 }), /budget/);
});
