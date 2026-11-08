const { randomBytes, timingSafeEqual } = require('node:crypto');
const { createServer, request } = require('node:http');
const { lstatSync, mkdirSync, readFileSync, renameSync, rmSync, writeFileSync } = require('node:fs');
const { join, resolve } = require('node:path');
const { callMemoryTool, memoryTools } = require('./memory-tools.cjs');

const maximumRequest = 256 * 1024;
const maximumResponse = 64 * 1024;
const toolNames = new Set(memoryTools.map(tool => tool.name));
const descriptorPath = root => join(resolve(root), '.memory', 'service.json');

function descriptor(root) {
  const file = descriptorPath(root);
  if (lstatSync(join(resolve(root), '.memory'), { throwIfNoEntry: false })?.isSymbolicLink()
    || lstatSync(file, { throwIfNoEntry: false })?.isSymbolicLink()) throw new Error('Brain service metadata cannot be a symbolic link.');
  let value;
  try {
    const info = lstatSync(file, { throwIfNoEntry: false });
    if (!info) return null;
    if (!info.isFile() || info.size > 4096) throw new Error('Invalid Brain service metadata.');
    value = JSON.parse(readFileSync(file, 'utf8'));
  } catch (error) {
    if (error.code === 'ENOENT') return null;
    throw new Error('Could not read Brain service metadata.');
  }
  if (value.version !== 1 || !Number.isInteger(value.port) || value.port < 1 || value.port > 65535
    || !Number.isInteger(value.pid) || value.pid < 1 || !/^[a-f0-9]{64}$/.test(value.token)) throw new Error('Invalid Brain service metadata.');
  return value;
}

function processExists(pid) {
  try { process.kill(pid, 0); return true; }
  catch (error) { return error.code !== 'ESRCH'; }
}

function invokeRemote(connection, name, args, source = {}) {
  const body = JSON.stringify({ name, args, ...(source.runId ? { runId: source.runId } : {}) });
  if (Buffer.byteLength(body) > maximumRequest) return Promise.reject(new Error('Brain request exceeds 256 KiB.'));
  return new Promise((resolve, reject) => {
    const req = request({ host: '127.0.0.1', port: connection.port, path: '/tools', method: 'POST',
      headers: { authorization: `Bearer ${connection.token}`, 'content-type': 'application/json', 'content-length': Buffer.byteLength(body) },
      timeout: 60_000,
    }, response => {
      const chunks = [];
      let size = 0;
      response.on('data', chunk => {
        size += chunk.length;
        if (size > maximumResponse) { response.destroy(); reject(new Error('Brain response exceeded its budget.')); }
        else chunks.push(chunk);
      });
      response.on('error', () => reject(new Error('The desktop Brain connection was interrupted.')));
      response.on('end', () => {
        try {
          const result = JSON.parse(Buffer.concat(chunks).toString('utf8'));
          if (response.statusCode !== 200 || !result.ok) throw new Error(result.error || 'The desktop Brain request failed.');
          resolve(result.result);
        } catch (error) { reject(new Error(error.message || 'Invalid desktop Brain response.')); }
      });
    });
    req.once('timeout', () => req.destroy(new Error('The desktop Brain request timed out.')));
    req.once('error', () => reject(new Error('The desktop Brain service is unavailable. Restart DoxieOS before retrying.')));
    req.end(body);
  });
}

function connectMemoryService({ root, source = {} }) {
  const connection = descriptor(root);
  if (!connection || !processExists(connection.pid)) return null;
  return { call: (name, args) => invokeRemote(connection, name, args, source) };
}

async function createMemoryService({ root, store }) {
  if (!store) throw new Error('Brain service requires its owning memory store.');
  const prior = descriptor(root);
  if (prior && processExists(prior.pid)) throw new Error('A DoxieOS service already owns Brain.');
  const token = randomBytes(32).toString('hex');
  const file = descriptorPath(root);
  mkdirSync(join(resolve(root), '.memory'), { recursive: true });
  let closing = false;
  let paused = false;
  let generation = 0;
  let tail = Promise.resolve();
  const sockets = new Set();
  let port;
  const server = createServer((incoming, outgoing) => {
    function reply(status, value) {
      const payload = JSON.stringify(value);
      if (Buffer.byteLength(payload) > maximumResponse) return reply(500, { ok: false, error: 'Brain response exceeded its budget.' });
      outgoing.writeHead(status, { 'content-type': 'application/json', 'cache-control': 'no-store', 'x-content-type-options': 'nosniff' });
      outgoing.end(payload);
    }
    const supplied = String(incoming.headers.authorization || '');
    const expected = `Bearer ${token}`;
    const suppliedBytes = Buffer.from(supplied);
    const expectedBytes = Buffer.from(expected);
    if (closing || incoming.headers.host !== `127.0.0.1:${port}` || incoming.headers.origin
      || suppliedBytes.length !== expectedBytes.length || !timingSafeEqual(suppliedBytes, expectedBytes)) {
      incoming.resume(); reply(403, { ok: false, error: 'Brain service authentication failed.' }); return;
    }
    if (incoming.method !== 'POST' || incoming.url !== '/tools' || !/^application\/json(?:;|$)/i.test(incoming.headers['content-type'] || '')) {
      incoming.resume(); reply(400, { ok: false, error: 'Unsupported Brain request.' }); return;
    }
    if (paused) { incoming.resume(); reply(503, { ok: false, error: 'Brain is resetting. Try again after it finishes.' }); return; }
    const requestGeneration = generation;
    if (Number(incoming.headers['content-length']) > maximumRequest) {
      incoming.resume(); reply(413, { ok: false, error: 'Brain request exceeds 256 KiB.' }); return;
    }
    let size = 0;
    const chunks = [];
    let oversized = false;
    incoming.on('data', chunk => {
      size += chunk.length;
      if (size > maximumRequest) {
        if (!oversized) { oversized = true; reply(413, { ok: false, error: 'Brain request exceeds 256 KiB.' }); }
      } else chunks.push(chunk);
    });
    incoming.on('end', () => {
      if (oversized) return;
      if (paused || requestGeneration !== generation) {
        reply(503, { ok: false, error: 'Brain reset invalidated this request. Try again after it finishes.' }); return;
      }
      let message;
      try {
        message = JSON.parse(Buffer.concat(chunks).toString('utf8'));
        if (!message || Array.isArray(message) || typeof message !== 'object'
          || Object.keys(message).some(key => !['name', 'args', 'runId'].includes(key)) || !toolNames.has(message.name)
          || (message.runId !== undefined && (typeof message.runId !== 'string' || message.runId.length > 200))) throw new Error('Unsupported Brain request.');
      } catch { reply(400, { ok: false, error: 'Invalid Brain tool request.' }); return; }
      // The desktop process owns the embedded database. Serialize tool access
      // so MCP processes never compete for its file lock or share a handle.
      tail = tail.catch(() => {}).then(async () => {
        if (closing || paused || requestGeneration !== generation) {
          reply(503, { ok: false, error: closing ? 'Brain is closing.' : 'Brain reset invalidated this request. Try again after it finishes.' }); return;
        }
        try {
          const result = await callMemoryTool(store, message.name, message.args || {}, { ...(message.runId ? { runId: message.runId } : {}) });
          reply(200, { ok: true, result });
        } catch (error) { reply(400, { ok: false, error: String(error.message || 'Brain operation failed.').slice(0, 600) }); }
      });
    });
  });
  server.on('connection', socket => { sockets.add(socket); socket.once('close', () => sockets.delete(socket)); });
  server.requestTimeout = 15_000;
  server.headersTimeout = 10_000;
  server.keepAliveTimeout = 1000;
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => { server.off('error', reject); resolve(); });
  });
  port = server.address().port;
  const temporary = `${file}.${token.slice(0, 12)}.tmp`;
  try {
    writeFileSync(temporary, JSON.stringify({ version: 1, pid: process.pid, port, token }), { flag: 'wx', mode: 0o600 });
    renameSync(temporary, file);
  } catch (error) { server.close(); throw error; }
  finally { rmSync(temporary, { force: true }); }
  return { port, async exclusive(action) {
    if (closing || paused) throw new Error('Brain is closing or resetting.');
    paused = true;
    generation++;
    const work = tail.catch(() => {}).then(action);
    tail = work.catch(() => {});
    try { return await work; } finally { paused = false; }
  }, async close() {
    if (closing) return;
    closing = true;
    try { if (descriptor(root)?.token === token) rmSync(file, { force: true }); } catch {}
    const stopped = new Promise(resolve => server.close(resolve));
    await tail;
    for (const socket of sockets) socket.destroy();
    await stopped;
  } };
}

module.exports = { createMemoryService, connectMemoryService };
