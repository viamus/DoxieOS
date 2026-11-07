const { spawn } = require('node:child_process');
const { accessSync, constants, existsSync, readFileSync, realpathSync, statSync } = require('node:fs');
const { createRequire } = require('node:module');
const { basename, delimiter, dirname, extname, isAbsolute, join, resolve } = require('node:path');

const windows = process.platform === 'win32';

function nativeCodexExecutable(shim, { platform = process.platform, arch = process.arch } = {}) {
  if (platform !== 'win32' || !/^codex\.(cmd|bat)$/i.test(basename(shim))) return null;
  const triple = { x64: 'x86_64-pc-windows-msvc', arm64: 'aarch64-pc-windows-msvc' }[arch];
  if (!triple) return null;
  try {
    // Recognize npm's generated global/local shim without executing shell text.
    const reference = readFileSync(shim, 'utf8').match(/(?:%dp0%|%~dp0)[\\/]?((?:node_modules[\\/]|\.\.[\\/])@openai[\\/]codex[\\/]bin[\\/]codex\.js)\b/i);
    if (!reference) return null;
    const entrypoint = resolve(dirname(shim), ...reference[1].split(/[\\/]/));
    const packageJson = realpathSync(join(dirname(entrypoint), '..', 'package.json'));
    const metadata = JSON.parse(readFileSync(packageJson, 'utf8'));
    if (metadata.name !== '@openai/codex' || metadata.bin?.codex !== 'bin/codex.js' || !statSync(entrypoint).isFile()) return null;
    const vendorRoots = [];
    try {
      const installed = createRequire(packageJson).resolve(`@openai/codex-win32-${arch}/package.json`);
      vendorRoots.push(join(dirname(installed), 'vendor'));
    } catch {}
    vendorRoots.push(join(dirname(packageJson), 'vendor'));
    for (const vendor of vendorRoots) {
      const executable = join(vendor, triple, 'bin', 'codex.exe');
      try {
        accessSync(executable, constants.X_OK);
        if (statSync(executable).isFile()) return executable;
      } catch {}
    }
  } catch {}
  return null;
}

function executablePath(command, env = process.env) {
  const search = isAbsolute(command) ? [''] : (env.Path || env.PATH || '').split(delimiter);
  const extensions = windows && !extname(command) ? ['.exe', '.com', '.cmd', '.bat'] : [''];
  for (const directory of search) {
    if (!directory && !isAbsolute(command)) continue;
    for (const extension of extensions) {
      const candidate = resolve(directory, command + extension);
      try {
        accessSync(candidate, constants.X_OK);
        if (statSync(candidate).isFile()) return nativeCodexExecutable(candidate) || candidate;
      } catch {}
    }
  }
  return null;
}

function invocation(executable, args) {
  if (!windows || !/\.(cmd|bat)$/i.test(executable)) return { executable, args };
  // Provider prompts are sent over stdin. Only fixed switches and validated
  // model/session names reach a Windows command shim.
  const values = [executable, ...args];
  if (values.some(value => /["%&|<>^!\r\n]/.test(value))) {
    throw new Error('The Windows provider shim requires a path and options without shell metacharacters. Select the provider executable in Settings.');
  }
  const command = values.map(value => `"${value}"`).join(' ');
  return { executable: process.env.ComSpec || join(process.env.SystemRoot, 'System32', 'cmd.exe'), args: ['/d', '/s', '/c', `"${command}"`], verbatim: true };
}

function launch(executable, args, options) {
  const command = invocation(executable, args);
  return spawn(command.executable, command.args, {
    ...options,
    windowsHide: true,
    windowsVerbatimArguments: command.verbatim,
    detached: !windows,
  });
}

function killTree(child) {
  if (!child.pid) return;
  if (windows) {
    const taskkill = join(process.env.SystemRoot, 'System32', 'taskkill.exe');
    if (existsSync(taskkill)) {
      const killer = spawn(taskkill, ['/pid', String(child.pid), '/t', '/f'], { windowsHide: true, stdio: 'ignore' });
      killer.once('error', () => child.kill());
      killer.once('exit', code => { if (code !== 0 && child.exitCode === null) child.kill(); });
      return;
    }
  }
  try {
    if (windows) child.kill();
    else process.kill(-child.pid, 'SIGKILL');
  } catch (error) {
    if (error.code !== 'ESRCH') throw error;
  }
}

module.exports = { executablePath, nativeCodexExecutable, invocation, launch, killTree };
