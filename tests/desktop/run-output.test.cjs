const assert = require('node:assert/strict');
const { test } = require('node:test');
const { presentRunOutput } = require('../../src/desktop/run-output.cjs');

const line = (source, text, timestamp = '2026-11-07T14:00:00Z') => ({ source, text, timestamp });

test('keeps the complete Markdown answer separate from MCP retry diagnostics and one named warning', () => {
  const answer = '## Weather\n\nSão Paulo: **24 °C**.\n\n- Rain later today.\n- Source: https://weather.example/forecast\n';
  const logs = [
    line('Stderr', '2026-11-07T14:00:00.101Z WARN rmcp::transport::worker: Failed to send request: reqwest::Error { kind: Request, url: "http://127.0.0.1:7850/mcp", source: ConnectError("tcp connect error", 127.0.0.1:7850, Os { code: 10061, message: "connection refused" }) }'),
    line('Stderr', '2026-11-07T14:00:01.101Z WARN rmcp::transport::worker: retrying request to http://127.0.0.1:7850/mcp, attempt 2'),
    line('Stderr', '2026-11-07T14:00:02.101Z ERROR codex_core::mcp_connection_manager: MCP client for `workspace-tools` failed to start: handshake failed'),
  ];
  const raw = [...logs, line('Stdout', answer)];
  const result = presentRunOutput(raw, { status: 'Completed', exitCode: 0, mcpServers: { 'workspace-tools': { url: 'http://127.0.0.1:7850/mcp' } } });
  assert.deepEqual(result.response, [{ text: answer, timestamp: raw.at(-1).timestamp }]);
  assert.deepEqual(result.diagnostics, logs);
  assert.equal(result.issues.length, 1);
  assert.deepEqual(result.issues[0], { kind: 'mcp', severity: 'warning', title: 'MCP workspace-tools unavailable',
    detail: 'Could not connect to the service at http://127.0.0.1:7850/mcp. Check that it is running and review its MCP settings.', count: 3 });
  assert.equal(raw[0].text, logs[0].text);
});

test('deduplicates local endpoints by host and port and sanitizes their displayed URL', () => {
  const result = presentRunOutput([
    line('Stderr', 'WARN rmcp::transport: failed request to http://user:secret@localhost:9090/mcp?token=private'),
    line('Stderr', 'WARN rmcp::transport: retrying http://localhost:9090/'),
    line('Stderr', 'WARN rmcp::transport: failed request to http://localhost:9091/mcp'),
  ], { status: 'Completed' });
  assert.equal(result.issues.length, 2);
  assert.equal(result.issues[0].count, 2);
  assert.equal(result.issues[0].title, 'MCP localhost:9090 unavailable');
  assert(!result.issues[0].detail.includes('secret'));
  assert(!result.issues[0].detail.includes('private'));
});

test('merges server-name and endpoint retry groups when later logs identify their relationship', () => {
  const result = presentRunOutput([
    line('Stderr', 'WARN rmcp::transport: failed request to http://localhost:9090/mcp'),
    line('Stderr', 'ERROR codex_core::mcp: server_name=workspace-tools failed to start'),
    line('Stderr', JSON.stringify({ level: 'ERROR', target: 'rmcp::transport', message: 'handshake failed at http://localhost:9090/mcp', fields: { server_name: 'workspace-tools' } })),
  ], { status: 'Completed' });
  assert.equal(result.issues.length, 1);
  assert.equal(result.issues[0].title, 'MCP workspace-tools unavailable');
  assert.equal(result.issues[0].count, 3);
});

test('keeps MCP servers on distinct paths independent and prefers an explicit server name', () => {
  const mcpServers = {
    alpha: { url: 'https://mcp.example/alpha' },
    beta: { url: 'https://mcp.example/beta' },
  };
  const result = presentRunOutput([
    line('Stderr', 'ERROR codex_core::mcp: server_name=beta failed to start'),
    line('Stderr', 'WARN rmcp::transport: failed request to https://mcp.example/alpha?token=hidden'),
    line('Stderr', 'WARN rmcp::transport: retrying https://mcp.example/beta'),
    line('Stderr', 'WARN rmcp::transport: retrying https://mcp.example/alpha'),
  ], { status: 'Completed', mcpServers });
  assert.deepEqual(result.issues.map(issue => [issue.title, issue.count]), [
    ['MCP beta unavailable', 2], ['MCP alpha unavailable', 2],
  ]);
  assert(result.issues[0].detail.includes('https://mcp.example/beta'));
  assert(result.issues[1].detail.includes('https://mcp.example/alpha'));
  assert(!result.issues[1].detail.includes('hidden'));
  const unnamed = presentRunOutput([
    line('Stderr', 'WARN rmcp::transport: failed request to https://mcp.example/alpha'),
    line('Stderr', 'WARN rmcp::transport: failed request to https://mcp.example/beta'),
  ], { status: 'Completed' });
  assert.equal(unnamed.issues.length, 2);
  const ambiguous = presentRunOutput([line('Stderr', 'WARN rmcp::transport: failed request to https://mcp.example/')], { mcpServers });
  assert.equal(ambiguous.issues[0].title, 'MCP mcp.example unavailable');
});

test('separates progress, explicit activity and legacy tool commands from assistant prose', () => {
  const answer = 'The service returned ERROR 401 at https://example.test.\n\nThis is the report you requested.';
  const result = presentRunOutput([
    line('Progress', 'I am checking the forecast.'), line('Activity', 'curl https://weather.example'),
    line('Stdout', '▶ shell Get-Date'), line('Stdout', answer),
  ], { status: 'Completed' });
  assert.deepEqual(result.response.map(item => item.text), [answer]);
  assert.deepEqual(result.activity.map(item => item.text), ['I am checking the forecast.', 'curl https://weather.example', '▶ shell Get-Date']);
  assert.deepEqual(result.issues, []);
  assert.deepEqual(result.diagnostics, []);
});

test('preserves application log examples and explicitly identified assistant response contents', () => {
  const applicationLog = '2026-11-07T14:00:00Z ERROR application: login failed with HTTP 401\n\nThis is the application incident report.';
  const providerLogExample = '2026-11-07T14:00:00Z ERROR codex_core::example: HTTP 401\n\nThis quoted log is part of the answer.';
  const jsonExample = JSON.stringify({ level: 'ERROR', target: 'application::auth', message: 'HTTP 401' });
  const result = presentRunOutput([
    line('Stdout', applicationLog), line('Stdout', jsonExample), line('Response', providerLogExample),
  ], { status: 'Completed' });
  assert.deepEqual(result.response.map(item => item.text), [applicationLog, jsonExample, providerLogExample]);
  assert.deepEqual(result.diagnostics, []);
  assert.deepEqual(result.issues, []);
  const assistant = presentRunOutput([line('Assistant', providerLogExample)], { status: 'Completed' });
  assert.equal(assistant.response[0].text, providerLogExample);
  assert.deepEqual(assistant.diagnostics, []);
  assert.deepEqual(assistant.issues, []);
});

test('presents only the last unphased Codex assistant message as the completed weather response', () => {
  const raw = [
    line('Assistant', 'Vou consultar a previsão para São Paulo.', '2026-11-07T14:00:00Z'),
    line('Activity', 'web weather São Paulo', '2026-11-07T14:00:01Z'),
    line('Assistant', 'Recebi os dados; vou organizar a resposta.', '2026-11-07T14:00:02Z'),
    line('Assistant', '## São Paulo\n\n**24 °C**, com chuva à tarde.\n', '2026-11-07T14:00:03Z'),
  ];
  for (const status of ['Completed', 'Succeeded']) {
    const result = presentRunOutput(raw, { status });
    assert.deepEqual(result.response, [{ text: raw[3].text, timestamp: raw[3].timestamp }]);
    assert.deepEqual(result.activity, raw.slice(0, 3).map(({ text, timestamp }) => ({ text, timestamp })));
    assert.deepEqual(result.diagnostics, []);
    assert.deepEqual(result.issues, []);
  }
  const running = presentRunOutput(raw, { status: 'Running' });
  assert.deepEqual(running.response, []);
  assert.deepEqual(running.activity, raw.map(({ text, timestamp }) => ({ text, timestamp })));
});

test('explicit final responses take precedence over unphased assistant commentary and remain visible while running', () => {
  const commentary = line('Assistant', 'Checking the available data.');
  const final = line('Response', '▶ This symbol and ERROR 401 are part of the final report.\n\nKeep this Markdown intact.');
  for (const status of ['Running', 'Completed']) {
    const result = presentRunOutput([commentary, final], { status });
    assert.deepEqual(result.response, [{ text: final.text, timestamp: final.timestamp }]);
    assert.deepEqual(result.activity, [{ text: commentary.text, timestamp: commentary.timestamp }]);
    assert.deepEqual(result.diagnostics, []);
    assert.deepEqual(result.issues, []);
  }
});

test('keeps harmless shell snapshot and credential fallback warnings only in diagnostics', () => {
  const logs = [
    line('Stderr', '2026-11-07T14:00:00Z WARN codex_core::shell_snapshot: Shell snapshot is not supported for PowerShell'),
    line('Stderr', '2026-11-07T14:00:00Z WARN codex_login: failed to read credentials from keyring; falling back to file storage'),
    line('Stderr', 'Unrecognized provider diagnostic'),
  ];
  const result = presentRunOutput(logs, { status: 'Completed', exitCode: 0 });
  assert.deepEqual(result.diagnostics, logs);
  assert.deepEqual(result.response, []);
  assert.deepEqual(result.issues, []);
});

test('keeps optional featured-plugin authentication and curated sync fallbacks in diagnostics', () => {
  const logs = [
    line('Stderr', '2026-11-07T14:00:00Z WARN codex_core::plugins: failed to warm featured plugin ids cache: HTTP 401 Unauthorized'),
    line('Stderr', '2026-11-07T14:00:00Z WARN codex_core::plugins: featured plugin request failed with 401 Unauthorized'),
    line('Stderr', '2026-11-07T14:00:00Z WARN codex_core::plugins: curated plugin git sync failed; falling back to cached plugins'),
  ];
  const result = presentRunOutput([...logs, line('Stdout', 'The weather report is ready.')], { status: 'Completed' });
  assert.deepEqual(result.diagnostics, logs);
  assert.deepEqual(result.issues, []);
  assert.equal(result.response[0].text, 'The weather report is ready.');
  const fatal = presentRunOutput([line('Fatal', logs[1].text)], { status: 'Failed' });
  assert.equal(fatal.issues[0].kind, 'authentication');
  assert.equal(fatal.issues[0].severity, 'error');
});

test('recognizes legacy internal structured logs while preserving ordinary assistant ERROR text', () => {
  const plain = line('Stdout', 'ERROR means the forecast service failed. This paragraph explains the result.');
  const log = line('Stdout', '\x1b[33m2026-11-07T14:00:00Z WARN rmcp::transport: failed to reach http://localhost:4000/mcp\x1b[0m');
  const json = line('Stdout', JSON.stringify({ level: 'ERROR', target: 'codex_core::mcp_connection_manager', message: 'MCP client for "search" failed to start', fields: { server_name: 'search' } }));
  const result = presentRunOutput([log, json, plain], { status: 'Completed' });
  assert.deepEqual(result.response, [{ text: plain.text, timestamp: plain.timestamp }]);
  assert.deepEqual(result.diagnostics, [log, json]);
  assert.equal(result.issues.length, 2);
  assert.equal(result.issues[1].title, 'MCP search unavailable');
});

test('surfaces provider authentication and quota errors without treating MCP authentication as provider sign-in', () => {
  const result = presentRunOutput([
    line('Stderr', 'HTTP 401 Unauthorized: Missing bearer or basic authentication in header'),
    line('Fatal', 'Authentication required: not logged in'),
    line('Stderr', 'HTTP 429 Too Many Requests: quota exceeded'),
    line('Stderr', 'WARN rmcp::transport: HTTP 401 Unauthorized from http://localhost:7890/mcp'),
  ], { status: 'Failed', exitCode: 1 });
  assert.deepEqual(result.issues.map(issue => [issue.kind, issue.severity, issue.count]), [
    ['authentication', 'error', 2], ['rate-limit', 'error', 1], ['mcp', 'warning', 1],
  ]);
  assert.deepEqual(result.response, []);
});

test('shows real fatal launch or transport errors concisely and retains their raw diagnostics', () => {
  for (const [message, title] of [
    ['spawn unavailable-provider ENOENT', 'Provider could not start'],
    ['stream disconnected before completion: network transport closed', 'Provider connection failed'],
  ]) {
    const raw = line('Fatal', message);
    const result = presentRunOutput([raw], { status: 'Failed', exitCode: 1 });
    assert.deepEqual(result.response, []);
    assert.deepEqual(result.diagnostics, [raw]);
    assert.deepEqual(result.issues, [{ kind: 'provider', severity: 'error', title, detail: message, count: 1 }]);
  }
  const multiline = line('Fatal', 'Provider transport closed\n    at worker.js:20\n    at process.js:5');
  const result = presentRunOutput([multiline], { status: 'Failed' });
  assert.equal(result.issues.length, 1);
  assert.equal(result.issues[0].detail, 'Provider transport closed');
});

test('failed legacy runs with unknown stderr receive a bounded useful error instead of an empty result', () => {
  const raw = line('Stderr', 'Unexpected worker exit: ' + 'details '.repeat(100));
  const result = presentRunOutput([raw], { status: 'Failed', exitCode: 1 });
  assert.deepEqual(result.response, []);
  assert.deepEqual(result.diagnostics, [raw]);
  assert.equal(result.issues[0].title, 'Agent run failed');
  assert.equal(result.issues[0].severity, 'error');
  assert.equal(result.issues[0].detail.length, 240);
  assert(result.issues[0].detail.startsWith('Unexpected worker exit:'));
  assert.deepEqual(presentRunOutput([], { status: 'Failed', exitCode: 7 }).issues, [{ kind: 'failure', severity: 'error',
    title: 'Agent run failed', detail: 'The provider exited with code 7.', count: 1 }]);
  const stack = presentRunOutput([line('Stderr', 'Unexpected worker exit\n  at worker.js:20\n  at process.js:5')], { status: 'Failed' });
  assert.equal(stack.issues[0].detail, 'Unexpected worker exit');
});

test('uses explicit run status before process exit code when presenting cancellation or completion', () => {
  for (const status of ['Cancelled', 'Interrupted', 'Running', 'Completed']) {
    const result = presentRunOutput([line('Stderr', 'Process ended')], { status, exitCode: 1 });
    assert.deepEqual(result.issues, []);
  }
  assert.equal(presentRunOutput([], { exitCode: 1 }).issues[0].kind, 'failure');
  assert.equal(presentRunOutput([], { status: 'unknown', exitCode: 1 }).issues[0].kind, 'failure');
  assert.equal(presentRunOutput([], { status: 'Failed', exitCode: 0 }).issues[0].kind, 'failure');
});
