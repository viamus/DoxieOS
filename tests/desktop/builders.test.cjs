const assert = require('node:assert/strict');
const { mkdtemp, readFile, rm, writeFile } = require('node:fs/promises');
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

test('reopens a builder transcript and configures the agent draft independently from the builder model', async () => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-builder-reopen-'));
  const databasePath = join(root, 'state.db');
  let storage = createStorage({ root, databasePath });
  try {
    storage.save('agents', { id: 'agent-builder', name: 'Agent Builder', body: 'Author an agent.' });
    const calls = [];
    const draft = { id: 'release-review', name: 'Release Review', description: 'Review a release.',
      body: 'Identify release blockers.', modes: { review: { description: 'Review a release report.' } },
      execution: { providerId: 'codex', model: 'generated-model' }, customField: { preserved: true } };
    const agents = {
      runBuilder(_kind, params) { calls.push(params); return { id: calls.length }; },
      async wait(id) {
        if (id === 1) await writeFile(join(calls[0].cwd, '.draft', 'manifest.json'), JSON.stringify(draft));
        return { status: 'Completed', output: [{ source: 'Stdout', text: id === 1 ? 'The draft is ready.' : 'I kept the draft.' }] };
      },
    };
    let builders = createBuilders({ storage, agents, emit: () => {} });
    const session = builders.start('agent', { providerId: 'codex', model: 'builder-model', effort: 'ultra' });
    const ready = await builders.message(session.id, 'Create a release reviewer.');
    const selected = builders.updateDraftExecution(session.id, { providerId: 'claude', model: 'agent-model', effort: 'high' });
    const execution = { providerId: 'claude-code', model: 'agent-model', effort: 'high' };
    assert.deepEqual(selected.draft, { ...draft, execution });
    assert.deepEqual(selected.messages, ready.messages);
    assert.equal(selected.providerId, 'codex');
    assert.equal(selected.model, 'builder-model');
    assert.equal(selected.effort, 'ultra');

    storage.close();
    storage = createStorage({ root, databasePath });
    builders = createBuilders({ storage, agents, emit: () => {} });
    const reopened = builders.list('agent').find(item => item.id === session.id);
    assert.deepEqual(reopened.messages, ready.messages);
    assert.deepEqual(reopened.draft, { ...draft, execution });
    await builders.message(session.id, 'Keep this agent and explain how to use it.');
    const request = JSON.parse(calls[1].prompt.split('\n\n').at(-1));
    assert.deepEqual(request.previousTurns, ready.messages);
    assert.equal(calls[1].providerId, 'codex');
    assert.equal(calls[1].model, 'builder-model');
    assert.equal(calls[1].effort, 'ultra');
    assert.deepEqual(builders.promote(session.id).execution, execution);
    assert.deepEqual(storage.get('agents', draft.id).execution, execution);
  } finally {
    storage.close();
    await rm(root, { recursive: true, force: true });
  }
});

test('clears draft execution to app defaults and rejects invalid or active draft updates', async () => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-builder-draft-execution-'));
  const storage = createStorage({ root, databasePath: ':memory:' });
  let finish;
  let response;
  try {
    for (const kind of ['agent', 'workflow']) storage.save('agents', { id: `${kind}-builder`, body: 'Author a draft.' });
    const events = [];
    const builders = createBuilders({ storage, emit: event => events.push(event), agents: {
      runBuilder() { return { id: 'working' }; },
      wait() { return new Promise(resolve => { finish = resolve; }); },
    } });
    const session = builders.start('agent', { providerId: 'codex', model: 'builder-model' });
    assert.throws(() => builders.updateDraftExecution(session.id, null), /no agent draft/);
    const manifest = join(root, '.sandbox', session.id, '.draft', 'manifest.json');
    const draft = { id: 'default-agent', name: 'Default Agent', body: 'Use the app defaults.',
      execution: { providerId: 'codex', model: 'old-model', effort: 'high' }, custom: ['keep'] };
    await writeFile(manifest, JSON.stringify(draft));
    const cleared = builders.updateDraftExecution(session.id, { providerId: '', model: '', effort: '' });
    assert.deepEqual(cleared.draft, { ...draft, execution: null });
    assert.equal(cleared.model, 'builder-model');
    assert.equal(events.at(-1).type, 'builder:changed');
    assert.deepEqual(events.at(-1).session.draft, cleared.draft);
    const unchanged = await readFile(manifest, 'utf8');
    for (const invalid of [[], { providerId: 'unknown' }, { model: 'missing-provider' },
      { providerId: 'codex', model: 'unsafe & model' }, { providerId: 'ollama', effort: 'high' }]) {
      assert.throws(() => builders.updateDraftExecution(session.id, invalid));
      assert.equal(await readFile(manifest, 'utf8'), unchanged);
    }
    response = builders.message(session.id, 'Explain the draft.');
    assert.throws(() => builders.updateDraftExecution(session.id, { providerId: 'codex', model: 'new-model' }), /already responding/);
    assert.equal(await readFile(manifest, 'utf8'), unchanged);
    finish({ status: 'Completed', output: [{ source: 'Stdout', text: 'The app chooses the provider.' }] });
    await response;
    assert.equal(builders.promote(session.id).execution, null);
    const workflow = builders.start('workflow');
    assert.throws(() => builders.updateDraftExecution(workflow.id, null), /workflow nodes/);
  } finally {
    if (finish) finish({ status: 'Cancelled', output: [] });
    await response;
    storage.close();
    await rm(root, { recursive: true, force: true });
  }
});

test('recovers interrupted builder messages and initialization from the persisted transcript', async () => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-builder-interrupted-'));
  const storage = createStorage({ root, databasePath: ':memory:' });
  try {
    storage.save('agents', { id: 'agent-builder', body: 'Author an agent.' });
    const calls = [];
    const agents = {
      runBuilder(_kind, params) { calls.push(params); return { id: calls.length }; },
      async wait() { return { status: 'Completed', output: [{ source: 'Stdout', text: 'Recovered response.' }] }; },
    };
    const builders = createBuilders({ storage, agents, emit: () => {} });
    const previousTurns = [{ role: 'user', text: 'Create a reviewer.' }, { role: 'assistant', text: 'What should it review?' }];
    const userTurn = { role: 'user', text: 'Release blockers.' };
    const interrupted = builders.start('agent', { providerId: 'codex', model: 'saved-model' });
    await writeFile(join(root, '.sandbox', interrupted.id, '.draft', 'desktop-session.json'), JSON.stringify({
      ...interrupted, status: 'Running', messages: [...previousTurns, userTurn],
    }));
    const reopened = createBuilders({ storage, agents, emit: () => {} });
    assert.equal(reopened.get(interrupted.id).status, 'Failed');
    assert.equal(reopened.get(interrupted.id).failure.kind, 'cancelled');
    const recovered = await reopened.retry(interrupted.id);
    assert.equal(recovered.status, 'Ready');
    assert.deepEqual(recovered.messages, [...previousTurns, userTurn, { role: 'assistant', text: 'Recovered response.' }]);
    assert.deepEqual(JSON.parse(calls[0].prompt.split('\n\n').at(-1)), {
      schema: 'doxie.builder-request.v1', stage: 'message', previousTurns, omittedTurns: 0, message: userTurn.text,
    });

    const initializing = builders.start('agent', { providerId: 'codex' });
    const initialPrompt = 'Ask what the new agent should do.';
    await writeFile(join(root, '.sandbox', initializing.id, '.draft', 'desktop-session.json'), JSON.stringify({
      ...initializing, status: 'Running', initializing: true, initialPrompt,
    }));
    const greeting = await reopened.retry(initializing.id);
    assert.equal(greeting.status, 'Ready');
    assert.equal(greeting.initializing, false);
    assert.deepEqual(greeting.messages, [{ role: 'assistant', text: 'Recovered response.' }]);
    const request = JSON.parse(calls[1].prompt.split('\n\n').at(-1));
    assert.equal(request.stage, 'initialize');
    assert.equal(request.message, initialPrompt);
    assert.deepEqual(request.previousTurns, []);
  } finally {
    storage.close();
    await rm(root, { recursive: true, force: true });
  }
});
