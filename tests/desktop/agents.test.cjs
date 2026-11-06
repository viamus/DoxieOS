const assert = require('node:assert/strict');
const { mkdtemp, rm, writeFile } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { test } = require('node:test');
const { createAgents, matches } = require('../../src/desktop/agents.cjs');
const { createStorage } = require('../../src/desktop/storage.cjs');
const { providerOutput } = require('../../src/desktop/providers.cjs');

test('reads legacy history and streams a new provider run with arguments on stdin', { timeout: 10_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), 'doxie agent test '));
  const storage = createStorage({ root, databasePath: ':memory:' });
  let agents;
  try {
    storage.db.exec(`
      CREATE TABLE agent_runs (id TEXT PRIMARY KEY, agent_id TEXT, arguments TEXT, started_at TEXT,
        finished_at TEXT, status TEXT, exit_code INTEGER);
      INSERT INTO agent_runs VALUES ('previous','example','','2026-02-01T00:00:00Z',NULL,'Running',NULL);
    `);
    const windows = process.platform === 'win32';
    const mock = join(root, 'mock.cjs');
    const executable = join(root, windows ? 'provider.cmd' : 'provider');
    await writeFile(mock, `
      let input = '';
      process.stdin.setEncoding('utf8');
      process.stdin.on('data', data => input += data);
      process.stdin.on('end', () => {
        const prompt = JSON.parse(input).message.content[0].text;
        console.log(JSON.stringify({ type: 'system', session_id: 'test-session' }));
        console.log(JSON.stringify({ type: 'assistant', message: { content: [{ type: 'text', text: prompt.split('\\n')[0] }] } }));
        console.log(JSON.stringify({ type: 'result', usage: { input_tokens: 12, output_tokens: 5 }, total_cost_usd: 0.02 }));
      });
    `);
    await writeFile(executable, windows ? `@"${process.execPath}" "%~dp0mock.cjs"\r\n` : `#!/bin/sh\nexec "${process.execPath}" "${mock}"\n`, { mode: 0o755 });
    storage.setSetting('desktop.providers', { 'claude-code': { executable } });
    storage.save('agents', { id: 'example', name: 'Example' });
    const events = [];
    agents = createAgents({ storage, emit: event => events.push(event) });
    assert.equal(agents.getRun('previous').status, 'Interrupted');
    const result = agents.start('example', { arguments: 'hello & world' });
    const complete = await agents.wait(result.id);
    assert.equal(complete.status, 'Completed');
    assert.equal(complete.sessionId, 'test-session');
    assert.match(complete.output[0].text, /hello & world/);
    assert.equal(complete.usage.inputTokens, 12);
    assert.equal(complete.usage.totalCostUsd, 0.02);
    assert.ok(events.some(event => event.type === 'agent:output'));
  } finally {
    await agents?.close();
    storage.close();
    await rm(root, { recursive: true, force: true });
  }
});

test('filters skill memory and captures Codex usage', () => {
  const context = { mode: 'run', provider: 'codex', workspace_id: 'lab' };
  assert.equal(matches('mode = "run" AND provider != "claude-code"', context), true);
  assert.equal(matches('workspace_id = "other"', context), false);
  assert.equal(matches('mode includes "run"', context), false);
  const output = providerOutput({ id: 'codex', model: 'gpt-5.5' }, JSON.stringify({
    type: 'turn.completed', usage: { input_tokens: 12, cached_input_tokens: 3, output_tokens: 5 },
  }));
  assert.equal(output.usage.inputTokens, 9);
  assert.equal(output.usage.cacheReadTokens, 3);
  assert.ok(output.usage.totalCostUsd > 0);
});
