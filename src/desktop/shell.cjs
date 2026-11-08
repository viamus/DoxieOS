const { realpath } = require('node:fs/promises');

async function openPath(shell, path) {
  // Packaged Windows processes can see a virtualized AppData location that
  // Explorer cannot access. Pass the physical path across that boundary.
  const physicalPath = await realpath(path);
  const error = await shell.openPath(physicalPath);
  if (error) throw new Error(error);
  return true;
}

module.exports = { openPath };
