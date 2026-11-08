const assert = require('node:assert/strict');
const { test } = require('node:test');
const { createNotifications, createActions } = require('../../src/desktop/ipc.cjs');

function fixture({ dismiss } = {}) {
  const settings = new Map();
  const events = [], shown = [], dismissed = [];
  const storage = { getSetting: key => settings.get(key), setSetting: (key, value) => settings.set(key, JSON.stringify(value)) };
  const notifications = createNotifications(storage, event => events.push(event), {
    show: notification => shown.push(notification), dismiss: id => { dismissed.push(id); dismiss?.(id); },
  });
  return { storage, notifications, events, shown, dismissed };
}

test('notification bus persists the full question, emits it and asks the OS to show it once', () => {
  const { storage, notifications, shown, events, dismissed } = fixture();
  const item = notifications.add({ kind: 'question', questionId: 'question', runId: 'run', title: 'Needs your answer', body: 'Which version?' });
  assert.equal(shown.length, 1);
  assert.equal(shown[0], item);
  assert.equal(events[0].notification, item);
  assert.equal(JSON.parse(storage.getSetting('desktop.notifications'))[0].body, 'Which version?');
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

test('deleting through IPC removes only the selected notification, including already read items, and persists it', () => {
  const { storage, notifications, events, dismissed } = fixture();
  const first = notifications.add({ title: 'Keep this question', kind: 'question', runId: 'run', questionId: 'pending' });
  const selected = notifications.add({ title: 'Delete this update', readAt: '2026-11-08T10:00:00.000Z' });
  const actions = createActions({ notifications });
  assert.equal(actions['notifications:delete'](selected.id), true);
  assert.deepEqual(notifications.list(), [first]);
  assert.deepEqual(dismissed, [selected.id]);
  assert.deepEqual(events.at(-1), { type: 'notifications:changed' });
  const restored = createNotifications(storage, () => {});
  assert.deepEqual(restored.list(), [first]);
  assert.equal(restored.list()[0].readAt, undefined, 'deleting another item must not resolve or mark the pending question');
});

test('deletion requires an explicit ID and unknown IDs do not clear the list', () => {
  const { notifications, events, dismissed } = fixture();
  const item = notifications.add({ title: 'Keep this update' });
  const actions = createActions({ notifications });
  for (const id of [undefined, null, '', '   ', 0, {}, []]) {
    assert.throws(() => actions['notifications:delete'](id), /notification ID is required/);
  }
  assert.equal(actions['notifications:delete']('unknown'), false);
  assert.deepEqual(notifications.list(), [item]);
  assert.deepEqual(dismissed, []);
  assert.equal(events.length, 1, 'invalid deletions do not emit changes');
});

test('clearing through IPC removes read and unread notifications, dismisses their toasts and stays empty after reload', () => {
  const { storage, notifications, events, dismissed } = fixture();
  const unread = notifications.add({ title: 'Unread question', kind: 'question', runId: 'run', questionId: 'pending' });
  const read = notifications.add({ title: 'Read update', readAt: '2026-11-08T10:00:00.000Z' });
  const actions = createActions({ notifications });
  assert.equal(actions['notifications:clear'](), true);
  assert.deepEqual(notifications.list(), []);
  assert.deepEqual(dismissed, [read.id, unread.id]);
  assert.deepEqual(events.at(-1), { type: 'notifications:changed' });
  assert.deepEqual(createNotifications(storage, () => {}).list(), []);
  assert.equal(actions['notifications:clear'](), false);
  assert.equal(events.filter(event => event.type === 'notifications:changed').length, 1);
});

test('native dismissal failures do not prevent individual deletion, clearing or persistence', () => {
  const { storage, notifications, dismissed } = fixture({ dismiss() { throw new Error('OS unavailable'); } });
  const first = notifications.add({ title: 'First' });
  const second = notifications.add({ title: 'Second' });
  const third = notifications.add({ title: 'Third' });
  assert.equal(notifications.remove(first.id), true);
  assert.deepEqual(createNotifications(storage, () => {}).list().map(item => item.id), [third.id, second.id]);
  assert.equal(notifications.clear(), true);
  assert.deepEqual(dismissed, [first.id, third.id, second.id]);
  assert.deepEqual(createNotifications(storage, () => {}).list(), []);
});
