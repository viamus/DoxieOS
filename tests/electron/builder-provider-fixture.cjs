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
  assert.equal(args[args.indexOf('--model') + 1], 'doxie/builder-fixture');
  const request = readFileSync(0, 'utf8').split('\n\n')
    .find(section => section.startsWith('{\n  "schema": "doxie.builder-request.v1"'));
  assert.deepEqual(JSON.parse(request), {
    schema: 'doxie.builder-request.v1', previousTurns: [], omittedTurns: 0,
    message: 'Build an agent that triages release notes.',
  });

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
}
