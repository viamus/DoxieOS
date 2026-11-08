import { expect, test, _electron as electron } from '@playwright/test';
import { createRequire } from 'node:module';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
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
      await tool('memory_relate', { fromId: review.id, toId: rollout.id, type: 'supports', weight: 0.85 });
    }
  } finally { child.stdin.end(); await ended; }
  const response = 'Completed the local verification and retained its reusable learnings through the harness.';
  console.log(JSON.stringify({ type: 'assistant', message: { content: [{ type: 'text', text: response }] } }));
  console.log(JSON.stringify({ type: 'result', result: response, is_error: false, usage: { input_tokens: 20, output_tokens: 8 }, total_cost_usd: 0.002 }));
})().catch(error => { child.kill(); console.error(error.stack); process.exitCode = 1; });
`;

test('observes Brain learning, lifecycle, weighted links and explicit provider import', async ({}, testInfo) => {
  const directory = await mkdtemp(join(tmpdir(), 'doxie-library-memory-'));
  const profile = join(directory, 'app-data');
  const root = join(profile, 'workbench');
  const codexHome = join(directory, 'codex-fixture');
  const claudeHome = join(directory, 'claude-fixture');
  const providerScript = join(directory, 'learning-provider.cjs');
  const provider = join(directory, process.platform === 'win32' ? 'learning-provider.cmd' : 'learning-provider');
  let application;
  try {
    const agentPath = join(root, '.doxie', 'agents', 'memory-learning-agent');
    await mkdir(agentPath, { recursive: true });
    await writeFile(join(agentPath, 'manifest.json'), JSON.stringify({ id: 'memory-learning-agent', displayName: 'Memory Learning Agent', category: 'Other', execution: { providerId: 'claude-code', model: 'doxie-memory-fixture' } }));
    await writeFile(join(agentPath, 'body.md'), 'Verify the deployment process and retain reusable evidence through the native memory tools.');
    await mkdir(join(codexHome, 'memories_v2'), { recursive: true });
    await mkdir(claudeHome, { recursive: true });
    await writeFile(join(codexHome, 'memories_v2', 'review.md'), '# Imported review checklist\n\nReview a release after checking its recorded evidence.');
    await writeFile(join(codexHome, 'memories_v2', 'large.md'), '# Large imported note\n\n' + 'Local semantic memory context. '.repeat(12000));
    await writeFile(providerScript, learningProvider);
    await writeFile(provider, process.platform === 'win32' ? `@echo off\r\n"${process.execPath}" "${providerScript}" %*\r\n` : `#!/bin/sh\nexec "${process.execPath}" "${providerScript}" "$@"\n`, { mode: 0o700 });
    const executablePath = process.env.DOXIE_TEST_EXECUTABLE;
    application = await electron.launch({ ...(executablePath ? { executablePath, args: [] } : { args: ['.'] }), env: { ...process.env, DOXIE_USER_DATA: profile, DOXIE_TEST: '1', CODEX_HOME: codexHome, CLAUDE_CONFIG_DIR: claudeHome } });
    const page = await application.firstWindow();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toBeVisible();
    await page.evaluate(executable => window.doxie.invoke('settings:save', { providers: { 'claude-code': { executable } } }), provider);
    await page.getByRole('link', { name: 'Brain', exact: true }).first().click();
    await expect(page.getByRole('heading', { name: 'Brain', exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Memory map', exact: true })).toBeVisible();
    await expect(page.getByText('No agent learnings yet', { exact: true })).toBeVisible();
    expect((await page.evaluate(() => window.doxie.invoke('memory:stats'))).total).toBe(0);
    await expect(page.getByRole('button', { name: 'Find import sources', exact: true })).toBeEnabled();
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
    await page.getByRole('link', { name: 'Brain', exact: true }).first().click();
    await expect(page.locator('.memory-map-edge')).toHaveCount(1);
    await expect(page.locator('.memory-map-edge title')).toContainText('85%');
    await page.getByLabel('Graph relationships', { exact: true }).selectOption('semantic');
    await expect(page.locator('.memory-map-edge')).toHaveCount(0);
    await page.getByLabel('Graph relationships', { exact: true }).selectOption('evidence');
    await expect(page.locator('.memory-map-edge')).toHaveCount(1);
    await page.getByLabel('Minimum connection strength', { exact: true }).selectOption('0.9');
    await expect(page.locator('.memory-map-edge')).toHaveCount(0);
    await page.getByLabel('Minimum connection strength', { exact: true }).selectOption('0');
    await page.getByLabel('Graph relationships', { exact: true }).selectOption('all');
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

    await page.getByRole('button', { name: 'Open memory Canary rollout', exact: true }).click();
    await page.locator('.memory-details .memory-history-controls > summary').click();
    await page.getByRole('button', { name: 'Flag for review', exact: true }).click();
    await expect(page.locator('.memory-details .memory-lifecycle')).toHaveText('Needs review');
    await page.getByText('History filters', { exact: true }).click();
    await page.getByLabel('Memory lifecycle', { exact: true }).selectOption('stale');
    await expect(page.locator('.memory-result')).toHaveCount(1);
    await page.getByRole('button', { name: 'Confirm current', exact: true }).click();
    await expect(page.locator('.memory-details .memory-lifecycle')).toHaveText('Current');
    await page.getByLabel('Memory lifecycle', { exact: true }).selectOption('');
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

    const beforeImport = (await page.evaluate(() => window.doxie.invoke('memory:stats'))).total;
    const sourcePanel = page.getByRole('region', { name: 'Import memories', exact: true });
    await expect(sourcePanel).toContainText('when you choose');
    await expect(sourcePanel.getByRole('checkbox')).toHaveCount(0);
    await sourcePanel.getByRole('button', { name: 'Find import sources', exact: true }).click();
    await expect(sourcePanel.getByRole('checkbox')).toHaveCount(1);
    await sourcePanel.getByRole('checkbox').check();
    await sourcePanel.getByRole('button', { name: 'Preview selected sources', exact: true }).click();
    await expect(sourcePanel.locator('.memory-import-file')).toHaveCount(2);
    expect((await page.evaluate(() => window.doxie.invoke('memory:stats'))).total).toBe(beforeImport);
    await sourcePanel.getByRole('checkbox', { name: /Large imported note/ }).uncheck();
    await sourcePanel.getByRole('button', { name: 'Import selected memories', exact: true }).click();
    await expect(sourcePanel.getByRole('status')).toContainText('1 memories added');
    expect((await page.evaluate(() => window.doxie.invoke('memory:stats'))).total).toBe(beforeImport + 1);
    await expect(page.locator('.memory-result').filter({ hasText: 'Imported review checklist' })).toBeVisible();
    await expect(page.locator('.memory-result').filter({ hasText: 'Imported review checklist' })).toContainText('Imported memory');
    expect((await page.evaluate(() => window.doxie.invoke('memory:semanticStatus'))).state).toBe('missing');
    await page.screenshot({ path: testInfo.outputPath('central-memory-map.png'), fullPage: true });
    expect(errors).toEqual([]);
  } finally {
    await application?.close().catch(() => {});
    if (dirname(resolve(directory)) !== resolve(tmpdir()) || !basename(directory).startsWith('doxie-library-memory-')) throw new Error('Refusing cleanup outside the memory test directory.');
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 150 });
  }
});


test('explores weighted semantic neighborhoods and preserves their provenance', async ({}, testInfo) => {
  const directory = await mkdtemp(join(tmpdir(), 'doxie-brain-map-'));
  const profile = join(directory, 'app-data');
  const root = join(profile, 'workbench');
  const require = createRequire(import.meta.url);
  const { createMemoryStore } = require('../../src/desktop/memory-store.cjs');
  let application;
  let store;
  try {
    await require('../../src/desktop/brain-runtime.cjs').initializeMemoryRuntime();
    store = createMemoryStore({ root });
    const add = (title, content, extra = {}) => store.remember({ title, content, source: { provider: 'doxie-agent', runId: 'synthetic-graph-fixture', section: title, label: 'Synthetic local graph test fixture.' }, ...extra });
    const rollout = add('Canary deployment', 'Release a small canary before promoting a deployment.', { tags: ['release', 'deployment'], origin: 'learned' });
    const review = add('Independent verification', 'A reviewer checks canary metrics before promotion.', { tags: ['release', 'verification'], origin: 'optimized' });
    const retries = add('Retry policy', 'Use five retries for transient deployment failures.', { kind: 'parameter', parameters: { maxRetries: 5 }, tags: ['deployment'] });
    const oldPolicy = add('Earlier retry policy', 'An earlier record suggested unlimited deployment retries.', { origin: 'imported', source: { provider: 'codex', path: 'synthetic/retries.md' } });
    const production = add('Production environment', 'Production receives verified canary deployments.', { kind: 'entity', tags: ['release'] });
    const owner = add('Release ownership', 'The release owner coordinates production promotion.');
    store.relate({ fromId: review.id, toId: rollout.id, type: 'supports', weight: 0.94 });
    store.relate({ fromId: retries.id, toId: oldPolicy.id, type: 'contradicts', weight: 0.82 });
    store.relate({ fromId: rollout.id, toId: production.id, type: 'mentions', weight: 0.88 });
    store.relate({ fromId: production.id, toId: owner.id, type: 'mentions', weight: 0.84 });
    store.review({ id: oldPolicy.id, status: 'stale', evidence: 'Synthetic record requires another review.' });
    for (const [memory, vector] of [[rollout, [1, 0.1, 0]], [review, [0.9, 0.2, 0.1]], [retries, [0.8, 0.3, 0.1]], [oldPolicy, [0.7, 0.4, 0.1]], [production, [0.85, 0.1, 0.3]]]) store.setEmbedding(memory.id, vector, { model: 'synthetic-local-fixture' });
    await store.rebuildSemanticRelations({ model: 'synthetic-local-fixture', threshold: 0.65, neighbors: 3 });
    await store.close(); store = null;
    const executablePath = process.env.DOXIE_TEST_EXECUTABLE;
    application = await electron.launch({ ...(executablePath ? { executablePath, args: [] } : { args: ['.'] }), env: { ...process.env, DOXIE_USER_DATA: profile, DOXIE_TEST: '1', CODEX_HOME: join(directory, 'empty-codex'), CLAUDE_CONFIG_DIR: join(directory, 'empty-claude') } });
    const page = await application.firstWindow();
    await page.getByRole('link', { name: 'Brain', exact: true }).first().click();
    await expect(page.locator('.memory-map-node')).toHaveCount(6);
    await expect.poll(() => page.locator('.memory-map-edge.inferred').count()).toBeGreaterThan(0);
    await expect(page.locator('.memory-map-edge.conflict')).toHaveCount(1);
    await page.getByRole('button', { name: 'Open memory Independent verification', exact: true }).click();
    await expect(page.locator('.memory-details')).toContainText('Optimized knowledge');
    await expect(page.locator('.memory-details')).toContainText('94% strength');
    await page.getByText('Unexpected connections', { exact: true }).click();
    await page.getByRole('button', { name: 'Explore associations', exact: true }).click();
    await expect(page.locator('.memory-association').first()).toContainText('Hypothesis');
    await expect(page.locator('.memory-association-path')).not.toHaveCount(0);
    await page.getByLabel('Graph relationships', { exact: true }).selectOption('semantic');
    await expect(page.locator('.memory-map-edge:not(.inferred)')).toHaveCount(0);
    await page.getByLabel('Graph relationships', { exact: true }).selectOption('conflicts');
    await expect(page.locator('.memory-map-edge')).toHaveCount(1);
    await page.getByLabel('Graph relationships', { exact: true }).selectOption('all');
    await page.screenshot({ path: testInfo.outputPath('brain-weighted-semantic-graph.png'), fullPage: true });
  } finally {
    await store?.close();
    await application?.close().catch(() => {});
    if (dirname(resolve(directory)) !== resolve(tmpdir()) || !basename(directory).startsWith('doxie-brain-map-')) throw new Error('Refusing cleanup outside the Brain test directory.');
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 150 });
  }
});


test('extracts typed knowledge locally and clears Brain only after confirmation', async ({}, testInfo) => {
  const directory = await mkdtemp(join(tmpdir(), 'doxie-brain-extract-'));
  const profile = join(directory, 'app-data');
  const root = join(profile, 'workbench');
  const codexHome = join(directory, 'codex-fixture');
  const providerFile = join(codexHome, 'memories_v2', 'service.md');
  const modelFile = join(root, '.models', 'semantic', 'preserved-model.txt');
  const content = '# Service settings\n\nRepository: https://github.com/example/service\n\n' + String.fromCharCode(96).repeat(3) + 'json\n{"retryLimit":5,"maxTokens":8000,"reviewRequired":true}\n' + String.fromCharCode(96).repeat(3);
  const require = createRequire(import.meta.url);
  const { createMemoryStore } = require('../../src/desktop/memory-store.cjs');
  let store;
  let application;
  try {
    await mkdir(dirname(providerFile), { recursive: true });
    await writeFile(providerFile, content);
    await mkdir(dirname(modelFile), { recursive: true });
    await writeFile(modelFile, 'Retain downloaded models when knowledge is cleared.');
    await require('../../src/desktop/brain-runtime.cjs').initializeMemoryRuntime();
    store = createMemoryStore({ root });
    const source = store.remember({ title: 'Service settings', content, origin: 'imported', source: { provider: 'codex', path: providerFile } });
    await store.close(); store = null;
    const executablePath = process.env.DOXIE_TEST_EXECUTABLE;
    application = await electron.launch({ ...(executablePath ? { executablePath, args: [] } : { args: ['.'] }), env: { ...process.env, DOXIE_USER_DATA: profile, DOXIE_TEST: '1', CODEX_HOME: codexHome, CLAUDE_CONFIG_DIR: join(directory, 'empty-claude') } });
    const page = await application.firstWindow();
    const dashboardBrain = page.locator('.metric[href="#/libraries"]');
    await expect(dashboardBrain.locator('.metric-count')).toHaveText('Brain');
    await expect(dashboardBrain).toContainText('1 memory');
    await expect.poll(async () => (await page.evaluate(() => window.doxie.invoke('memory:extractionStatus'))).state).toBe('ready');
    const automaticallyExtracted = await page.evaluate(() => window.doxie.invoke('memory:stats'));
    expect(automaticallyExtracted.entities).toBeGreaterThan(0);
    expect(automaticallyExtracted.parameters).toBeGreaterThan(0);
    await expect(dashboardBrain).toContainText(`${automaticallyExtracted.entities} ${automaticallyExtracted.entities === 1 ? 'entity' : 'entities'}`);
    await page.getByRole('link', { name: 'Brain', exact: true }).first().click();
    await expect(page.locator('.memory-stats .memory-stat')).toHaveCount(4);
    await expect(page.getByText('Need review', { exact: true })).toHaveCount(0);
    const extraction = page.getByRole('region', { name: 'Extract knowledge', exact: true });
    await extraction.getByRole('button', { name: 'Extract knowledge', exact: true }).click();
    await expect(extraction.getByRole('status')).toContainText('memories processed');
    const extracted = await page.evaluate(() => window.doxie.invoke('memory:stats'));
    expect(extracted.entities).toBeGreaterThan(0);
    expect(extracted.parameters).toBeGreaterThan(0);
    expect(extracted.total).toBe(automaticallyExtracted.total);
    const parameters = await page.evaluate(() => window.doxie.invoke('memory:list', { kind: 'parameter' }));
    const settings = parameters.find(memory => memory.parameters?.retryLimit === 5);
    expect(settings?.parameters).toMatchObject({ retryLimit: 5, maxTokens: 8000, reviewRequired: true });
    expect(settings.source).toMatchObject({ provider: 'doxie-extractor', path: 'memory:' + source.id });
    await page.getByLabel('Memory type', { exact: true }).selectOption('parameter');
    await expect(page.locator('.memory-result')).toHaveCount(parameters.length);
    const extractedParameter = page.locator('.memory-result').filter({ hasText: settings.title });
    await expect(extractedParameter).toContainText('Locally extracted');
    await extractedParameter.click();
    await expect(page.locator('.memory-details')).toContainText('Locally extracted');
    await page.locator('.memory-details').getByRole('button', { name: 'Open source memory', exact: true }).click();
    await expect(page.locator('.memory-details h3')).toHaveText('Service settings');
    await extraction.getByRole('button', { name: 'Extract knowledge', exact: true }).click();
    await expect(extraction.getByRole('button', { name: 'Extract knowledge', exact: true })).toBeEnabled();
    expect((await page.evaluate(() => window.doxie.invoke('memory:stats'))).total).toBe(extracted.total);
    await page.getByRole('button', { name: 'Clear Brain', exact: true }).click();
    const confirmation = page.getByRole('dialog');
    await expect(confirmation).toContainText('Original provider files and downloaded models are preserved');
    await confirmation.getByRole('button', { name: 'Cancel', exact: true }).click();
    expect((await page.evaluate(() => window.doxie.invoke('memory:stats'))).total).toBe(extracted.total);
    await page.getByLabel('Memory type', { exact: true }).selectOption('');
    await page.screenshot({ path: testInfo.outputPath('brain-extracted-knowledge.png'), fullPage: true });
    await page.getByRole('button', { name: 'Clear Brain', exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Clear Brain', exact: true }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(page.getByText('No agent learnings yet', { exact: true })).toBeVisible();
    expect(await page.evaluate(() => window.doxie.invoke('memory:stats'))).toMatchObject({ total: 0, memories: 0, entities: 0, parameters: 0, relations: 0 });
    expect(await readFile(providerFile, 'utf8')).toBe(content);
    expect(await readFile(modelFile, 'utf8')).toContain('Retain downloaded models');
    await page.getByRole('link', { name: 'Dashboard', exact: true }).first().click();
    await expect(dashboardBrain.locator('.metric-count')).toHaveText('Brain');
    await expect(dashboardBrain).toContainText('0 memories');
    await expect(dashboardBrain).toContainText('0 entities');
  } finally {
    await store?.close();
    await application?.close().catch(() => {});
    if (dirname(resolve(directory)) !== resolve(tmpdir()) || !basename(directory).startsWith('doxie-brain-extract-')) throw new Error('Refusing cleanup outside the Brain extraction test directory.');
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 150 });
  }
});
