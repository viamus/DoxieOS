const assert = require('node:assert/strict');
const { mkdir, mkdtemp, readFile, rm, writeFile } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { test } = require('node:test');
const toml = require('@iarna/toml');
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

test('normalizes quoted executable paths on save and refresh while preserving arguments and credentials', async () => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-mcp-command-'));
  const storage = createStorage({ root, databasePath: ':memory:' });
  try {
    const command = 'C:\\Program Files\\Doxie Tools\\node.exe';
    const args = ['script with spaces.js', "'literal argument'", '--mode', 'review'];
    const environment = { TEST_TOKEN: 'fixture-value' };
    const stdio = { id: 'quoted-tool', transport: 'stdio', command: `  '${command}'  `, arguments: args,
      environment, providers: ['codex', 'claude-code'], enabled: true, extraJson: '{"startup_timeout_sec":31}' };
    const remote = { id: 'remote-tool', transport: 'http', url: 'https://example.test/mcp',
      providers: ['codex', 'claude-code'], enabled: true, headers: { Authorization: 'Bearer fixture-value' } };
    const mcp = createMcp(storage);
    mcp.save([stdio, remote]);
    const saved = JSON.parse(storage.getSetting('provider_mcp_servers'));
    assert.equal(saved[0].command, command);
    assert.deepEqual(saved[0].arguments, args);
    assert.deepEqual(saved[0].environment, environment);
    const codexFile = join(root, '.providers', 'codex', 'config.toml');
    const claudeFile = join(root, '.mcp.json');
    let codex = toml.parse(await readFile(codexFile, 'utf8')).mcp_servers;
    let claude = JSON.parse(await readFile(claudeFile, 'utf8')).mcpServers;
    for (const servers of [codex, claude]) {
      assert.equal(servers['quoted-tool'].command, command);
      assert.deepEqual(servers['quoted-tool'].args, args);
      assert.deepEqual(servers['quoted-tool'].env, environment);
      assert.equal(servers['quoted-tool'].startup_timeout_sec, 31);
    }
    assert.equal(codex['remote-tool'].http_headers.Authorization, 'Bearer fixture-value');
    assert.equal(claude['remote-tool'].headers.Authorization, 'Bearer fixture-value');

    // Existing registries may still contain shell-style quotes from an older import.
    storage.setSetting('provider_mcp_servers', saved.map(item => item.id === stdio.id ? { ...item, command: `"${command}"` } : item));
    mcp.refresh();
    codex = toml.parse(await readFile(codexFile, 'utf8')).mcp_servers;
    claude = JSON.parse(await readFile(claudeFile, 'utf8')).mcpServers;
    assert.equal(codex[stdio.id].command, command);
    assert.equal(claude[stdio.id].command, command);
    assert.deepEqual(codex[stdio.id].args, args);
    for (const empty of ['""', "''"]) assert.throws(() => mcp.save([{ ...stdio, command: empty }]), /needs a command/);
  } finally {
    storage.close();
    await rm(root, { recursive: true, force: true });
  }
});

test('imports executable commands without literal shell quotes or duplicate provider entries', async () => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-mcp-import-command-'));
  const previous = Object.fromEntries(['HOME', 'USERPROFILE', 'CODEX_HOME', 'CLAUDE_CONFIG_DIR'].map(key => [key, process.env[key]]));
  const storage = createStorage({ root: join(root, 'workbench'), databasePath: ':memory:' });
  try {
    process.env.HOME = root;
    process.env.USERPROFILE = root;
    process.env.CODEX_HOME = join(root, 'installed-codex');
    process.env.CLAUDE_CONFIG_DIR = join(root, 'installed-claude');
    await mkdir(process.env.CODEX_HOME);
    const command = 'C:\\Program Files\\Doxie Tools\\node.exe';
    const args = ['script with spaces.js'];
    const environment = { TEST_TOKEN: 'fixture-value' };
    const codexFile = join(process.env.CODEX_HOME, 'config.toml');
    const codexText = toml.stringify({ mcp_servers: { imported: { command: `'${command}'`, args, env: environment } } });
    const claudeFile = join(root, '.claude.json');
    const claudeText = JSON.stringify({ mcpServers: { imported: { command: `"${command}"`, args, env: environment } } });
    await writeFile(codexFile, codexText);
    await writeFile(claudeFile, claudeText);
    const imported = createMcp(storage).importGlobals();
    assert.equal(imported.length, 1);
    assert.equal(imported[0].command, command);
    assert.deepEqual(imported[0].arguments, args);
    assert.deepEqual(imported[0].providers, ['claude-code', 'codex']);
    const generated = toml.parse(await readFile(join(storage.root, '.providers', 'codex', 'config.toml'), 'utf8')).mcp_servers.imported;
    assert.equal(generated.command, command);
    assert.deepEqual(generated.args, args);
    assert.deepEqual(generated.env, environment);
    assert.equal(await readFile(codexFile, 'utf8'), codexText);
    assert.equal(await readFile(claudeFile, 'utf8'), claudeText);
  } finally {
    for (const [key, value] of Object.entries(previous)) {
      if (value === undefined) delete process.env[key]; else process.env[key] = value;
    }
    storage.close();
    await rm(root, { recursive: true, force: true });
  }
});
