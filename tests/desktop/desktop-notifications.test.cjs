const assert = require('node:assert/strict');
const { test } = require('node:test');
const { EventEmitter } = require('node:events');
const { mkdtempSync, rmSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { createDesktopNotifications, prepareWindowsNotificationShortcut, notificationRoute, WINDOWS_APP_ID, WINDOWS_TOAST_CLSID } = require('../../src/desktop/desktop-notifications.cjs');

function fixture(options = {}) {
  const instances = [];
  const calls = [];
  const errors = [];
  class Notification extends EventEmitter {
    static isSupported() { return options.supported !== false; }
    constructor(value) {
      super();
      if (options.constructorError) throw new Error('Native constructor unavailable');
      this.options = value;
      instances.push(this);
    }
    show() {
      if (options.showError) throw new Error('Native show unavailable');
      calls.push('notification:show');
    }
    close() { calls.push('notification:close'); this.emit('close', {}); }
  }
  const window = {
    isDestroyed: () => Boolean(options.destroyed), isMinimized: () => options.minimized !== false,
    restore: () => calls.push('window:restore'), show: () => calls.push('window:show'), focus: () => calls.push('window:focus'),
  };
  const notifications = createDesktopNotifications({
    Notification, window: () => window, navigate: href => calls.push(href),
    onError: error => errors.push(error.message), ...(options.enabled === false ? { enabled: false } : {}),
  });
  return { notifications, instances, calls, errors };
}

test('questions request a native toast even when the app is already visible', () => {
  const { notifications, instances, calls } = fixture({ minimized: false });
  assert.equal(notifications.show({ id: 'question-1', kind: 'question', title: 'Question', body: 'Which project?', href: '/agents/review?run=one' }), true);
  assert.equal(instances[0].options.timeoutType, 'never');
  assert.equal(instances[0].options.silent, false);
  assert.equal(instances[0].options.groupId, 'doxie-questions');
  assert.deepEqual(calls, ['notification:show']);
  instances[0].emit('click');
  assert.deepEqual(calls.slice(1), ['window:show', 'window:focus', '/agents/review?run=one']);
});

test('clicking a toast restores and focuses the app before opening its internal source', () => {
  const { notifications, instances, calls } = fixture();
  notifications.show({ id: 'run-1', href: '/workflows/review?run=one' });
  instances[0].emit('click');
  assert.deepEqual(calls, ['notification:show', 'window:restore', 'window:show', 'window:focus', '/workflows/review?run=one']);
});

test('notification routes cannot open external URLs, protocol-relative URLs or Windows paths', () => {
  for (const href of ['https://example.org', '//example.org/agents/test', 'file:///C:/private', '/agents\\private', '/agents/test\nmalformed', '/unexpected/path', undefined]) {
    assert.equal(notificationRoute(href), '/notifications');
  }
  assert.equal(notificationRoute('/agents/new?session=existing'), '/agents/new?session=existing');
  const { notifications, instances, calls } = fixture();
  notifications.show({ href: 'https://example.org' });
  instances[0].emit('click');
  assert.equal(calls.at(-1), '/notifications');
});

test('native failure or unsupported systems leave the caller free to retain the in-app notification', () => {
  const unsupported = fixture({ supported: false });
  assert.equal(unsupported.notifications.show({ id: 'unsupported' }), false);
  assert.equal(unsupported.instances.length, 0);
  for (const options of [{ constructorError: true }, { showError: true }]) {
    const { notifications, errors } = fixture(options);
    assert.equal(notifications.show({ id: 'failed' }), false);
    assert.equal(errors.length, 1);
  }
  const failed = fixture();
  failed.notifications.show({ id: 'os-failure' });
  failed.instances[0].emit('failed', {}, 'Notifications are disabled');
  assert.deepEqual(failed.errors, ['Notifications are disabled']);
  assert.equal(failed.notifications.show({ id: 'os-failure' }), true, 'a failed delivery can be retried');
});

test('native toasts deduplicate the same notification and limit text without splitting Unicode characters', () => {
  const { notifications, instances } = fixture();
  assert.equal(notifications.show({ id: 'same', title: '😀'.repeat(100), body: '😀'.repeat(300) }), true);
  assert.equal(notifications.show({ id: 'same' }), false);
  assert.equal(instances.length, 1);
  assert.equal(Array.from(instances[0].options.title).length, 80);
  assert.equal(Array.from(instances[0].options.body).length, 200);
  assert.equal(instances[0].options.body.endsWith('…'), true);
});

test('Action Center toasts retain their click handler after the visible popup times out', () => {
  const { notifications, instances, calls } = fixture();
  notifications.show({ id: 'timed-out', href: '/history' });
  instances[0].emit('close', { reason: 'timedOut' });
  assert.equal(notifications.dismiss('timed-out'), true);
  assert.equal(calls.at(-1), 'notification:close');
});

test('closing the manager dismisses native notifications and prevents new toasts', () => {
  const { notifications, calls } = fixture();
  notifications.show({ id: 'first' });
  notifications.show({ id: 'second' });
  notifications.close();
  assert.equal(calls.filter(call => call === 'notification:close').length, 2);
  assert.equal(notifications.show({ id: 'third' }), false);
  assert.equal(notifications.dismiss('first'), false);
  const disabled = fixture({ enabled: false });
  assert.equal(disabled.notifications.show({ id: 'test-profile' }), false);
});

test('clicking a toast after its window was destroyed does not throw or navigate', () => {
  const { notifications, instances, calls, errors } = fixture({ destroyed: true });
  notifications.show({ id: 'orphaned', href: '/history' });
  instances[0].emit('click');
  assert.deepEqual(calls, ['notification:show']);
  assert.deepEqual(errors, []);
});

test('packaged Windows apps register a per-user shortcut matching the stable toast identity', () => {
  const directory = mkdtempSync(join(tmpdir(), 'doxie-toast-'));
  try {
    const writes = [];
    const executable = join(directory, 'DoxieOS.exe');
    const app = { isPackaged: true, getPath: name => { assert.equal(name, 'appData'); return directory; } };
    const shell = { writeShortcutLink: (...args) => { writes.push(args); return true; } };
    assert.equal(prepareWindowsNotificationShortcut({ app, shell, platform: 'win32', executable, icon: 'icon.png' }), true);
    assert.equal(writes.length, 1);
    assert.equal(writes[0][0], join(directory, 'Microsoft', 'Windows', 'Start Menu', 'Programs', 'DoxieOS Desktop.lnk'));
    assert.equal(writes[0][2].appUserModelId, WINDOWS_APP_ID);
    assert.equal(writes[0][2].toastActivatorClsid, WINDOWS_TOAST_CLSID);
    assert.equal(writes[0][2].target, executable);
    assert.equal(prepareWindowsNotificationShortcut({ app: { isPackaged: false }, shell, platform: 'win32' }), false);
    assert.equal(prepareWindowsNotificationShortcut({ app, shell, platform: 'linux' }), false);
    assert.equal(writes.length, 1, 'development and other platforms do not create shortcuts');
  } finally { rmSync(directory, { recursive: true, force: true }); }
});

test('Windows shortcut registration failures do not stop application initialization', () => {
  const directory = mkdtempSync(join(tmpdir(), 'doxie-toast-error-'));
  try {
    const errors = [];
    assert.equal(prepareWindowsNotificationShortcut({
      app: { isPackaged: true, getPath: () => directory }, shell: { writeShortcutLink: () => false }, platform: 'win32',
      onError: error => errors.push(error.message),
    }), false);
    assert.equal(errors.length, 1);
  } finally { rmSync(directory, { recursive: true, force: true }); }
});
