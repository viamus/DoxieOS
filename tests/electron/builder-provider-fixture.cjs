const assert = require('node:assert/strict');
const { existsSync, mkdirSync, readFileSync, writeFileSync } = require('node:fs');
const { join } = require('node:path');

const args = process.argv.slice(2);
assert.equal(process.env.DOXIE_TEST, '1');
assert.equal(process.env.CODEX_HOME, join(process.env.DOXIE_USER_DATA, 'local-codex'));
const auth = join(process.env.CODEX_HOME, 'auth.json');

if (args[0] === 'login') {
  mkdirSync(process.env.CODEX_HOME, { recursive: true });
  writeFileSync(auth, '{}', { flag: 'wx' });
  console.log('Fixture sign-in complete');
} else {
  assert.equal(args[0], 'exec');
  assert.ok(args.includes('--ephemeral'));
  assert.ok(args.includes('--ignore-user-config'));
  assert.ok(!process.env.OPENAI_API_KEY);
  assert.ok(!process.env.OPENAI_BASE_URL);
  const input = readFileSync(0, 'utf8');
  if (input.includes('## Doxie request\n\n')) {
    const request = JSON.parse(input.split('## Doxie request\n\n')[1].split('\n').slice(1).join('\n').split('\n```')[0]);
    assert.equal(request.schema, 'doxie.agent-request.v1');
    assert.equal(request.agentId, 'release-note-triage');
    assert.equal(args[args.indexOf('--model') + 1], 'doxie/saved-agent');
    assert.ok(args.includes("forced_login_method='chatgpt'"));
    assert.ok(args.includes("model_reasoning_effort='high'"));
    assert.ok(existsSync(auth));
    if (request.arguments === 'fixture-fail') {
      console.log(JSON.stringify({ type: 'turn.failed', error: { message: 'Fixture provider failure.' } }));
      process.exitCode = 1;
      return;
    }
    console.error('2026-01-01T12:00:00Z WARN codex_core::shell_snapshot: Shell snapshot not supported yet for PowerShell');
    for (let attempt = 1; attempt <= 3; attempt++) {
      console.error(`2026-01-01T12:00:00Z WARN codex_rmcp_client::streamable_http_retry: MCP initialize failed retrying attempt=${attempt} http://localhost:8999/`);
    }
    console.error('2026-01-01T12:00:00Z WARN codex_mcp::rmcp_client: MCP server startup failed server_name="fixture-tools" error=HTTP request failed for http://localhost:8999/');
    console.log(JSON.stringify({ type: 'item.completed', item: { type: 'agent_message', text: 'Checking the supplied material.' } }));
    console.log(JSON.stringify({ type: 'item.completed', item: { type: 'command_execution', command: 'fixture-internal-command --inspect' } }));
    console.log(JSON.stringify({ type: 'item.completed', item: { type: 'agent_message',
      text: 'Saved agent completed using the installed sign-in and saved model.\n\n| Entry | Impact |\n| --- | --- |\n| Example update | Low |' } }));
    process.exit(0);
  }
  assert.equal(args[args.indexOf('--model') + 1], 'doxie/builder-fixture');
  const request = input.split('\n\n')
    .find(section => {
      try { return JSON.parse(section)?.schema === 'doxie.builder-request.v1'; }
      catch { return false; }
    });
  assert.ok(request, 'The builder must send an explicit JSON request.');
  const parsed = JSON.parse(request);
  assert.equal(parsed.schema, 'doxie.builder-request.v1');
  assert.equal(parsed.omittedTurns, 0);
  if (parsed.stage === 'initialize') {
    assert.deepEqual(parsed.previousTurns, []);
    assert.ok(parsed.message);
    setTimeout(() => {
      console.log(JSON.stringify({ type: 'item.completed', item: { type: 'agent_message', text: "I'm ready to build your agent. What should it do?" } }));
    }, 1400);
  } else {
    assert.equal(parsed.message, 'Build an agent\nthat triages release notes.');
    assert.equal(parsed.previousTurns.filter(turn => turn.role === 'user').length, 0);
    assert.ok(parsed.previousTurns.every(turn => turn.role === 'assistant'));
    assert.ok(!JSON.stringify(parsed.previousTurns).includes('Fixture diagnostic'));

    setTimeout(() => {
      if (!existsSync(auth)) {
        console.error('Fixture diagnostic that must stay out of the builder chat');
        console.log(JSON.stringify({ type: 'turn.failed', error: { message: 'HTTP 401 Unauthorized: Missing bearer or basic authentication in header' } }));
        process.exitCode = 1;
      } else {
        writeFileSync(join('.draft', 'manifest.json'), JSON.stringify({
          id: 'release-note-triage', name: 'Release Note Triage', description: 'Sorts release notes by impact.',
          body: 'Read supplied release notes and report each entry with its impact.',
        }));
        console.log(JSON.stringify({ type: 'item.completed', item: { type: 'agent_message', text: 'The release note triage draft is ready.' } }));
      }
    }, 900);
  }
}
