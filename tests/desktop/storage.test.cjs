const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { DatabaseSync } = require('node:sqlite');
const { test } = require('node:test');
const Zip = require('adm-zip');
const { createStorage } = require('../../src/desktop/storage.cjs');

function setup(t) {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'doxie-storage-test-'));
  const stores = [];
  const open = options => {
    const storage = createStorage({ root: path.join(home, 'project'), databasePath: ':memory:', ...options });
    stores.push(storage);
    return storage;
  };
  t.after(() => {
    for (const storage of stores) storage.close();
    fs.rmSync(home, { force: true, recursive: true });
  });
  return { home, open };
}

function write(file, content) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, content);
}

test('opens existing SQLite settings without altering another table and reads configured catalogs', t => {
  const { home, open } = setup(t);
  const databasePath = path.join(home, 'state.db');
  const legacy = new DatabaseSync(databasePath);
  legacy.exec(`CREATE TABLE key_value_settings (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL);
    INSERT INTO key_value_settings VALUES ('ui.language', 'pt-BR', '2026-01-01');
    CREATE TABLE previous_release_data (value TEXT);
    INSERT INTO previous_release_data VALUES ('preserve');`);
  const catalog = path.join(home, 'catalog');
  legacy.prepare('INSERT INTO key_value_settings VALUES (?, ?, ?)').run('doxie.catalog.customRoots', JSON.stringify([{ Id: 'team', Name: 'Team', Path: catalog }]), '2026-01-01');
  legacy.close();
  write(path.join(catalog, 'skills', 'review', 'manifest.json'), '{ // catalog comment\n "id": "review", "displayName": "Team Review",\n}');
  write(path.join(catalog, 'skills', 'review', 'body.md'), 'Review this change.');
  const storage = open({ databasePath, settings: { 'ui.language': 'en' } });
  assert.equal(storage.getSetting('ui.language'), 'pt-BR');
  assert.equal(storage.get('agents', 'review').name, 'Team Review');
  assert.equal(storage.get('skills', 'review').catalogId, 'team');
  assert.equal(storage.db.prepare('SELECT value FROM previous_release_data').get().value, 'preserve');
  storage.setSetting('ui.language', 'en');
  assert.equal(storage.getSetting('ui.language'), 'en');
});

test('reads old workspace mounts and preserves hand-authored context files', t => {
  const { open } = setup(t);
  const storage = open();
  const workspace = path.join(storage.roots.workspaces, 'release');
  write(path.join(workspace, 'workspace.json'), JSON.stringify({ Name: 'Release', MountedLibraries: ['policy'], MountedAgents: ['review'], CreatedAt: '2026-01-02T12:00:00Z' }));
  write(path.join(workspace, 'AGENTS.md'), 'Team instructions\n');
  storage.save('libraries', { id: 'policy', name: 'Policy' });
  storage.writeFile('libraries', 'policy', 'memory/review.md', '---\nname: Review policy\ntype: Project\n---\n\nCheck the changelog.\n');
  const saved = storage.save('workspaces', { id: 'release', description: 'Current release' });
  assert.equal(saved.name, 'Release');
  assert.equal(saved.createdAt, '2026-01-02T12:00:00Z');
  assert.deepEqual(saved.mountedLibraryIds, ['policy']);
  assert.deepEqual(saved.mountedAgentIds, ['review']);
  assert.equal(storage.get('libraries', 'policy').memories[0].name, 'Review policy');
  assert.match(fs.readFileSync(path.join(workspace, 'CLAUDE.md'), 'utf8'), /@.*memory\/review\.md/);
  assert.equal(fs.readFileSync(path.join(workspace, 'AGENTS.md'), 'utf8'), 'Team instructions\n');
  storage.save('workspaces', { id: 'release', mountedLibraryIds: [] });
  assert.equal(fs.existsSync(path.join(workspace, 'CLAUDE.md')), false);
});

test('confines workspace file operations and identifies binary previews', t => {
  const { home, open } = setup(t);
  const storage = open();
  const workspace = storage.save('workspaces', { id: 'project-one' });
  const outside = path.join(home, 'external');
  write(path.join(outside, 'guard.txt'), 'leave alone');
  fs.symlinkSync(outside, path.join(workspace.path, 'escape'), process.platform === 'win32' ? 'junction' : 'dir');
  assert.throws(() => storage.readFile('workspaces', workspace.id, '../external/guard.txt'), /Unsafe/);
  assert.throws(() => storage.writeFile('workspaces', workspace.id, 'escape/guard.txt', 'overwrite'), /Symbolic/);
  assert.throws(() => storage.writeFile('workspaces', workspace.id, 'note.md:stream', 'overwrite'), /Unsafe/);
  assert.equal(storage.listFiles('workspaces', workspace.id).some(file => file.name === 'escape'), false);
  write(path.join(workspace.path, 'data.bin'), Buffer.from([0, 1, 2]));
  assert.equal(storage.readFile('workspaces', workspace.id, 'data.bin').isBinary, true);
  const file = storage.writeFile('workspaces', workspace.id, 'notes/summary.md', 'Review complete.');
  assert.equal(file.relativePath, 'notes/summary.md');
  assert.equal(file.content, 'Review complete.');
  assert.equal(fs.readFileSync(path.join(outside, 'guard.txt'), 'utf8'), 'leave alone');
});

test('copies legacy skills to canonical format and regenerates shims idempotently', t => {
  const { open } = setup(t);
  const storage = open();
  const old = path.join(storage.root, '.claude', 'skills', 'review');
  write(path.join(old, 'SKILL.md'), '---\nname: review\ndescription: Review change\n---\n\n# Review\n');
  write(path.join(old, 'orchestrator.json'), JSON.stringify({ category: 'Delivery', modes: { inspect: { description: 'Inspect' } } }));
  write(path.join(old, 'inspect.md'), 'Inspect the diff.\n');
  write(path.join(storage.root, 'AGENTS.md'), '# Manual project instructions\n');
  assert.deepEqual(storage.detectLegacy().map(item => item.id), ['review']);
  assert.deepEqual(storage.migrateLegacy(), [{ kind: 'skills', id: 'review', migrated: true }]);
  assert.equal(storage.get('agents', 'review').category, 'Delivery');
  assert.match(storage.get('skills', 'review').body, /# Review/);
  assert.equal(fs.readFileSync(path.join(storage.root, '.agents', 'skills', 'review', 'inspect.md'), 'utf8'), 'Inspect the diff.\n');
  assert.equal(fs.readFileSync(path.join(storage.root, 'AGENTS.md'), 'utf8'), '# Manual project instructions\n');
  assert.equal(storage.regenerate().changed, 0);
  assert.deepEqual(storage.detectLegacy(), []);
});

test('round-trips a legacy-format workflow ZIP with its agent and stages collisions before writing', t => {
  const { home, open } = setup(t);
  const origin = open();
  const destination = open({ root: path.join(home, 'destination') });
  origin.save('agents', { id: 'review', name: 'Review', description: 'Check a change', body: 'Read the change.', systemPrompt: 'Check tests.' });
  origin.writeFile('agents', 'review', 'refs/checklist.md', 'Keep a checklist.');
  origin.save('workflows', { id: 'publish', name: 'Publish', trigger: { kind: 'manual' }, nodes: [{ id: 'step-one', agentId: 'review' }] });
  const archive = origin.exportItem('workflows', 'publish');
  assert.deepEqual(new Zip(archive).getEntries().map(entry => entry.entryName).sort(), ['agents/review.zip', 'doxie-workflow-export.json', 'workflow/publish/workflow.json']);
  destination.save('agents', { id: 'review', name: 'Different', body: 'Other' });
  assert.throws(() => destination.importItem('workflows', archive), /already exists/);
  assert.equal(destination.get('workflows', 'publish'), null);
  assert.equal(destination.get('agents', 'review').body, 'Other');
  destination.importItem('workflows', archive, { overwrite: true });
  assert.equal(destination.get('workflows', 'publish').nodes[0].agentId, 'review');
  assert.equal(destination.get('agents', 'review').systemPrompt, 'Check tests.');
  assert.equal(destination.readFile('agents', 'review', 'refs/checklist.md').content, 'Keep a checklist.');
  assert.equal(destination.importItem('workflows', archive).id, 'publish');
});

test('moves an agent between configured catalogs with its subagent, memory and companion files', t => {
  const { home, open } = setup(t);
  const storage = open();
  storage.saveCatalogs([{ id: 'team', name: 'Team', path: path.join(home, 'team') }]);
  storage.save('agents', { id: 'review', name: 'Review', body: 'Review changes.', systemPrompt: 'Check results.' });
  storage.writeFile('agents', 'review', 'memories/test.md', '---\npriority: High\ncondition: mode=review\n---\n\nRun tests.');
  storage.writeFile('agents', 'review', 'refs/input.txt', 'reference');
  const updated = storage.save('agents', { id: 'review', name: 'Team review', catalogId: 'team' });
  assert.equal(updated.name, 'Team review');
  assert.equal(updated.systemPrompt, 'Check results.');
  assert.equal(updated.memories[0].priority, 'High');
  assert.equal(updated.memories[0].condition, 'mode=review');
  assert.equal(storage.get('agents', 'review', 'default'), null);
  assert.equal(storage.readFile('agents', 'review', 'refs/input.txt', 'team').content, 'reference');
});

test('rejects malformed ZIP paths and oversized declared entries before extraction', t => {
  const { open } = setup(t);
  const storage = open();
  const zip = new Zip();
  zip.addFile('xx/escape.md', Buffer.from('unsafe'));
  const badPath = Buffer.from(zip.toBuffer());
  const oldName = Buffer.from('xx/escape.md');
  for (let offset = badPath.indexOf(oldName); offset >= 0; offset = badPath.indexOf(oldName, offset + 1)) badPath.write('../escape.md', offset);
  assert.throws(() => storage.importItem('libraries', badPath), /Unsafe/);
  const oversized = zip.toBuffer();
  const central = oversized.indexOf(Buffer.from([0x50, 0x4b, 0x01, 0x02]));
  oversized.writeUInt32LE(65 * 1024 * 1024, central + 24);
  assert.throws(() => storage.importItem('libraries', oversized), /extraction limits/);
  assert.deepEqual(storage.list('libraries'), []);
});
