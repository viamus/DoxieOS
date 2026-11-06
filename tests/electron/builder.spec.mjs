import { expect, test, _electron as electron } from '@playwright/test';
import { mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

test('authenticates Codex after a failed builder turn, retries it once, and saves the agent', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'doxie-builder-e2e-'));
  const profile = join(directory, 'app-data');
  const localCodex = join(profile, 'local-codex');
  const executablePath = process.env.DOXIE_TEST_EXECUTABLE;
  const fixture = fileURLToPath(new URL('./builder-provider-fixture.cjs', import.meta.url));
  const windows = process.platform === 'win32';
  const mock = join(directory, windows ? 'provider.cmd' : 'provider');
  let application;
  try {
    await writeFile(mock, windows ? `@"${process.execPath}" "${fixture}" %*\r\n`
      : `#!/bin/sh\nexec "${process.execPath}" "${fixture}" "$@"\n`, { mode: 0o755 });
    application = await electron.launch({
      ...(executablePath ? { executablePath, args: [] } : { args: ['.'] }),
      env: {
        ...process.env, DOXIE_USER_DATA: profile, DOXIE_TEST: '1', CODEX_HOME: localCodex,
        OPENAI_API_KEY: 'test', OPENAI_BASE_URL: 'http://127.0.0.1:1',
      },
    });
    const page = await application.firstWindow();
    await expect(page.getByRole('heading', { name: 'Dashboard' })).toBeVisible();
    await page.evaluate(executable => window.doxie.invoke('settings:save', {
      defaultProvider: 'codex', providers: { codex: { executable, model: 'doxie/builder-fixture' } },
    }), mock);

    await page.getByRole('link', { name: 'Agents', exact: true }).first().click();
    await expect(page.getByText('No agents yet', { exact: true })).toBeVisible();
    await page.getByRole('link', { name: 'New agent', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Build an agent' })).toBeVisible();
    await page.getByLabel('Provider', { exact: true }).selectOption('codex');
    await page.getByRole('button', { name: 'Start session' }).click();
    await expect(page).toHaveURL(/#\/agents\/new\?.*session=/);
    const sessionId = new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('session');
    const message = 'Build an agent that triages release notes.';
    await page.getByRole('textbox', { name: 'Message to builder' }).fill(message);
    await page.getByRole('button', { name: 'Send', exact: true }).click();

    await expect(page.getByRole('alert')).toContainText(/sign in|authenticat/i);
    await expect(page.getByText(message, { exact: true })).toHaveCount(1);
    await expect(page.getByText('Fixture diagnostic that must stay out of the builder chat')).toHaveCount(0);
    const failed = await page.evaluate(id => window.doxie.invoke('builder:get', id), sessionId);
    expect(failed).toMatchObject({ status: 'Failed', failure: { kind: 'authentication' }, messages: [{ role: 'user', text: message }] });

    await page.getByRole('button', { name: 'Authenticate', exact: true }).click();
    await expect(page).toHaveURL(/#\/consoles\?.*session=/);
    const authSessionId = new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('session');
    await expect.poll(() => page.evaluate(id => window.doxie.invoke('console:snapshot', id), authSessionId))
      .toMatchObject({ kind: 'auth', providerId: 'codex', status: 'Completed', output: expect.stringContaining('Fixture sign-in complete') });
    expect(await readFile(join(localCodex, 'auth.json'), 'utf8')).toBe('{}');
    await expect(stat(join(profile, 'workbench', '.providers', 'codex', 'auth.json'))).rejects.toThrow(/ENOENT/);

    await page.getByRole('link', { name: 'Back to builder' }).click();
    await expect(page).toHaveURL(/#\/agents\/new\?.*session=/);
    await page.getByRole('button', { name: 'Retry last message' }).click();
    await expect(page.getByText('The release note triage draft is ready.', { exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Release Note Triage' })).toBeVisible();
    await expect(page.getByText(message, { exact: true })).toHaveCount(1);
    const recovered = await page.evaluate(id => window.doxie.invoke('builder:get', id), sessionId);
    expect(recovered.messages.map(turn => turn.role)).toEqual(['user', 'assistant']);
    expect(recovered.failure).toBeFalsy();
    await page.getByRole('button', { name: 'Save to catalog' }).click();
    await expect(page).toHaveURL(/#\/agents\/release-note-triage/);
    await expect(page.getByRole('heading', { name: 'Release Note Triage' })).toBeVisible();
    expect(await page.evaluate(() => window.doxie.invoke('catalog:get', 'agents', 'release-note-triage')))
      .toMatchObject({ body: 'Read supplied release notes and report each entry with its impact.' });
  } finally {
    await application?.close().catch(() => {});
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 150 });
  }
});
