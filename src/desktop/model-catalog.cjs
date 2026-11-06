const { mkdir, mkdtemp, readFile, rm, stat } = require('node:fs/promises');
const { get } = require('node:http');
const { homedir, tmpdir } = require('node:os');
const { basename, isAbsolute, join, parse, relative, resolve, sep } = require('node:path');
const toml = require('@iarna/toml');
const { normalizeModel } = require('./execution.cjs');
const { executablePath, launch, killTree } = require('./processes.cjs');
const { safePath } = require('./storage.cjs');

const localProviders = new Map([
  ['ollama', 'http://127.0.0.1:11434'],
  ['lmstudio', 'http://127.0.0.1:1234'],
]);
const maxProfileBytes = 8 * 1024 * 1024;
const maxMetadataBytes = 1024 * 1024;
const maxModels = 256;

function localProviderBaseUrl(providerId) {
  return localProviders.get(providerId) ?? null;
}

function makeResult(providerId, source, models, message, observedAt) {
  return {
    providerId, source, models: models ?? [],
    status: models?.length ? 'ready' : models ? 'empty' : 'unavailable',
    ...(message ? { message } : {}), ...(observedAt ? { observedAt } : {}),
  };
}

function modelRow(value, displayName, source) {
  let id;
  try { id = normalizeModel(value); }
  catch { return null; }
  const credential = /(?:\bsk-(?:ant-|proj-)?[a-z0-9_-]{24,}|\bgithub_pat_[a-z0-9_]+|\bghp_[a-z0-9]+|\beyJ[a-z0-9_-]{24,})/i;
  if (!id || credential.test(id)) return null;
  const label = typeof displayName === 'string' && !credential.test(displayName)
    ? displayName.replace(/[\p{Cc}\p{Cf}]/gu, ' ').trim().slice(0, 160) : '';
  return { id, label: label || id, source };
}

function uniqueModels(rows) {
  const values = new Map();
  for (const row of rows) {
    if (row && !values.has(row.id)) values.set(row.id, row);
    if (values.size === maxModels) break;
  }
  return [...values.values()];
}

async function readProfile(file, parse) {
  try {
    const info = await stat(file);
    if (!info.isFile() || info.size > maxProfileBytes) return null;
    const data = await readFile(file, 'utf8');
    if (Buffer.byteLength(data) > maxProfileBytes) return null;
    return parse(data.replace(/^\uFEFF/, ''));
  } catch { return null; }
}

function codexRows(catalog, source) {
  const rows = Array.isArray(catalog) ? catalog : catalog?.models;
  if (!Array.isArray(rows)) return null;
  return uniqueModels(rows.filter(row => row && (!row.visibility || row.visibility === 'list'))
    .map(row => modelRow(row.slug, row.display_name, source)));
}

function declaredCatalogPath(directory, declared) {
  if (typeof declared !== 'string' || !declared.trim() || /^[\\/]{2}|^[\\/](?:device|globalroot|\?\?)[\\/]/i.test(declared)) return null;
  const candidate = isAbsolute(declared) ? declared : resolve(directory, declared);
  if (/^[\\/]{2}/.test(candidate)) return null;
  try {
    const inProfile = relative(directory, candidate);
    if (inProfile && !isAbsolute(inProfile) && inProfile !== '..' && !inProfile.startsWith(`..${sep}`)) {
      return safePath(directory, inProfile);
    }
    const base = parse(candidate).root;
    return safePath(base, relative(base, candidate));
  } catch { return null; }
}

async function codexProfile(directory, scope) {
  const config = await readProfile(join(directory, 'config.toml'), toml.parse);
  const file = declaredCatalogPath(directory, config?.model_catalog_json);
  if (file) {
    const source = `codex-${scope}-catalog`;
    const models = codexRows(await readProfile(file, JSON.parse), source);
    if (models) return makeResult('codex', source, models, 'Models declared in the local Codex catalog.');
  }
  const source = `codex-${scope}-cache`;
  const cache = await readProfile(join(directory, 'models_cache.json'), JSON.parse);
  const models = codexRows(cache, source);
  if (models) {
    const observedAt = typeof cache?.fetched_at === 'string' && Number.isFinite(Date.parse(cache.fetched_at)) ? cache.fetched_at : null;
    const message = scope === 'host' ? 'Models cached by the installed Codex profile. Access may differ in the Doxie profile.'
      : 'Models cached by the Doxie Codex profile. The cache does not verify current account access.';
    return makeResult('codex', source, models, message, observedAt);
  }
  const configSource = `codex-${scope}-settings`;
  const configured = modelRow(config?.model, null, configSource);
  return configured ? makeResult('codex', configSource, [configured], 'A model is configured locally. No model catalog was found.') : null;
}

async function discoverCodex({ environment, homeDirectory, hostEnvironment, useHostProfiles }) {
  const host = resolve(hostEnvironment.CODEX_HOME || join(homeDirectory, '.codex'));
  const selected = resolve(environment.CODEX_HOME || host);
  const primary = await codexProfile(selected, selected === host ? 'host' : 'workbench');
  if (primary && !primary.source.endsWith('-settings')) return primary;
  const fallback = useHostProfiles && selected !== host ? await codexProfile(host, 'host') : null;
  return (fallback?.models.length && fallback) || primary || fallback
    || makeResult('codex', null, null, 'No local Codex model catalog is available.');
}

function configuredClaude(settings, environment, source) {
  const overrides = settings?.env ?? {};
  const custom = environment.ANTHROPIC_CUSTOM_MODEL_OPTION ?? overrides.ANTHROPIC_CUSTOM_MODEL_OPTION;
  const customName = environment.ANTHROPIC_CUSTOM_MODEL_OPTION_NAME ?? overrides.ANTHROPIC_CUSTOM_MODEL_OPTION_NAME;
  const lineup = Array.isArray(settings?.modelPicker?.options) ? settings.modelPicker.options : [];
  const allowed = Array.isArray(settings?.availableModels) ? settings.availableModels : [];
  return [modelRow(environment.ANTHROPIC_MODEL ?? overrides.ANTHROPIC_MODEL ?? settings?.model, null, source),
    ...lineup.map(row => modelRow(row?.model, row?.label, source)),
    ...(settings?.modelPicker?.replaceBuiltInOptions ? [] : [modelRow(custom, customName, source), ...allowed.map(id => modelRow(id, null, source))])];
}

async function claudeProfile(directory, scope, environment, globalProfile) {
  const settings = await readProfile(join(directory, 'settings.json'), JSON.parse);
  const profile = await readProfile(globalProfile ?? join(directory, '.claude.json'), JSON.parse);
  const settingsSource = `claude-${scope}-settings`;
  const cacheSource = `claude-${scope}-cache`;
  const cached = Array.isArray(profile?.additionalModelOptionsCache) ? profile.additionalModelOptionsCache : [];
  const models = uniqueModels([
    ...configuredClaude(settings, environment, settingsSource),
    ...(settings?.modelPicker?.replaceBuiltInOptions ? [] : cached.map(row => modelRow(row?.value ?? row?.model ?? row?.id, row?.displayName ?? row?.label, cacheSource))),
  ]);
  const exclusive = settings?.modelPicker?.replaceBuiltInOptions === true
    || (Array.isArray(settings?.availableModels) && settings.availableModels.length > 0);
  return { models, exclusive };
}

function parseClaudeInitialization(output) {
  for (const line of output.split(/\r?\n/)) {
    try {
      const { type, response } = JSON.parse(line);
      if (type !== 'control_response' || response?.request_id !== 'model-catalog' || response.subtype !== 'success') continue;
      const models = response.response?.models;
      if (Array.isArray(models)) return uniqueModels(models.map(row => modelRow(row?.value, row?.displayName, 'claude-cli-advertised')));
    } catch { /* A startup diagnostic is not part of the control protocol. */ }
  }
  return null;
}

async function probeClaude(executable, environment, workspaceRoot) {
  const directory = workspaceRoot ? safePath(resolve(workspaceRoot), '.runtime') : tmpdir();
  if (workspaceRoot) await mkdir(directory, { recursive: true });
  const profile = await mkdtemp(safePath(directory, 'model-catalog-'));
  try {
    // Query only the installed CLI's initialization metadata. Use a fresh
    // profile, no credentials or project settings, and no user messages.
    const inherited = new Set(['path', 'pathext', 'systemroot', 'comspec', 'temp', 'tmp', 'lang']);
    const env = Object.fromEntries(Object.entries(environment).filter(([key]) => inherited.has(key.toLowerCase())));
    const loopback = 'http://127.0.0.1:1';
    Object.assign(env, {
      CLAUDE_CONFIG_DIR: profile, ANTHROPIC_BASE_URL: loopback,
      HTTP_PROXY: loopback, HTTPS_PROXY: loopback, ALL_PROXY: loopback,
      CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1', DISABLE_AUTOUPDATER: '1',
      DISABLE_TELEMETRY: '1', DISABLE_ERROR_REPORTING: '1',
    });
    const args = ['--print', '--input-format', 'stream-json', '--output-format', 'stream-json', '--verbose', '--bare',
      '--strict-mcp-config', '--setting-sources', '', '--no-session-persistence'];
    const child = launch(executable, args, { cwd: profile, env, stdio: ['pipe', 'pipe', 'ignore'] });
    return await new Promise(result => {
      const output = [];
      let bytes = 0;
      let finished = false;
      function settle(models) {
        if (finished) return;
        finished = true;
        clearTimeout(timer);
        result(models);
      }
      function stop() {
        if (finished) return;
        try { killTree(child); }
        catch { /* The process may have already exited. */ }
        settle(null);
      }
      const timer = setTimeout(stop, 4000);
      child.stdout.on('data', chunk => {
        bytes += chunk.length;
        if (bytes > maxMetadataBytes) stop();
        else output.push(chunk);
      });
      child.on('error', () => settle(null));
      child.on('close', code => settle(code === 0 ? parseClaudeInitialization(Buffer.concat(output).toString('utf8')) : null));
      child.stdin.on('error', () => {});
      child.stdin.end(`${JSON.stringify({ type: 'control_request', request_id: 'model-catalog', request: { subtype: 'initialize', hooks: {} } })}\n`);
    });
  } finally {
    try { await rm(safePath(directory, basename(profile)), { recursive: true, force: true, maxRetries: 2, retryDelay: 50 }); }
    catch { /* A terminated probe can briefly leave its profile locked on Windows. */ }
  }
}

async function discoverClaude({ environment, homeDirectory, hostEnvironment, useHostProfiles, executable, workspaceRoot }, cliProbe) {
  const defaultHost = resolve(homeDirectory, '.claude');
  const host = resolve(hostEnvironment.CLAUDE_CONFIG_DIR || defaultHost);
  const hostProfile = host === defaultHost ? join(homeDirectory, '.claude.json') : join(host, '.claude.json');
  const selected = resolve(environment.CLAUDE_CONFIG_DIR || host);
  const sameProfile = selected === host;
  const local = await claudeProfile(selected, sameProfile ? 'host' : 'workbench', environment,
    sameProfile ? hostProfile : null);
  const fallback = !local.models.length && !local.exclusive && useHostProfiles && !sameProfile
    ? await claudeProfile(host, 'host', hostEnvironment, hostProfile) : null;
  const profile = local.models.length || local.exclusive ? local : fallback ?? local;
  const command = !local.exclusive && executablePath(executable || 'claude', environment);
  let advertised = null;
  if (command) {
    try { advertised = await cliProbe(command, environment, workspaceRoot); }
    catch { /* Older CLI builds may not support metadata-only startup. */ }
  }
  const models = uniqueModels([...profile.models, ...(advertised ?? [])]);
  if (models.length) {
    const source = models.every(model => model.source === models[0].source) ? models[0].source : 'claude-profile';
    const message = source === 'claude-cli-advertised' ? 'Choices advertised by the installed CLI. Account access was not checked.'
      : 'Models configured or cached locally, with choices advertised by the CLI where available. Account access was not checked.';
    return makeResult('claude-code', source, models, message);
  }
  if (local.exclusive || advertised) return makeResult('claude-code', local.exclusive ? 'claude-profile' : 'claude-cli-advertised', [], 'No model choices were found.');
  return makeResult('claude-code', null, null, command ? 'No local Claude model catalog is available.' : 'Claude Code CLI was not found.');
}

function loopbackJson(url) {
  return new Promise((resolve, reject) => {
    const request = get(url, { headers: { Accept: 'application/json' }, signal: AbortSignal.timeout(2000) }, response => {
      if (response.statusCode !== 200) {
        response.resume();
        reject(new Error('Model list unavailable.'));
        return;
      }
      let bytes = 0;
      const chunks = [];
      response.on('data', chunk => {
        bytes += chunk.length;
        if (bytes > maxMetadataBytes) request.destroy(new Error('Model list too large.'));
        else chunks.push(chunk);
      });
      response.on('error', reject);
      response.on('end', () => {
        try { resolve(JSON.parse(Buffer.concat(chunks).toString('utf8'))); }
        catch { reject(new Error('Invalid local model list.')); }
      });
    });
    request.on('error', reject);
  });
}

async function discoverLocal(providerId, requestJson) {
  const origin = localProviderBaseUrl(providerId);
  const source = `${providerId}-loopback`;
  try {
    if (providerId === 'ollama') {
      const response = await requestJson(`${origin}/api/tags`);
      if (!Array.isArray(response?.models)) throw new Error('Invalid Ollama model list.');
      const rows = response.models.filter(row => row && !row.remote_model && !row.remote_host && !/(?:[:\-])cloud$/i.test(row.name ?? row.model ?? ''));
      const models = uniqueModels(rows.map(row => modelRow(row.name ?? row.model, null, source)));
      return makeResult(providerId, source, models, models.length ? null : 'Ollama has no local models.');
    }
    const [available, metadata] = await Promise.all([requestJson(`${origin}/v1/models`), requestJson(`${origin}/api/v1/models`).catch(() => null)]);
    if (!Array.isArray(available?.data)) throw new Error('Invalid LM Studio model list.');
    const modelDetails = new Map();
    for (const detail of Array.isArray(metadata?.models) ? metadata.models : []) {
      if (!detail) continue;
      modelDetails.set(detail.key, detail);
      for (const instance of Array.isArray(detail.loaded_instances) ? detail.loaded_instances : []) modelDetails.set(instance?.id, detail);
    }
    const models = uniqueModels(available.data.map(row => {
      const details = modelDetails.get(row?.id);
      if (['embedding', 'embeddings', 'decision'].includes(details?.type ?? row?.type)) return null;
      return modelRow(row?.id, details?.display_name, source);
    }));
    return makeResult(providerId, source, models, models.length ? null : 'LM Studio exposes no language models.');
  } catch {
    const name = providerId === 'ollama' ? 'Ollama' : 'LM Studio';
    return makeResult(providerId, source, null, `${name} did not return a model list from its local server.`);
  }
}

async function discoverModels({ providerId, environment = process.env, homeDirectory = homedir(), hostEnvironment = {},
  useHostProfiles = true, executable, workspaceRoot },
  { requestJson = loopbackJson, claudeProbe = probeClaude } = {}) {
  if (providerId === 'codex') return discoverCodex({ environment, homeDirectory, hostEnvironment, useHostProfiles });
  if (providerId === 'claude-code') return discoverClaude({ environment, homeDirectory, hostEnvironment, useHostProfiles, executable, workspaceRoot }, claudeProbe);
  if (localProviderBaseUrl(providerId)) return discoverLocal(providerId, requestJson);
  return makeResult(providerId, null, null, 'This provider has no local model discovery.');
}

module.exports = { discoverModels, localProviderBaseUrl };
