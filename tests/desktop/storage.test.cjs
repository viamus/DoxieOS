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

test('imports a workbench into managed storage without writing to it or copying private configuration', t => {
  const { home, open } = setup(t);
  const source = path.join(home, 'checkout');
  const workspaceSource = path.join(source, '.workspace', 'release');
  write(path.join(source, '.doxie', 'skills', 'review', 'manifest.json'), '{"id":"review","displayName":"Review"}');
  write(path.join(source, '.doxie', 'skills', 'review', 'body.md'), 'Review this change.');
  write(path.join(workspaceSource, 'workspace.json'), '{"Name":"Release"}');
  write(path.join(workspaceSource, 'notes', 'summary.md'), 'Existing notes');
  write(path.join(workspaceSource, '.git', 'config'), 'repository settings');
  write(path.join(workspaceSource, '.env.local'), 'private configuration');
  const outside = path.join(home, 'other');
  write(path.join(outside, 'private.txt'), 'private outside file');
  fs.symlinkSync(outside, path.join(workspaceSource, 'junction'), process.platform === 'win32' ? 'junction' : 'dir');
  const storage = open({ managed: true, workspacesRoot: path.join(source, '.workspace') });
  const imported = storage.importWorkbench(source);
  assert.equal(imported.skills, 1);
  assert.equal(imported.workspaces, 1);
  assert.equal(imported.files, 4);
  assert.equal(storage.get('skills', 'review').body, 'Review this change.');
  assert.equal(storage.get('workspaces', 'release').path, path.join(storage.root, '.workspace', 'release'));
  assert.deepEqual(storage.listFiles('workspaces', 'release').map(item => item.name).sort(), ['notes', 'workspace.json']);
  storage.writeFile('workspaces', 'release', 'notes/summary.md', 'Local changes');
  storage.save('skills', { id: 'review', body: 'Local review instructions' });
  assert.equal(storage.importWorkbench(source).workspaces, 0);
  write(path.join(source, '.doxie', 'skills', 'review', 'memories', 'review.md'), 'Run the tests.');
  write(path.join(workspaceSource, 'notes', 'extra.md'), 'New notes');
  const repaired = storage.importWorkbench(source);
  assert.equal(repaired.skills, 1);
  assert.equal(repaired.workspaces, 1);
  assert.equal(storage.get('skills', 'review').body, 'Local review instructions');
  assert.equal(storage.get('skills', 'review').memories[0].content, 'Run the tests.');
  assert.equal(storage.readFile('workspaces', 'release', 'notes/extra.md').content, 'New notes');
  assert.equal(storage.readFile('workspaces', 'release', 'notes/summary.md').content, 'Local changes');
  assert.equal(fs.readFileSync(path.join(workspaceSource, 'notes', 'summary.md'), 'utf8'), 'Existing notes');
  assert.throws(() => storage.importWorkbench(home), /outside the destination workbench/);
  assert.equal(fs.existsSync(path.join(source, '.claude')), false);
});

test('localizes legacy and newly selected catalog roots before permitting writes', t => {
  const { home, open } = setup(t);
  const legacyRoot = path.join(home, 'previous');
  const oldCatalog = path.join(legacyRoot, 'team');
  const external = path.join(home, 'other');
  write(path.join(oldCatalog, 'libraries', 'policy', 'library.json'), '{"name":"Team policy"}');
  write(path.join(external, 'skills', 'review', 'manifest.json'), '{"id":"review"}');
  write(path.join(external, 'skills', 'review', 'body.md'), 'Review this change.');
  const storage = open({ managed: true, legacyRoot, settings: { 'doxie.catalog.customRoots': [{ id: 'team', name: 'Team', path: 'team' }] } });
  const team = storage.listCatalogs().find(catalog => catalog.id === 'team');
  assert.equal(team.path, path.join(storage.root, '.doxie', 'catalogs', 'team'));
  assert.equal(storage.get('libraries', 'policy', 'team').name, 'Team policy');
  storage.saveCatalogs([...storage.listCatalogs(), { id: 'other', name: 'Other', path: external }]);
  assert.equal(storage.get('skills', 'review', 'other').path, path.join(storage.root, '.doxie', 'catalogs', 'other', 'agents', 'review'));
  storage.save('skills', { id: 'review', catalogId: 'other', body: 'New local body' });
  assert.equal(fs.readFileSync(path.join(external, 'skills', 'review', 'body.md'), 'utf8'), 'Review this change.');
  assert.equal(fs.existsSync(path.join(external, 'workflows')), false);
  assert.equal(fs.existsSync(path.join(oldCatalog, 'agents')), false);
});

test('repairs missing bundled files on startup while retaining managed edits', t => {
  const { home, open } = setup(t);
  const bundle = path.join(home, 'bundle', '.doxie', 'skills', 'review');
  write(path.join(bundle, 'manifest.json'), '{"id":"review","displayName":"Review"}');
  write(path.join(bundle, 'body.md'), 'Bundled body');
  const first = open({ managed: true, bundledRoot: path.join(home, 'bundle') });
  first.save('skills', { id: 'review', body: 'Local body' });
  write(path.join(bundle, 'guide.md'), 'New bundled guide');
  write(path.join(bundle, '.env'), 'private configuration');
  write(path.join(bundle, 'local-notes.md'), 'Untracked instructions');
  const restarted = open({ managed: true, bundledRoot: path.join(home, 'bundle'), bundledFiles: [
    '.doxie/skills/review/body.md', '.doxie/skills/review/guide.md', '.doxie/skills/review/.env',
  ] });
  assert.equal(restarted.get('skills', 'review').body, 'Local body');
  assert.equal(restarted.readFile('skills', 'review', 'guide.md').content, 'New bundled guide');
  assert.equal(fs.existsSync(path.join(restarted.get('skills', 'review').path, '.env')), false);
  assert.equal(fs.existsSync(path.join(restarted.get('skills', 'review').path, 'local-notes.md')), false);
});

test('updates shipped connector definitions in an existing profile and preserves extra files', t => {
  const { home, open } = setup(t);
  for (const mode of ['tracked', 'packaged']) {
    const bundledRoot = path.join(home, `bundle-${mode}`);
    const toolSource = path.join(bundledRoot, '.doxie', 'tools', 'library');
    const agentSource = path.join(bundledRoot, '.doxie', 'agents', 'review');
    const files = ['.doxie/tools/library/manifest.json', '.doxie/tools/library/body.md',
      '.doxie/agents/review/manifest.json', '.doxie/agents/review/body.md'];
    const options = { root: path.join(home, `profile-${mode}`), managed: true, bundledRoot,
      bundledFiles: mode === 'tracked' ? files : undefined };
    write(path.join(toolSource, 'manifest.json'), '{"id":"library","modes":{"create":{}}}');
    write(path.join(toolSource, 'body.md'), 'Initial connector instructions.');
    write(path.join(agentSource, 'manifest.json'), '{"id":"review"}');
    write(path.join(agentSource, 'body.md'), 'Bundled agent.');
    const first = open(options);
    const installedTool = first.get('tools', 'library');
    assert.deepEqual(Object.keys(installedTool.modes), ['create']);
    first.save('agents', { id: 'review', body: 'Local agent changes.' });
    write(path.join(installedTool.path, 'local-notes.md'), 'Local connector notes.');
    write(path.join(toolSource, 'manifest.json'), '{"id":"library","modes":{"create":{},"mount":{"fields":[{"id":"libraryId","lookup":"libraries"}]}}}');
    write(path.join(toolSource, 'body.md'), 'Updated connector instructions.');
    write(path.join(toolSource, 'local-notes.md'), 'An unlisted file.');
    write(path.join(agentSource, 'body.md'), 'Updated bundled agent.');
    const restarted = open(options);
    const updated = restarted.get('tools', 'library');
    assert.equal(updated.modes.mount.fields[0].lookup, 'libraries');
    assert.equal(updated.body, 'Updated connector instructions.');
    assert.equal(fs.readFileSync(path.join(updated.path, 'local-notes.md'), 'utf8'), 'Local connector notes.');
    assert.equal(restarted.get('agents', 'review').body, 'Local agent changes.');
  }
});

test('creates a managed workspace from a selected folder without using its metadata or altering its files', t => {
  const { home, open } = setup(t);
  const source = path.join(home, 'external-project');
  write(path.join(source, 'workspace.json'), '{"Name":"Source title","MountedLibraries":["unwanted"]}');
  write(path.join(source, 'WORKSPACE.md'), 'Existing notes');
  write(path.join(source, 'docs', 'overview.md'), 'Project overview');
  write(path.join(source, '.env'), 'private configuration');
  write(path.join(source, '.providers', 'codex', 'auth.json'), 'Provider profile fixture');
  const storage = open({ managed: true });
  const workspace = storage.save('workspaces', { id: 'project-copy', name: 'Managed copy', path: source });
  assert.equal(workspace.path, path.join(storage.root, '.workspace', 'project-copy'));
  assert.equal(workspace.name, 'Managed copy');
  assert.deepEqual(workspace.mountedLibraryIds, []);
  assert.equal(storage.readFile('workspaces', workspace.id, 'WORKSPACE.md').content, 'Existing notes');
  assert.equal(storage.readFile('workspaces', workspace.id, 'docs/overview.md').content, 'Project overview');
  assert.equal(fs.existsSync(path.join(workspace.path, '.env')), false);
  assert.equal(fs.existsSync(path.join(workspace.path, '.providers')), false);
  assert.equal(fs.readFileSync(path.join(source, 'workspace.json'), 'utf8'), '{"Name":"Source title","MountedLibraries":["unwanted"]}');
  assert.equal(fs.existsSync(path.join(source, 'memory')), false);
  assert.equal(storage.save('workspaces', { id: workspace.id, name: 'Renamed', path: workspace.path }).name, 'Renamed');
  write(path.join(source, 'memory'), 'Project file');
  storage.save('workspaces', { id: 'copy-with-memory-file', name: 'Another copy', path: source });
  assert.equal(storage.readFile('workspaces', 'copy-with-memory-file', 'memory').content, 'Project file');
});

test('keeps connectors and internal builders out of the agent catalog and migrates a legacy agent on save', t => {
  const { home, open } = setup(t);
  const root = path.join(home, 'project');
  const legacyAgent = path.join(root, '.doxie', 'skills', 'review');
  write(path.join(legacyAgent, 'manifest.json'), '{"id":"review"}');
  write(path.join(legacyAgent, 'body.md'), 'Review this change.');
  write(path.join(root, '.doxie', 'agents', 'review.md'), '---\nname: review\nmodel: inherit\n---\n\nReview the diff.');
  write(path.join(root, '.doxie', 'skills', 'aggregate', 'manifest.json'), '{"id":"aggregate"}');
  write(path.join(root, '.doxie', 'skills', 'aggregate', 'body.md'), 'Old connector.');
  write(path.join(root, '.doxie', 'tools', 'aggregate', 'manifest.json'), '{"id":"aggregate","kind":"workflow"}');
  write(path.join(root, '.doxie', 'tools', 'aggregate', 'body.md'), 'Native connector.');
  write(path.join(root, '.doxie', 'agents', 'prompt', 'manifest.json'), '{"id":"prompt","execution":{"providerId":"codex","model":"legacy-prompt"}}');
  write(path.join(root, '.doxie', 'agents', 'prompt', 'body.md'), 'Previous Prompt instructions.');
  write(path.join(root, '.doxie', 'tools', 'prompt', 'manifest.json'), '{"id":"prompt","kind":"workflow","modelBacked":true}');
  write(path.join(root, '.doxie', 'tools', 'prompt', 'body.md'), 'Run the transformation.');
  write(path.join(root, '.claude', 'skills', 'prompt', 'SKILL.md'), 'Legacy generated Prompt.');
  write(path.join(root, '.claude', 'skills', 'prompt', 'doxie', 'generated'), 'Doxie-generated');
  const storage = open({ managed: true });
  storage.save('agents', { id: 'workflow-builder', body: 'Author a workflow.' });
  assert.deepEqual(storage.list('agents').map(agent => agent.id), ['review']);
  assert.deepEqual(storage.list('tools').map(tool => tool.id), ['aggregate', 'prompt']);
  assert.equal(storage.get('agents', 'workflow-builder').body, 'Author a workflow.');
  assert.equal(storage.get('agents', 'prompt'), null);
  assert.equal(storage.get('tools', 'prompt').modelBacked, true);
  assert.deepEqual(storage.getDispatchAgent('prompt').execution, { providerId: 'codex', model: 'legacy-prompt' });
  const promptExecution = { providerId: 'claude-code', model: 'prompt-new' };
  storage.setConnectorExecution('prompt', promptExecution);
  assert.deepEqual(storage.getDispatchAgent('prompt').execution, promptExecution);
  assert.equal(storage.getDispatchAgent('prompt').path, path.join(root, '.doxie', 'tools', 'prompt'));
  assert.equal(storage.getDispatchAgent('prompt').body, 'Run the transformation.');
  storage.setConnectorExecution('prompt', null);
  assert.equal(storage.get('tools', 'prompt').execution, null);
  assert.throws(() => storage.setConnectorExecution('aggregate', promptExecution), /does not support/);
  assert.equal(storage.get('agents', 'review').path, legacyAgent);
  const execution = { providerId: 'codex', model: 'gpt-5.5' };
  const agent = storage.save('agents', { id: 'review', execution });
  assert.deepEqual(agent.execution, execution);
  assert.equal(agent.path, path.join(root, '.doxie', 'agents', 'review'));
  assert.equal(agent.agentPath, path.join(agent.path, 'agent.md'));
  assert.equal(agent.model, 'inherit');
  assert.equal(agent.body, 'Review this change.');
  assert.throws(() => storage.save('agents', { id: 'review', execution: { model: 'orphan-model' } }), /Select a provider/);
  assert.deepEqual(storage.get('agents', 'review').execution, execution);
  assert.equal(storage.get('skills', 'aggregate'), null);
  assert.throws(() => storage.save('agents', { id: 'aggregate', body: 'Override' }), /built-in connector/);
  assert.equal(fs.existsSync(path.join(root, '.claude', 'skills', 'aggregate')), false);
  assert.equal(fs.existsSync(path.join(root, '.claude', 'skills', 'prompt')), false);
  assert.equal(fs.existsSync(path.join(root, '.claude', 'skills', 'workflow-builder')), false);
});

test('creates a native library atomically and refuses duplicate IDs', t => {
  const { open } = setup(t);
  const storage = open({ managed: true });
  assert.throws(() => storage.createLibrary({ id: 'bad-library', memories: [{ id: 'same', content: 'first' }, { id: 'same', content: 'second' }] }), /duplicate/);
  assert.equal(storage.get('libraries', 'bad-library'), null);
  assert.throws(() => storage.createLibrary({ id: 'invalid-path', memories: [{ relativePath: 'docs/review.md', content: 'Ignored' }] }), /under memory/);
  assert.throws(() => storage.createLibrary({ id: 'duplicate-path', memories: [
    { relativePath: 'memories/review.md', content: 'First' }, { relativePath: 'Memories/Review.MD', content: 'Second' },
  ] }), /duplicate/);
  const binary = Buffer.from('Preserve UTF-8 text.\r\n');
  const result = storage.createLibrary({ id: 'policy', name: 'Policy', memories: [
    { id: 'review', content: 'Review changes.' }, { relativePath: 'memories/engineering/checklist.md', content: binary },
  ] });
  assert.equal(result.memories.find(memory => memory.relativePath === 'memory/review.md').content, 'Review changes.');
  assert.throws(() => storage.createLibrary({ id: 'policy' }), /already exists/);
  assert.equal(storage.get('libraries', 'policy').memories.length, 2);
  assert.deepEqual(fs.readFileSync(path.join(result.path, 'memories', 'engineering', 'checklist.md')), binary);
});
