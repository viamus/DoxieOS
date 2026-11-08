import { expect, test, _electron as electron } from '@playwright/test';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';

const providerFixture = String.raw`
const assert = require('node:assert/strict');
const { appendFileSync, readFileSync, writeFileSync } = require('node:fs');
const { join } = require('node:path');
const input = JSON.parse(readFileSync(0, 'utf8'));
const prompt = input.message.content.map(part => part.text || '').join('\n');
const section = prompt.split('## Doxie request\n\n')[1];
const request = JSON.parse(section.split('\n').slice(1).join('\n').split('\n' + String.fromCharCode(96).repeat(3))[0]);
const continued = prompt.includes('## User clarifications\n');
const consumer = process.env.DOXIE_AGENT_ID === 'question-consumer';
const clarifications = continued ? JSON.parse(prompt.split('## User clarifications\n')[1].split('\nContinue the original task')[0]) : [];
appendFileSync(process.env.QUESTION_FIXTURE_TRACE, JSON.stringify({
  agentId: request.agentId, nodeId: request.handoff?.nodeId, continued,
  answer: clarifications.at(-1)?.answer,
}) + '\n');
let response;
if (consumer) {
  assert.equal(request.handoff.sources.length, 1);
  const handoff = JSON.parse(request.handoff.sources[0].contextJson);
  assert.equal(handoff.answer, 'Use the staging environment in São Paulo.');
  writeFileSync(join(request.outputDirectory, 'receipt.json'), JSON.stringify(handoff));
  response = 'The downstream step received the user-approved environment.';
} else if (!continued) {
  if (process.env.DOXIE_WORKFLOW_CONTEXT_OUT) writeFileSync(process.env.DOXIE_WORKFLOW_CONTEXT_OUT, JSON.stringify({ stage: 'before-answer' }));
  writeFileSync(process.env.DOXIE_USER_QUESTION_OUT, JSON.stringify({
    question: 'Which environment should I prepare?', options: ['Development', 'Staging'],
    context: 'The plan is drafted. Wait for the environment before publishing the result.',
  }));
  response = 'I need your environment choice before continuing.';
} else {
  const answer = clarifications.at(-1).answer;
  writeFileSync(join(request.outputDirectory, 'answer.md'), '# Selected environment\n\n' + answer);
  if (process.env.DOXIE_WORKFLOW_CONTEXT_OUT) writeFileSync(process.env.DOXIE_WORKFLOW_CONTEXT_OUT, JSON.stringify({ answer, approvedBy: 'operator' }));
  response = 'Prepared the plan using your answer: ' + answer;
}
console.log(JSON.stringify({ type: 'assistant', message: { content: [{ type: 'text', text: response }] } }));
console.log(JSON.stringify({ type: 'result', result: response, is_error: false,
  usage: { input_tokens: continued ? 30 : 20, output_tokens: continued ? 7 : 5 }, total_cost_usd: continued ? 0.003 : 0.002 }));
`;

async function traceEntries(file) {
  try { return (await readFile(file, 'utf8')).trim().split('\n').filter(Boolean).map(line => JSON.parse(line)); }
  catch (error) { if (error.code === 'ENOENT') return []; throw error; }
}

test('answers agent and workflow questions in Electron and follows their notifications without running downstream early', async ({}, testInfo) => {
  const directory = await mkdtemp(join(tmpdir(), 'doxie-questions-'));
  const profile = join(directory, 'app-data');
  const root = join(profile, 'workbench');
  const fixture = join(directory, 'question-provider.cjs');
  const executable = join(directory, process.platform === 'win32' ? 'question-provider.cmd' : 'question-provider');
  const trace = join(directory, 'provider-trace.jsonl');
  const answer = 'Use the staging environment in São Paulo.';
  let application;
  try {
    for (const [id, displayName] of [['clarifying-agent', 'Question Agent'], ['question-consumer', 'Question Consumer']]) {
      const path = join(root, '.doxie', 'agents', id);
      await mkdir(path, { recursive: true });
      await writeFile(join(path, 'manifest.json'), JSON.stringify({
        id, displayName, category: 'Other', description: 'Fixture for user clarification.',
        execution: { providerId: 'claude-code', model: 'doxie-question-fixture' },
      }));
      await writeFile(join(path, 'body.md'), 'Ask for the missing environment before preparing the final result.');
    }
    await writeFile(fixture, providerFixture);
    await writeFile(executable, process.platform === 'win32'
      ? `@echo off\r\n"${process.execPath}" "${fixture}" %*\r\n`
      : `#!/bin/sh\nexec "${process.execPath}" "${fixture}" "$@"\n`, { mode: 0o700 });
    const executablePath = process.env.DOXIE_TEST_EXECUTABLE;
    application = await electron.launch({
      ...(executablePath ? { executablePath, args: [] } : { args: ['.'] }),
      env: { ...process.env, DOXIE_USER_DATA: profile, DOXIE_TEST: '1' },
    });
    const page = await application.firstWindow();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toBeVisible();
    await page.evaluate(({ executable, trace }) => window.doxie.invoke('settings:save', {
      defaultProvider: 'claude-code', providers: { 'claude-code': { executable, model: 'doxie-question-fixture', environment: { QUESTION_FIXTURE_TRACE: trace } } },
    }), { executable, trace });

    await page.evaluate(() => { window.location.hash = '/agents/clarifying-agent'; });
    await expect(page.getByRole('heading', { name: 'Question Agent', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Run agent', exact: true }).click();
    await expect(page).toHaveURL(/#\/agents\/clarifying-agent.*run=/);
    const standaloneId = new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('run');
    expect(standaloneId).toBeTruthy();
    await expect.poll(async () => (await page.evaluate(id => window.doxie.invoke('agent:run', id), standaloneId)).status).toBe('AwaitingInput');
    let question = page.getByRole('form', { name: 'Waiting for your answer' });
    await expect(question).toContainText('Which environment should I prepare?');
    await expect(question.getByRole('button', { name: 'Send answer', exact: true })).toBeDisabled();
    await expect(page.getByRole('region', { name: 'Agent response', exact: true })).toContainText('Waiting for your answer.');
    await expect(page.getByLabel('Guide the running agent…')).toHaveCount(0);
    const pending = await page.evaluate(id => window.doxie.invoke('agent:run', id), standaloneId);
    const notification = (await page.evaluate(() => window.doxie.invoke('bootstrap'))).notifications.find(item => item.runId === standaloneId && item.questionId === pending.pendingQuestion.id);
    expect(notification).toMatchObject({ kind: 'question', body: 'Which environment should I prepare?' });
    expect(notification.readAt).toBeUndefined();
    await page.screenshot({ path: testInfo.outputPath('agent-question.png'), fullPage: true });

    await page.evaluate(() => { window.location.hash = '/notifications'; });
    await expect(page.getByRole('heading', { name: 'Notifications', exact: true })).toBeVisible();
    const notificationPanel = page.locator('.notification-row').filter({ has: page.locator('.notification-title').filter({ hasText: notification.title }) });
    await expect(notificationPanel).toContainText(notification.body);
    await notificationPanel.getByRole('button', { name: `Show notification: ${notification.title}`, exact: true }).click();
    await notificationPanel.getByRole('button', { name: 'View source', exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`run=${standaloneId}`));
    question = page.getByRole('form', { name: 'Waiting for your answer' });
    await question.getByRole('button', { name: 'Staging', exact: true }).click();
    await expect(question.getByLabel('Your answer', { exact: true })).toHaveValue('Staging');
    expect((await traceEntries(trace)).filter(item => item.agentId === 'clarifying-agent')).toHaveLength(1);
    await question.getByLabel('Your answer', { exact: true }).fill(answer);
    await question.getByRole('button', { name: 'Send answer', exact: true }).click();
    await expect.poll(async () => (await page.evaluate(id => window.doxie.invoke('agent:run', id), standaloneId)).status).toBe('Completed');
    const completed = await page.evaluate(id => window.doxie.invoke('agent:run', id), standaloneId);
    expect(completed.id).toBe(standaloneId);
    expect(completed.interactions).toHaveLength(1);
    expect(completed.interactions[0]).toMatchObject({ question: pending.pendingQuestion.question, answer });
    expect(completed.contextMetrics).toMatchObject({ dispatches: 2, reportedDispatches: 2, usageComplete: true });
    expect(completed.usage).toMatchObject({ inputTokens: 50, outputTokens: 12, totalCostUsd: 0.005 });
    await expect(page.getByRole('region', { name: 'Agent response', exact: true })).toContainText(answer);
    await page.locator('summary').filter({ hasText: 'Questions and answers (1)' }).click();
    await expect(page.locator('.question-history')).toContainText(answer);
    await page.locator('summary').filter({ hasText: 'Context sent (estimate)' }).click();
    await expect(page.locator('.context-metrics')).toContainText('Model requests');
    expect((await page.evaluate(() => window.doxie.invoke('bootstrap'))).notifications.find(item => item.id === notification.id).readAt).toBeTruthy();
    await page.reload();
    await expect(page.getByRole('region', { name: 'Agent response', exact: true })).toContainText(answer);

    await page.evaluate(() => window.doxie.invoke('catalog:save', 'workflows', {
      id: 'question-flow', name: 'Question flow', enabled: true, trigger: { kind: 'Manual' },
      nodes: [
        { id: 'clarify', label: 'Clarify target', kind: 'Agent', agentId: 'clarifying-agent' },
        { id: 'after-answer', label: 'After answer', kind: 'Agent', agentId: 'question-consumer' },
      ],
      edges: [{ fromNodeId: 'clarify', toNodeId: 'after-answer' }],
    }));
    await page.evaluate(() => { window.location.hash = '/workflows/question-flow'; });
    await expect(page.getByRole('heading', { name: 'Question flow', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Run workflow', exact: true }).click();
    await expect(page).toHaveURL(/#\/workflows\/question-flow.*run=/);
    const workflowId = new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('run');
    await expect.poll(async () => (await page.evaluate(id => window.doxie.invoke('workflow:run', id), workflowId)).status).toBe('AwaitingInput');
    const waitingWorkflow = await page.evaluate(id => window.doxie.invoke('workflow:run', id), workflowId);
    expect(waitingWorkflow.nodeRuns.clarify).toMatchObject({ status: 'AwaitingInput', pendingQuestion: { question: 'Which environment should I prepare?' } });
    expect(waitingWorkflow.nodeRuns['after-answer'].status).toBe('Pending');
    expect((await traceEntries(trace)).filter(item => item.agentId === 'question-consumer')).toHaveLength(0);
    await expect(page.getByRole('heading', { name: 'Your answer is needed', exact: true })).toBeVisible();
    await page.locator('.graph-node').filter({ hasText: 'After answer' }).click();
    await expect(page.getByRole('heading', { name: 'Your answer is needed', exact: true })).toBeVisible();
    await expect(page.getByRole('form', { name: 'Waiting for your answer' })).toBeVisible();
    await page.screenshot({ path: testInfo.outputPath('workflow-question.png'), fullPage: true });

    const workflowNotification = (await page.evaluate(() => window.doxie.invoke('bootstrap'))).notifications.find(item => item.kind === 'question' && item.runId === waitingWorkflow.nodeRuns.clarify.agentRunId);
    expect(workflowNotification).toBeTruthy();
    await page.evaluate(() => { window.location.hash = '/'; });
    await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toBeVisible();
    await application.evaluate(({ BrowserWindow }, href) => {
      BrowserWindow.getAllWindows()[0].webContents.send('doxie:event', { type: 'navigate', href });
    }, workflowNotification.href);
    await expect(page).toHaveURL(new RegExp(`run=${waitingWorkflow.nodeRuns.clarify.agentRunId}`));
    await expect(page.getByRole('form', { name: 'Waiting for your answer' })).toBeVisible();
    await page.evaluate(run => { window.location.hash = `/workflows/question-flow?run=${run}`; }, workflowId);
    await expect(page.getByRole('heading', { name: 'Your answer is needed', exact: true })).toBeVisible();
    const workflowQuestion = page.getByRole('form', { name: 'Waiting for your answer' });
    await workflowQuestion.getByLabel('Your answer', { exact: true }).fill(answer);
    await workflowQuestion.getByRole('button', { name: 'Send answer', exact: true }).click();
    await expect.poll(async () => (await page.evaluate(id => window.doxie.invoke('workflow:run', id), workflowId)).status).toBe('Succeeded');
    await expect(page.getByRole('heading', { name: 'Your answer is needed', exact: true })).toHaveCount(0);
    expect(JSON.parse(await readFile(join(root, '.runs', workflowId, 'after-answer', 'receipt.json'), 'utf8'))).toEqual({ answer, approvedBy: 'operator' });
    const entries = await traceEntries(trace);
    expect(entries.filter(item => item.nodeId === 'clarify')).toHaveLength(2);
    expect(entries.filter(item => item.agentId === 'question-consumer')).toHaveLength(1);
    expect(entries.findIndex(item => item.agentId === 'question-consumer')).toBeGreaterThan(entries.findIndex(item => item.nodeId === 'clarify' && item.continued));
    expect((await page.evaluate(() => window.doxie.invoke('bootstrap'))).notifications.find(item => item.id === workflowNotification.id).readAt).toBeTruthy();
    expect(errors).toEqual([]);
  } finally {
    await application?.close().catch(() => {});
    if (dirname(resolve(directory)) !== resolve(tmpdir()) || !basename(directory).startsWith('doxie-questions-')) throw new Error('Refusing cleanup outside the question test directory.');
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 150 });
  }
});
