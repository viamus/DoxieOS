const assert = require('node:assert/strict');
const { test } = require('node:test');
const { mkdir, mkdtemp, readFile, rm, writeFile } = require('node:fs/promises');
const { join } = require('node:path');
const { tmpdir } = require('node:os');
const pty = require('node-pty');
const { createConsoles } = require('../../src/desktop/consoles.cjs');

test('Claude consoles inject the mandatory Brain server even when the profile contains an override', async t => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-console-brain-'));
  await mkdir(join(root, '.runtime'));
  const configured = { mcpServers: { doxie_library_memory: { command: 'overridden-tool', enabled: false }, reference: { command: 'fixture-reference' } } };
  await writeFile(join(root, '.mcp.json'), JSON.stringify(configured));
  const storage = { root, memory: {}, getSetting(key) {
    return key === 'desktop.providers' ? { 'claude-code': { executable: process.execPath } } : null;
  } };
  const original = pty.spawn;
  let dispatch;
  let finish;
  pty.spawn = (executable, args, options) => {
    dispatch = { executable, args, options };
    return { onExit(callback) { finish = callback; }, onData() {}, kill() { finish({ exitCode: 0 }); } };
  };
  const consoles = createConsoles({ storage, emit() {} });
  t.after(async () => { await consoles.close(); pty.spawn = original; await rm(root, { recursive: true, force: true }); });
  const result = consoles.start({ providerId: 'claude-code' });
  // Windows invocation can provide one escaped command string to node-pty.
  const dispatched = typeof dispatch.args === 'string' ? dispatch.args : dispatch.args.join(' ');
  assert(dispatched.includes('--strict-mcp-config'));
  assert(dispatched.includes('--append-system-prompt'));
  assert(dispatched.includes('mandatory shared knowledge connector'));
  const runtime = join(root, '.runtime', 'console-mcp', `${result.id}.json`);
  const tools = JSON.parse(await readFile(runtime, 'utf8')).mcpServers;
  assert.equal(tools.doxie_library_memory.command, process.execPath);
  assert.equal(tools.reference.command, 'fixture-reference');
  assert.equal(await readFile(join(root, '.mcp.json'), 'utf8'), JSON.stringify(configured));
  consoles.stop(result.id);
  await assert.rejects(readFile(runtime), { code: 'ENOENT' });
});
