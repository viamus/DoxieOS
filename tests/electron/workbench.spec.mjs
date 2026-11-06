import { expect, test, _electron as electron } from '@playwright/test';
import { mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

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
    const { storage: paths, agents } = await page.evaluate(() => window.doxie.invoke('bootstrap'));
    expect(paths['App data']).toBe(profile);
    expect(paths['State database']).toBe(join(profile, 'state.db'));
    expect(paths.workspaces).toBe(join(root, '.workspace'));
    expect(agents.some(agent => agent.id === 'approval-gate')).toBe(true);
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

    await page.evaluate(() => window.doxie.invoke('catalog:save', 'workflows', {
        id: 'review-flow', name: 'Review flow', enabled: true, trigger: { kind: 'manual' },
        nodes: [{ id: 'review', kind: 'Agent', agentId: 'approval-gate', label: 'Review' }], edges: [],
    }));
    await page.evaluate(() => { window.location.hash = '/workflows/review-flow'; });
    await expect(page.getByRole('heading', { name: 'Review flow' })).toBeVisible();
    await page.getByRole('button', { name: 'Run workflow' }).click();
    await expect(page.getByText('Awaiting your decision')).toBeVisible();
    const runId = new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('run');
    expect(runId).toBeTruthy();
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
