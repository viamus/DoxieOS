const { spawn } = require('node:child_process');
const { accessSync, constants, existsSync, statSync } = require('node:fs');
const { delimiter, extname, isAbsolute, join, resolve } = require('node:path');

const windows = process.platform === 'win32';

function executablePath(command, env = process.env) {
  const search = isAbsolute(command) ? [''] : (env.Path || env.PATH || '').split(delimiter);
  const extensions = windows && !extname(command) ? ['.exe', '.com', '.cmd', '.bat'] : [''];
  for (const directory of search) {
    if (!directory && !isAbsolute(command)) continue;
    for (const extension of extensions) {
      const candidate = resolve(directory, command + extension);
      try {
        accessSync(candidate, constants.X_OK);
        if (statSync(candidate).isFile()) return candidate;
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

module.exports = { executablePath, invocation, launch, killTree };
