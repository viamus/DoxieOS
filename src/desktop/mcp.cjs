const { existsSync, lstatSync, mkdirSync, readFileSync, renameSync, rmSync, writeFileSync } = require('node:fs');
const { homedir } = require('node:os');
const { dirname, join } = require('node:path');
const { createHash, randomUUID } = require('node:crypto');
const toml = require('@iarna/toml');
const { readJson, jsonSetting, providerEnvironment } = require('./providers.cjs');
const { safePath } = require('./storage.cjs');

const key = 'provider_mcp_servers';
const begin = '# BEGIN DOXIE MCP SERVERS';
const end = '# END DOXIE MCP SERVERS';
const block = /^# BEGIN DOXIE MCP SERVERS\r?\n[\s\S]*?^# END DOXIE MCP SERVERS(?:\r?\n)?/gm;
const secret = '••••••••';
const providers = ['claude-code', 'codex'];

function normalizeCommand(value) {
  if (typeof value !== 'string') return value;
  const command = value.trim();
  return command.replace(/^(['"])([\s\S]*)\1$/, '$2');
}

function parseArguments(value) {
  if (Array.isArray(value)) return value.map(String);
  return (String(value || '').match(/"[^"]*"|'[^']*'|[^\s"']+/g) || []).map(arg => arg.replace(/^(['"])(.*)\1$/, '$2'));
}

function parseEnvironment(value) {
  if (value && typeof value === 'object') return value;
  return Object.fromEntries(String(value || '').split(/\r?\n/).filter(line => /^\w+=/.test(line)).map(line => {
    const split = line.indexOf('=');
    return [line.slice(0, split), line.slice(split + 1)];
  }));
}

function extra(server) {
  try { return JSON.parse(server.extraJson || '{}') || {}; } catch { return {}; }
}

function read(file) {
  return existsSync(file) ? readFileSync(file, 'utf8').replace(/^\uFEFF/, '') : '';
}

function atomicWrite(file, value) {
  if (lstatSync(file, { throwIfNoEntry: false })?.isSymbolicLink()) throw new Error('MCP configuration is a symbolic link.');
  mkdirSync(dirname(file), { recursive: true });
  const temporary = `${file}.${randomUUID()}.tmp`;
  try {
    writeFileSync(temporary, value, { flag: 'wx', mode: 0o600 });
    renameSync(temporary, file);
  } finally {
    rmSync(temporary, { force: true });
  }
}

function readProviderEntries(items, provider) {
  return Object.entries(items || {}).filter(([, value]) => value && typeof value === 'object').map(([id, config]) => {
    const { url, command, args, env, type, transport, enabled, ...metadata } = config;
    return {
      id, name: id, transport: url ? 'http' : 'stdio', url, command: normalizeCommand(command), arguments: args || [],
      environment: env, providers: [provider], enabled: enabled !== false, extraJson: JSON.stringify(metadata),
    };
  });
}

const fingerprint = item => item.transport === 'http'
  ? `http:${item.url?.replace(/\/$/, '')}`
  : JSON.stringify([normalizeCommand(item.command), parseArguments(item.arguments)]);

function ordered(value) {
  if (Array.isArray(value)) return value.map(ordered);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.keys(value).sort().map(key => [key, ordered(value[key])]));
  }
  return value;
}

function equivalentClaude(config, definition) {
  const { env, args, command, type, transport, url, ...configMetadata } = config;
  const declared = definition.transport === 'http'
    ? { type: 'http', url: definition.url, metadata: extra(definition) }
    : { command: normalizeCommand(definition.command), args: parseArguments(definition.arguments), env: parseEnvironment(definition.environment), metadata: extra(definition) };
  const present = url
    ? { type: 'http', url, metadata: configMetadata }
    : { command: normalizeCommand(command), args: args || [], env: env || {}, metadata: configMetadata };
  return JSON.stringify(ordered(declared)) === JSON.stringify(ordered(present));
}

function createMcp(storage, { nativeServers = [] } = {}) {
  const home = homedir();
  const globalCodexPath = join(process.env.CODEX_HOME || join(home, '.codex'), 'config.toml');
  const codexPath = join(providerEnvironment(storage).CODEX_HOME, 'config.toml');
  const claudePath = safePath(storage.root, '.mcp.json');
  const rootKey = createHash('sha256').update(process.platform === 'win32' ? storage.root.toLowerCase() : storage.root).digest('hex');
  const lastManaged = `desktop.mcp.claude-managed.${rootKey}`;
  const nativeIds = new Set(nativeServers.map(server => server.id.toLowerCase()));
  const list = () => [...jsonSetting(storage, key, []).filter(server => !nativeIds.has(String(server.id).toLowerCase())), ...nativeServers];
  function globalClaude() {
    const config = process.env.CLAUDE_CONFIG_DIR || join(home, '.claude');
    const paths = new Set([join(home, '.claude.json'), join(config, '.claude.json'), join(config, 'mcp.json')]);
    return [...paths].flatMap(file => readProviderEntries(readJson(file).mcpServers, 'claude-code'));
  }

  function publicList() {
    return list().map(server => {
      const metadata = extra(server);
      const headers = metadata.headers || metadata.http_headers;
      const masked = headers ? Object.fromEntries(Object.keys(headers).map(header => [header, secret])) : undefined;
      return {
        id: server.id, name: server.name, transport: server.transport, command: server.command, arguments: server.arguments,
        url: server.url, providers: server.providers, enabled: server.enabled, environment: server.environment ? secret : '',
        headers: masked, extraJson: JSON.stringify(masked ? { headers: masked } : {}), builtin: nativeIds.has(server.id.toLowerCase()),
      };
    });
  }

  function sync(items) {
    const existingClaudeText = read(claudePath);
    const claude = existingClaudeText ? JSON.parse(existingClaudeText) : {};
    const previousManaged = jsonSetting(storage, lastManaged, null);
    const legacyManaged = previousManaged ?? list().filter(item =>
      item.enabled && item.providers?.includes('claude-code') && Object.hasOwn(claude.mcpServers || {}, item.id)
      && equivalentClaude(claude.mcpServers[item.id], item)).map(item => item.id);
    const managed = new Set(legacyManaged);
    const claudeServers = Object.fromEntries(Object.entries(claude.mcpServers || {}).filter(([id]) => !managed.has(id)));
    const codexText = read(codexPath);
    const unmanagedText = codexText.replace(block, '');
    const unmanaged = toml.parse(unmanagedText).mcp_servers || {};
    const otherCodex = readProviderEntries(unmanaged, 'codex');
    const codexServers = Object.create(null);
    const written = [];
    for (const server of items) {
      if (!server.enabled) continue;
      const metadata = extra(server);
      const { headers, http_headers: httpHeaders, ...other } = metadata;
      const requestHeaders = headers || httpHeaders;
      const config = server.transport === 'http'
        ? { ...other, url: server.url }
        : { ...other, command: normalizeCommand(server.command), args: parseArguments(server.arguments), env: parseEnvironment(server.environment) };
      if (server.providers?.includes('claude-code') && !Object.hasOwn(claudeServers, server.id)) {
        claudeServers[server.id] = { ...config, ...(server.transport === 'http' ? { type: 'http', ...(requestHeaders ? { headers: requestHeaders } : {}) } : {}) };
        written.push(server.id);
      }
      if (server.providers?.includes('codex') && !otherCodex.some(item => item.id === server.id || fingerprint(item) === fingerprint(server))) {
        codexServers[server.id] = { ...config, ...(server.transport === 'http' && requestHeaders ? { http_headers: requestHeaders } : {}) };
      }
    }
    const codexResult = Object.keys(codexServers).length
      ? `${unmanagedText.trimEnd()}\n\n${begin}\n${toml.stringify({ mcp_servers: codexServers })}${end}\n`
      : unmanagedText;
    if (codexResult !== codexText) atomicWrite(codexPath, codexResult);
    const claudeResult = JSON.stringify({ ...claude, mcpServers: claudeServers }, null, 2) + '\n';
    if ((existingClaudeText || written.length) && existingClaudeText !== claudeResult) atomicWrite(claudePath, claudeResult);
    if (JSON.stringify(previousManaged) !== JSON.stringify(written)) storage.setSetting(lastManaged, written);
  }

  function save(values) {
    if (!Array.isArray(values)) throw new Error('MCP servers must be a list.');
    const previous = new Map(list().map(server => [server.id, server]));
    const normalized = new Map();
    for (const item of values) {
      if (nativeIds.has(String(item.id).toLowerCase())) continue;
      if (!/^[a-z0-9][a-z0-9_-]*$/i.test(item.id || '') || ['__proto__', 'constructor', 'prototype'].includes(item.id)) throw new Error('Invalid MCP server ID.');
      const prior = previous.get(item.id);
      const transport = item.transport === 'http' ? 'http' : 'stdio';
      const command = normalizeCommand(item.command);
      if (transport === 'http' && !/^https?:\/\//i.test(item.url || '')) throw new Error('MCP server URL must use HTTP or HTTPS.');
      if (transport === 'stdio' && (typeof command !== 'string' || !command.trim())) throw new Error('A stdio MCP server needs a command.');
      const oldMetadata = extra(prior || {});
      const metadata = { ...oldMetadata, ...extra(item) };
      if (item.headers) {
        const oldHeaders = oldMetadata.headers || oldMetadata.http_headers || {};
        metadata.headers = Object.fromEntries(Object.entries(item.headers).map(([name, value]) => [name, value === secret ? oldHeaders[name] || '' : String(value)]));
      }
      normalized.set(item.id, {
        id: item.id, name: String(item.name || item.id), transport, url: item.url, command,
        arguments: parseArguments(item.arguments), environment: item.environment === secret ? prior?.environment : item.environment,
        providers: providers.filter(provider => item.providers?.includes(provider)), enabled: item.enabled !== false, extraJson: JSON.stringify(metadata),
      });
    }
    const items = [...normalized.values()];
    sync([...items, ...nativeServers]);
    storage.setSetting(key, items);
    return publicList();
  }

  function importGlobals() {
    const codex = toml.parse(read(globalCodexPath).replace(block, ''));
    const combined = list();
    for (const item of [...globalClaude(), ...readProviderEntries(codex.mcp_servers, 'codex')]) {
      const existing = combined.find(server => server.id === item.id || fingerprint(server) === fingerprint(item));
      if (existing) existing.providers = [...new Set([...existing.providers, ...item.providers])];
      else combined.push(item);
    }
    return save(combined);
  }

  return { publicList, save, importGlobals, refresh: () => sync(list()) };
}

module.exports = { createMcp };
