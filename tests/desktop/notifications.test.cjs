const assert = require('node:assert/strict');
const { test } = require('node:test');
const { createNotifications, createActions } = require('../../src/desktop/ipc.cjs');

function fixture() {
  const settings = new Map();
  const events = [], shown = [], dismissed = [];
  const storage = { getSetting: key => settings.get(key), setSetting: (key, value) => settings.set(key, value) };
  const notifications = createNotifications(storage, event => events.push(event), {
    show: notification => shown.push(notification), dismiss: id => dismissed.push(id),
  });
  return { storage, notifications, events, shown, dismissed };
}

test('notification bus persists the full question, emits it and asks the OS to show it once', () => {
  const { storage, notifications, shown, events, dismissed } = fixture();
  const item = notifications.add({ kind: 'question', questionId: 'question', runId: 'run', title: 'Needs your answer', body: 'Which version?' });
  assert.equal(shown.length, 1);
  assert.equal(shown[0], item);
  assert.equal(events[0].notification, item);
  assert.equal(storage.getSetting('desktop.notifications')[0].body, 'Which version?');
  notifications.add({ kind: 'question-resolved', runId: 'run' });
  assert.equal(notifications.list().length, 1);
  assert.ok(item.readAt);
  assert.deepEqual(dismissed, [item.id]);
  assert.equal(shown.length, 1);
});

test('answering through IPC dismisses the matching question only after accepting the answer', () => {
  const { notifications, dismissed } = fixture();
  const item = notifications.add({ kind: 'question', runId: 'run', questionId: 'pending', body: 'Which version?' });
  const actions = createActions({ notifications, agents: { answer(id, questionId, text) {
    assert.equal(id, 'run');
    if (questionId !== 'pending') throw new Error('Stale question');
    assert.equal(text, 'Preview');
    return { id, status: 'Running' };
  } } });
  assert.throws(() => actions['agent:answer']('run', 'old', 'Preview'), /Stale/);
  assert.equal(item.readAt, undefined);
  assert.deepEqual(actions['agent:answer']('run', 'pending', 'Preview'), { id: 'run', status: 'Running' });
  assert.ok(item.readAt);
  assert.deepEqual(dismissed, [item.id]);
});

test('native notification failure does not lose the in-app message', () => {
  const { storage } = fixture();
  const notifications = createNotifications(storage, () => {}, { show() { throw new Error('OS notification unavailable'); } });
  const item = notifications.add({ title: 'Question', body: 'Which version?' });
  assert.equal(notifications.list()[0], item);
  assert.equal(notifications.read(item.id), true);
});
