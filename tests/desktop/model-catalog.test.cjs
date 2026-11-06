const assert = require('node:assert/strict');
const { mkdir, mkdtemp, rm, writeFile } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const { dirname, join } = require('node:path');
const test = require('node:test');
const { discoverModels, localProviderBaseUrl } = require('../../src/desktop/model-catalog.cjs');

async function fixture(t) {
  const root = await mkdtemp(join(tmpdir(), 'doxie-model-catalog-test-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  async function put(relative, content) {
    const file = join(root, relative);
    await mkdir(dirname(file), { recursive: true });
    await writeFile(file, typeof content === 'string' ? content : JSON.stringify(content));
  }
  return { root, put };
}

test('uses the selected Codex cache, omits hidden and invalid entries, and strips credentials', async t => {
  const { root, put } = await fixture(t);
  const observedAt = '2026-11-06T12:12:23.466564800Z';
  const secret = ['sk', 'proj', 'this-must-not-be-exported-in-metadata'].join('-');
  await put('.providers/codex/models_cache.json', {
    fetched_at: observedAt, identity: { authToken: secret }, models: [
      { slug: 'doxie/producer-v1', display_name: 'Fixture producer', visibility: 'list' },
      { slug: 'private-auxiliary', display_name: 'Hidden', visibility: 'hide' },
      { slug: 'doxie/producer-v1', display_name: 'Duplicate', visibility: 'list' },
      { slug: 'beta\ninvalid', display_name: 'Invalid', visibility: 'list' },
      { slug: secret, visibility: 'list' },
      { slug: 'real-model', display_name: `Disallowed label ${secret}`, visibility: 'list' },
    ],
  });
  await put('.codex/models_cache.json', { models: [{ slug: 'host-only', visibility: 'list' }] });
  const options = { providerId: 'codex', homeDirectory: root, environment: { CODEX_HOME: join(root, '.providers', 'codex') } };
  const catalog = await discoverModels(options);
  assert.equal(catalog.status, 'ready');
  assert.equal(catalog.source, 'codex-workbench-cache');
  assert.equal(catalog.observedAt, observedAt);
  assert.deepEqual(catalog.models, [
    { id: 'doxie/producer-v1', label: 'Fixture producer', source: 'codex-workbench-cache' },
    { id: 'real-model', label: 'real-model', source: 'codex-workbench-cache' },
  ]);
  assert(!JSON.stringify(catalog).includes(secret));
});

test('distinguishes an explicit Codex catalog, a host cache and an unavailable profile', async t => {
  const { root, put } = await fixture(t);
  await put('.codex/models_cache.json', { models: [{ slug: 'host-only', visibility: 'list' }] });
  const options = { providerId: 'codex', homeDirectory: root, environment: { CODEX_HOME: join(root, 'isolated') } };
  assert.equal((await discoverModels(options)).source, 'codex-host-cache');
  assert.equal((await discoverModels({ ...options, useHostProfiles: false })).status, 'unavailable');
  await put('isolated/config.toml', 'model_catalog_json = "custom-models.json"\n');
  await put('isolated/custom-models.json', { models: [{ slug: 'declared-only', display_name: 'Declared', visibility: 'list' }] });
  assert.deepEqual((await discoverModels(options)).models, [{ id: 'declared-only', label: 'Declared', source: 'codex-workbench-catalog' }]);
  await put('isolated/custom-models.json', { models: [] });
  assert.equal((await discoverModels(options)).status, 'empty');
  await put('isolated/config.toml', `model_catalog_json = '${'\\\\?\\'}${join(root, 'isolated', 'custom-models.json')}'\n`);
  assert.equal((await discoverModels(options)).source, 'codex-host-cache');
});

test('uses configured host profile directories only when fallback is enabled', async t => {
  const { root, put } = await fixture(t);
  await put('custom-codex/models_cache.json', { models: [{ slug: 'codex-host' }] });
  await put('custom-claude/.claude.json', { additionalModelOptionsCache: [{ value: 'claude-host' }] });
  const options = {
    homeDirectory: root, executable: process.execPath,
    environment: { CODEX_HOME: join(root, 'app-codex'), CLAUDE_CONFIG_DIR: join(root, 'app-claude') },
    hostEnvironment: { CODEX_HOME: join(root, 'custom-codex'), CLAUDE_CONFIG_DIR: join(root, 'custom-claude') },
  };
  const mock = { claudeProbe: async () => null };
  for (const [providerId, id] of [['codex', 'codex-host'], ['claude-code', 'claude-host']]) {
    assert.deepEqual((await discoverModels({ ...options, providerId }, mock)).models.map(row => row.id), [id]);
    assert.equal((await discoverModels({ ...options, providerId, useHostProfiles: false }, mock)).status, 'unavailable');
  }
});

test('merges locally configured Claude choices with advertised CLI choices and respects an exclusive lineup', async t => {
  const { root, put } = await fixture(t);
  await put('isolated/settings.json', { model: 'gateway-model', env: { ANTHROPIC_API_KEY: 'do-not-return' } });
  await put('isolated/.claude.json', { additionalModelOptionsCache: [{ value: 'custom-cached', displayName: 'Cached' }] });
  const options = { providerId: 'claude-code', homeDirectory: root, workspaceRoot: root, executable: process.execPath,
    environment: { CLAUDE_CONFIG_DIR: join(root, 'isolated') } };
  let queried = 0;
  const transport = { claudeProbe: async (_executable, _environment, workspaceRoot) => {
    assert.equal(workspaceRoot, root);
    queried++;
    return [{ id: 'sonnet', label: 'Sonnet', source: 'claude-cli-advertised' }];
  } };
  const catalog = await discoverModels(options, transport);
  assert.equal(catalog.status, 'ready');
  assert.deepEqual(catalog.models.map(({ id, source }) => [id, source]), [
    ['gateway-model', 'claude-workbench-settings'], ['custom-cached', 'claude-workbench-cache'], ['sonnet', 'claude-cli-advertised'],
  ]);
  assert(!JSON.stringify(catalog).includes('do-not-return'));
  await put('isolated/settings.json', { modelPicker: { replaceBuiltInOptions: true, options: [{ model: 'only-this', label: 'Only this' }] } });
  const exclusive = await discoverModels(options, transport);
  assert.deepEqual(exclusive.models.map(row => row.id), ['only-this']);
  assert.equal(queried, 1);
});

test('queries only fixed loopback list endpoints and selects usable local model identifiers', async () => {
  const urls = [];
  const responses = {
    'http://127.0.0.1:11434/api/tags': { models: [
      { name: 'qwen3:8b' }, { model: 'gemma4:cloud' }, { model: 'upstream', remote_host: 'https://ignored.invalid' },
    ] },
    'http://127.0.0.1:1234/v1/models': { data: [{ id: 'local-loaded' }, { id: 'text-embed' }, { id: 'listed-without-metadata' }] },
    'http://127.0.0.1:1234/api/v1/models': { models: [
      { type: 'llm', key: 'author/model', display_name: 'Loaded', loaded_instances: [{ id: 'local-loaded' }] },
      { type: 'embedding', key: 'text-embed' }, { type: 'llm', key: 'downloaded-but-not-exposed' },
    ] },
  };
  const transport = { requestJson: async url => { urls.push(url); return responses[url]; } };
  const environment = { OLLAMA_HOST: 'https://external.invalid', LM_STUDIO_API_KEY: 'never-return' };
  const ollama = await discoverModels({ providerId: 'ollama', environment }, transport);
  assert.deepEqual(ollama.models.map(row => row.id), ['qwen3:8b']);
  const lmstudio = await discoverModels({ providerId: 'lmstudio', environment }, transport);
  assert.deepEqual(lmstudio.models.map(row => [row.id, row.label]), [['local-loaded', 'Loaded'], ['listed-without-metadata', 'listed-without-metadata']]);
  assert.deepEqual(urls, Object.keys(responses));
  assert.equal(localProviderBaseUrl('lmstudio'), 'http://127.0.0.1:1234');
  assert.equal(localProviderBaseUrl('codex'), null);
  const unavailable = await discoverModels({ providerId: 'ollama' }, { requestJson: async () => { throw new Error('secret error'); } });
  assert.equal(unavailable.status, 'unavailable');
  assert(!unavailable.message.includes('secret error'));
});
