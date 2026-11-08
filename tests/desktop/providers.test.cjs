const assert = require('node:assert/strict');
const { mkdir, mkdtemp, readFile, rm, writeFile } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { test } = require('node:test');
const toml = require('@iarna/toml');
const { headlessArgs, interactiveCodexArgs, providerFor, settingsFor } = require('../../src/desktop/providers.cjs');

test('saved Codex agents share installed sign-in, preserve Doxie tools and ignore inherited API routing', async () => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-provider-auth-'));
  const previous = Object.fromEntries(['CODEX_HOME', 'OPENAI_API_KEY', 'OPENAI_BASE_URL', 'CODEX_API_KEY'].map(key => [key, process.env[key]]));
  try {
    const installed = join(root, 'installed');
    const isolated = join(root, '.providers', 'codex');
    await mkdir(installed);
    await mkdir(isolated, { recursive: true });
    const hostConfig = 'model_provider = "unrelated-gateway"\ncli_auth_credentials_store = "file"\n[mcp_servers.unrelated]\nurl = "http://127.0.0.1:1"\n';
    await writeFile(join(installed, 'config.toml'), hostConfig);
    await writeFile(join(installed, 'auth.json'), '{}');
    const tools = {
      reference: { command: 'node', args: ['tool with spaces.js'], env: { TOKEN: 'fixture-value' }, startup_timeout_sec: 12 },
      remote: { url: 'https://example.test/mcp?one=1&two=2', http_headers: { Authorization: 'Bearer fixture' } },
      disabled: { url: 'https://example.test/disabled', enabled: false },
    };
    await writeFile(join(isolated, 'config.toml'), toml.stringify({ mcp_servers: tools }));
    process.env.CODEX_HOME = installed;
    process.env.OPENAI_API_KEY = 'inherited-key';
    process.env.CODEX_API_KEY = 'inherited-key';
    process.env.OPENAI_BASE_URL = 'http://127.0.0.1:1';
    const storage = { root, getSetting: key => key === 'desktop.providers' ? { codex: { executable: process.execPath } } : null };
    const provider = providerFor(storage, 'codex', { model: 'saved-agent-model', effort: 'medium' });
    assert.equal(provider.environment.CODEX_HOME, installed);
    assert.equal(provider.localCodex, true);
    for (const key of ['OPENAI_API_KEY', 'OPENAI_BASE_URL', 'CODEX_API_KEY']) assert.equal(provider.environment[key], undefined);
    assert.equal(settingsFor(storage).providers.codex.authenticated, true);
    const args = headlessArgs(provider);
    assert.ok(args.includes('--ignore-user-config'));
    assert.ok(args.includes("forced_login_method='chatgpt'"));
    assert.ok(args.includes("cli_auth_credentials_store='file'"));
    assert.equal(args[args.indexOf('--model') + 1], 'saved-agent-model');
    const config = args.find(value => value.startsWith('mcp_servers='));
    assert.deepEqual(toml.parse(config).mcp_servers, tools);
    assert.ok(!config.includes('unrelated'));
    const interactive = interactiveCodexArgs(provider);
    assert.ok(!interactive.includes('--ignore-user-config'));
    assert.ok(interactive.includes("model_provider='openai'"));
    const interactiveTools = Object.fromEntries(Object.entries(tools).map(([id, value]) => [id, { ...value, enabled: value.enabled !== false }]));
    assert.deepEqual(toml.parse(interactive.find(value => value.startsWith('mcp_servers='))).mcp_servers,
      { unrelated: { enabled: false }, ...interactiveTools });
    const collision = interactiveCodexArgs({ ...provider, installedMcpIds: ['remote', 'doxie_remote'] });
    const collisionTools = toml.parse(collision.find(value => value.startsWith('mcp_servers='))).mcp_servers;
    assert.deepEqual(collisionTools.remote, { enabled: false });
    assert.deepEqual(collisionTools.doxie_remote, { enabled: false });
    assert.deepEqual(collisionTools.doxie_remote_2, { ...tools.remote, enabled: true });
    assert.equal(await readFile(join(installed, 'config.toml'), 'utf8'), hostConfig);
    await assert.rejects(readFile(join(isolated, 'auth.json')), /ENOENT/);
    const builder = providerFor(storage, 'codex', { localCodex: true, mcp: false });
    assert.equal(builder.environment.CODEX_HOME, provider.environment.CODEX_HOME);
    assert.ok(!headlessArgs(builder).some(value => value.startsWith('mcp_servers=')));
    // Settings authentication calls providerFor without builder-only options.
    assert.equal(providerFor(storage, 'codex').environment.CODEX_HOME, installed);

    const explicit = providerFor(storage, 'codex', { env: { OPENAI_API_KEY: 'explicit-key' } });
    assert.equal(explicit.localCodex, false);
    assert.equal(explicit.environment.CODEX_HOME, isolated);
    assert.equal(explicit.environment.OPENAI_API_KEY, 'explicit-key');
    assert.ok(!headlessArgs(explicit).includes("forced_login_method='chatgpt'"));
    for (const id of ['ollama', 'lmstudio']) {
      const local = providerFor(storage, id, { model: 'local-model' });
      assert.equal(local.localCodex, false);
      assert.equal(local.environment.CODEX_HOME, isolated);
      assert.ok(!headlessArgs(local).includes("forced_login_method='chatgpt'"));
      assert.deepEqual(interactiveCodexArgs(local), []);
      assert.ok(headlessArgs(local).some(value => value.endsWith('.requires_openai_auth=false')));
    }
    storage.memory = {};
    for (const options of [{}, { localCodex: true, mcp: false }, { env: { OPENAI_API_KEY: 'explicit-key' } }]) {
      const native = providerFor(storage, 'codex', options);
      const servers = toml.parse(headlessArgs(native).find(value => value.startsWith('mcp_servers='))).mcp_servers;
      assert.equal(servers.doxie_library_memory.command, process.execPath);
      assert.equal(servers.doxie_library_memory.env.ELECTRON_RUN_AS_NODE, '1');
      assert.equal(servers.doxie_library_memory.enabled, true);
      if (options.mcp === false) assert.deepEqual(Object.keys(servers), ['doxie_library_memory']);
    }
    for (const id of ['codex', 'ollama', 'lmstudio']) {
      const provider = providerFor(storage, id, { model: 'local-model', localCodex: false });
      const servers = toml.parse(interactiveCodexArgs(provider).find(value => value.startsWith('mcp_servers='))).mcp_servers;
      assert.equal(servers.doxie_library_memory.command, process.execPath, `${id} consoles must carry Brain explicitly`);
      assert.equal(servers.doxie_library_memory.enabled, true);
      assert.equal(provider.nativeMemory.required, true);
    }
  } finally {
    for (const [key, value] of Object.entries(previous)) {
      if (value === undefined) delete process.env[key]; else process.env[key] = value;
    }
    await rm(root, { recursive: true, force: true });
  }
});
