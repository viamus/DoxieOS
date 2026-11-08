import { expect, test, _electron as electron } from '@playwright/test';
import { mkdir, mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import memoryStore from '../../src/desktop/memory-store.cjs';

test('runs the workbench, a local terminal and a persisted approval workflow in Electron', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'doxie-e2e-'));
  const profile = join(directory, 'app-data');
  const root = join(profile, 'workbench');
  const outside = join(directory, 'outside');
  const codexProfile = join(root, '.providers', 'codex');
  const producerModel = {
    slug: 'doxie/producer-v1', display_name: 'Fixture producer', visibility: 'list',
    supported_reasoning_levels: [{ effort: 'medium', description: 'Balanced effort' }, { effort: 'high', description: 'Thorough effort' }],
  };
  const consumerModel = {
    slug: 'doxie/consumer-v2', display_name: 'Fixture consumer', visibility: 'list',
    supported_reasoning_levels: [{ effort: 'low', description: 'Quick effort' }],
  };
  const configuredModel = { slug: 'doxie/configured-v3', display_name: 'Fixture settings default', visibility: 'list' };
  const retiredAgent = join(root, '.doxie', 'agents', 'workflow-output-contract-designer');
  await mkdir(retiredAgent, { recursive: true });
  await writeFile(join(retiredAgent, 'manifest.json'), JSON.stringify({
    id: 'workflow-output-contract-designer', displayName: 'Workflow Output Contract Designer', category: 'Builder',
  }));
  await writeFile(join(retiredAgent, 'body.md'), 'Legacy bundled agent.');
  await mkdir(codexProfile, { recursive: true });
  await writeFile(join(codexProfile, 'models_cache.json'), JSON.stringify({ models: [producerModel] }));
  const executablePath = process.env.DOXIE_TEST_EXECUTABLE;
  const launchOptions = {
    ...(executablePath ? { executablePath, args: [] } : { args: ['.'] }),
    env: {
      ...process.env, DOXIE_USER_DATA: profile, DOXIE_TEST: '1',
      DOXIE_ROOT: outside, DOXIE_DATABASE_PATH: join(outside, 'state.db'), Storage__WorkspacesDirectory: join(outside, 'workspaces'),
    },
  };
  let application;
  try {
    application = await electron.launch(launchOptions);
    const page = await application.firstWindow();
    const pageErrors = [];
    page.on('pageerror', error => pageErrors.push(error.message));
    await expect(page.getByRole('heading', { name: 'Dashboard' })).toBeVisible();
    if (process.env.DOXIE_TEST_SCREENSHOT) await page.screenshot({ path: process.env.DOXIE_TEST_SCREENSHOT });
    expect(new URL(page.url()).protocol).toBe('doxie:');
    expect(await page.evaluate(() => typeof window.require)).toBe('undefined');
    const { storage: paths, agents, connectors } = await page.evaluate(() => window.doxie.invoke('bootstrap'));
    expect(paths['App data']).toBe(profile);
    expect(paths['State database']).toBe(join(profile, 'state.db'));
    expect(paths.workspaces).toBe(join(root, '.workspace'));
    expect(agents).toEqual([]);
    expect(connectors.map(connector => connector.id).sort()).toEqual(['aggregate', 'approval-gate', 'if-else', 'library', 'loop', 'prompt', 'sandbox', 'workspace-memory', 'write-to-workspace']);
    expect(paths.agents).toBe(join(root, '.doxie', 'agents'));
    await expect(stat(retiredAgent)).rejects.toThrow(/ENOENT/);
    await expect(stat(outside)).rejects.toThrow(/ENOENT/);
    const security = await application.evaluate(({ BrowserWindow }) => {
      const options = BrowserWindow.getAllWindows()[0].webContents.getLastWebPreferences();
      return { contextIsolation: options.contextIsolation, nodeIntegration: options.nodeIntegration, sandbox: options.sandbox };
    });
    expect(security).toEqual({ contextIsolation: true, nodeIntegration: false, sandbox: true });

    await page.getByRole('link', { name: 'Agents', exact: true }).first().click();
    await expect(page.getByRole('heading', { name: 'Agents', exact: true })).toBeVisible();
    await expect(page.getByText('Workflow Output Contract Designer', { exact: true })).toHaveCount(0);

    await page.getByRole('link', { name: 'Workspaces', exact: true }).first().click();
    await page.getByRole('button', { name: 'New workspace', exact: true }).first().click();
    await page.getByRole('dialog').getByLabel('Name', { exact: true }).fill('Migration lab');
    await page.getByRole('dialog').getByRole('button', { name: 'Create workspace' }).click();
    await expect(page.getByText('Migration lab', { exact: true }).first()).toBeVisible();
    const workspace = await page.evaluate(() => window.doxie.invoke('catalog:get', 'workspaces', 'migration-lab'));
    await writeFile(join(workspace.path, 'verification.md'), '# Local workspace\n');
    const preview = await page.evaluate(() => window.doxie.invoke('files:read', 'migration-lab', 'verification.md'));
    expect(preview.content).toContain('# Local workspace');
    await expect(page.evaluate(() => window.doxie.invoke('files:read', 'migration-lab', '../state.db'))).rejects.toThrow(/Unsafe/);

    const consoleSession = await page.evaluate(() => window.doxie.invoke('console:start', { workspaceId: 'migration-lab' }));
    await page.evaluate(id => window.doxie.invoke('console:write', id, 'echo DOXIE_TERMINAL_VERIFIED\r'), consoleSession.id);
    await expect.poll(async () => (await page.evaluate(id => window.doxie.invoke('console:snapshot', id), consoleSession.id)).output).toContain('DOXIE_TERMINAL_VERIFIED');
    await page.evaluate(id => window.doxie.invoke('console:stop', id), consoleSession.id);

    await page.evaluate(() => { window.location.hash = '/workflows/new'; });
    await page.getByRole('heading', { name: 'New workflow' }).waitFor();
    await page.getByLabel('Name', { exact: true }).fill('Review flow');
    await page.getByLabel('Node type').selectOption('approval-gate');
    await page.getByRole('button', { name: 'Add node', exact: true }).click();
    await page.getByRole('button', { name: 'Save workflow', exact: true }).first().click();
    await expect(page.getByRole('heading', { name: 'Review flow' })).toBeVisible();
    await page.getByRole('button', { name: 'Run workflow' }).click();
    await page.locator('.graph-node').filter({ hasText: 'Approval Gate' }).click();
    await expect(page.getByText('Awaiting your decision')).toBeVisible();
    const runId = new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('run');
    expect(runId).toBeTruthy();
    await expect(page.evaluate(id => window.doxie.invoke('run-files:list', id, '.doxie-transient'), runId)).rejects.toThrow(/private to the runner/);
    await page.getByRole('button', { name: 'Approve', exact: true }).click();
    await page.getByRole('dialog').getByLabel('Decision comment').fill('Checked in Electron');
    await page.getByRole('dialog').getByRole('button', { name: 'Approve', exact: true }).click();
    await expect.poll(async () => (await page.evaluate(id => window.doxie.invoke('workflow:run', id), runId)).status).toBe('Succeeded');
    expect(await readFile(join(root, '.doxie', 'workflows', 'review-flow', 'workflow.json'), 'utf8')).toContain('Review flow');
    await page.getByRole('link', { name: 'Settings', exact: true }).first().click();
    await page.getByRole('button', { name: 'Clean temporary files', exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Clean temporary files', exact: true }).click();
    await expect(page.getByText('Removed 1 temporary folder.')).toBeVisible();
    await expect(stat(join(root, '.runs', runId))).rejects.toThrow(/ENOENT/);
    expect(await readFile(join(workspace.path, 'verification.md'), 'utf8')).toContain('Local workspace');

    const harness = memoryStore.createMemoryStore({ root });
    try {
      harness.remember({ title: 'Review notes', content: 'Keep decisions traceable.', source: { provider: 'doxie-agent', runId: 'electron-harness-fixture', section: 'Review notes' } });
      harness.remember({ title: 'Standard review step', content: 'Keep decisions traceable with an independent reviewer.', source: { provider: 'doxie-agent', runId: 'electron-harness-fixture', section: 'Standard review step' } });
    } finally { harness.close(); }
    await page.getByRole('link', { name: 'Library', exact: true }).first().click();
    await expect(page.getByRole('heading', { name: 'Library', exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'New memory', exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'New library', exact: true })).toHaveCount(0);
    await page.getByRole('button', { name: 'Open memory Review notes', exact: true }).click();
    await expect(page.locator('.memory-details')).toContainText('Keep decisions traceable.');

    await page.evaluate(() => { window.location.hash = '/workflows/new'; });
    await page.getByLabel('Name', { exact: true }).fill('Query reference');
    await page.getByLabel('Node type').selectOption('library');
    await page.getByRole('button', { name: 'Add node', exact: true }).click();
    await page.getByLabel('Mode', { exact: true }).selectOption('query');
    await page.getByLabel(/Search knowledge/).fill('traceable');
    await expect(page.getByLabel(/Target workspace/)).toHaveCount(0);
    await page.getByRole('button', { name: 'Save workflow', exact: true }).first().click();
    const queryDefinition = await page.evaluate(() => window.doxie.invoke('catalog:get', 'workflows', 'query-reference'));
    expect(queryDefinition.workspaceId).toBeFalsy();
    const queryNode = queryDefinition.nodes.find(node => node.toolId === 'library');
    await page.evaluate(async ({ definition, queryNodeId }) => {
      await window.doxie.invoke('catalog:save', 'workflows', {
        ...definition,
        nodes: [{ id: 'remember-reference', label: 'Remember reference', kind: 'Tool', toolId: 'library', toolMode: 'remember',
          inputs: { title: 'Native review lesson', content: 'A traceable decision records the reviewer and result.' } }, ...definition.nodes],
        edges: [{ fromNodeId: 'remember-reference', toNodeId: queryNodeId }],
      });
    }, { definition: queryDefinition, queryNodeId: queryNode.id });
    const queried = await page.evaluate(() => window.doxie.invoke('workflow:start', 'query-reference'));
    await expect.poll(async () => (await page.evaluate(id => window.doxie.invoke('workflow:run', id), queried.id)).status).toBe('Succeeded');
    const nativeMemoryRun = await page.evaluate(id => window.doxie.invoke('workflow:run', id), queried.id);
    expect(nativeMemoryRun.nodeRuns['remember-reference'].hasTransientContext).toBe(true);
    expect(nativeMemoryRun.nodeRuns[queryNode.id].hasTransientContext).toBe(true);
    const queryReceipt = JSON.parse(await readFile(join(root, '.runs', queried.id, queryNode.id, 'library.json'), 'utf8'));
    expect(queryReceipt).toMatchObject({ schema: 'doxie.native-memory.v1', action: 'query' });
    expect(queryReceipt.result.results.some(memory => memory.title === 'Standard review step')).toBe(true);
    expect(queryReceipt.result.results.some(memory => memory.title === 'Native review lesson')).toBe(true);

    await page.getByRole('link', { name: 'Connectors', exact: true }).first().click();
    await expect(page.getByRole('heading', { name: 'Connectors', exact: true })).toBeVisible();
    await page.evaluate(() => { window.location.hash = '/connectors/prompt'; });
    await page.getByRole('button', { name: 'Execution defaults' }).click();
    await page.getByRole('dialog').getByLabel('Provider', { exact: true }).selectOption('codex');
    await expect.poll(() => page.evaluate(async () => (await window.doxie.invoke('models:list', 'codex')).models.map(({ id }) => id)))
      .toContain('doxie/producer-v1');
    await expect(page.getByRole('dialog').locator('datalist option[value="doxie/producer-v1"]')).toHaveCount(1);
    await writeFile(join(codexProfile, 'models_cache.json'), JSON.stringify({ models: [producerModel, consumerModel] }));
    await page.getByRole('dialog').getByRole('button', { name: 'Refresh models' }).click();
    await expect(page.getByRole('dialog').locator('datalist option[value="doxie/consumer-v2"]')).toHaveCount(1);
    await page.getByRole('dialog').getByLabel('Model', { exact: true }).fill('doxie/producer-v1');
    const defaultsEffort = page.getByRole('dialog').getByRole('group', { name: 'Reasoning effort' });
    await defaultsEffort.getByRole('radio', { name: 'High', exact: true }).check();
    await expect(defaultsEffort.getByRole('radio', { name: 'Low', exact: true })).toHaveCount(0);
    await page.getByRole('dialog').getByRole('button', { name: 'Save defaults' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    const fixture = fileURLToPath(new URL('./provider-fixture.cjs', import.meta.url));
    const windows = process.platform === 'win32';
    const mock = join(directory, windows ? 'provider.cmd' : 'provider');
    await writeFile(mock, windows ? `@"${process.execPath}" "${fixture}" %*\r\n`
      : `#!/bin/sh\nexec "${process.execPath}" "${fixture}" "$@"\n`, { mode: 0o755 });
    await page.evaluate(async executable => {
      await window.doxie.invoke('settings:save', { providers: { codex: { executable } } });
    }, mock);
    const standaloneEffort = page.getByRole('group', { name: 'Reasoning effort' });
    await standaloneEffort.getByRole('radio', { name: 'Medium', exact: true }).check();
    await page.getByLabel(/Prompt \(what to do with the input\)/).fill('Run the Prompt connector');
    await page.getByRole('button', { name: 'Run connector', exact: true }).click();
    await expect(page).toHaveURL(/#\/connectors\/prompt.*run=/);
    const standaloneId = new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('run');
    await expect.poll(async () => (await page.evaluate(id => window.doxie.invoke('agent:run', id), standaloneId)).status).toBe('Completed');
    expect(await page.evaluate(id => window.doxie.invoke('agent:run', id), standaloneId)).toMatchObject({ connectorId: 'prompt', effort: 'medium' });

    await page.evaluate(async () => {
      await window.doxie.invoke('catalog:save', 'workflows', {
        id: 'context-flow', name: 'Context flow', enabled: true, trigger: { kind: 'Manual' },
        nodes: [
          { id: 'producer', label: 'Produce context', kind: 'Tool', toolId: 'prompt', toolMode: 'run', inputs: { prompt: 'Produce transient context' } },
          { id: 'consumer', label: 'Consume context', kind: 'Agent', agentId: 'prompt', inputs: { prompt: 'Consume transient context' }, execution: { providerId: 'codex', model: 'doxie/consumer-v2' } },
        ],
        edges: [{ fromNodeId: 'producer', toNodeId: 'consumer' }],
      });
    });
    await page.evaluate(() => { window.location.hash = '/workflows/context-flow/edit'; });
    await page.getByRole('button', { name: /Consume context/ }).click();
    const consumerEffort = page.getByRole('group', { name: 'Reasoning effort' });
    await consumerEffort.getByRole('radio', { name: 'Low', exact: true }).check();
    await expect(consumerEffort.getByRole('radio', { name: 'High', exact: true })).toHaveCount(0);
    await page.getByRole('button', { name: 'Save workflow', exact: true }).first().click();
    await expect(page.getByRole('heading', { name: 'Context flow', exact: true })).toBeVisible();
    const contextualRun = await page.evaluate(() => window.doxie.invoke('workflow:start', 'context-flow'));
    let finishedContext;
    await expect.poll(async () => {
      finishedContext = await page.evaluate(id => window.doxie.invoke('workflow:run', id), contextualRun.id);
      return finishedContext.status;
    }, { timeout: 15_000 }).toBe('Succeeded');
    expect(finishedContext.nodeRuns.producer).toMatchObject({ model: 'doxie/producer-v1', effort: 'high', hasTransientContext: true });
    expect(finishedContext.nodeRuns.consumer).toMatchObject({ model: 'doxie/consumer-v2', effort: 'low' });
    expect(await readFile(join(root, '.runs', contextualRun.id, 'consumer', 'result.json'), 'utf8'))
      .toBe('{ "buildId": 9007199254740993, "note": "Preserve this context" }\n');
    await expect(stat(join(root, '.runs', contextualRun.id, '.doxie-transient'))).rejects.toThrow(/ENOENT/);

    await page.getByRole('link', { name: 'Settings', exact: true }).first().click();
    const codexSettings = page.locator('.item-row').filter({ has: page.getByText('OpenAI Codex', { exact: true }) });
    await codexSettings.getByRole('button', { name: 'Configure provider' }).click();
    const providerDialog = page.getByRole('dialog');
    await expect(providerDialog.getByRole('heading', { name: 'Configure OpenAI Codex' })).toBeVisible();
    await expect(providerDialog.getByLabel('Model names', { exact: true })).toHaveCount(0);
    const modelDropdown = providerDialog.getByRole('combobox', { name: 'Default model', exact: true });
    await expect(modelDropdown.getByRole('option', { name: 'Fixture producer', exact: true })).toHaveCount(1);
    await expect(modelDropdown.getByRole('option', { name: 'Fixture settings default', exact: true })).toHaveCount(0);
    await writeFile(join(codexProfile, 'models_cache.json'), JSON.stringify({ models: [producerModel, consumerModel, configuredModel] }));
    await providerDialog.getByRole('button', { name: 'Refresh models' }).click();
    await modelDropdown.selectOption({ label: 'Fixture settings default' });
    await providerDialog.getByRole('button', { name: 'Save provider' }).click();
    await expect(providerDialog).toHaveCount(0);
    expect(pageErrors).toEqual([]);

    await application.close();
    application = await electron.launch(launchOptions);
    const restarted = await application.firstWindow();
    await expect(restarted.getByRole('heading', { name: 'Dashboard' })).toBeVisible();
    const restored = await restarted.evaluate(id => window.doxie.invoke('workflow:run', id), runId);
    expect(restored.status).toBe('Succeeded');
    const restoredContext = await restarted.evaluate(id => window.doxie.invoke('workflow:run', id), contextualRun.id);
    expect(restoredContext.nodeRuns.consumer.effort).toBe('low');
    const restoredStandalone = await restarted.evaluate(id => window.doxie.invoke('agent:run', id), standaloneId);
    expect(restoredStandalone.effort).toBe('medium');
    const restoredSettings = await restarted.evaluate(async () => (await window.doxie.invoke('bootstrap')).settings);
    expect(restoredSettings.providers.codex.model).toBe(configuredModel.slug);
  } finally {
    await application?.close().catch(() => {});
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 150 });
  }
});
