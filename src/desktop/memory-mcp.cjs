const { createInterface } = require('node:readline');
const { isAbsolute, resolve } = require('node:path');
const { createMemoryStore } = require('./memory-store.cjs');
const { createSemanticMemory } = require('./semantic-memory.cjs');
const { memoryTools, nativeInstructions, callMemoryTool } = require('./memory-tools.cjs');
const { connectMemoryService } = require('./memory-service.cjs');
const { initializeMemoryRuntime } = require('./brain-runtime.cjs');

function createMemoryProtocol(store, { source = {}, invoke = (name, args) => callMemoryTool(store, name, args, source) } = {}) {
  let initialized = false;
  let ready = false;
  return async function handle(message) {
    const id = message?.id;
    const error = (code, text) => ({ jsonrpc: '2.0', id: id ?? null, error: { code, message: text } });
    if (!message || message.jsonrpc !== '2.0' || typeof message.method !== 'string') return error(-32600, 'Invalid request.');
    if (!Object.hasOwn(message, 'id')) {
      if (message.method === 'notifications/initialized' && initialized) ready = true;
      return null;
    }
    if (!['string', 'number'].includes(typeof id)) return error(-32600, 'Invalid request ID.');
    if (message.method === 'initialize') {
      initialized = true;
      const requested = message.params?.protocolVersion;
      const supported = ['2025-11-25', '2025-06-18', '2025-03-26', '2024-11-05'];
      return { jsonrpc: '2.0', id, result: { protocolVersion: supported.includes(requested) ? requested : supported[0],
        capabilities: { tools: { listChanged: false } }, serverInfo: { name: 'doxie-brain', version: '2.1.0' }, instructions: nativeInstructions } };
    }
    if (message.method === 'ping') return { jsonrpc: '2.0', id, result: {} };
    if (!ready) return error(-32002, 'Initialize the memory server first.');
    if (message.method === 'tools/list') return { jsonrpc: '2.0', id, result: { tools: memoryTools } };
    if (message.method !== 'tools/call') return error(-32601, 'Method not found.');
    try {
      const result = await invoke(message.params?.name, message.params?.arguments || {});
      return { jsonrpc: '2.0', id, result: { content: [{ type: 'text', text: JSON.stringify(result) }], structuredContent: result } };
    } catch (failure) {
      return { jsonrpc: '2.0', id, result: { isError: true, content: [{ type: 'text', text: String(failure.message || 'Memory operation failed.').slice(0, 600) }] } };
    }
  };
}

async function serve() {
  const index = process.argv.indexOf('--root');
  const root = process.argv[index + 1];
  if (index < 0 || typeof root !== 'string' || !isAbsolute(root)) throw new Error('An absolute DoxieOS workbench root is required.');
  const source = { runId: process.env.DOXIE_MEMORY_RUN_ID || undefined };
  const service = connectMemoryService({ root: resolve(root), source });
  if (!service) await initializeMemoryRuntime();
  const store = service ? null : createMemoryStore({ root: resolve(root) });
  if (store) store.semantic = createSemanticMemory({ root: resolve(root), store });
  const handle = createMemoryProtocol(store, { source, ...(service ? { invoke: service.call } : {}) });
  const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
  let tail = Promise.resolve();
  input.on('line', line => {
    tail = tail.then(async () => {
      let result;
      if (Buffer.byteLength(line) > 256 * 1024) result = { jsonrpc: '2.0', id: null, error: { code: -32600, message: 'Request exceeds 256 KiB.' } };
      else {
        try { result = await handle(JSON.parse(line)); }
        catch { result = { jsonrpc: '2.0', id: null, error: { code: -32700, message: 'Invalid JSON.' } }; }
      }
      if (result) process.stdout.write(JSON.stringify(result) + '\n');
    }).catch(error => process.stderr.write(`${error.message}\n`));
  });
  input.once('close', () => { void tail.finally(async () => { if (store) { await store.semantic.close(); store.close(); } }); });
}

if (require.main === module) void serve().catch(error => { process.stderr.write(`${error.message}\n`); process.exitCode = 1; });
module.exports = { createMemoryProtocol };
