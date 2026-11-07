const assert = require('node:assert/strict');
const { mkdtemp, readFile, readdir, rm, writeFile } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { test } = require('node:test');
const { createAgents, matches } = require('../../src/desktop/agents.cjs');
const { createStorage } = require('../../src/desktop/storage.cjs');
const { headlessArgs, inputMessage, providerOutput, providerFor, settingsFor } = require('../../src/desktop/providers.cjs');
const { normalizeExecution, resolveExecution } = require('../../src/desktop/execution.cjs');
const { invocation } = require('../../src/desktop/processes.cjs');

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
    const captured = join(root, 'captured.json');
    const executable = join(root, windows ? 'provider.cmd' : 'provider');
    await writeFile(mock, `
      let input = '';
      process.stdin.setEncoding('utf8');
      process.stdin.on('data', data => input += data);
      process.stdin.on('end', () => {
        const prompt = JSON.parse(input).message.content[0].text;
        const section = prompt.split('## Doxie request\\n\\n')[1];
        const request = JSON.parse(section.split('\\n').slice(1).join('\\n').split('\\n\x60\x60\x60')[0]);
        const model = process.argv[process.argv.indexOf('--model') + 1];
        const effort = process.argv[process.argv.indexOf('--effort') + 1];
        require('node:fs').writeFileSync(process.env.DOXIE_TEST_CAPTURE, JSON.stringify({ request, model,
          effort, effortEnvironment: process.env.CLAUDE_CODE_EFFORT_LEVEL, ephemeral: process.argv.includes('--no-session-persistence'),
          contextOutputPath: process.env.DOXIE_WORKFLOW_CONTEXT_OUT }));
        console.log(JSON.stringify({ type: 'system', session_id: 'test-session' }));
        console.log(JSON.stringify({ type: 'assistant', message: { content: [{ type: 'text', text: 'Finished' }] } }));
        console.log(JSON.stringify({ type: 'result', usage: { input_tokens: 12, output_tokens: 5 }, total_cost_usd: 0.02 }));
      });
    `);
    await writeFile(executable, windows ? `@"${process.execPath}" "%~dp0mock.cjs" %*\r\n` : `#!/bin/sh\nexec "${process.execPath}" "${mock}" "$@"\n`, { mode: 0o755 });
    storage.setSetting('desktop.providers', { 'claude-code': { executable } });
    storage.save('agents', { id: 'example', name: 'Example', execution: { providerId: 'claude-code', model: 'agent-model', effort: 'medium' } });
    const events = [];
    agents = createAgents({ storage, emit: event => events.push(event) });
    assert.equal(agents.getRun('previous').status, 'Interrupted');
    assert.equal(agents.getRun('previous').effort, null);
    storage.save('agents', { id: 'legacy-persistent', modes: { run: { keepSessionAlive: true } } });
    assert.throws(() => agents.start('legacy-persistent'), /native Loop or Cron workflow/);
    const raw = '  first line\n    exact spacing & payload  ';
    const contextJson = '{ "large": 9007199254740993, "text": "unmodified" }\n';
    const handoff = { schema: 'doxie.workflow-handoff.v1', workflowId: 'flow', runId: 'run', nodeId: 'target',
      sources: [{ kind: 'node', nodeId: 'source', contextJson, artifacts: [] }] };
    const contextOutputPath = join(root, 'context.json');
    const result = agents.start('example', { arguments: raw, inputs: { text: raw }, handoff, contextOutputPath,
      effort: 'xhigh', workflowRunId: 'run', env: { DOXIE_TEST_CAPTURE: captured, CLAUDE_CODE_EFFORT_LEVEL: 'low' } });
    const complete = await agents.wait(result.id);
    assert.equal(complete.status, 'Completed');
    assert.equal(complete.sessionId, 'test-session');
    assert.equal(complete.model, 'agent-model');
    assert.equal(complete.effort, 'xhigh');
    assert.equal(complete.arguments, 'Workflow run / target');
    const dispatch = JSON.parse(await readFile(captured, 'utf8'));
    assert.equal(dispatch.model, 'agent-model');
    assert.equal(dispatch.effort, 'xhigh');
    assert.equal(dispatch.effortEnvironment, 'xhigh');
    assert.equal(dispatch.ephemeral, true);
    assert.equal(dispatch.request.arguments, raw);
    assert.deepEqual(dispatch.request.inputs, { text: raw });
    assert.deepEqual(dispatch.request.handoff, handoff);
    assert.equal(dispatch.contextOutputPath, contextOutputPath);
    assert.deepEqual(await readdir(join(root, '.runtime', 'agent-hints')), []);
    assert.equal(complete.usage.inputTokens, 12);
    assert.equal(complete.usage.totalCostUsd, 0.02);
    assert.ok(events.some(event => event.type === 'agent:output'));
  } finally {
    await agents?.close();
    storage.close();
    await rm(root, { recursive: true, force: true });
  }
});

test('filters agent memory and captures Codex usage', () => {
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

test('keeps provider commentary and tool activity separate from the final response', () => {
  const provider = { id: 'codex' };
  const item = value => providerOutput(provider, JSON.stringify({ type: 'item.completed', item: value }));
  assert.deepEqual(item({ type: 'agent_message', phase: 'commentary', text: 'Checking the source.' }),
    { source: 'Progress', lines: ['Checking the source.'] });
  assert.deepEqual(item({ type: 'command_execution', command: 'some long command' }),
    { source: 'Activity', lines: ['▶ some long command'] });
  const table = '| City | Temperature |\n| --- | --- |\n| Example | 24 °C |';
  assert.deepEqual(item({ type: 'agent_message', phase: 'final_answer', text: table }),
    { source: 'Response', lines: [table] });
  assert.deepEqual(item({ type: 'agent_message', text: table }), { source: 'Assistant', lines: [table] });
  assert.deepEqual(providerOutput({ id: 'claude-code' }, JSON.stringify({ type: 'assistant', message: { content: [
    { type: 'text', text: table }, { type: 'tool_use', name: 'Bash', input: { command: 'internal-command' } },
  ] } })), { source: 'Response', lines: [table], activity: ['▶ Bash internal-command'] });
  assert.equal(providerOutput(provider, JSON.stringify({ type: 'turn.failed', error: { message: 'Invalid credentials.' } })).error, true);
});

test('resolves explicit execution overrides ahead of provider-specific agent preferences', () => {
  const settings = { defaultProvider: 'codex', providers: { codex: { model: 'codex-default' }, 'claude-code': { model: 'claude-default' } } };
  const preference = normalizeExecution({ providerId: 'claude', model: 'team/agent-v1' });
  assert.deepEqual(resolveExecution(settings, preference), { providerId: 'claude-code', model: 'team/agent-v1' });
  assert.deepEqual(resolveExecution(settings, preference, { model: 'override' }), { providerId: 'claude-code', model: 'override' });
  assert.deepEqual(resolveExecution(settings, preference, { providerId: 'codex' }), { providerId: 'codex', model: 'codex-default' });
  assert.throws(() => normalizeExecution({ model: 'no-provider' }), /Select a provider/);
  assert.throws(() => normalizeExecution({ providerId: 'codex', model: 'unsafe & value' }), /Invalid provider model/);
  assert.ok(headlessArgs({ id: 'codex', model: 'codex-default' }).includes('--ephemeral'));
});

test('scopes inherited reasoning effort to its provider and model', () => {
  const settings = { defaultProvider: 'codex', providers: {
    codex: { model: 'default-model', effort: 'medium' }, 'claude-code': { model: null, effort: 'low' },
  } };
  const preference = { providerId: 'codex', model: 'custom-model', effort: 'ultra' };
  assert.deepEqual(resolveExecution(settings, preference), preference);
  assert.deepEqual(resolveExecution(settings, preference, { model: 'default-model' }), { providerId: 'codex', model: 'default-model', effort: 'medium' });
  assert.deepEqual(resolveExecution(settings, preference, { model: 'another-model' }), { providerId: 'codex', model: 'another-model' });
  assert.deepEqual(resolveExecution(settings, preference, { providerId: 'claude-code' }), { providerId: 'claude-code', model: null, effort: 'low' });
  assert.equal(resolveExecution(settings, preference, { effort: 'high' }).effort, 'high');
  assert.throws(() => normalizeExecution({ effort: 'high' }), /Select a provider/);
  assert.throws(() => normalizeExecution({ providerId: 'ollama', effort: 'high' }), /Unsupported reasoning effort/);
  assert.throws(() => normalizeExecution({ providerId: 'codex', effort: 'high&unsafe' }), /Unsupported reasoning effort/);
  const args = headlessArgs({ id: 'codex', effort: 'ultra' });
  assert.deepEqual(args.slice(args.indexOf('-c'), args.indexOf('-c') + 2), ['-c', "model_reasoning_effort='ultra'"]);
  assert.doesNotThrow(() => invocation('codex.cmd', args));
});

test('uses the CLI default for new Codex profiles and keeps only the chosen model from old settings', () => {
  const store = getSetting => ({ root: tmpdir(), getSetting });
  const fresh = settingsFor(store(() => null)).providers.codex;
  assert.equal(fresh.model, null);
  assert.equal(headlessArgs(fresh).includes('--model'), false);
  const old = { codex: { model: 'gateway/codex', models: ['old-suggestion'], effort: 'high' } };
  const saved = settingsFor(store(key => key === 'desktop.providers' ? old : null)).providers.codex;
  assert.equal(saved.model, 'gateway/codex');
  assert.equal(saved.effort, 'high');
  assert.equal(Object.hasOwn(saved, 'models'), false);
});

test('routes explicitly selected local models through the Codex Responses client', () => {
  const storage = { root: tmpdir(), getSetting: () => null };
  assert.throws(() => providerFor(storage, 'ollama'), /Select a model for Ollama/);
  for (const [id, port] of [['ollama', 11434], ['lmstudio', 1234]]) {
    const model = 'llama3.2:latest';
    const provider = { id, name: id, model, localBaseUrl: `http://127.0.0.1:${port}` };
    const args = headlessArgs(provider);
    assert.equal(normalizeExecution({ providerId: id, model }).providerId, id);
    assert.ok(args.includes(`model_providers.doxie_${id}.base_url='${provider.localBaseUrl}/v1'`));
    assert.ok(args.includes(`model_providers.doxie_${id}.requires_openai_auth=false`));
    assert.ok(args.includes('--ephemeral'));
    assert.ok(!args.includes('--oss'));
    assert.equal(args[args.indexOf('--model') + 1], model);
    assert.doesNotThrow(() => invocation('codex.cmd', args));
    assert.equal(inputMessage(provider, 'Hello'), 'Hello');
    assert.deepEqual(providerOutput(provider, '{"type":"thread.started","thread_id":"local"}'), { sessionId: 'local' });
  }
});
