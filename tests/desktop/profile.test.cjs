const assert = require('node:assert/strict');
const { existsSync, mkdtempSync, rmSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { DatabaseSync } = require('node:sqlite');
const { test } = require('node:test');
const { prepareProfile } = require('../../src/desktop/profile.cjs');

test('copies a legacy SQLite database including WAL data and checks migration once', async () => {
  const root = mkdtempSync(join(tmpdir(), 'doxie-profile-'));
  const legacy = join(root, 'legacy.db');
  const directory = join(root, 'profile');
  const original = new DatabaseSync(legacy);
  try {
    original.exec("PRAGMA journal_mode=WAL; CREATE TABLE records (value TEXT); INSERT INTO records VALUES ('legacy');");
    const profile = await prepareProfile(directory, [legacy]);
    assert.equal(profile.root, join(directory, 'workbench'));
    assert.equal(profile.databasePath, join(directory, 'state.db'));
    const copied = new DatabaseSync(profile.databasePath, { readOnly: true });
    try { assert.equal(copied.prepare('SELECT value FROM records').get().value, 'legacy'); }
    finally { copied.close(); }
    original.exec("UPDATE records SET value = 'changed later';");
    await prepareProfile(directory, [legacy]);
    const reopened = new DatabaseSync(profile.databasePath, { readOnly: true });
    try { assert.equal(reopened.prepare('SELECT value FROM records').get().value, 'legacy'); }
    finally { reopened.close(); }
    rmSync(profile.databasePath);
    await prepareProfile(directory, [legacy]);
    assert.equal(existsSync(profile.databasePath), false);
    assert.equal(original.prepare('SELECT value FROM records').get().value, 'changed later');
  } finally {
    original.close();
    rmSync(root, { recursive: true, force: true });
  }
});
