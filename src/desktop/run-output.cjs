const ansi = /\x1b\[[0-?]*[ -/]*[@-~]/g;
const logPrefix = /^\d{4}-\d{2}-\d{2}[T ][\d:.+Z-]+\s+(?:TRACE|DEBUG|INFO|WARN(?:ING)?|ERROR|FATAL)\b\s*/i;
const urlPattern = /https?:\/\/[^\s"'<>\\)\]}]+/gi;
const stackLine = /^(?:at\s|\d+:\s|stack backtrace|caused by:\s*$)/i;
const harmlessDiagnostic = text => /shell\s+snapshot[^\n]*(?:unsupported|not supported)|(?:unsupported|not supported)[^\n]*shell\s+snapshot|(?:credential|keyring)[^\n]*(?:falling back|fallback|using (?:the )?file)/i.test(text);
const optionalPluginDiagnostic = text => /featured[_ -]+plugin[^\n]*(?:cache|request|fetch)|(?:cache|request|fetch)[^\n]*featured[_ -]+plugin|curated[^\n]*plugin[^\n]*(?:sync|git)[^\n]*(?:fallback|falling back|using cached)/i.test(text);

function serviceUrl(text) {
  try {
    const url = new URL(text.replace(/[.,;:]+$/, ''));
    if (!['http:', 'https:'].includes(url.protocol)) return null;
    url.username = '';
    url.password = '';
    url.search = '';
    url.hash = '';
    return url;
  } catch { return null; }
}

function urlsIn(text) {
  return [...text.matchAll(urlPattern)].map(match => serviceUrl(match[0])).filter(Boolean);
}

function concise(text, limit = 240) {
  const safe = String(text || '').replace(ansi, '').replace(urlPattern, match => serviceUrl(match)?.href || '[service URL]');
  const lines = safe.split(/\r?\n/).map(line => line.trim()).filter(Boolean);
  const useful = lines.filter(line => !stackLine.test(line));
  const value = (useful.at(-1) || lines.at(-1) || '').replace(logPrefix, '').replace(/\s+/g, ' ');
  return value.length > limit ? `${value.slice(0, limit - 1)}…` : value;
}

function internalLog(text) {
  const plain = text.replace(ansi, '');
  const providerTarget = /^(?:codex(?:_[\w]+)?|rmcp)(?:::|:|\s|$)/i;
  if (logPrefix.test(plain) && providerTarget.test(plain.replace(logPrefix, ''))) return true;
  try {
    const value = JSON.parse(text);
    return value && /^(?:trace|debug|info|warn|warning|error|fatal)$/i.test(value.level || '')
      && typeof value.target === 'string' && providerTarget.test(value.target);
  } catch { return false; }
}

function diagnosticText(text) {
  try {
    const value = JSON.parse(text);
    if (value && typeof value.message === 'string') return `${value.target || ''} ${value.message} ${JSON.stringify(value.fields || {})}`;
  } catch {}
  return text.replace(ansi, '');
}

function mcpName(text) {
  return text.match(/\bserver_name["']?\s*[=:]\s*["'`]?([\w.:-]+)/i)?.[1]
    || text.match(/\bMCP\s+(?:client|server)\s+(?:for\s+)?["'`]([^"'`]+)["'`]/i)?.[1]
    || text.match(/\bmcp_servers\.([\w.-]+)/i)?.[1];
}

function configuredServers(value) {
  const servers = Array.isArray(value) ? value.map(server => [server.id || server.name, server]) : Object.entries(value || {});
  return servers.flatMap(([name, server]) => {
    const url = typeof server?.url === 'string' ? serviceUrl(server.url) : null;
    return name && url ? [{ name, url }] : [];
  });
}

const endpointKey = url => `mcp-url:${url.origin}${url.pathname.replace(/\/+$/, '')}`;

function configuredServer(servers, explicitName, urls) {
  if (explicitName) return servers.find(server => server.name.toLowerCase() === explicitName.toLowerCase());
  const exact = servers.filter(server => urls.some(url => endpointKey(url) === endpointKey(server.url)));
  if (exact.length) return exact.length === 1 ? exact[0] : null;
  const origin = servers.filter(server => urls.some(url => url.origin === server.url.origin));
  return origin.length === 1 ? origin[0] : null;
}

function presentRunOutput(lines, { status, exitCode, mcpServers } = {}) {
  const response = [];
  const activity = [];
  const diagnostics = [];
  const diagnosticLines = [];
  const issues = [];
  const indexed = new Map();
  const endpoints = new Map();
  const servers = configuredServers(mcpServers);
  const runStatus = String(status || '').toLowerCase();
  const knownStatus = ['completed', 'succeeded', 'cancelled', 'canceled', 'interrupted', 'running', 'queued', 'awaitingapproval'].includes(runStatus);
  const failed = runStatus === 'failed' || (!knownStatus && Number.isInteger(exitCode) && exitCode !== 0);
  const output = Array.isArray(lines) ? lines : [];
  const sourceOf = line => String(line?.source || 'Stdout').toLowerCase();
  const valid = line => typeof line?.text === 'string' && line.text.trim();
  const explicitResponse = output.some(line => sourceOf(line) === 'response' && valid(line));
  const lastAssistant = ['completed', 'succeeded'].includes(runStatus) && !explicitResponse
    ? output.findLastIndex(line => sourceOf(line) === 'assistant' && valid(line)) : -1;
  const addIssue = (keys, issue) => {
    const matches = [...new Set(keys.map(key => indexed.get(key)).filter(Boolean))];
    const existing = matches[0];
    if (existing) {
      for (const duplicate of matches.slice(1)) {
        existing.count += duplicate.count;
        if (duplicate.severity === 'error') existing.severity = 'error';
        issues.splice(issues.indexOf(duplicate), 1);
        for (const [key, value] of indexed) if (value === duplicate) indexed.set(key, existing);
      }
      existing.count++;
      if (issue.severity === 'error') existing.severity = 'error';
      for (const key of keys) indexed.set(key, existing);
      return existing;
    }
    const entry = { ...issue, count: 1 };
    issues.push(entry);
    for (const key of keys) indexed.set(key, entry);
    return entry;
  };

  for (const [index, raw] of output.entries()) {
    const line = typeof raw === 'string' ? { source: 'Stdout', text: raw } : raw;
    if (!line || typeof line.text !== 'string' || !line.text.trim()) continue;
    const source = String(line.source || 'Stdout').toLowerCase();
    if (['stderr', 'fatal'].includes(source) || (source === 'stdout' && internalLog(line.text))) {
      diagnostics.push(raw);
      const parts = source === 'fatal' ? [line.text] : line.text.split(/\r?\n/);
      for (const text of parts.filter(value => value.trim())) diagnosticLines.push({ source, text: diagnosticText(text) });
    } else if (source === 'response' || (source === 'assistant' && index === lastAssistant)) {
      response.push({ text: line.text, timestamp: line.timestamp });
    } else if (['progress', 'activity', 'assistant'].includes(source) || /^\s*▶/.test(line.text)) {
      activity.push({ text: line.text, timestamp: line.timestamp });
    } else if (source === 'stdout') {
      response.push({ text: line.text, timestamp: line.timestamp });
    }
  }

  for (const { source, text } of diagnosticLines) {
    const fatal = source === 'fatal';
    if ((harmlessDiagnostic(text) || optionalPluginDiagnostic(text)) && !fatal) continue;
    const urls = urlsIn(text);
    const explicitName = mcpName(text);
    const configured = configuredServer(servers, explicitName, urls);
    // A bare origin in retry chatter can refer to one already identified service.
    // Distinct paths and explicit names retain their own endpoint identities.
    const fallbackKeys = !explicitName && !configured ? urls.flatMap(url => {
      if (url.pathname !== '/' || servers.filter(server => server.url.origin === url.origin).length > 1) return [];
      const matches = [...endpoints].filter(([, endpoint]) => endpoint.origin === url.origin);
      const groups = new Set(matches.map(([key]) => indexed.get(key)));
      return groups.size === 1 ? matches.map(([key]) => key) : [];
    }) : [];
    const knownEndpoint = urls.some(url => indexed.has(endpointKey(url))) || fallbackKeys.length;
    const mcp = /\bmcp\b|\brmcp::|mcp_client|mcp_servers|server_name/i.test(text) || knownEndpoint || configured;
    const problem = /\b(?:error|failed|failure|unavailable|refused|timeout|timed out|retry|retrying|unauthorized)\b|\b(?:401|403|404|429|5\d\d)\b/i.test(text);
    if (mcp && problem) {
      const name = explicitName || configured?.name;
      const url = configured?.url || urls[0];
      const serviceEndpoints = configured ? [configured.url] : urls;
      const keys = [name && `mcp-name:${name.toLowerCase()}`, ...serviceEndpoints.map(endpointKey), ...fallbackKeys].filter(Boolean);
      for (const endpoint of serviceEndpoints) endpoints.set(endpointKey(endpoint), endpoint);
      const displayName = String(name || url?.host || 'service').slice(0, 80);
      const service = url ? ` at ${url.href.length > 100 ? `${url.href.slice(0, 99)}…` : url.href}` : '';
      const auth = /\b(?:401|403|unauthorized|authentication)\b/i.test(text);
      const issue = addIssue(keys.length ? keys : ['mcp-unknown'], { kind: 'mcp', severity: fatal ? 'error' : 'warning',
        title: `MCP ${displayName} unavailable`,
        detail: `Could not connect to the service${service}. ${auth ? 'Check its sign-in and MCP settings.' : 'Check that it is running and review its MCP settings.'}` });
      if (name) issue.title = `MCP ${displayName} unavailable`;
      if (url) issue.detail = `Could not connect to the service${service}. ${auth ? 'Check its sign-in and MCP settings.' : 'Check that it is running and review its MCP settings.'}`;
      continue;
    }
    if (/\b401\b|\bunauthori[sz]ed\b|missing (?:bearer|api key)|authentication (?:failed|required)|not logged in|(?:login|sign.in) (?:is )?required/i.test(text)) {
      addIssue(['authentication'], { kind: 'authentication', severity: 'error', title: 'Authentication required',
        detail: 'Sign in to the provider, then retry the run.' });
    } else if (/\b429\b|rate.limit|quota exceeded|too many requests/i.test(text)) {
      addIssue(['rate-limit'], { kind: 'rate-limit', severity: 'error', title: 'Provider usage limit reached',
        detail: 'Retry when the provider is available or choose another model.' });
    } else if (fatal) {
      const title = /\bENOENT\b|\bspawn\b|CLI was not found|executable[^\n]*not found/i.test(text) ? 'Provider could not start'
        : /\b(?:connection|transport|disconnected|network)\b/i.test(text) ? 'Provider connection failed' : 'Provider error';
      addIssue([`provider:${concise(text)}`], { kind: 'provider', severity: 'error', title, detail: concise(text) });
    }
  }

  if (failed && !issues.some(issue => issue.severity === 'error')) {
    const lastUseful = diagnosticLines.map(line => concise(line.text)).filter(text => text && !stackLine.test(text) && !harmlessDiagnostic(text) && !optionalPluginDiagnostic(text)).at(-1);
    const detail = lastUseful || (Number.isInteger(exitCode) ? `The provider exited with code ${exitCode}.` : 'The provider did not complete this run.');
    addIssue(['failure'], { kind: 'failure', severity: 'error', title: 'Agent run failed', detail });
  }
  return { response, activity, issues, diagnostics };
}

module.exports = { presentRunOutput };
