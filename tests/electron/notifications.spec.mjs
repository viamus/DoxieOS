import { expect, test, _electron as electron } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

test('browses compact notifications and persists individual deletion, read state and clearing', async ({}, testInfo) => {
  const directory = await mkdtemp(join(tmpdir(), 'doxie-notifications-'));
  const profile = join(directory, 'app-data');
  const root = join(profile, 'workbench');
  const now = Date.now();
  const longBody = 'Review the proposed changes before continuing. '.repeat(14).trim();
  const items = [
    { id: 'report', title: 'Report ready', body: 'The weekly operations report is ready to review.', severity: 'Success', href: '/history' },
    { id: 'review', title: 'Review needed', body: longBody, severity: 'Warning', content: '# Review notes\n\nVerify the **updated totals** before publishing.', contentFormat: 'markdown', href: '/history' },
    { id: 'synced', title: 'Workspace synced', body: 'The project files are up to date.', severity: 'Info', readAt: new Date(now - 60_000).toISOString() },
    { id: 'failed', title: 'Previous run failed', body: 'The provider was unavailable. Open the run to retry.', severity: 'Error', readAt: new Date(now - 120_000).toISOString() },
  ].map((item, index) => ({ ...item, timestamp: new Date(now - index * 60_000).toISOString() }));
  // Keep node:sqlite outside Playwright's ESM loader on Node 22.
  execFileSync(process.execPath, ['-e', `
    const { readFileSync } = require('node:fs');
    const { createStorage } = require('./src/desktop/storage.cjs');
    const storage = createStorage({ root: process.argv[1], databasePath: process.argv[2] });
    try { storage.setSetting('desktop.notifications', JSON.parse(readFileSync(0, 'utf8'))); }
    finally { storage.close(); }
  `, root, join(profile, 'state.db')], { cwd: process.cwd(), input: JSON.stringify(items), windowsHide: true });

  const executablePath = process.env.DOXIE_TEST_EXECUTABLE;
  const options = {
    ...(executablePath ? { executablePath, args: [] } : { args: ['.'] }),
    env: { ...process.env, DOXIE_USER_DATA: profile, DOXIE_TEST: '1' },
  };
  let application;
  const errors = [];
  const openNotifications = async () => {
    const page = await application.firstWindow();
    page.on('pageerror', error => errors.push(error.message));
    await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toBeVisible();
    await page.evaluate(() => { window.location.hash = '/notifications'; });
    await expect(page.getByRole('heading', { name: 'Notifications', exact: true })).toBeVisible();
    return page;
  };

  try {
    application = await electron.launch(options);
    let page = await openNotifications();
    const rows = () => page.locator('.notification-list > .notification-row');
    const row = title => rows().filter({ has: page.locator('.notification-title').filter({ hasText: title }) });
    await expect(rows()).toHaveCount(4);
    await expect(page.getByRole('button', { name: 'All 4', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('button', { name: 'Unread 2', exact: true })).toHaveAttribute('aria-pressed', 'false');
    for (const detail of await page.locator('.notification-detail').all()) await expect(detail).toBeHidden();
    const heights = await rows().evaluateAll(elements => elements.map(element => element.getBoundingClientRect().height));
    expect(heights.every(height => height <= 110)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath('notifications-compact.png'), fullPage: true });

    const review = row('Review needed');
    await review.getByRole('button', { name: 'Show notification: Review needed', exact: true }).click();
    await expect(review.getByRole('button', { name: 'Hide notification: Review needed', exact: true })).toHaveAttribute('aria-expanded', 'true');
    await expect(review.locator('.notification-detail')).toContainText(longBody);
    await expect(review.locator('.notification-detail')).toContainText('updated totals');
    await expect(review).toHaveAttribute('data-unread', 'true');
    await page.screenshot({ path: testInfo.outputPath('notifications-expanded.png'), fullPage: true });
    await review.getByRole('button', { name: 'View source', exact: true }).click();
    await expect(page).toHaveURL(/#\/history$/);
    await page.evaluate(() => { window.location.hash = '/notifications'; });
    await expect(rows()).toHaveCount(4);

    await page.getByRole('button', { name: 'Unread 2', exact: true }).click();
    await expect(rows()).toHaveCount(2);
    await expect(row('Workspace synced')).toHaveCount(0);
    await page.getByRole('button', { name: 'Delete notification: Review needed', exact: true }).click();
    await expect(rows()).toHaveCount(1);
    await expect(page.getByRole('button', { name: 'Unread 1', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await page.getByRole('button', { name: 'All 3', exact: true }).click();
    await page.getByRole('button', { name: 'Delete notification: Workspace synced', exact: true }).click();
    await expect(rows()).toHaveCount(2);
    expect((await page.evaluate(() => window.doxie.invoke('bootstrap'))).notifications.map(item => item.id)).toEqual(['report', 'failed']);

    await application.close();
    application = await electron.launch(options);
    page = await openNotifications();
    await expect(rows()).toHaveCount(2);
    await expect(page.getByRole('button', { name: 'All 2', exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Unread 1', exact: true })).toBeVisible();

    await application.evaluate(({ BrowserWindow }) => { BrowserWindow.getAllWindows()[0].setSize(760, 700); });
    const overflow = await page.evaluate(() => {
      const main = document.querySelector('main');
      return { document: document.documentElement.scrollWidth > window.innerWidth, main: main.scrollWidth > main.clientWidth };
    });
    expect(overflow).toEqual({ document: false, main: false });
    await page.screenshot({ path: testInfo.outputPath('notifications-narrow.png'), fullPage: true });

    await page.getByRole('button', { name: 'Mark read: Report ready', exact: true }).click();
    await expect(row('Report ready')).toHaveAttribute('data-unread', 'false');
    await expect(page.getByRole('button', { name: 'Mark all read', exact: true })).toBeDisabled();
    await page.getByRole('button', { name: 'Unread 0', exact: true }).click();
    await expect(rows()).toHaveCount(0);
    await expect(page.getByText('No unread notifications', { exact: true })).toBeVisible();
    await page.reload();
    await expect(page.getByRole('button', { name: 'Unread 0', exact: true })).toBeVisible();

    await page.getByRole('button', { name: 'Clear all', exact: true }).click();
    await expect(page.getByRole('dialog')).toContainText('Clear all notifications');
    await page.getByRole('dialog').getByRole('button', { name: 'Cancel', exact: true }).click();
    expect((await page.evaluate(() => window.doxie.invoke('bootstrap'))).notifications).toHaveLength(2);
    await page.getByRole('button', { name: 'Clear all', exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Clear all', exact: true }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Clear all', exact: true })).toBeDisabled();
    await page.getByRole('button', { name: 'All 0', exact: true }).click();
    await expect(page.getByText('No notifications', { exact: true })).toBeVisible();
    await application.close();
    application = await electron.launch(options);
    page = await openNotifications();
    await expect(page.getByText('No notifications', { exact: true })).toBeVisible();
    expect((await page.evaluate(() => window.doxie.invoke('bootstrap'))).notifications).toEqual([]);
    expect(errors).toEqual([]);
  } finally {
    await application?.close().catch(() => {});
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 150 });
  }
});
