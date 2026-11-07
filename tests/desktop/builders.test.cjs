const assert = require('node:assert/strict');
const { mkdtemp, rm, writeFile } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { test } = require('node:test');
const { createBuilders } = require('../../src/desktop/builders.cjs');
const { createStorage } = require('../../src/desktop/storage.cjs');

test('builder sends explicit prior turns to a fresh provider call and preserves an older workflow draft', async () => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-builder-'));
  const storage = createStorage({ root, databasePath: ':memory:' });
  try {
    storage.save('agents', { id: 'workflow-builder', name: 'Workflow Builder', body: 'Author a workflow.',
      execution: { providerId: 'codex', model: 'builder-default' } });
    const calls = [];
    const agents = {
      runBuilder(kind, params) { calls.push(params); return { id: kind }; },
      async wait() {
        return { status: 'Completed', sessionId: 'provider-session-to-ignore',
          output: [{ source: 'Stdout', text: 'Draft recorded.' }] };
      },
    };
    const builders = createBuilders({ storage, agents, emit: () => {} });
    const session = builders.start('workflow', { model: 'builder-override', effort: 'high' });
    await writeFile(join(storage.roots.sandboxes, session.id, '.draft', 'workflow.json'), '{"id":"legacy","name":"Legacy draft"}');
    assert.equal(builders.get(session.id).draft.name, 'Legacy draft');
    await builders.message(session.id, 'A first message\nwith exact spacing.');
    await builders.message(session.id, 'Now revise the draft.');
    assert.equal(calls.length, 2);
    assert.equal(calls[1].providerId, 'codex');
    assert.equal(calls[1].model, 'builder-override');
    assert.equal(calls[1].effort, 'high');
    assert.equal(calls[1].sessionId, undefined);
    const request = JSON.parse(calls[1].prompt.split('\n\n').at(-1));
    assert.deepEqual(request.previousTurns, [
      { role: 'user', text: 'A first message\nwith exact spacing.' },
      { role: 'assistant', text: 'Draft recorded.' },
    ]);
    assert.equal(request.message, 'Now revise the draft.');
  } finally {
    storage.close();
    await rm(root, { recursive: true, force: true });
  }
});

test('starts the first builder response automatically without a user turn or an empty manifest', async () => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-builder-initialize-'));
  const storage = createStorage({ root, databasePath: ':memory:' });
  try {
    storage.save('agents', { id: 'agent-builder', body: 'Help the user build an agent.' });
    const requests = [];
    let finish;
    const waiting = new Promise(resolve => { finish = resolve; });
    const builders = createBuilders({ storage, emit: () => {}, agents: {
      runBuilder(_kind, params) { requests.push(params); return { id: 'greeting' }; },
      wait() { return waiting; },
    } });
    const created = builders.start('agent', { providerId: 'codex', initialize: true });
    assert.equal(created.status, 'Running');
    assert.equal(created.initializing, true);
    assert.equal(created.draft, null);
    assert.deepEqual(created.messages, []);
    const request = JSON.parse(requests[0].prompt.split('\n\n').at(-1));
    assert.equal(request.stage, 'initialize');
    assert.deepEqual(request.previousTurns, []);
    finish({ status: 'Completed', output: [{ source: 'Stdout', text: 'What should your agent do?' }] });
    await new Promise(resolve => setImmediate(resolve));
    const ready = builders.get(created.id);
    assert.equal(ready.status, 'Ready');
    assert.equal(ready.initializing, false);
    assert.equal(ready.draft, null);
    assert.deepEqual(ready.messages, [{ role: 'assistant', text: 'What should your agent do?' }]);
  } finally {
    storage.close();
    await rm(root, { recursive: true, force: true });
  }
});

test('retries a builder message after authentication without duplicating it or replaying provider logs', async () => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-builder-auth-'));
  const storage = createStorage({ root, databasePath: ':memory:' });
  try {
    storage.save('agents', { id: 'agent-builder', name: 'Agent Builder', body: 'Author an agent.' });
    const message = 'Build an agent that extracts blockers.';
    const draft = { id: 'extract-blockers', name: 'Extract Blockers', body: 'Extract release blockers from the supplied report.' };
    const calls = [];
    const agents = {
      runBuilder(kind, params) {
        assert.equal(kind, 'agent');
        calls.push(params);
        return { id: calls.length };
      },
      async wait(id) {
        if (id === 1) {
          return { status: 'Failed', exitCode: 1, output: [
            { source: 'Stderr', text: 'Reconnecting... 5/5' },
            { source: 'Stderr', text: 'unexpected status 401 Unauthorized: Missing bearer or basic authentication in header' },
          ] };
        }
        await writeFile(join(calls[id - 1].cwd, '.draft', 'manifest.json'), JSON.stringify(draft));
        return { status: 'Completed', output: [{ source: 'Stdout', text: 'Draft recorded.' }] };
      },
    };
    const builders = createBuilders({ storage, agents, emit: () => {} });
    const session = builders.start('agent', { providerId: 'codex', model: 'original-model', effort: 'high' });
    const failed = await builders.message(session.id, message);
    assert.equal(failed.status, 'Failed');
    assert.equal(failed.failure?.kind, 'authentication');
    assert.deepEqual(failed.messages.filter(turn => turn.role === 'user').map(turn => turn.text), [message]);
    assert(!JSON.stringify(failed.messages).includes('Reconnecting'));

    const retried = await builders.retry(session.id);
    assert.equal(retried.status, 'Ready');
    assert.equal(retried.failure ?? null, null);
    assert.deepEqual(retried.messages.map(({ role, text }) => ({ role, text })), [
      { role: 'user', text: message },
      { role: 'assistant', text: 'Draft recorded.' },
    ]);
    assert.equal(calls.length, 2);
    assert.equal(calls[1].providerId, 'codex');
    assert.equal(calls[1].model, 'original-model');
    assert.equal(calls[1].effort, 'high');
    const retryRequest = JSON.parse(calls[1].prompt.split('\n\n').at(-1));
    assert.deepEqual(retryRequest.previousTurns, []);
    assert.equal(retryRequest.message, message);

    assert.equal(builders.get(session.id).draft.id, draft.id);
    assert.equal(builders.promote(session.id).id, draft.id);
    assert.equal(storage.get('agents', draft.id).body, draft.body);
    assert.equal(builders.get(session.id).status, 'Saved');
  } finally {
    storage.close();
    await rm(root, { recursive: true, force: true });
  }
});

test('resumes a legacy failed builder turn with updated execution settings', async () => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-builder-legacy-auth-'));
  const storage = createStorage({ root, databasePath: ':memory:' });
  try {
    storage.save('agents', { id: 'agent-builder', name: 'Agent Builder', body: 'Author an agent.' });
    let captured;
    const builders = createBuilders({ storage, emit: () => {}, agents: {
      runBuilder(_kind, params) { captured = params; return { id: 'retry' }; },
      async wait() { return { status: 'Completed', output: [{ source: 'Stdout', text: 'I can revise it.' }] }; },
    } });
    const { id, kind } = builders.start('agent', { providerId: 'codex', model: 'old-model' });
    const previousTurns = [
      { role: 'user', text: 'Create an agent.' },
      { role: 'assistant', text: 'What should it review?' },
    ];
    const message = 'Review release blockers.';
    const metadata = join(storage.roots.sandboxes, id, '.draft', 'desktop-session.json');
    await writeFile(metadata, JSON.stringify({ id, kind, providerId: 'codex', model: 'old-model', status: 'Failed', messages: [
      ...previousTurns, { role: 'user', text: message }, { role: 'assistant', text: 'unexpected status 401 Unauthorized: Missing bearer or basic authentication in header' },
    ] }));

    assert.equal(builders.get(id).failure?.kind, 'authentication');
    const changed = builders.updateExecution(id, { providerId: 'claude-code', model: 'other-model', effort: 'medium' });
    assert.equal(changed.providerId, 'claude-code');
    const retried = await builders.retry(id);
    assert.equal(retried.status, 'Ready');
    assert.equal(captured.model, 'other-model');
    assert.equal(captured.effort, 'medium');
    assert.equal(captured.providerId, 'claude-code');
    const request = JSON.parse(captured.prompt.split('\n\n').at(-1));
    assert.deepEqual(request.previousTurns, previousTurns);
    assert.equal(request.message, message);
    assert.equal(retried.messages.length, 4);
    assert(!JSON.stringify(retried.messages).includes('401'));
  } finally {
    storage.close();
    await rm(root, { recursive: true, force: true });
  }
});
