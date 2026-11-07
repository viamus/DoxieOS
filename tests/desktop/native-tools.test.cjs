const assert = require('node:assert/strict');
const { mkdir, mkdtemp, readFile, rm, stat, writeFile } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { test } = require('node:test');
const { createStorage } = require('../../src/desktop/storage.cjs');
const { createWorkflows } = require('../../src/desktop/workflows.cjs');

test('native library copies declared bytes and native sandbox expires without a provider', async () => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-tools-'));
  const storage = createStorage({ root, databasePath: ':memory:' });
  const engine = createWorkflows({ storage, agents: { start() { assert.fail('A native tool invoked a provider'); } } });
  try {
    const workspace = storage.save('workspaces', { id: 'source', name: 'Source' });
    await mkdir(join(workspace.path, 'notes'));
    const content = Buffer.from('Exact Markdown bytes.\r\n\r\n    Preserve indentation.\n');
    await writeFile(join(workspace.path, 'notes', 'source.md'), content);
    storage.save('workflows', { id: 'native-tools', name: 'Native tools', workspaceId: workspace.id,
      nodes: [
        { id: 'allocate', kind: 'Tool', toolId: 'sandbox', toolMode: 'new', inputs: { tag: 'native' } },
        { id: 'capture', kind: 'Tool', toolId: 'library', toolMode: 'from-files', inputs: { id: 'traceable', paths: 'notes/source.md' } },
      ],
      edges: [{ fromNodeId: 'allocate', toNodeId: 'capture' }],
    });
    const result = await engine.wait(engine.run('native-tools').id);
    assert.equal(result.status, 'Succeeded', JSON.stringify(result.nodeRuns));
    const library = storage.get('libraries', 'traceable');
    assert.deepEqual(await readFile(join(library.path, 'memories', 'workspace-1', 'notes', 'source.md')), content);
    const receipt = JSON.parse(await readFile(join(storage.roots.runs, result.id, 'allocate', 'sandbox.json'), 'utf8'));
    assert.equal(receipt.expires, 'workflow-end');
    await assert.rejects(stat(receipt.directory), { code: 'ENOENT' });
  } finally {
    await engine.close();
    storage.close();
    await rm(root, { recursive: true, force: true });
  }
});
