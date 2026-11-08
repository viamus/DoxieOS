const assert = require('node:assert/strict');
const { mkdirSync, mkdtempSync, realpathSync, rmSync, symlinkSync, writeFileSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { test } = require('node:test');
const { openPath } = require('../../src/desktop/shell.cjs');

function fixture(t) {
  const root = mkdtempSync(join(tmpdir(), 'doxie-shell-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const directory = join(root, 'Workspace com espaços e ação');
  mkdirSync(directory);
  const file = join(directory, 'documento com ação.txt');
  writeFileSync(file, 'workspace document');
  const physicalDirectory = realpathSync.native(directory);
  const alias = join(root, 'Alias do workspace');
  symlinkSync(physicalDirectory, alias, process.platform === 'win32' ? 'junction' : 'dir');
  return {
    directory: alias,
    file: join(alias, 'documento com ação.txt'),
    physicalDirectory,
    physicalFile: realpathSync.native(file),
    missing: join(root, 'missing'),
  };
}

test('opens a directory using its physical path instead of its alias', async (t) => {
  const { directory, physicalDirectory } = fixture(t);
  const opened = [];
  const shell = { async openPath(path) { opened.push(path); return ''; } };

  assert.notEqual(directory, physicalDirectory);
  assert.equal(await openPath(shell, directory), true);
  assert.deepEqual(opened, [physicalDirectory]);
});

test('opens a file using its physical path through a directory alias', async (t) => {
  const { file, physicalFile } = fixture(t);
  const opened = [];
  const shell = { async openPath(path) { opened.push(path); return ''; } };

  assert.notEqual(file, physicalFile);
  assert.equal(await openPath(shell, file), true);
  assert.deepEqual(opened, [physicalFile]);
});

test('reports the native shell error after resolving the physical path', async (t) => {
  const { directory, physicalDirectory } = fixture(t);
  const message = 'The native shell could not open this directory';
  const shell = { async openPath(path) { assert.equal(path, physicalDirectory); return message; } };

  await assert.rejects(openPath(shell, directory), { message });
});

test('propagates a rejected native shell operation', async (t) => {
  const { directory } = fixture(t);
  const failure = new Error('Native shell rejected the request');
  const shell = { async openPath() { throw failure; } };

  await assert.rejects(openPath(shell, directory), error => error === failure);
});

test('rejects a missing path before calling the native shell', async (t) => {
  const { missing } = fixture(t);
  const shell = { async openPath() { assert.fail('Missing paths must not reach the native shell'); } };

  await assert.rejects(openPath(shell, missing), { code: 'ENOENT' });
});
