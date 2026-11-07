const { stat } = require('node:fs/promises');
const { join } = require('node:path');
const { launch: launchProcess } = require('./processes.cjs');

async function openPath(shell, path, { platform = process.platform, launch = launchProcess } = {}) {
  const error = await shell.openPath(path);
  if (!error) return true;

  const windowsDirectory = process.env.SystemRoot || process.env.WINDIR;
  if (platform !== 'win32' || !windowsDirectory || !(await stat(path).catch(() => null))?.isDirectory()) {
    throw new Error(error);
  }

  // Some Windows shell configurations reject Electron's folder verb. Ask
  // Explorer directly, keeping the validated directory as one argument.
  const child = launch(join(windowsDirectory, 'explorer.exe'), [path], { cwd: path, stdio: 'ignore' });
  return new Promise((resolve, reject) => {
    child.once('error', reject);
    child.once('spawn', () => { child.unref(); resolve(true); });
  });
}

module.exports = { openPath };
