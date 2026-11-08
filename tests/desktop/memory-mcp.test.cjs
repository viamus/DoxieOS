const assert = require('node:assert/strict');
const { test } = require('node:test');
const { mkdtemp, rm, readFile, mkdir, writeFile } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { spawn } = require('node:child_process');
const { createHash } = require('node:crypto');
const { EventEmitter } = require('node:events');
const { createInterface } = require('node:readline');
const toml = require('@iarna/toml');
const { createStorage } = require('../../src/desktop/storage.cjs');
const { createMemoryStore } = require('../../src/desktop/memory-store.cjs');
const { createMemoryProtocol } = require('../../src/desktop/memory-mcp.cjs');
const { nativeMemoryServer, memoryServerId, callMemoryTool } = require('../../src/desktop/memory-tools.cjs');
const { createMemoryService } = require('../../src/desktop/memory-service.cjs');
const { createSemanticMemory } = require('../../src/desktop/semantic-memory.cjs');
const { createMcp } = require('../../src/desktop/mcp.cjs');

test('native memory protocol negotiates, retrieves, learns separate concepts and relates their evidence', async t => {
  const store = createMemoryStore({ root: tmpdir(), databasePath: ':memory:' });
  t.after(() => store.close());
  const handle = createMemoryProtocol(store, { source: { runId: 'one-run' } });
  const call = (id, method, params) => handle({ jsonrpc: '2.0', id, method, params });
  assert.equal((await call(0, 'tools/list')).error.code, -32002);
  assert.equal((await call(1, 'initialize', { protocolVersion: '2025-11-25' })).result.protocolVersion, '2025-11-25');
  assert.equal(await handle({ jsonrpc: '2.0', method: 'notifications/initialized' }), null);
  const tools = (await call(2, 'tools/list')).result.tools;
  assert(tools.some(tool => tool.name === 'memory_search' && tool.annotations.readOnlyHint));
  const invoke = (name, args) => call(3, 'tools/call', { name, arguments: args });
  const first = (await invoke('memory_remember', { title: 'Release policy', content: 'Releases require review.', evidence: 'Reviewed release checklist.' })).result.structuredContent;
  const second = (await invoke('memory_remember', { title: 'Output format', content: 'Deliver reports in Markdown.' })).result.structuredContent;
  assert.equal(first.source.runId, 'one-run');
  assert.equal(first.source.label, 'Reviewed release checklist.');
  assert.equal(store.stats().memories, 2);
  await invoke('memory_relate', { fromId: first.id, toId: second.id, type: 'supports' });
  assert.equal((await invoke('memory_graph', { limit: 20 })).result.structuredContent.edges.length, 1);
  const search = (await invoke('memory_search', { query: 'release review', maxBytes: 4000 })).result.structuredContent;
  assert(search.results.some(item => item.id === first.id));
  assert.equal((await invoke('memory_get', { id: first.id })).result.structuredContent.content, first.content);
  for (const [name, args] of [['memory_search', {}], ['memory_get', { id: first.id, maxBytes: -5 }], ['memory_remember', { title: 'Bad', content: 'Bad', arbitrary: true }]]) {
    assert.equal((await invoke(name, args)).result.isError, true);
  }
  assert.equal((await call(4, 'missing/method')).error.code, -32601);
});

test('the native memory server is retained in managed profiles without changing external provider configuration', async t => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-native-memory-profile-'));
  const storage = createStorage({ root, databasePath: ':memory:' });
  t.after(async () => { storage.close(); await rm(root, { recursive: true, force: true }); });
  const native = nativeMemoryServer(root);
  const mcp = createMcp(storage, { nativeServers: [native] });
  mcp.refresh();
  assert.equal(mcp.publicList().find(server => server.id === memoryServerId).builtin, true);
  mcp.save([{ id: memoryServerId, command: 'not-the-native-server', enabled: false }]);
  const codex = toml.parse(await readFile(join(root, '.providers', 'codex', 'config.toml'), 'utf8')).mcp_servers;
  const claude = JSON.parse(await readFile(join(root, '.mcp.json'), 'utf8')).mcpServers;
  assert.equal(codex[memoryServerId].command, process.execPath);
  assert.equal(claude[memoryServerId].env.ELECTRON_RUN_AS_NODE, '1');
  assert.equal(JSON.parse(storage.getSetting('provider_mcp_servers')).length, 0);
});

test('Brain replaces a disabled reserved Codex URL entry without losing unrelated managed profile settings', async t => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-reserved-brain-profile-'));
  const storage = createStorage({ root, databasePath: ':memory:' });
  t.after(async () => { storage.close(); await rm(root, { recursive: true, force: true }); });
  const file = join(root, '.providers', 'codex', 'config.toml');
  await mkdir(join(root, '.providers', 'codex'), { recursive: true });
  await writeFile(file, toml.stringify({ model: 'saved-model', features: { fixture: true }, mcp_servers: {
    [memoryServerId]: { url: 'https://old.example.test/brain', enabled: false, http_headers: { 'X-Fixture': 'old-header' } },
    reference: { command: 'fixture-reference', args: ['retained'] },
  } }));
  const mcp = createMcp(storage, { nativeServers: [nativeMemoryServer(root)] });
  mcp.refresh();
  const config = toml.parse(await readFile(file, 'utf8'));
  assert.equal(config.model, 'saved-model');
  assert.deepEqual(config.features, { fixture: true });
  assert.deepEqual(config.mcp_servers.reference, { command: 'fixture-reference', args: ['retained'] });
  assert.equal(config.mcp_servers[memoryServerId].command, process.execPath);
  assert.equal(config.mcp_servers[memoryServerId].enabled, true);
  assert.equal(config.mcp_servers[memoryServerId].url, undefined);
  assert.equal(config.mcp_servers[memoryServerId].http_headers, undefined);
  const first = await readFile(file, 'utf8');
  mcp.refresh();
  assert.equal(await readFile(file, 'utf8'), first, 'repeated refresh must preserve the completed repair');
});

test('the stdio server proxies shared persistent memory and semantic inference through its desktop owner', { timeout: 30000 }, async t => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-memory-stdio-'));
  const store = createMemoryStore({ root });
  const weights = Buffer.from('local verified embedding fixture');
  class EmbeddingWorker extends EventEmitter {
    postMessage(message) { queueMicrotask(() => this.emit('message', { type: 'result', id: message.id, vectors: message.texts.map(() => [1, 0, 0]) })); }
    async terminate() { this.emit('exit', 0); return 0; }
  }
  store.semantic = createSemanticMemory({ root, store,
    model: { id: 'stdio-fixture', name: 'Fixture model', repository: 'fixture/model', revision: 'pinned', dimensions: 3,
      files: [{ path: 'config.json', bytes: weights.length, sha256: createHash('sha256').update(weights).digest('hex') }] },
    request: async () => new Response(weights), createWorker: () => new EmbeddingWorker(), timeoutMs: 5000,
  });
  const service = await createMemoryService({ root, store });
  const child = spawn(process.execPath, [join(__dirname, '../../src/desktop/memory-mcp.cjs'), '--root', root], {
    windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'], env: { ...process.env, NODE_NO_WARNINGS: '1' },
  });
  const responses = new Map();
  const pending = new Map();
  let stderr = '';
  child.stderr.on('data', chunk => { stderr += chunk; });
  createInterface({ input: child.stdout }).on('line', line => {
    const message = JSON.parse(line);
    const resolve = pending.get(message.id);
    if (resolve) { pending.delete(message.id); resolve(message); } else responses.set(message.id, message);
  });
  const ended = new Promise(resolve => child.once('close', resolve));
  t.after(async () => { child.stdin.end(); await ended; await service.close(); await store.semantic.close(); store.close(); await rm(root, { recursive: true, force: true }); });
  function call(id, method, params) {
    const promise = new Promise(resolve => pending.set(id, resolve));
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
    return promise;
  }
  await call(1, 'initialize', { protocolVersion: '2025-06-18' });
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
  const result = await call(2, 'tools/call', { name: 'memory_remember', arguments: { title: 'Shared knowledge', content: 'Knowledge is independent of workspaces.' } });
  assert.equal(result.result.isError, undefined, stderr);
  assert.equal(store.search({ query: 'independent workspaces' }).results.length, 1);
  const fromDesktop = store.remember({ title: 'Desktop fact', content: 'Native graph tools share the library.' });
  const read = await call(3, 'tools/call', { name: 'memory_get', arguments: { id: fromDesktop.id } });
  assert.equal(read.result.structuredContent.content, fromDesktop.content);
  await store.semantic.install();
  const semantic = await call(4, 'tools/call', { name: 'memory_search', arguments: { query: 'neural association' } });
  assert.equal(semantic.result.isError, undefined, stderr);
  assert.equal(semantic.result.structuredContent.mode, 'hybrid');
  assert(semantic.result.structuredContent.results.some(item => item.id === fromDesktop.id));
  assert.equal(stderr, '', 'proxy clients must not open a competing database handle');
});

test('Brain tools validate semantic weights and lifecycle updates and preserve returned relationship metadata', async () => {
  const calls = [];
  const node = { id: 'reviewed', title: 'Verified release rule', kind: 'memory', content: 'Review before release.',
    origin: 'optimized', status: 'stale', reviewAfter: '2026-11-01T00:00:00Z', lifecycle: { status: 'stale', confidence: 0.8 } };
  const edge = { id: 'edge', fromId: 'one', toId: 'two', type: 'semantic', weight: 0.86, semanticScore: 0.9,
    correlationScore: 0.6, distance: 0.14, inferred: true, model: 'local-fixture' };
  const store = {
    relate(args) { calls.push(args); return args; },
    review(args) { calls.push(args); return { ...node, status: args.status }; },
    graph(args) { calls.push(args); return { nodes: [{ ...node, id: 'one' }, { ...node, id: 'two' }], edges: [edge] }; },
  };
  assert.equal((await callMemoryTool(store, 'memory_relate', { fromId: 'one', toId: 'two', type: 'supports', weight: 0.75 })).weight, 0.75);
  for (const weight of [-1, 1.1, NaN, '0.5']) await assert.rejects(callMemoryTool(store, 'memory_relate', { fromId: 'one', toId: 'two', type: 'related', weight }), /Invalid weight/);
  await assert.rejects(callMemoryTool(store, 'memory_review', { id: 'reviewed', status: 'current' }), /evidence is required/);
  await assert.rejects(callMemoryTool(store, 'memory_review', { id: 'reviewed', status: 'current', evidence: 'Checked source', reviewAfter: 'not-a-date' }), /Invalid reviewAfter/);
  const result = await callMemoryTool(store, 'memory_review', { id: 'reviewed', status: 'current', evidence: 'Checked source', reviewAfter: '2027-01-01T00:00:00Z' });
  assert.equal(result.origin, 'optimized');
  assert.equal(result.status, 'current');
  const graph = await callMemoryTool(store, 'memory_graph', { nodeId: 'one', minWeight: 0.5, relationType: 'semantic', includeStale: true });
  assert.equal(graph.edges[0].weight, edge.weight);
  assert.equal(graph.edges[0].semanticScore, edge.semanticScore);
  assert.equal(graph.edges[0].correlationScore, edge.correlationScore);
  assert.equal(graph.edges[0].distance, edge.distance);
  assert.equal(graph.edges[0].inferred, true);
  assert.deepEqual(calls.at(-1), { nodeId: 'one', minWeight: 0.5, relationType: 'semantic', includeStale: true });
});

for (const runtime of ['node-wasm', 'electron-wasm']) {
  test(`standalone ${runtime} MCP flushes replies and exits when its client closes stdin`, { timeout: 10000 }, async t => {
    const root = await mkdtemp(join(tmpdir(), 'doxie-memory-standalone-'));
    const executable = runtime === 'electron-wasm' ? require('electron') : process.execPath;
    const child = spawn(executable, [join(__dirname, '../../src/desktop/memory-mcp.cjs'), '--root', root], {
      windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'], env: { ...process.env,
        NODE_NO_WARNINGS: '1', DOXIE_BRAIN_RUNTIME: 'wasm', ...(runtime === 'electron-wasm' ? { ELECTRON_RUN_AS_NODE: '1' } : {}) },
    });
    let output = '';
    let errors = '';
    let timer;
    child.stdout.on('data', chunk => { output += chunk; });
    child.stderr.on('data', chunk => { errors += chunk; });
    const ended = new Promise((resolve, reject) => {
      timer = setTimeout(() => { child.kill(); reject(new Error(`Standalone Brain did not exit: ${errors}`)); }, 5000);
      child.once('close', code => { clearTimeout(timer); resolve(code); });
      child.once('error', reject);
    });
    t.after(async () => { clearTimeout(timer); if (child.exitCode === null) child.kill(); await ended.catch(() => {}); await rm(root, { recursive: true, force: true }); });
    child.stdin.end([
      { jsonrpc: '2.0', id: 1, method: 'initialize', params: {} },
      { jsonrpc: '2.0', method: 'notifications/initialized' },
      { jsonrpc: '2.0', id: 2, method: 'tools/call', params: { name: 'memory_remember', arguments: { title: 'Standalone learning', content: 'Retain verified evidence.' } } },
    ].map(message => JSON.stringify(message)).join('\n') + '\n');
    assert.equal(await ended, 0, errors);
    const replies = output.trim().split('\n').map(line => JSON.parse(line));
    assert.equal(replies.length, 2);
    assert.equal(replies[1].result.isError, undefined, errors);
    assert.equal(replies[1].result.structuredContent.content, 'Retain verified evidence.');
    const reopened = createMemoryStore({ root });
    try { assert.equal(reopened.search({ query: 'verified evidence' }).results.length, 1); }
    finally { reopened.close(); }
  });
}
