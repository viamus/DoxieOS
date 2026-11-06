const assert = require('node:assert/strict');
const { mkdir, mkdtemp, readFile, rm, writeFile } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { test } = require('node:test');
const { createMcp } = require('../../src/desktop/mcp.cjs');
const { createStorage } = require('../../src/desktop/storage.cjs');
const { providerEnvironment } = require('../../src/desktop/providers.cjs');

test('reconciles old Claude MCP entries and preserves authored entries and masked credentials', async () => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-mcp-'));
  const previousHome = process.env.CODEX_HOME;
  process.env.CODEX_HOME = join(root, 'codex-home');
  const storage = createStorage({ root, databasePath: ':memory:' });
  try {
    const outsideConfig = join(process.env.CODEX_HOME, 'config.toml');
    const outsideText = 'model = "external-default"\n';
    await mkdir(process.env.CODEX_HOME);
    await writeFile(outsideConfig, outsideText);
    const environment = providerEnvironment(storage, { CODEX_HOME: process.env.CODEX_HOME, CLAUDE_CONFIG_DIR: process.env.CODEX_HOME });
    const localConfig = join(root, '.providers', 'codex', 'config.toml');
    assert.equal(environment.CODEX_HOME, join(root, '.providers', 'codex'));
    assert.equal(environment.CLAUDE_CONFIG_DIR, join(root, '.providers', 'claude'));
    const legacy = {
      id: 'doxie-test-server', transport: 'stdio', command: 'example-command', arguments: '--port 13',
      environment: 'TEST_KEY=example-value', providers: ['claude-code'], enabled: true,
    };
    const file = join(root, '.mcp.json');
    await writeFile(file, JSON.stringify({ mcpServers: {
      [legacy.id]: { command: legacy.command, args: ['--port', '13'], env: { TEST_KEY: 'example-value' } },
      'authored-entry': { command: 'custom' },
    } }));
    storage.setSetting('provider_mcp_servers', [legacy]);
    const mcp = createMcp(storage);
    assert.ok(!JSON.stringify(mcp.publicList()).includes('example-value'));
    mcp.save([{ ...mcp.publicList()[0], arguments: '--port 22', providers: ['claude-code', 'codex'] }]);
    let updated = JSON.parse(await readFile(file, 'utf8')).mcpServers;
    assert.deepEqual(updated[legacy.id].args, ['--port', '22']);
    assert.equal(updated[legacy.id].env.TEST_KEY, 'example-value');
    assert.match(await readFile(localConfig, 'utf8'), /BEGIN DOXIE MCP SERVERS/);
    await rm(localConfig);
    mcp.refresh();
    assert.match(await readFile(localConfig, 'utf8'), /BEGIN DOXIE MCP SERVERS/);
    mcp.save([]);
    updated = JSON.parse(await readFile(file, 'utf8')).mcpServers;
    assert.deepEqual(updated, { 'authored-entry': { command: 'custom' } });
    assert.doesNotMatch(await readFile(localConfig, 'utf8'), /BEGIN DOXIE MCP SERVERS/);
    assert.equal(await readFile(outsideConfig, 'utf8'), outsideText);
  } finally {
    if (previousHome === undefined) delete process.env.CODEX_HOME;
    else process.env.CODEX_HOME = previousHome;
    storage.close();
    await rm(root, { recursive: true, force: true });
  }
});
