const assert = require('node:assert/strict');
const { test } = require('node:test');
const { mkdtemp, readFile, rm, stat, writeFile } = require('node:fs/promises');
const { request } = require('node:http');
const { join } = require('node:path');
const { tmpdir } = require('node:os');
const { createMemoryService, connectMemoryService } = require('../../src/desktop/memory-service.cjs');

function send(port, body, headers = {}) {
  return new Promise((resolve, reject) => {
    const input = JSON.stringify(body);
    const req = request({ host: '127.0.0.1', port, path: '/tools', method: 'POST', headers: {
      'content-type': 'application/json', 'content-length': Buffer.byteLength(input), ...headers,
    } }, response => {
      let text = '';
      response.on('data', chunk => { text += chunk; });
      response.on('end', () => resolve({ status: response.statusCode, body: JSON.parse(text) }));
    });
    req.once('error', reject);
    req.end(input);
  });
}

test('the desktop Brain bridge authenticates local tools, rejects browser origins and removes endpoint credentials on close', async t => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-brain-service-'));
  let closed = false;
  const calls = [];
  const store = {
    search(args) { calls.push(args); return { query: args.query, results: [{ id: 'brain-1', title: 'Verified fact' }] }; },
    remember(args) { calls.push(args); return { id: 'brain-2', title: args.title, content: args.content, source: args.source }; },
  };
  const service = await createMemoryService({ root, store });
  t.after(async () => { if (!closed) await service.close(); await rm(root, { recursive: true, force: true }); });
  const file = join(root, '.memory', 'service.json');
  const descriptor = JSON.parse(await readFile(file, 'utf8'));
  assert.equal(descriptor.pid, process.pid);
  await assert.rejects(createMemoryService({ root, store }), /already owns Brain/);
  if (process.platform !== 'win32') assert.equal((await stat(file)).mode & 0o777, 0o600);
  const body = { name: 'memory_search', args: { query: 'release' } };
  assert.equal((await send(service.port, body)).status, 403);
  const authorization = `Bearer ${descriptor.token}`;
  assert.equal((await send(service.port, body, { authorization, origin: 'http://example.test' })).status, 403);
  assert.equal((await send(service.port, body, { authorization, host: `example.test:${service.port}` })).status, 403);
  assert.equal((await send(service.port, { name: 'arbitrary_read', args: { path: 'C:/secret' } }, { authorization })).status, 400);
  assert.equal((await send(service.port, { ...body, root: 'C:/outside' }, { authorization })).status, 400);
  assert.equal((await send(service.port, { ...body, args: { query: 'release', path: 'C:/outside' } }, { authorization })).status, 400);
  assert.equal((await send(service.port, { ...body, args: { query: 'x'.repeat(256 * 1024) } }, { authorization })).status, 413);
  assert.equal(calls.length, 0);
  const client = connectMemoryService({ root, source: { runId: 'run-fixture' } });
  const result = await client.call('memory_search', { query: 'release' });
  assert.equal(result.results[0].id, 'brain-1');
  const learning = await client.call('memory_remember', { title: 'Verified release rule', content: 'Review releases.', evidence: 'Current source checked' });
  assert.equal(learning.source.runId, 'run-fixture');
  assert.equal(learning.source.label, 'Current source checked');
  await service.close(); closed = true;
  await assert.rejects(readFile(file), { code: 'ENOENT' });
  assert.equal(connectMemoryService({ root }), null);
});

test('MCP refuses to fall back to an independent store when a live desktop endpoint becomes unavailable', async t => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-brain-unavailable-'));
  const service = await createMemoryService({ root, store: { search() { return { results: [] }; } } });
  const descriptor = await readFile(join(root, '.memory', 'service.json'), 'utf8');
  await service.close();
  await writeFile(join(root, '.memory', 'service.json'), descriptor);
  t.after(() => rm(root, { recursive: true, force: true }));
  const client = connectMemoryService({ root });
  assert(client);
  await assert.rejects(client.call('memory_search', { query: 'release' }), /service is unavailable/);
});
