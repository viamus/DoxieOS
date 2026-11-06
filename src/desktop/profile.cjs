const { randomUUID } = require('node:crypto');
const { existsSync, mkdirSync, renameSync, rmSync, writeFileSync } = require('node:fs');
const { join } = require('node:path');
const { backup, DatabaseSync } = require('node:sqlite');
const { safePath } = require('./storage.cjs');

async function prepareProfile(directory, legacyDatabases = []) {
  mkdirSync(directory, { recursive: true });
  const root = safePath(directory, 'workbench');
  mkdirSync(root, { recursive: true });
  for (const provider of ['codex', 'claude']) {
    mkdirSync(safePath(root, `.providers/${provider}`), { recursive: true });
  }
  const databasePath = safePath(directory, 'state.db');
  const migrationMarker = safePath(directory, '.sqlite-migration-checked');

  if (!existsSync(migrationMarker)) {
    const legacy = !existsSync(databasePath) && legacyDatabases.find(path => path !== databasePath && existsSync(path));
    if (legacy) {
      const temporary = join(directory, `state-${randomUUID()}.tmp`);
      const source = new DatabaseSync(legacy, { readOnly: true });
      try {
        await backup(source, temporary);
        renameSync(temporary, databasePath);
      } finally {
        source.close();
        rmSync(temporary, { force: true });
      }
    }
    writeFileSync(migrationMarker, '', { flag: 'wx' });
  }

  return { root, databasePath };
}

module.exports = { prepareProfile };
