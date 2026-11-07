import { expect, test, _electron as electron } from '@playwright/test';
import { mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

test('opens the builder automatically, sends with Enter, and recovers a failed turn before saving the agent', async () => {
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
    await page.evaluate(() => {
      Object.defineProperty(navigator, 'language', { configurable: true, value: 'pt-BR' });
      window.speechCapabilityProbe = { queries: [], starts: 0, instances: 0 };
      class LocalSpeechFixture {
        constructor() { window.speechCapabilityProbe.instances++; }
        static async available(options) {
          window.speechCapabilityProbe.queries.push(options);
          return 'unavailable';
        }
        start() { window.speechCapabilityProbe.starts++; }
      }
      LocalSpeechFixture.prototype.processLocally = false;
      window.SpeechRecognition = LocalSpeechFixture;
    });

    await page.getByRole('link', { name: 'Agents', exact: true }).first().click();
    await expect(page.getByText('No agents yet', { exact: true })).toBeVisible();
    await page.getByRole('link', { name: 'New agent', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Build an agent' })).toBeVisible();
    await expect(page).toHaveURL(/#\/agents\/new\?.*session=/);
    await expect(page.getByRole('button', { name: 'Start session' })).toHaveCount(0);
    await expect(page.getByRole('status').filter({ hasText: /preparing|working|starting|thinking/i }).first()).toBeVisible();
    await expect(page.getByRole('button', { name: 'Send', exact: true })).toBeDisabled();
    await expect(page.getByText("I'm ready to build your agent. What should it do?", { exact: true })).toBeVisible();
    await expect(page.getByText('Waiting for your message.', { exact: true })).toBeVisible();
    const sessionId = new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('session');
    const initial = await page.evaluate(id => window.doxie.invoke('builder:get', id), sessionId);
    expect(initial.draft).toBeNull();
    expect(initial.messages.map(turn => turn.role)).toEqual(['assistant']);
    expect(await page.evaluate(() => window.doxie.invoke('builder:list', 'agent'))).toHaveLength(1);
    await page.getByRole('button', { name: 'Start local dictation', exact: true }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Local dictation is unavailable for pt-BR on this installation.' })).toBeVisible();
    expect(await page.evaluate(() => window.speechCapabilityProbe)).toEqual({
      queries: [{ langs: ['pt-BR'], processLocally: true }], starts: 0, instances: 0,
    });
    const composer = page.getByRole('textbox', { name: 'Message to builder' });
    const message = 'Build an agent\nthat triages release notes.';
    await composer.fill('Build an agent');
    await composer.press('Shift+Enter');
    await expect(composer).toHaveValue('Build an agent\n');
    expect((await page.evaluate(id => window.doxie.invoke('builder:get', id), sessionId)).messages).toEqual(initial.messages);
    await composer.pressSequentially('that triages release notes.');
    await composer.press('Enter');
    await expect(composer).toHaveValue('');
    await expect(page.getByRole('status').filter({ hasText: 'Builder is working…' }).first()).toBeVisible();
    await expect(page.getByRole('button', { name: 'Send', exact: true })).toBeDisabled();

    await expect(page.getByRole('alert')).toContainText(/sign in|authenticat/i);
    await expect(page.locator('.chat-message.user')).toHaveCount(1);
    await expect(page.getByText('Fixture diagnostic that must stay out of the builder chat')).toHaveCount(0);
    const failed = await page.evaluate(id => window.doxie.invoke('builder:get', id), sessionId);
    expect(failed).toMatchObject({ status: 'Failed', failure: { kind: 'authentication' } });
    expect(failed.messages).toEqual([...initial.messages, { role: 'user', text: message }]);

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
    await expect(page.locator('.chat-message.user')).toHaveCount(1);
    const recovered = await page.evaluate(id => window.doxie.invoke('builder:get', id), sessionId);
    expect(recovered.messages.slice(0, -1)).toEqual([...initial.messages, { role: 'user', text: message }]);
    expect(recovered.messages.at(-1).role).toBe('assistant');
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
