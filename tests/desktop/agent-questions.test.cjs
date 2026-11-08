const assert = require('node:assert/strict');
const { test } = require('node:test');
const { mkdtemp, writeFile, readFile, readdir, rm } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { createStorage } = require('../../src/desktop/storage.cjs');
const { createAgents } = require('../../src/desktop/agents.cjs');
const { readQuestion, validateAnswer } = require('../../src/desktop/questions.cjs');

async function until(check) {
  for (let attempt = 0; attempt < 150; attempt++) {
    if (check()) return;
    await new Promise(resolve => setTimeout(resolve, 20));
  }
  throw new Error('The fixture did not reach its expected state.');
}

async function fixture(t, scenario = 'question', nativeMemory = false) {
  const root = await mkdtemp(join(tmpdir(), 'doxie-questions-'));
  const storage = createStorage({ root, databasePath: ':memory:' });
  const capture = join(root, 'capture.json');
  const executable = join(root, process.platform === 'win32' ? 'provider.cmd' : 'provider');
  await writeFile(join(root, 'mock.cjs'), `
    const fs = require('node:fs');
    let input = '';
    process.stdin.setEncoding('utf8');
    process.stdin.on('data', data => input += data);
    process.stdin.on('end', () => {
      const prompt = JSON.parse(input).message.content[0].text;
      const file = process.env.DOXIE_FIXTURE_CAPTURE;
      const entries = fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, 'utf8')) : [];
      const turn = entries.length;
      entries.push({ prompt, hints: fs.readFileSync(process.env.DOXIE_OPERATOR_HINTS_FILE, 'utf8'),
        mcpConfig: process.argv.includes('--mcp-config') ? JSON.parse(fs.readFileSync(process.argv[process.argv.indexOf('--mcp-config') + 1], 'utf8')) : null,
        strictMcp: process.argv.includes('--strict-mcp-config'),
        contextExists: Boolean(process.env.DOXIE_WORKFLOW_CONTEXT_OUT && fs.existsSync(process.env.DOXIE_WORKFLOW_CONTEXT_OUT)),
        memoryIndex: process.env.DOXIE_CONTEXT_INDEX ? JSON.parse(fs.readFileSync(process.env.DOXIE_CONTEXT_INDEX, 'utf8')) : null });
      fs.writeFileSync(file, JSON.stringify(entries));
      const scenario = process.env.DOXIE_FIXTURE_SCENARIO;
      if ((scenario === 'repeat' ? turn < 2 : turn === 0) && scenario !== 'complete') {
        const question = { question: 'Which release should I inspect?', options: ['Stable', 'Preview'], context: 'Read the manifest; no release was changed.' };
        fs.writeFileSync(process.env.DOXIE_USER_QUESTION_OUT, scenario === 'invalid' ? '{broken' : JSON.stringify(question));
        if (process.env.DOXIE_WORKFLOW_CONTEXT_OUT) fs.writeFileSync(process.env.DOXIE_WORKFLOW_CONTEXT_OUT, '{"stale":true}');
      }
      console.log(JSON.stringify({ type: 'assistant', message: { content: [{ type: 'text', text: turn ? 'Inspected the chosen release.' : 'First turn.' }] } }));
      if (!(scenario === 'missing-usage' && turn === 1)) console.log(JSON.stringify({ type: 'result', usage: { input_tokens: 12, output_tokens: 5 }, total_cost_usd: 0.02 }));
      if (scenario === 'failed') process.exitCode = 1;
    });
  `);
  await writeFile(executable, process.platform === 'win32' ? `@"${process.execPath}" "%~dp0mock.cjs" %*\r\n`
    : `#!/bin/sh\nexec "${process.execPath}" "${join(root, 'mock.cjs')}" "$@"\n`, { mode: 0o755 });
  storage.setSetting('desktop.providers', { 'claude-code': { executable,
    environment: { DOXIE_FIXTURE_CAPTURE: capture, DOXIE_FIXTURE_SCENARIO: scenario } } });
  storage.save('agents', { id: 'inspect', name: 'Inspect release' });
  const notifications = [];
  if (nativeMemory) storage.memory = {};
  const agents = createAgents({ storage, notify: data => notifications.push(data) });
  t.after(async () => { await agents.close(); storage.close(); await rm(root, { recursive: true, force: true }); });
  return { root, storage, agents, notifications, captured: async () => JSON.parse(await readFile(capture, 'utf8')) };
}

test('a question pauses the same run, notifies once and resumes with exact answers and accumulated usage', async t => {
  const { root, agents, notifications, captured } = await fixture(t);
  const questions = [];
  const run = agents.start('inspect', { arguments: 'Inspect the selected release.', contextOutputPath: join(root, 'context.json'),
    onQuestion: (question, id) => questions.push({ question, id }) });
  let settled = false;
  const result = agents.wait(run.id).then(value => { settled = true; return value; });
  await until(() => agents.getRun(run.id).status === 'AwaitingInput');
  assert.equal(settled, false);
  const waiting = agents.getRun(run.id);
  assert.equal(waiting.finishedAt, null);
  assert.equal(waiting.pendingQuestion.question, 'Which release should I inspect?');
  assert.equal(notifications.filter(item => item.kind === 'question').length, 1);
  assert.equal(agents.clearHistory(), 0);
  assert.throws(() => agents.answer(run.id, 'stale', 'Preview'), /no longer awaiting/);
  assert.throws(() => agents.answer(run.id, waiting.pendingQuestion.id, '  '), /Enter an answer/);
  agents.addHint(run.id, 'Use the existing checkout.');
  const answer = '  Preview\nKeep the exact target.  ';
  assert.equal(agents.answer(run.id, waiting.pendingQuestion.id, answer).id, run.id);
  assert.throws(() => agents.answer(run.id, waiting.pendingQuestion.id, answer), /no longer awaiting/);
  const completed = await result;
  assert.equal(completed.status, 'Completed');
  assert.equal(completed.pendingQuestion, null);
  assert.equal(completed.interactions[0].answer, answer);
  assert.equal(completed.usage.inputTokens, 24);
  assert.equal(completed.usage.outputTokens, 10);
  assert.equal(completed.usage.totalCostUsd, 0.04);
  assert.equal(completed.contextMetrics.dispatches, 2);
  assert.equal(completed.contextMetrics.usageComplete, true);
  const dispatches = await captured();
  assert(dispatches[1].prompt.includes(JSON.stringify(answer)));
  assert(dispatches[1].prompt.includes('Read the manifest; no release was changed.'));
  assert(dispatches[1].hints.includes('Use the existing checkout.'));
  assert.equal(dispatches[1].contextExists, false);
  assert.equal(questions[0].id, run.id);
  assert.equal(questions.at(-1).question, null);
  assert.deepEqual(await readdir(join(root, '.runtime', 'agent-hints')), []);
});

test('autonomous learning and native run provenance apply to ordinary agents and builder executions', async t => {
  const { root, agents, captured } = await fixture(t, 'complete', true);
  await writeFile(join(root, '.mcp.json'), JSON.stringify({ mcpServers: { 'configured-tool': { command: 'fixture-tool', args: ['fixture'] } } }));
  const run = agents.start('inspect', { arguments: 'Verify the supplied input.' });
  assert.equal((await agents.wait(run.id)).status, 'Completed');
  const builder = agents.runBuilder('agent', { providerId: 'claude-code', cwd: root, prompt: 'Prepare the draft.' });
  assert.equal((await agents.wait(builder.id)).status, 'Completed');
  const [ordinary, building] = await captured();
  for (const [dispatch, id] of [[ordinary, run.id], [building, builder.id]]) {
    assert(dispatch.prompt.includes('Autonomous harness learning'));
    assert(dispatch.prompt.includes('Do not wait for a user to fill a Brain form'));
    assert(dispatch.prompt.includes('Before substantive work, call memory_search'));
    assert(dispatch.prompt.includes('inspect memory_graph'));
    assert(dispatch.prompt.includes('Revalidate stale memories'));
    assert.equal(dispatch.mcpConfig.mcpServers.doxie_library_memory.env.DOXIE_MEMORY_RUN_ID, id);
    assert.equal(dispatch.strictMcp, true);
  }
  assert.equal(ordinary.mcpConfig.mcpServers['configured-tool'].command, 'fixture-tool');
  assert.equal(building.mcpConfig.mcpServers['configured-tool'], undefined);
});

test('stopping a run waiting for the user resolves wait and prevents a late answer', async t => {
  const { agents } = await fixture(t);
  const run = agents.start('inspect');
  await until(() => agents.getRun(run.id).status === 'AwaitingInput');
  const questionId = agents.getRun(run.id).pendingQuestion.id;
  assert.equal(agents.stop(run.id), true);
  assert.equal((await agents.wait(run.id)).status, 'Cancelled');
  assert.throws(() => agents.answer(run.id, questionId, 'Preview'), /no longer active/);
});

test('invalid question JSON fails the run and a failed provider cannot publish a pending question', async t => {
  for (const scenario of ['invalid', 'failed']) {
    const { agents, notifications } = await fixture(t, scenario);
    const run = agents.start('inspect');
    const result = await agents.wait(run.id);
    assert.equal(result.status, 'Failed');
    assert.equal(result.pendingQuestion, null);
    assert.equal(notifications.filter(item => item.kind === 'question').length, 0);
    if (scenario === 'invalid') assert(result.output.some(line => /valid UTF-8 JSON/.test(line.text)));
  }
});

test('partial provider usage remains explicitly incomplete across clarification turns', async t => {
  const { agents } = await fixture(t, 'missing-usage');
  const run = agents.start('inspect');
  await until(() => agents.getRun(run.id).status === 'AwaitingInput');
  agents.answer(run.id, agents.getRun(run.id).pendingQuestion.id, 'Stable');
  const result = await agents.wait(run.id);
  assert.equal(result.usage.inputTokens, 12);
  assert.equal(result.usage.totalCostUsd, null);
  assert.equal(result.contextMetrics.reportedDispatches, 1);
  assert.equal(result.contextMetrics.usageComplete, false);
});

test('a second clarification rejects the first question ID and preserves both answers', async t => {
  const { agents } = await fixture(t, 'repeat');
  const run = agents.start('inspect');
  await until(() => agents.getRun(run.id).status === 'AwaitingInput');
  const first = agents.getRun(run.id).pendingQuestion.id;
  agents.answer(run.id, first, 'Preview');
  await until(() => agents.getRun(run.id).status === 'AwaitingInput');
  const second = agents.getRun(run.id).pendingQuestion.id;
  assert.notEqual(first, second);
  assert.throws(() => agents.answer(run.id, first, 'Wrong late answer'), /no longer awaiting/);
  agents.answer(run.id, second, 'Stable after all');
  const result = await agents.wait(run.id);
  assert.equal(result.status, 'Completed');
  assert.deepEqual(result.interactions.map(item => item.answer), ['Preview', 'Stable after all']);
  assert.equal(result.usage.inputTokens, 36);
});

test('startup marks a question from an ended application session as interrupted', async t => {
  const { agents, storage } = await fixture(t);
  const run = agents.start('inspect');
  await until(() => agents.getRun(run.id).status === 'AwaitingInput');
  const question = agents.getRun(run.id).pendingQuestion;
  await agents.close();
  storage.db.prepare("UPDATE agent_runs SET status='AwaitingInput', finished_at=NULL, question_json=? WHERE id=?").run(JSON.stringify(question), run.id);
  const reopened = createAgents({ storage });
  const restored = reopened.getRun(run.id);
  assert.equal(restored.status, 'Interrupted');
  assert.equal(restored.pendingQuestion, null);
  assert.ok(restored.finishedAt);
  assert.throws(() => reopened.answer(run.id, question.id, 'Late answer'), /no longer active/);
  await reopened.close();
});

test('agent prompts preserve explicit inputs without injecting workspace notes or memory folders', async t => {
  const { root, storage, agents, captured } = await fixture(t, 'complete');
  storage.save('agents', { id: 'inspect', name: 'Inspect release', body: 'Mandatory instructions. '.repeat(3500) });
  const workspace = storage.save('workspaces', { id: 'release', name: 'Release' });
  storage.writeFile('workspaces', workspace.id, 'WORKSPACE.md', 'Always preserve the release branch.');
  for (let i = 0; i < 8; i++) storage.writeFile('workspaces', workspace.id, `memory/reference-${i}.md`, `Reference ${i}\n${'x'.repeat(9000)}\nEnd ${i}`);
  const raw = '  Exact input\nWith spacing  ';
  const run = agents.start('inspect', { workspaceId: workspace.id, inputs: { raw }, arguments: raw });
  const result = await agents.wait(run.id);
  const [{ prompt, memoryIndex }] = await captured();
  assert(prompt.includes('Mandatory instructions. '.repeat(3500)));
  assert(!prompt.includes('Always preserve the release branch.'));
  assert(prompt.includes(JSON.stringify(raw)));
  assert(!prompt.includes('End 0'));
  assert(!prompt.includes('Reference 2\n'));
  assert.equal(memoryIndex, null);
  assert.equal(result.contextMetrics.memory.selectedItems, 0);
  assert.equal(result.contextMetrics.memory.omittedItems, 0);
  assert.equal(await readFile(join(workspace.path, 'WORKSPACE.md'), 'utf8'), 'Always preserve the release branch.');
  assert.deepEqual(await readdir(join(root, '.runtime', 'agent-hints')), []);
});

test('question files and answers reject oversized, empty or malformed values', async t => {
  const root = await mkdtemp(join(tmpdir(), 'doxie-question-schema-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const file = join(root, 'question.json');
  assert.equal(readQuestion(file), null);
  for (const data of [{ question: '' }, { question: 'Question?', options: [12] }, { question: 'Question?', context: {} }]) {
    await writeFile(file, JSON.stringify(data));
    assert.throws(() => readQuestion(file));
  }
  await writeFile(file, JSON.stringify({ question: 'Question?', options: ['Yes', 'No'] }));
  const question = readQuestion(file);
  assert.equal(validateAnswer(question, question.id, '  My answer  '), '  My answer  ');
  assert.throws(() => validateAnswer(question, question.id, 'x'.repeat(17000)), /16 KiB/);
});
