import { expect, test, _electron as electron } from '@playwright/test';
import { mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

test('runs the workbench, a local terminal and a persisted approval workflow in Electron', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'doxie-e2e-'));
  const profile = join(directory, 'app-data');
  const root = join(profile, 'workbench');
  const outside = join(directory, 'outside');
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
    const { storage: paths, agents, tools } = await page.evaluate(() => window.doxie.invoke('bootstrap'));
    expect(paths['App data']).toBe(profile);
    expect(paths['State database']).toBe(join(profile, 'state.db'));
    expect(paths.workspaces).toBe(join(root, '.workspace'));
    expect(agents.map(agent => agent.id).sort()).toEqual(['agent-builder', 'prompt', 'workflow-builder', 'workflow-output-contract-designer']);
    expect(tools.map(tool => tool.id).sort()).toEqual(['aggregate', 'approval-gate', 'if-else', 'library', 'loop', 'sandbox', 'write-to-workspace']);
    expect(paths.agents).toBe(join(root, '.doxie', 'agents'));
    await expect(stat(outside)).rejects.toThrow(/ENOENT/);
    const security = await application.evaluate(({ BrowserWindow }) => {
      const options = BrowserWindow.getAllWindows()[0].webContents.getLastWebPreferences();
      return { contextIsolation: options.contextIsolation, nodeIntegration: options.nodeIntegration, sandbox: options.sandbox };
    });
    expect(security).toEqual({ contextIsolation: true, nodeIntegration: false, sandbox: true });

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

    await page.getByRole('link', { name: 'Libraries', exact: true }).first().click();
    await page.getByRole('button', { name: 'New library', exact: true }).click();
    await page.getByRole('dialog').getByLabel('Name', { exact: true }).fill('Local reference');
    await page.getByRole('dialog').getByRole('button', { name: 'Create library' }).click();
    await page.getByRole('button', { name: 'New memory', exact: true }).click();
    await page.getByRole('dialog').getByLabel('Name', { exact: true }).fill('Review notes');
    await page.getByRole('dialog').getByLabel('Markdown', { exact: true }).fill('Keep decisions traceable.');
    await page.getByRole('dialog').getByRole('button', { name: 'Save memory' }).click();
    await page.locator('summary').filter({ hasText: 'Review notes' }).click();
    await expect(page.getByText('Keep decisions traceable.', { exact: true })).toBeVisible();

    await page.evaluate(() => { window.location.hash = '/agents/prompt'; });
    await page.getByRole('button', { name: 'Execution defaults' }).click();
    await page.getByRole('dialog').getByLabel('Provider', { exact: true }).selectOption('codex');
    await page.getByRole('dialog').getByLabel('Model', { exact: true }).fill('doxie/producer-v1');
    await page.getByRole('dialog').getByRole('button', { name: 'Save defaults' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    const fixture = fileURLToPath(new URL('./provider-fixture.cjs', import.meta.url));
    const windows = process.platform === 'win32';
    const mock = join(directory, windows ? 'provider.cmd' : 'provider');
    await writeFile(mock, windows ? `@"${process.execPath}" "${fixture}" %*\r\n`
      : `#!/bin/sh\nexec "${process.execPath}" "${fixture}" "$@"\n`, { mode: 0o755 });
    const contextualRun = await page.evaluate(async executable => {
      await window.doxie.invoke('settings:save', { providers: { codex: { executable } } });
      await window.doxie.invoke('catalog:save', 'workflows', {
        id: 'context-flow', name: 'Context flow', enabled: true, trigger: { kind: 'Manual' },
        nodes: [
          { id: 'producer', kind: 'Agent', agentId: 'prompt', inputs: { prompt: 'Produce transient context' } },
          { id: 'consumer', kind: 'Agent', agentId: 'prompt', inputs: { prompt: 'Consume transient context' }, execution: { providerId: 'codex', model: 'doxie/consumer-v2' } },
        ],
        edges: [{ fromNodeId: 'producer', toNodeId: 'consumer' }],
      });
      return window.doxie.invoke('workflow:start', 'context-flow');
    }, mock);
    let finishedContext;
    await expect.poll(async () => {
      finishedContext = await page.evaluate(id => window.doxie.invoke('workflow:run', id), contextualRun.id);
      return finishedContext.status;
    }, { timeout: 15_000 }).toBe('Succeeded');
    expect(finishedContext.nodeRuns.producer).toMatchObject({ model: 'doxie/producer-v1', hasTransientContext: true });
    expect(finishedContext.nodeRuns.consumer).toMatchObject({ model: 'doxie/consumer-v2' });
    expect(await readFile(join(root, '.runs', contextualRun.id, 'consumer', 'result.json'), 'utf8'))
      .toBe('{ "buildId": 9007199254740993, "note": "Preserve this context" }\n');
    await expect(stat(join(root, '.runs', contextualRun.id, '.doxie-transient'))).rejects.toThrow(/ENOENT/);
    expect(pageErrors).toEqual([]);

    await application.close();
    application = await electron.launch(launchOptions);
    const restarted = await application.firstWindow();
    await expect(restarted.getByRole('heading', { name: 'Dashboard' })).toBeVisible();
    const restored = await restarted.evaluate(id => window.doxie.invoke('workflow:run', id), runId);
    expect(restored.status).toBe('Succeeded');
  } finally {
    await application?.close().catch(() => {});
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 150 });
  }
});
