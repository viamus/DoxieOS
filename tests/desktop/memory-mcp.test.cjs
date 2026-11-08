const assert = require('node:assert/strict');
const { test } = require('node:test');
const { mkdtemp, rm, readFile } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { spawn } = require('node:child_process');
const { createInterface } = require('node:readline');
const toml = require('@iarna/toml');
const { createStorage } = require('../../src/desktop/storage.cjs');
const { createMemoryStore } = require('../../src/desktop/memory-store.cjs');
const { createMemoryProtocol } = require('../../src/desktop/memory-mcp.cjs');
const { nativeMemoryServer, memoryServerId } = require('../../src/desktop/memory-tools.cjs');
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

test('the stdio server exchanges only JSON-RPC and shares persistent memory with the desktop process', { timeout: 10000 }, async t => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-memory-stdio-'));
  const store = createMemoryStore({ root });
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
  t.after(async () => { child.stdin.end(); await ended; store.close(); await rm(root, { recursive: true, force: true }); });
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
});
