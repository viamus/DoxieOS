const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const { mkdirSync, mkdtempSync, rmSync, writeFileSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { test } = require('node:test');
const { openPath } = require('../../src/desktop/shell.cjs');

function fixture(t) {
  const root = mkdtempSync(join(tmpdir(), 'doxie-shell-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const directory = join(root, 'Workspace com espaços e ação');
  mkdirSync(directory);
  const file = join(directory, 'document.txt');
  writeFileSync(file, 'workspace document');
  return { directory, file, missing: join(root, 'missing') };
}

function windowsEnvironment(t, values) {
  const keys = ['SystemRoot', 'WINDIR'];
  const previous = Object.fromEntries(keys.map(key => [key, process.env[key]]));
  for (const key of keys) {
    if (values[key] === undefined) delete process.env[key];
    else process.env[key] = values[key];
  }
  t.after(() => {
    for (const key of keys) {
      if (previous[key] === undefined) delete process.env[key];
      else process.env[key] = previous[key];
    }
  });
}

test('uses the native shell without launching another file manager when it succeeds', async (t) => {
  const { directory } = fixture(t);
  const opened = [];
  const shell = { async openPath(path) { opened.push(path); return ''; } };
  const launch = () => assert.fail('The successful native shell must not launch Explorer');

  assert.equal(await openPath(shell, directory, { platform: 'win32', launch }), true);
  assert.deepEqual(opened, [directory]);
});

test('falls back to Explorer for an existing Windows directory and preserves its full path', async (t) => {
  const { directory } = fixture(t);
  windowsEnvironment(t, { SystemRoot: join(tmpdir(), 'Windows root') });
  let request;
  let child;
  let markLaunched;
  let unreferenced = false;
  const launched = new Promise(resolve => { markLaunched = resolve; });
  const launch = (...args) => {
    request = args;
    child = new EventEmitter();
    child.unref = () => { unreferenced = true; };
    markLaunched();
    return child;
  };

  let settled = false;
  const pending = openPath({ openPath: async () => 'Failed to open path' }, directory, { platform: 'win32', launch })
    .finally(() => { settled = true; });
  await launched;
  await Promise.resolve();
  assert.equal(settled, false);
  assert.equal(unreferenced, false);
  child.emit('spawn');
  assert.equal(await pending, true);
  assert.deepEqual(request, [join(process.env.SystemRoot, 'explorer.exe'), [directory], { cwd: directory, stdio: 'ignore', windowsHide: false }]);
  assert.equal(unreferenced, true);
});

test('uses WINDIR when SystemRoot is unavailable', async (t) => {
  const { directory } = fixture(t);
  windowsEnvironment(t, { WINDIR: join(tmpdir(), 'Windows fallback') });
  let executable;
  const launch = (path) => {
    executable = path;
    const child = new EventEmitter();
    child.unref = () => {};
    queueMicrotask(() => child.emit('spawn'));
    return child;
  };

  assert.equal(await openPath({ openPath: async () => 'Failed to open path' }, directory, { platform: 'win32', launch }), true);
  assert.equal(executable, join(process.env.WINDIR, 'explorer.exe'));
});

test('reports an Explorer launch failure instead of reporting success', async (t) => {
  const { directory } = fixture(t);
  windowsEnvironment(t, { SystemRoot: join(tmpdir(), 'Windows root') });
  const failure = new Error('Explorer could not start');
  const launch = () => {
    const child = new EventEmitter();
    child.unref = () => assert.fail('A failed process must not be unreferenced as a successful launch');
    queueMicrotask(() => child.emit('error', failure));
    return child;
  };

  await assert.rejects(openPath({ openPath: async () => 'Failed to open path' }, directory, { platform: 'win32', launch }), failure);
});

test('preserves the native error when Explorer fallback does not apply', async (t) => {
  const paths = fixture(t);
  const message = 'The native shell could not open this item';
  const launch = () => assert.fail('Explorer fallback is unavailable for this item');
  windowsEnvironment(t, { SystemRoot: join(tmpdir(), 'Windows root') });

  for (const [platform, path] of [['linux', paths.directory], ['darwin', paths.directory], ['win32', paths.file], ['win32', paths.missing]]) {
    await assert.rejects(openPath({ openPath: async () => message }, path, { platform, launch }), { message });
  }
});

test('preserves the native error when neither Windows directory variable is available', async (t) => {
  const { directory } = fixture(t);
  windowsEnvironment(t, {});
  const message = 'Failed to open path';
  const launch = () => assert.fail('Explorer must not be resolved through the search path');

  await assert.rejects(openPath({ openPath: async () => message }, directory, { platform: 'win32', launch }), { message });
});

test('propagates a rejected native shell operation without attempting fallback', async (t) => {
  const { directory } = fixture(t);
  const failure = new Error('Native shell rejected the request');
  const launch = () => assert.fail('A rejected native operation must propagate its error');

  await assert.rejects(openPath({ openPath: async () => { throw failure; } }, directory, { platform: 'win32', launch }), failure);
});
