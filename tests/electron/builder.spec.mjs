import { expect, test, _electron as electron } from '@playwright/test';
import { mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

test('starts explicitly, resumes builder sessions after restart, and saves independent agent execution defaults', async ({}, testInfo) => {
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
    const launchOptions = {
      ...(executablePath ? { executablePath } : {}),
      args: [...(executablePath ? [] : ['.']), '--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'],
      env: {
        ...process.env, DOXIE_USER_DATA: profile, DOXIE_TEST: '1', CODEX_HOME: localCodex,
        OPENAI_API_KEY: 'test', OPENAI_BASE_URL: 'http://127.0.0.1:1',
      },
    };
    application = await electron.launch(launchOptions);
    let page = await application.firstWindow();
    await expect(page.getByRole('heading', { name: 'Dashboard' })).toBeVisible();
    await page.evaluate(executable => window.doxie.invoke('settings:save', {
      defaultProvider: 'codex', providers: { codex: { executable, model: 'doxie/builder-fixture' } },
    }), mock);
    await application.evaluate(({ BrowserWindow, ipcMain }) => {
      const original = ipcMain._invokeHandlers.get('doxie:invoke');
      const contents = BrowserWindow.getAllWindows()[0].webContents;
      const totalBytes = 45 * 1024 * 1024;
      const status = { state: 'missing', receivedBytes: 0, totalBytes };
      globalThis.speechFixture = { installs: 0, requests: [] };
      const changed = () => contents.send('doxie:event', { type: 'speech:changed', status: { ...status } });
      ipcMain.removeHandler('doxie:invoke');
      ipcMain.handle('doxie:invoke', async (event, action, args) => {
        if (!['speech:status', 'speech:install', 'speech:transcribe'].includes(action)) return original(event, action, args);
        if (action === 'speech:status') return { ...status };
        if (action === 'speech:install') {
          globalThis.speechFixture.installs++;
          status.state = 'downloading';
          status.receivedBytes = totalBytes / 2;
          changed();
          await new Promise(resolve => setTimeout(resolve, 400));
          status.state = 'ready';
          status.receivedBytes = totalBytes;
          changed();
          return { ...status };
        }
        const { samples, language } = args[0];
        if (!(samples instanceof Float32Array) || !samples.length || samples.length > 16_000 * 60
          || samples.some(sample => !Number.isFinite(sample))) throw new Error('Invalid captured PCM samples');
        globalThis.speechFixture.requests.push({ length: samples.length, language });
        await new Promise(resolve => setTimeout(resolve, 400));
        return { text: 'Texto ditado para o builder.' };
      });
    });
    await page.evaluate(() => {
      Object.defineProperty(navigator, 'language', { configurable: true, value: 'pt-BR' });
      const capture = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
      window.speechCaptureTracks = [];
      navigator.mediaDevices.getUserMedia = async constraints => {
        const stream = await capture(constraints);
        window.speechCaptureTracks.push(...stream.getTracks());
        return stream;
      };
    });

    await page.getByRole('link', { name: 'Agents', exact: true }).first().click();
    await expect(page.getByText('No agents yet', { exact: true })).toBeVisible();
    await page.locator('.toolbar').getByRole('link', { name: 'Sessions', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Start a builder session', exact: true })).toBeVisible();
    await expect(page.getByText('No previous sessions', { exact: true })).toBeVisible();
    expect(await page.evaluate(() => window.doxie.invoke('builder:list', 'agent'))).toHaveLength(0);
    await page.getByRole('link', { name: 'Agents', exact: true }).first().click();
    await page.getByRole('link', { name: 'New agent', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Build an agent' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Start session', exact: true })).toBeVisible();
    expect(await page.evaluate(() => window.doxie.invoke('builder:list', 'agent'))).toHaveLength(0);
    await page.getByRole('button', { name: 'Start session', exact: true }).click();
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
    const composer = page.getByRole('textbox', { name: 'Message to builder' });
    await page.getByRole('button', { name: 'Start local dictation', exact: true }).click();
    const downloadModel = page.getByRole('button', { name: 'Download voice model', exact: true });
    await expect(downloadModel).toBeVisible();
    expect(await application.evaluate(() => globalThis.speechFixture.installs)).toBe(0);
    await downloadModel.click();
    await expect(page.getByRole('status').filter({ hasText: /Downloading voice model/ })).toBeVisible();
    await expect(page.getByRole('status').filter({ hasText: 'Voice model is ready. Click the microphone to dictate.' })).toBeVisible();
    expect(await application.evaluate(() => globalThis.speechFixture.installs)).toBe(1);
    await page.getByRole('button', { name: 'Start local dictation', exact: true }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Listening… Click the microphone again to finish.' })).toBeVisible();
    await expect.poll(() => page.evaluate(() => document.querySelector('.voice-input')?.dataset.phase)).toBe('listening');
    await new Promise(resolve => setTimeout(resolve, 250));
    await page.getByRole('button', { name: 'Stop dictation', exact: true }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Transcribing locally…' })).toBeVisible();
    await expect(composer).toHaveValue('Texto ditado para o builder.');
    expect(await page.evaluate(() => window.speechCaptureTracks.map(track => track.readyState))).toEqual(['ended']);
    expect((await application.evaluate(() => globalThis.speechFixture.requests))).toEqual([
      { length: expect.any(Number), language: 'pt' },
    ]);
    expect((await page.evaluate(id => window.doxie.invoke('builder:get', id), sessionId)).messages).toEqual(initial.messages);
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
    await expect(page.getByRole('heading', { name: 'Release Note Triage' }).last()).toBeVisible();
    await expect(page.locator('.chat-message.user')).toHaveCount(1);
    const recovered = await page.evaluate(id => window.doxie.invoke('builder:get', id), sessionId);
    expect(recovered.messages.slice(0, -1)).toEqual([...initial.messages, { role: 'user', text: message }]);
    expect(recovered.messages.at(-1).role).toBe('assistant');
    expect(recovered.failure).toBeFalsy();

    const draftHeader = page.locator('.panel-header').filter({ has: page.getByRole('heading', { name: 'Draft manifest', exact: true }) });
    await expect(draftHeader.getByRole('button', { name: 'Save to catalog', exact: true })).toBeVisible();
    await draftHeader.getByRole('button', { name: 'Change agent execution', exact: true }).click();
    const executionDialog = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: 'Agent execution defaults', exact: true }) });
    await executionDialog.getByLabel('Provider', { exact: true }).selectOption('codex');
    await executionDialog.getByLabel('Model', { exact: true }).fill('doxie/saved-agent');
    await executionDialog.getByRole('radio', { name: 'High', exact: true }).check();
    await page.screenshot({ path: testInfo.outputPath('agent-execution.png') });
    await executionDialog.getByRole('button', { name: 'Apply agent execution', exact: true }).click();
    await expect(executionDialog).toHaveCount(0);
    const configured = await page.evaluate(id => window.doxie.invoke('builder:get', id), sessionId);
    expect(configured).toMatchObject({ providerId: 'codex', model: 'doxie/builder-fixture',
      draft: { execution: { providerId: 'codex', model: 'doxie/saved-agent', effort: 'high' } } });

    await page.getByRole('link', { name: 'Agents', exact: true }).first().click();
    await page.locator('.toolbar').getByRole('link', { name: 'Sessions', exact: true }).click();
    const sessions = page.locator('.panel').filter({ has: page.getByRole('heading', { name: 'Resume a session', exact: true }) });
    await expect(sessions.getByText('Release Note Triage', { exact: true })).toBeVisible();
    await expect(sessions).not.toContainText(sessionId);
    await page.screenshot({ path: testInfo.outputPath('builder-sessions.png') });
    await page.getByRole('link', { name: 'Agents', exact: true }).first().click();
    await page.getByRole('link', { name: 'Build agent', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Start a builder session', exact: true })).toBeVisible();
    expect(await page.evaluate(() => window.doxie.invoke('builder:list', 'agent'))).toHaveLength(1);
    await sessions.getByText('Release Note Triage', { exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`session=${sessionId}`));
    await expect(page.locator('.chat-message.user')).toHaveCount(1);
    expect((await page.evaluate(id => window.doxie.invoke('builder:get', id), sessionId)).messages).toEqual(configured.messages);

    // A new main process must find the same persisted session without a provider call.
    await application.close();
    application = await electron.launch(launchOptions);
    page = await application.firstWindow();
    await expect(page.getByRole('heading', { name: 'Dashboard' })).toBeVisible();
    await page.getByRole('link', { name: 'Agents', exact: true }).first().click();
    await page.locator('.toolbar').getByRole('link', { name: 'Sessions', exact: true }).click();
    await page.locator('.panel').filter({ has: page.getByRole('heading', { name: 'Resume a session', exact: true }) })
      .getByText('Release Note Triage', { exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`session=${sessionId}`));
    await expect(page.locator('.chat-message.user')).toHaveCount(1);
    const resumed = await page.evaluate(id => window.doxie.invoke('builder:get', id), sessionId);
    expect(resumed.messages).toEqual(configured.messages);
    expect(resumed.draft).toEqual(configured.draft);
    expect(await page.evaluate(() => window.doxie.invoke('builder:list', 'agent'))).toHaveLength(1);
    await page.getByRole('button', { name: 'Change agent execution', exact: true }).click();
    await expect(page.getByRole('dialog').getByLabel('Model', { exact: true })).toHaveValue('doxie/saved-agent');
    await page.getByRole('dialog').getByRole('button', { name: 'Cancel', exact: true }).click();
    await page.getByRole('button', { name: 'Save to catalog' }).click();
    await expect(page).toHaveURL(/#\/agents\/release-note-triage/);
    await expect(page.getByRole('heading', { name: 'Release Note Triage' })).toBeVisible();
    expect(await page.evaluate(() => window.doxie.invoke('catalog:get', 'agents', 'release-note-triage')))
      .toMatchObject({ body: 'Read supplied release notes and report each entry with its impact.',
        execution: { providerId: 'codex', model: 'doxie/saved-agent', effort: 'high' } });
    await expect(page.getByLabel('Provider', { exact: true })).toHaveCount(0);
    await expect(page.getByLabel('Model', { exact: true })).toHaveCount(0);
    await expect(page.getByRole('group', { name: 'Reasoning effort' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Execution defaults', exact: true })).toHaveCount(0);
    await expect(page.getByRole('link', { name: 'Edit agent', exact: true })).toBeVisible();
    await expect(page.getByRole('region', { name: 'Agent execution', exact: true })).toContainText('doxie/saved-agent');
    await page.getByRole('button', { name: 'Run agent', exact: true }).click();
    await expect(page).toHaveURL(/#\/agents\/release-note-triage.*run=/);
    const runId = new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('run');
    await expect.poll(() => page.evaluate(id => window.doxie.invoke('agent:run', id), runId))
      .toMatchObject({ status: 'Completed', model: 'doxie/saved-agent', effort: 'high' });
    await expect(page.getByText('Saved agent completed using the installed sign-in and saved model.', { exact: true })).toBeVisible();
    const response = page.getByRole('region', { name: 'Agent response', exact: true });
    await expect(response.getByRole('table')).toBeVisible();
    await expect(response).not.toContainText('codex_rmcp_client');
    await expect(response).not.toContainText('fixture-internal-command');
    await expect(response).not.toContainText('Checking the supplied material.');
    await expect(page.getByRole('region', { name: 'Run issues', exact: true })).toContainText('MCP fixture-tools unavailable');
    const diagnostics = page.locator('details').filter({ has: page.locator('summary').filter({ hasText: /^Diagnostics \(/ }) });
    await expect(diagnostics).not.toHaveAttribute('open', '');
    await expect(page.getByLabel('Agent diagnostics', { exact: true })).toBeHidden();
    await diagnostics.locator('summary').first().click();
    await expect(page.getByLabel('Agent diagnostics', { exact: true })).toContainText('codex_rmcp_client');
    await diagnostics.locator('summary').first().click();
    const activity = page.locator('details').filter({ has: page.locator('summary').filter({ hasText: /^Activity \(/ }) });
    await expect(page.getByLabel('Agent activity', { exact: true })).toBeHidden();
    await activity.locator('summary').first().click();
    await expect(page.getByLabel('Agent activity', { exact: true })).toContainText('fixture-internal-command');
    await activity.locator('summary').first().click();
    await page.screenshot({ path: testInfo.outputPath('agent-response.png'), fullPage: true });
    await page.getByLabel('Arguments / prompt', { exact: true }).fill('fixture-fail');
    await page.getByRole('button', { name: 'Run agent', exact: true }).click();
    await expect.poll(async () => new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('run')).not.toBe(runId);
    const failedRunId = new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('run');
    await expect.poll(() => page.evaluate(id => window.doxie.invoke('agent:run', id), failedRunId)).toMatchObject({ status: 'Failed' });
    await expect(page.getByRole('region', { name: 'Run issues', exact: true }).getByRole('alert')).toContainText('Fixture provider failure.');

    await page.getByRole('link', { name: 'Agents', exact: true }).first().click();
    await page.locator('.toolbar').getByRole('link', { name: 'Sessions', exact: true }).click();
    await page.getByRole('button', { name: 'Remove session', exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Cancel', exact: true }).click();
    expect(await page.evaluate(() => window.doxie.invoke('builder:list', 'agent'))).toHaveLength(1);
    await page.getByRole('button', { name: 'Remove session', exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Remove session', exact: true }).click();
    await expect(page.getByText('No previous sessions', { exact: true })).toBeVisible();
    expect(await page.evaluate(() => window.doxie.invoke('catalog:get', 'agents', 'release-note-triage'))).toBeTruthy();
    await page.evaluate(async () => {
      for (let index = 0; index < 2; index++) await window.doxie.invoke('builder:start', 'agent', { editingId: 'release-note-triage' });
    });
    await expect(page.getByRole('button', { name: 'Remove session', exact: true })).toHaveCount(2);
    await page.getByRole('button', { name: 'Clear sessions', exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Clear sessions', exact: true }).click();
    await expect(page.getByText('No previous sessions', { exact: true })).toBeVisible();
    expect(await page.evaluate(() => window.doxie.invoke('builder:list', 'agent'))).toHaveLength(0);
  } finally {
    await application?.close().catch(() => {});
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 150 });
  }
});
