const assert = require('node:assert/strict');
const { mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { dirname, join } = require('node:path');
const { test } = require('node:test');
const { executablePath, nativeCodexExecutable, invocation } = require('../../src/desktop/processes.cjs');

function fixture(t, { local = false, linked = false } = {}) {
  const root = mkdtempSync(join(tmpdir(), 'doxie-codex-process-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const modules = join(root, 'node_modules');
  const packageRoot = linked ? join(root, 'store', 'codex', 'node_modules', '@openai', 'codex') : join(modules, '@openai', 'codex');
  const write = (file, text = '') => {
    mkdirSync(dirname(file), { recursive: true });
    writeFileSync(file, text, { mode: 0o755 });
    return file;
  };
  write(join(packageRoot, 'package.json'), JSON.stringify({ name: '@openai/codex', bin: { codex: 'bin/codex.js' } }));
  write(join(packageRoot, 'bin', 'codex.js'), '// installed Codex launcher');
  if (linked) {
    mkdirSync(join(modules, '@openai'), { recursive: true });
    symlinkSync(packageRoot, join(modules, '@openai', 'codex'), process.platform === 'win32' ? 'junction' : 'dir');
  }
  const shim = write(join(local ? join(modules, '.bin') : root, 'codex.cmd'),
    `@ECHO off\r\nnode "%dp0%\\${local ? '..' : 'node_modules'}\\@openai\\codex\\bin\\codex.js" %*\r\n`);
  const binary = (base, arch = 'x64') => write(join(base, 'vendor', arch === 'arm64' ? 'aarch64-pc-windows-msvc' : 'x86_64-pc-windows-msvc', 'bin', 'codex.exe'));
  const optional = (arch = 'x64') => {
    // Optional packages live beside the physical main package, including pnpm links.
    const base = join(dirname(packageRoot), `codex-win32-${arch}`);
    write(join(base, 'package.json'), JSON.stringify({ name: '@openai/codex' }));
    return binary(base, arch);
  };
  return { root, packageRoot, shim, write, binary, optional };
}

test('prefers the installed optional Windows Codex binary over the legacy vendor layout', t => {
  const files = fixture(t);
  files.binary(files.packageRoot);
  const native = files.optional();
  assert.equal(nativeCodexExecutable(files.shim, { platform: 'win32', arch: 'x64' }), native);
  assert.deepEqual(invocation(native, ['-c', 'mcp_servers.test={command="tool",args=["a&b"]}']), {
    executable: native, args: ['-c', 'mcp_servers.test={command="tool",args=["a&b"]}'],
  });
  if (process.platform === 'win32' && process.arch === 'x64') {
    assert.equal(executablePath(files.shim), native);
    assert.equal(executablePath('codex', { Path: files.root }), native);
  }
});

test('resolves local npm shims and optional packages beside a linked main package', t => {
  const files = fixture(t, { local: true, linked: true });
  const native = files.optional('arm64');
  assert.equal(nativeCodexExecutable(files.shim, { platform: 'win32', arch: 'arm64' }), native);
});

test('falls back to the main Codex package vendor binary, including .bat shims', t => {
  const files = fixture(t);
  const native = files.binary(files.packageRoot);
  const bat = files.write(join(files.root, 'codex.bat'), '@node "%~dp0node_modules\\@openai\\codex\\bin\\codex.js" %*');
  assert.equal(nativeCodexExecutable(bat, { platform: 'win32', arch: 'x64' }), native);
  files.optional();
  rmSync(join(dirname(files.packageRoot), 'codex-win32-x64', 'vendor'), { recursive: true });
  assert.equal(nativeCodexExecutable(files.shim, { platform: 'win32', arch: 'x64' }), native);
});

test('leaves missing, unsupported and custom provider shims unchanged', t => {
  const files = fixture(t);
  assert.equal(nativeCodexExecutable(files.shim, { platform: 'win32', arch: 'x64' }), null);
  if (process.platform === 'win32') assert.equal(executablePath(files.shim), files.shim);
  files.optional();
  assert.equal(nativeCodexExecutable(files.shim, { platform: 'linux', arch: 'x64' }), null);
  assert.equal(nativeCodexExecutable(files.shim, { platform: 'win32', arch: 'ia32' }), null);
  const custom = files.write(join(files.root, 'provider.cmd'), '@node custom-provider.js %*');
  assert.equal(nativeCodexExecutable(custom, { platform: 'win32', arch: 'x64' }), null);
  assert.equal(executablePath(custom), custom);
  files.write(files.shim, '@node custom-codex.js %*');
  assert.equal(nativeCodexExecutable(files.shim, { platform: 'win32', arch: 'x64' }), null);
  assert.equal(executablePath(files.shim), files.shim);
});

test('keeps shell metacharacter rejection for Windows command shims', { skip: process.platform !== 'win32' }, () => {
  for (const value of ['command="tool"', 'a&b', 'a|b', '%TEMP%', 'a\nb']) {
    assert.throws(() => invocation('provider.cmd', ['-c', value]), /shell metacharacters/);
  }
  assert.doesNotThrow(() => invocation('provider.cmd', ['exec', '--model', 'safe/model']));
});
