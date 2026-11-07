import { expect, test, _electron as electron } from '@playwright/test';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

test('opens the workspace folder from its card and file browser and reports native failures', async () => {
  const profile = await mkdtemp(join(tmpdir(), 'doxie folder e2e-'));
  const executablePath = process.env.DOXIE_TEST_EXECUTABLE;
  let application;
  try {
    application = await electron.launch({
      ...(executablePath ? { executablePath, args: [] } : { args: ['.'] }),
      env: { ...process.env, DOXIE_USER_DATA: profile, DOXIE_TEST: '1' },
    });
    const page = await application.firstWindow();
    await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toBeVisible();
    const workspace = await page.evaluate(() => window.doxie.invoke('catalog:save', 'workspaces', {
      id: 'folder-test', name: 'Folder test',
    }));
    await application.evaluate(({ shell }) => {
      globalThis.folderRequests = [];
      shell.openPath = async path => { globalThis.folderRequests.push(path); return ''; };
    });

    await page.getByRole('link', { name: 'Workspaces', exact: true }).first().click();
    await page.getByRole('button', { name: 'Folder', exact: true }).click();
    await expect.poll(() => application.evaluate(() => globalThis.folderRequests)).toEqual([workspace.path]);
    await page.getByRole('link', { name: 'Files', exact: true }).click();
    await page.getByRole('button', { name: 'Open folder', exact: true }).click();
    await expect.poll(() => application.evaluate(() => globalThis.folderRequests)).toEqual([workspace.path, workspace.path]);

    await application.evaluate(({ shell }) => {
      shell.openPath = async () => { throw new Error('Folder launcher unavailable'); };
    });
    await page.getByRole('button', { name: 'Open folder', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('Folder launcher unavailable');
  } finally {
    await application?.close().catch(() => {});
    await rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 150 });
  }
});
