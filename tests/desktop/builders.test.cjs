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
