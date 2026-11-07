const assert = require('node:assert/strict');
const { test } = require('node:test');
const { createActions } = require('../../src/desktop/ipc.cjs');
const { resolveExecution } = require('../../src/desktop/execution.cjs');

function fixture() {
  const agent = { id: 'review-agent', catalogId: 'team', execution: { providerId: 'codex', model: 'saved-agent-model', effort: 'high' } };
  const connector = { id: 'prompt', modelBacked: true, execution: { providerId: 'codex', model: 'saved-connector-model' } };
  const calls = [];
  const settings = { defaultProvider: 'claude-code', providers: {
    'claude-code': { model: 'application-default', effort: 'medium' },
    codex: { model: 'application-codex-default', effort: 'low' },
  } };
  const storage = { get(kind, id, catalogId) {
    if (kind === 'agents' && id === agent.id && (!catalogId || catalogId === agent.catalogId)) return agent;
    if (kind === 'tools' && id === connector.id) return connector;
    return null;
  } };
  const agents = { start(id, params) {
    calls.push({ id, params });
    const entry = id === agent.id ? agent : connector;
    return { id: 'new-run', ...resolveExecution(settings, entry.execution, params) };
  } };
  return { agent, connector, calls, actions: createActions({ storage, agents }) };
}

test('direct agent runs use saved execution while preserving inputs, workspace and catalog', () => {
  const { agent, actions, calls } = fixture();
  const params = { catalogId: 'team', workspaceId: 'review-workspace', mode: 'review',
    arguments: 'Inspect the release.', inputs: { report: 'release.md' }, env: { REVIEW_FORMAT: 'markdown' } };
  const result = actions['agent:start'](agent.id, params);
  assert.deepEqual(result, { id: 'new-run', ...agent.execution });
  assert.equal(calls.length, 1);
  assert.deepEqual(calls[0], { id: agent.id, params: { ...params, modeId: undefined } });
  for (const key of ['providerId', 'model', 'effort']) assert.equal(Object.hasOwn(calls[0].params, key), false);
});

test('direct agent IPC rejects provider, model and reasoning overrides before launching a run', () => {
  const { agent, actions, calls } = fixture();
  for (const [key, value] of Object.entries({ providerId: 'claude-code', model: 'other-model', effort: 'low' })) {
    assert.throws(() => actions['agent:start'](agent.id, { catalogId: 'team', [key]: value }), /Use Edit agent/);
    assert.equal(calls.length, 0);
    // Supplying the saved value is still a per-run override and must use the editor.
    assert.throws(() => actions['agent:start'](agent.id, { [key]: agent.execution[key] }), /Use Edit agent/);
    assert.equal(calls.length, 0);
  }
  for (const value of [false, 0, {}, []]) {
    assert.throws(() => actions['agent:start'](agent.id, { model: value }), /Use Edit agent/);
  }
  assert.equal(calls.length, 0);
});

test('empty execution fields are stripped from direct agent runs without changing the caller payload', () => {
  const { agent, actions, calls } = fixture();
  for (const empty of ['', '   ', undefined, null]) {
    const params = { providerId: empty, model: empty, effort: empty, fields: { report: 'release.md' } };
    const result = actions['agent:start'](agent.id, params);
    assert.deepEqual(result, { id: 'new-run', ...agent.execution });
    const forwarded = calls.at(-1).params;
    assert.deepEqual(forwarded.inputs, params.fields);
    for (const key of ['providerId', 'model', 'effort']) {
      assert.equal(Object.hasOwn(forwarded, key), false);
      assert.equal(Object.hasOwn(params, key), true);
      assert.equal(params[key], empty);
    }
  }
});

test('model-backed connector runs retain explicit provider, model and reasoning overrides', () => {
  const { connector, actions, calls } = fixture();
  const execution = { providerId: 'claude-code', model: 'connector-run-model', effort: 'max' };
  const result = actions['connector:start'](connector.id, { ...execution, inputs: { prompt: 'Summarize this report.' } });
  assert.deepEqual(result, { id: 'new-run', ...execution });
  assert.equal(calls.length, 1);
  for (const [key, value] of Object.entries(execution)) assert.equal(calls[0].params[key], value);
  assert.deepEqual(calls[0].params.inputs, { prompt: 'Summarize this report.' });
});
