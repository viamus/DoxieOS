import { expect, test, _electron as electron } from '@playwright/test';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';

const learningProvider = String.raw`
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { spawn } = require('node:child_process');
const { createInterface } = require('node:readline');
const input = JSON.parse(readFileSync(0, 'utf8'));
const prompt = input.message.content.map(part => part.text || '').join('\n');
const section = prompt.split('## Doxie request\n\n')[1];
const request = JSON.parse(section.split('\n').slice(1).join('\n').split('\n' + String.fromCharCode(96).repeat(3))[0]);
assert(prompt.includes('memory_remember'), 'Every agent run exposes autonomous memory learning.');
const configIndex = process.argv.indexOf('--mcp-config');
assert(configIndex >= 0, 'The harness supplied native memory tools.');
const config = JSON.parse(readFileSync(process.argv[configIndex + 1], 'utf8')).mcpServers.doxie_library_memory;
assert(config.env.DOXIE_MEMORY_RUN_ID, 'Run provenance is injected by the harness.');
const child = spawn(config.command, config.args, { env: { ...process.env, ...config.env }, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
let sequence = 0;
let stderr = '';
const pending = new Map();
child.stderr.on('data', data => { stderr += data.toString(); });
createInterface({ input: child.stdout }).on('line', line => {
  const message = JSON.parse(line);
  const request = pending.get(message.id);
  if (request) { pending.delete(message.id); clearTimeout(request.timer); request.resolve(message); }
});
const ended = new Promise(resolve => child.once('close', code => { for (const call of pending.values()) { clearTimeout(call.timer); call.reject(new Error('Memory tool process exited: ' + stderr)); } resolve(code); }));
function rpc(method, params) {
  const id = ++sequence;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Memory tool timed out: ' + stderr)), 20000);
    pending.set(id, { resolve, reject, timer });
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });
}
async function tool(name, args) {
  const response = await rpc('tools/call', { name, arguments: args });
  assert(!response.error && !response.result.isError, JSON.stringify(response));
  return response.result.structuredContent;
}
(async () => {
  try {
    await rpc('initialize', { protocolVersion: '2025-11-25' });
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
    if (request.arguments === 'refresh-learnings') {
      const found = await tool('memory_search', { query: 'Canary rollout', kind: 'parameter' });
      const previous = found.results.find(memory => memory.title === 'Canary rollout');
      assert(previous, 'The next run can retrieve prior agent knowledge.');
      await tool('memory_remember', { id: previous.id, title: 'Canary rollout reviewed', kind: 'parameter',
        content: 'The staging deployment allows five retries after independent review.', tags: ['release', 'deployment'],
        parameters: { environment: 'staging', retryLimit: 5, requiresReview: true }, evidence: 'Verified updated fixture deployment settings.' });
    } else {
      const rollout = await tool('memory_remember', { title: 'Canary rollout', kind: 'parameter',
        content: 'Prepare the environment and retain the learned deployment settings.', tags: ['release', 'deployment'],
        parameters: { environment: 'staging', retryLimit: 3, requiresReview: true }, evidence: 'Verified fixture deployment settings.' });
      const review = await tool('memory_remember', { title: 'Release verification', content: 'A reviewer verifies the release before it is published.', evidence: 'Verified fixture review process.' });
      await tool('memory_relate', { fromId: review.id, toId: rollout.id, type: 'supports' });
    }
  } finally { child.stdin.end(); await ended; }
  const response = 'Completed the local verification and retained its reusable learnings through the harness.';
  console.log(JSON.stringify({ type: 'assistant', message: { content: [{ type: 'text', text: response }] } }));
  console.log(JSON.stringify({ type: 'result', result: response, is_error: false, usage: { input_tokens: 20, output_tokens: 8 }, total_cost_usd: 0.002 }));
})().catch(error => { child.kill(); console.error(error.stack); process.exitCode = 1; });
`;

test('observes autonomous agent learnings, revision history and harness-managed source sync', async ({}, testInfo) => {
  const directory = await mkdtemp(join(tmpdir(), 'doxie-library-memory-'));
  const profile = join(directory, 'app-data');
  const root = join(profile, 'workbench');
  const providerScript = join(directory, 'learning-provider.cjs');
  const provider = join(directory, process.platform === 'win32' ? 'learning-provider.cmd' : 'learning-provider');
  let application;
  try {
    const agentPath = join(root, '.doxie', 'agents', 'memory-learning-agent');
    await mkdir(agentPath, { recursive: true });
    await writeFile(join(agentPath, 'manifest.json'), JSON.stringify({ id: 'memory-learning-agent', displayName: 'Memory Learning Agent', category: 'Other', execution: { providerId: 'claude-code', model: 'doxie-memory-fixture' } }));
    await writeFile(join(agentPath, 'body.md'), 'Verify the deployment process and retain reusable evidence through the native memory tools.');
    await writeFile(providerScript, learningProvider);
    await writeFile(provider, process.platform === 'win32' ? `@echo off\r\n"${process.execPath}" "${providerScript}" %*\r\n` : `#!/bin/sh\nexec "${process.execPath}" "${providerScript}" "$@"\n`, { mode: 0o700 });
    const executablePath = process.env.DOXIE_TEST_EXECUTABLE;
    application = await electron.launch({ ...(executablePath ? { executablePath, args: [] } : { args: ['.'] }), env: { ...process.env, DOXIE_USER_DATA: profile, DOXIE_TEST: '1' } });
    const page = await application.firstWindow();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toBeVisible();
    await page.evaluate(executable => window.doxie.invoke('settings:save', { providers: { 'claude-code': { executable } } }), provider);
    await page.getByRole('link', { name: 'Library', exact: true }).first().click();
    await expect(page.getByRole('heading', { name: 'Library', exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Memory map', exact: true })).toBeVisible();
    await expect(page.getByText('No agent learnings yet', { exact: true })).toBeVisible();
    await expect(page.getByRole('region', { name: 'Search by meaning', exact: true })).toContainText('135 MB');
    await expect(page.getByRole('button', { name: 'Enable semantic search', exact: true })).toBeEnabled();
    for (const name of ['New memory', 'New library', 'Revise memory', 'Archive memory', 'Connect']) await expect(page.getByRole('button', { name, exact: true })).toHaveCount(0);

    await page.evaluate(() => { window.location.hash = '/agents/memory-learning-agent'; });
    await expect(page.getByRole('heading', { name: 'Memory Learning Agent', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Run agent', exact: true }).click();
    await expect(page).toHaveURL(/#\/agents\/memory-learning-agent.*run=/);
    const firstRunId = new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('run');
    await expect.poll(async () => (await page.evaluate(id => window.doxie.invoke('agent:run', id), firstRunId)).status).toBe('Completed');
    const firstRun = await page.evaluate(id => window.doxie.invoke('agent:run', id), firstRunId);
    expect(firstRun.contextMetrics.dispatches).toBe(1);
    const original = (await page.evaluate(() => window.doxie.invoke('memory:list', { kind: 'parameter' })))[0];
    expect(original.parameters).toEqual({ environment: 'staging', requiresReview: true, retryLimit: 3 });
    expect(original.source).toMatchObject({ provider: 'doxie-agent', runId: firstRunId });
    await page.getByRole('link', { name: 'Library', exact: true }).first().click();
    await expect(page.locator('.memory-map-edge')).toHaveCount(1);
    await page.getByRole('button', { name: 'Open memory Canary rollout', exact: true }).click();
    await expect(page.locator('.memory-details').getByRole('heading', { name: 'Canary rollout', exact: true })).toBeVisible();
    await expect(page.locator('.memory-parameters')).toContainText('staging');
    await expect(page.locator('.memory-details')).toContainText(firstRunId);
    for (const name of ['New memory', 'New library', 'Revise memory', 'Archive memory', 'Connect']) await expect(page.getByRole('button', { name, exact: true })).toHaveCount(0);
    const draggable = page.getByRole('button', { name: 'Open memory Canary rollout', exact: true });
    await expect(draggable).toBeVisible();
    const graphNode = await draggable.elementHandle();
    expect(graphNode).not.toBeNull();
    const initialPosition = await draggable.getAttribute('transform');
    const refreshLibrary = () => application.evaluate(async ({ BrowserWindow }) => {
      const contents = BrowserWindow.getAllWindows()[0].webContents;
      for (let index = 0; index < 8; index++) {
        contents.send('doxie:event', { type: 'memory:changed' });
        await new Promise(resolve => setTimeout(resolve, 20));
      }
    });
    await refreshLibrary();
    expect(await graphNode.evaluate(node => node.isConnected)).toBe(true);
    await expect(draggable).toHaveAttribute('transform', initialPosition);
    await expect(draggable).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('.memory-details').getByRole('heading', { name: 'Canary rollout', exact: true })).toBeVisible();
    await draggable.scrollIntoViewIfNeeded();
    let bounds;
    await expect.poll(async () => {
      bounds = await draggable.boundingBox();
      return Boolean(bounds?.width && bounds?.height);
    }).toBe(true);
    await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
    await page.mouse.down();
    await page.mouse.move(bounds.x + bounds.width / 2 + 38, bounds.y + bounds.height / 2 + 24, { steps: 5 });
    await page.mouse.up();
    await expect(draggable).not.toHaveAttribute('transform', initialPosition);
    const draggedPosition = await draggable.getAttribute('transform');
    await page.getByRole('button', { name: 'Zoom in memory map', exact: true }).click();
    await expect(page.locator('.memory-map-scale')).toHaveText('120%');
    await refreshLibrary();
    expect(await graphNode.evaluate(node => node.isConnected)).toBe(true);
    await expect(draggable).toHaveAttribute('transform', draggedPosition);
    await expect(page.locator('.memory-map-scale')).toHaveText('120%');
    await page.getByRole('button', { name: 'Reset memory map', exact: true }).click();
    await expect(page.locator('.memory-map-scale')).toHaveText('100%');
    await page.getByRole('searchbox', { name: 'Search memories…', exact: true }).fill('x'.repeat(513));
    await expect(page.getByRole('alert').filter({ hasText: '512' })).toBeVisible();
    await expect(page.getByText('Showing the last available view.', { exact: true })).toBeVisible();
    expect(await graphNode.evaluate(node => node.isConnected)).toBe(true);
    await expect(draggable).toHaveAttribute('aria-pressed', 'true');
    await page.getByRole('searchbox', { name: 'Search memories…', exact: true }).fill('');
    await expect(page.getByRole('alert').filter({ hasText: '512' })).toHaveCount(0);
    await page.getByRole('searchbox', { name: 'Search memories…', exact: true }).fill('canary');
    await expect(page.locator('.memory-result')).toHaveCount(1);
    await page.getByLabel('Memory type', { exact: true }).selectOption('entity');
    await expect(page.getByText('No matching memories', { exact: true })).toBeVisible();
    await page.getByLabel('Memory type', { exact: true }).selectOption('');
    await page.getByRole('searchbox', { name: 'Search memories…', exact: true }).fill('');
    await expect(page.locator('.memory-result')).toHaveCount(2);

    const nextRun = await page.evaluate(() => window.doxie.invoke('agent:start', 'memory-learning-agent', { arguments: 'refresh-learnings' }));
    await expect.poll(async () => (await page.evaluate(id => window.doxie.invoke('agent:run', id), nextRun.id)).status).toBe('Completed');
    expect((await page.evaluate(id => window.doxie.invoke('agent:run', id), nextRun.id)).contextMetrics.dispatches).toBe(1);
    await expect(page.getByRole('button', { name: 'Open memory Canary rollout reviewed', exact: true })).toBeVisible();
    const revised = (await page.evaluate(() => window.doxie.invoke('memory:search', { query: 'Canary rollout reviewed' }))).results.find(memory => memory.title === 'Canary rollout reviewed');
    expect(revised.id).not.toBe(original.id);
    expect(revised.parameters.retryLimit).toBe(5);
    expect(revised.source).toMatchObject({ provider: 'doxie-agent', runId: nextRun.id });
    expect((await page.evaluate(id => window.doxie.invoke('memory:get', id), original.id)).supersededBy).toBe(revised.id);
    await page.getByRole('button', { name: 'Open memory Canary rollout reviewed', exact: true }).click();
    await page.locator('.memory-details').getByRole('button', { name: 'Open previous version', exact: true }).click();
    await expect(page.locator('.memory-details').getByRole('heading', { name: 'Canary rollout', exact: true })).toBeVisible();
    await page.locator('.memory-details').getByRole('button', { name: 'Open current version', exact: true }).click();
    await expect(page.locator('.memory-details').getByRole('heading', { name: 'Canary rollout reviewed', exact: true })).toBeVisible();

    const feed = await page.evaluate(() => window.doxie.invoke('memory:feedStatus'));
    expect(feed.state).toBe('disabled');
    expect(feed.sources).toEqual([]);
    const sourcePanel = page.getByRole('region', { name: 'Agent memory sources', exact: true });
    await expect(sourcePanel).toContainText('Automatic source sync is disabled for this profile.');
    await expect(sourcePanel.getByRole('button', { name: 'Refresh agent memories', exact: true })).toBeDisabled();
    for (const name of ['Import Codex', 'Import Claude', 'Choose folder', 'New memory', 'New library', 'Revise memory', 'Archive memory', 'Connect']) await expect(page.getByRole('button', { name, exact: true })).toHaveCount(0);
    expect((await page.evaluate(() => window.doxie.invoke('memory:semanticStatus'))).state).toBe('missing');
    await page.screenshot({ path: testInfo.outputPath('central-memory-map.png'), fullPage: true });
    expect(errors).toEqual([]);
  } finally {
    await application?.close().catch(() => {});
    if (dirname(resolve(directory)) !== resolve(tmpdir()) || !basename(directory).startsWith('doxie-library-memory-')) throw new Error('Refusing cleanup outside the memory test directory.');
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 150 });
  }
});
