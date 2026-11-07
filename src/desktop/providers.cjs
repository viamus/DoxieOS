const { existsSync, readFileSync } = require('node:fs');
const { homedir } = require('node:os');
const { join, resolve } = require('node:path');
const toml = require('@iarna/toml');
const { executablePath } = require('./processes.cjs');
const { safePath } = require('./storage.cjs');
const { reasoningEfforts, normalizeEffort, resolveExecution } = require('./execution.cjs');
const { localProviderBaseUrl } = require('./model-catalog.cjs');

const definitions = {
  'claude-code': { id: 'claude-code', name: 'Claude Code', executable: 'claude' },
  codex: { id: 'codex', name: 'OpenAI Codex', executable: 'codex' },
  ollama: { id: 'ollama', name: 'Ollama', executable: 'codex', requiresAuth: false, localBaseUrl: localProviderBaseUrl('ollama') },
  lmstudio: { id: 'lmstudio', name: 'LM Studio', executable: 'codex', requiresAuth: false, localBaseUrl: localProviderBaseUrl('lmstudio') },
};

const installedCodexHome = () => resolve(process.env.CODEX_HOME || join(homedir(), '.codex'));
const apiVariable = /^(openai_api_key|openai_base_url|codex_api_key)$/i;
const explicitApiConfiguration = (...environments) => environments.some(environment =>
  Object.entries(environment || {}).some(([key, value]) => apiVariable.test(key) && value));

function installedCodexSettings(home) {
  try {
    return toml.parse(readFileSync(join(home, 'config.toml'), 'utf8').replace(/^\uFEFF/, ''));
  } catch { return {}; }
}

function readJson(path, fallback = {}) {
  try {
    return JSON.parse(readFileSync(path, 'utf8').replace(/^\uFEFF/, ''));
  } catch {
    return fallback;
  }
}

function jsonSetting(storage, key, fallback) {
  try {
    const value = storage.getSetting(key);
    return typeof value === 'string' ? JSON.parse(value) : value ?? fallback;
  } catch {
    return fallback;
  }
}

function providerEnvironment(storage, environment = process.env) {
  return {
    ...environment,
    CODEX_HOME: safePath(storage.root, '.providers/codex'),
    CLAUDE_CONFIG_DIR: safePath(storage.root, '.providers/claude'),
  };
}

function settingsFor(storage) {
  const saved = jsonSetting(storage, 'desktop.providers', {});
  const providers = Object.fromEntries(Object.entries(definitions).map(([id, defaults]) => {
    const executable = saved[id]?.executable || (defaults.localBaseUrl && saved.codex?.executable) || defaults.executable;
    const provider = {
      ...defaults, executable, model: saved[id]?.model ?? null, effort: saved[id]?.effort ?? null,
      environment: saved[id]?.environment, requiresAuth: defaults.requiresAuth !== false,
    };
    const env = providerEnvironment(storage, { ...process.env, ...provider.environment });
    return [id, {
      ...provider, reasoningEfforts: reasoningEfforts[id] || [],
      installed: Boolean(executablePath(provider.executable, env)),
      ...(id === 'codex' ? { localAuthenticated: existsSync(join(installedCodexHome(), 'auth.json')) } : {}),
      authenticated: !provider.requiresAuth ? null : id === 'codex'
        ? explicitApiConfiguration(provider.environment)
          ? Boolean(env.OPENAI_API_KEY || env.CODEX_API_KEY || existsSync(join(env.CODEX_HOME, 'auth.json')))
          : existsSync(join(installedCodexHome(), 'auth.json'))
        : Boolean(env.ANTHROPIC_API_KEY || env.CLAUDE_CODE_OAUTH_TOKEN || existsSync(join(env.CLAUDE_CONFIG_DIR, '.credentials.json'))),
    }];
  }));
  const requested = storage.getSetting('default_agent_provider');
  return {
    language: storage.getSetting('ui.language') || 'en',
    defaultProvider: Object.hasOwn(providers, requested) ? requested : 'claude-code',
    mcpServers: jsonSetting(storage, 'provider_mcp_servers', []),
    providers,
    catalogs: storage.listCatalogs?.() || [],
  };
}

function providerFor(storage, id, options = {}) {
  const settings = settingsFor(storage);
  const { providerId, model, effort = null } = resolveExecution(settings, options.preference, { ...options, providerId: id });
  const provider = settings.providers[providerId];
  if (provider.localBaseUrl && !model) throw new Error(`Select a model for ${provider.name}.`);
  const environment = providerEnvironment(storage, { ...process.env, ...provider.environment, ...options.env });
  const localCodex = providerId === 'codex' && (options.localCodex === true
    || (options.localCodex !== false && !explicitApiConfiguration(provider.environment, options.env)));
  if (localCodex) {
    environment.CODEX_HOME = installedCodexHome();
    for (const key of Object.keys(environment)) {
      if (apiVariable.test(key)) delete environment[key];
    }
  }
  delete environment.ELECTRON_RUN_AS_NODE;
  delete environment.CLAUDECODE;
  if (providerId === 'claude-code' && effort) environment.CLAUDE_CODE_EFFORT_LEVEL = effort;
  const executable = executablePath(provider.executable, environment);
  if (!executable) throw new Error(`${provider.name} CLI was not found. Configure its executable in Settings.`);
  let mcpServers;
  if (localCodex && options.mcp !== false) {
    const config = join(providerEnvironment(storage).CODEX_HOME, 'config.toml');
    // The runtime shares installed credentials, while its tools belong to DoxieOS.
    // Never import the installed user's plugins, model routing or MCP settings.
    mcpServers = existsSync(config) ? toml.parse(readFileSync(config, 'utf8').replace(/^\uFEFF/, '')).mcp_servers : null;
  }
  const installed = localCodex ? installedCodexSettings(environment.CODEX_HOME) : {};
  const store = installed.cli_auth_credentials_store;
  return { ...provider, executable, model, effort, environment, localCodex, mcpServers,
    ...(localCodex ? { authStore: ['auto', 'file', 'keyring', 'ephemeral'].includes(store) ? store : null,
      installedMcpIds: Object.keys(installed.mcp_servers || {}) } : {}) };
}

function usesCodex(provider) {
  return provider.id === 'codex' || Boolean(provider.localBaseUrl);
}

function localProviderArgs(provider) {
  if (!provider.localBaseUrl) return [];
  const id = `doxie_${provider.id}`;
  const config = `model_providers.${id}`;
  return [
    '-c', `model_provider='${id}'`,
    '-c', `${config}.name='${provider.name}'`,
    '-c', `${config}.base_url='${provider.localBaseUrl}/v1'`,
    '-c', `${config}.wire_api='responses'`,
    '-c', `${config}.requires_openai_auth=false`,
    '-c', "web_search='disabled'",
  ];
}

function effortArgs(provider) {
  const effort = normalizeEffort(provider.effort, provider.id);
  if (!effort) return [];
  return provider.id === 'codex' ? ['-c', `model_reasoning_effort='${effort}'`] : ['--effort', effort];
}

function interactiveCodexArgs(provider) {
  if (!provider.localCodex) return [];
  // Interactive Codex has no --ignore-user-config switch. Disable installed
  // servers for this invocation, then restore only the DoxieOS tool registry.
  const installed = new Set(provider.installedMcpIds || []);
  const reserved = new Set([...installed, ...Object.keys(provider.mcpServers || {})]);
  const servers = Object.fromEntries([...installed].map(id => [id, { enabled: false }]));
  for (const [id, server] of Object.entries(provider.mcpServers || {})) {
    // Config overrides deep-merge entries. Reusing a host ID could retain its
    // URL, command or credentials, so give collisions an unused console alias.
    let alias = id;
    if (installed.has(id)) {
      alias = `doxie_${id}`;
      let suffix = 2;
      while (reserved.has(alias)) alias = `doxie_${id}_${suffix++}`;
    }
    reserved.add(alias);
    servers[alias] = { ...server, enabled: server.enabled !== false };
  }
  return ['-c', "model_provider='openai'", '-c', "forced_login_method='chatgpt'",
    ...(provider.authStore ? ['-c', `cli_auth_credentials_store='${provider.authStore}'`] : []),
    ...(Object.keys(servers).length ? ['-c', `mcp_servers=${toml.stringify.value(servers)}`] : [])];
}

function headlessArgs(provider) {
  const model = provider.model ? ['--model', provider.model] : [];
  const effort = effortArgs(provider);
  if (usesCodex(provider)) {
    const localAuth = provider.localCodex ? ['--ignore-user-config', '-c', "forced_login_method='chatgpt'",
      ...(provider.authStore ? ['-c', `cli_auth_credentials_store='${provider.authStore}'`] : []),
      ...(provider.mcpServers && Object.keys(provider.mcpServers).length ? ['-c', `mcp_servers=${toml.stringify.value(provider.mcpServers)}`] : [])] : [];
    return ['exec', '--dangerously-bypass-approvals-and-sandbox', '--skip-git-repo-check', '--json',
      '--ephemeral', ...localAuth, ...localProviderArgs(provider), ...model, ...effort, '--color', 'never', '-'];
  }
  return [
    '--print', '--input-format', 'stream-json', '--output-format', 'stream-json',
    '--verbose', '--include-partial-messages', '--dangerously-skip-permissions',
    '--no-session-persistence', ...model, ...effort,
  ];
}

function inputMessage(provider, prompt) {
  if (usesCodex(provider)) return prompt;
  const content = [{ type: 'text', text: prompt }];
  return JSON.stringify({ type: 'user', message: { role: 'user', content } }) + '\n';
}

function providerOutput(provider, line) {
  if (!line.trim()) return {};
  let event;
  try { event = JSON.parse(line); }
  catch { return { lines: [line] }; }
  if (!event || typeof event !== 'object') return { lines: [line] };
  if (usesCodex(provider)) {
    if (event.type === 'thread.started') return { sessionId: event.thread_id };
    if (event.type === 'item.completed') {
      if (event.item?.type === 'agent_message') return { lines: [event.item.text] };
      if (event.item?.type === 'command_execution') return { lines: [`▶ ${event.item.command}`] };
    }
    if (event.type === 'turn.completed' && event.usage) {
      const { input_tokens = 0, cached_input_tokens = 0, output_tokens = 0, reasoning_output_tokens = 0 } = event.usage;
      const input = Math.max(0, input_tokens - cached_input_tokens);
      const output = output_tokens + reasoning_output_tokens;
      const large = input_tokens > 272_000;
      return { usage: {
        inputTokens: input, outputTokens: output, cacheReadTokens: cached_input_tokens, cacheCreationTokens: 0,
        totalCostUsd: provider.id === 'codex' && /^gpt-5\.5($|-2026-04-23$)/.test(provider.model)
          ? ((input * 5 + cached_input_tokens * 0.5) * (large ? 2 : 1) + output * 30 * (large ? 1.5 : 1)) / 1_000_000
          : null,
      } };
    }
    if (['turn.failed', 'error'].includes(event.type)) return { lines: [event.error?.message || event.message || 'Codex turn failed.'], error: true };
    return {};
  }
  if (event.type === 'system' && event.session_id) return { sessionId: event.session_id };
  if (event.type === 'assistant') {
    const lines = [];
    for (const block of event.message?.content || []) {
      if (block.type === 'text' && block.text) lines.push(block.text);
      if (block.type === 'tool_use') {
        const detail = block.input?.file_path || block.input?.description || block.input?.pattern || block.input?.command || '';
        lines.push(`▶ ${block.name} ${String(detail).slice(0, 160)}`.trimEnd());
      }
    }
    return { lines };
  }
  if (event.type !== 'result') return {};
  const usage = event.usage;
  return {
    lines: event.is_error ? [event.result || event.errors?.join('\n') || 'Claude turn failed.'] : [],
    fallback: event.result,
    error: Boolean(event.is_error),
    sessionId: event.session_id,
    usage: usage || Number.isFinite(event.total_cost_usd) ? {
      inputTokens: usage?.input_tokens || 0,
      outputTokens: usage?.output_tokens || 0,
      cacheReadTokens: usage?.cache_read_input_tokens || 0,
      cacheCreationTokens: usage?.cache_creation_input_tokens || 0,
      totalCostUsd: event.total_cost_usd ?? null,
    } : undefined,
  };
}

module.exports = { definitions, settingsFor, providerFor, providerEnvironment, usesCodex, localProviderArgs, interactiveCodexArgs, headlessArgs, inputMessage, providerOutput, readJson, jsonSetting };
