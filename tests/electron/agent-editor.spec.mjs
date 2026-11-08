import { expect, test, _electron as electron } from '@playwright/test';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

test('edits saved agent settings in a dedicated scoped editor and keeps execution read-only', async ({}, testInfo) => {
  const directory = await mkdtemp(join(tmpdir(), 'doxie-agent-editor-'));
  const profile = join(directory, 'app-data');
  const root = join(profile, 'workbench');
  const teamSource = join(directory, 'team-source');
  const id = 'shared-agent';
  const execution = { providerId: 'codex', model: 'doxie/editor-v1', effort: 'medium' };
  const modes = { consult: { description: 'Consult the supplied city.', fields: [{ id: 'city', label: 'City', required: true }] } };
  const requirements = [{ kind: 'Tool', name: 'web', required: true }];
  const custom = { preserved: true, version: 7 };
  const adapter = '---\nname: shared-agent\ndescription: Provider adapter\n---\n\nKeep these adapter instructions.\n';
  let application;
  try {
    for (const [path, displayName] of [[join(root, '.doxie', 'agents', id), 'Default Agent'], [join(teamSource, 'agents', id), 'Team Agent']]) {
      await mkdir(join(path, 'refs'), { recursive: true });
      await writeFile(join(path, 'manifest.json'), JSON.stringify({ id, displayName, category: 'Other', description: 'Existing description.', modes, requirements, custom, execution }));
      await writeFile(join(path, 'body.md'), 'Original instructions.');
      await writeFile(join(path, 'agent.md'), adapter);
      await writeFile(join(path, 'refs', 'reference.md'), 'Preserve this reference.');
    }
    const codexProfile = join(root, '.providers', 'codex');
    await mkdir(codexProfile, { recursive: true });
    await writeFile(join(codexProfile, 'models_cache.json'), JSON.stringify({ models: ['v1', 'v2'].map(version => ({
      slug: `doxie/editor-${version}`, display_name: `Editor ${version}`, visibility: 'list',
      supported_reasoning_levels: [{ effort: 'medium' }, { effort: 'high' }],
    })) }));
    const executablePath = process.env.DOXIE_TEST_EXECUTABLE;
    application = await electron.launch({
      ...(executablePath ? { executablePath, args: [] } : { args: ['.'] }),
      env: { ...process.env, DOXIE_USER_DATA: profile, DOXIE_TEST: '1' },
    });
    const page = await application.firstWindow();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toBeVisible();
    await page.evaluate(path => window.doxie.invoke('settings:save', { catalogs: [{ id: 'team', name: 'Team catalog', path }] }), teamSource);
    await page.reload();
    await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toBeVisible();
    await page.evaluate(() => { window.location.hash = '/agents/shared-agent?catalog=team'; });
    await expect(page.getByRole('heading', { name: 'Team Agent', exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Execution defaults', exact: true })).toHaveCount(0);
    await expect(page.getByLabel('Provider', { exact: true })).toHaveCount(0);
    await expect(page.getByLabel('Model', { exact: true })).toHaveCount(0);
    await expect(page.getByRole('group', { name: 'Reasoning effort' })).toHaveCount(0);
    expect(await page.evaluate(() => window.doxie.invoke('builder:list', 'agent'))).toHaveLength(0);
    await page.getByRole('link', { name: 'Edit agent', exact: true }).click();
    await expect(page).toHaveURL(/#\/agents\/shared-agent\/edit\?catalog=team$/);
    await expect(page.getByRole('heading', { name: 'Edit agent', exact: true })).toBeVisible();
    await expect(page.getByLabel('Agent ID', { exact: true })).toHaveValue(id);
    await expect(page.getByLabel('Agent ID', { exact: true })).toHaveAttribute('readonly', '');
    await expect(page.getByLabel('Catalog', { exact: true })).toHaveAttribute('readonly', '');
    await expect(page.getByLabel('Name', { exact: true })).toHaveValue('Team Agent');
    await expect(page.getByLabel('Model', { exact: true })).toHaveValue('doxie/editor-v1');
    await page.getByLabel('Model', { exact: true }).fill('doxie/editor-v2');
    await page.getByRole('link', { name: 'Cancel', exact: true }).click();
    expect((await page.evaluate(() => window.doxie.invoke('catalog:get', 'agents', 'shared-agent', 'team'))).execution).toEqual(execution);
    await page.getByRole('link', { name: 'Edit agent', exact: true }).click();
    await page.getByLabel('Name', { exact: true }).fill('Team Agent Revised');
    await page.getByLabel('Description', { exact: true }).fill('Updated description.');
    await page.getByLabel('Instructions', { exact: true }).fill('Updated agent instructions.');
    await page.getByLabel('Model', { exact: true }).fill('doxie/editor-v2');
    await page.getByRole('radio', { name: 'High', exact: true }).check();
    await page.screenshot({ path: testInfo.outputPath('agent-editor.png'), fullPage: true });
    await page.getByRole('button', { name: 'Save agent', exact: true }).click();
    await expect(page).toHaveURL(/#\/agents\/shared-agent\?catalog=team$/);
    await expect(page.getByRole('heading', { name: 'Team Agent Revised', exact: true })).toBeVisible();
    await expect(page.getByRole('region', { name: 'Agent execution', exact: true })).toContainText('doxie/editor-v2');
    await expect(page.getByRole('button', { name: 'Execution defaults', exact: true })).toHaveCount(0);
    await expect(page.getByLabel('Model', { exact: true })).toHaveCount(0);
    await expect(page.getByRole('group', { name: 'Reasoning effort' })).toHaveCount(0);
    const saved = await page.evaluate(() => window.doxie.invoke('catalog:get', 'agents', 'shared-agent', 'team'));
    expect(saved).toMatchObject({ name: 'Team Agent Revised', description: 'Updated description.', body: 'Updated agent instructions.',
      modes, requirements, custom, execution: { providerId: 'codex', model: 'doxie/editor-v2', effort: 'high' } });
    expect(await readFile(join(saved.path, 'agent.md'), 'utf8')).toBe(adapter);
    expect(await readFile(join(saved.path, 'refs', 'reference.md'), 'utf8')).toBe('Preserve this reference.');
    expect(await page.evaluate(() => window.doxie.invoke('catalog:get', 'agents', 'shared-agent', 'default')))
      .toMatchObject({ name: 'Default Agent', body: 'Original instructions.', execution });
    // Bootstrap lists one entry per ID; the explicit default copy must still be editable.
    await page.evaluate(() => { window.location.hash = '/agents/shared-agent?catalog=default'; });
    await expect(page.getByRole('heading', { name: 'Default Agent', exact: true })).toBeVisible();
    await page.getByRole('link', { name: 'Edit agent', exact: true }).click();
    await expect(page).toHaveURL(/#\/agents\/shared-agent\/edit\?catalog=default$/);
    await expect(page.getByLabel('Name', { exact: true })).toHaveValue('Default Agent');
    await page.getByLabel('Description', { exact: true }).fill('Default catalog description.');
    await page.getByRole('button', { name: 'Save agent', exact: true }).click();
    await expect(page).toHaveURL(/#\/agents\/shared-agent\?catalog=default$/);
    await expect(page.getByRole('heading', { name: 'Default Agent', exact: true })).toBeVisible();
    expect(await page.evaluate(() => window.doxie.invoke('catalog:get', 'agents', 'shared-agent', 'default')))
      .toMatchObject({ name: 'Default Agent', description: 'Default catalog description.', execution });
    expect(await page.evaluate(() => window.doxie.invoke('catalog:get', 'agents', 'shared-agent', 'team')))
      .toMatchObject({ name: 'Team Agent Revised', description: 'Updated description.' });
    await expect(page.evaluate(() => window.doxie.invoke('agent:start', 'shared-agent', { catalogId: 'team', model: 'bypass-model' }))).rejects.toThrow(/Use Edit agent/);
    expect((await page.evaluate(() => window.doxie.invoke('bootstrap'))).agentRuns).toHaveLength(0);
    expect(await page.evaluate(() => window.doxie.invoke('builder:list', 'agent'))).toHaveLength(0);
    await page.evaluate(() => { window.location.hash = '/agents/shared-agent/edit?catalog=missing'; });
    await expect(page.getByRole('heading', { name: 'Could not load agent', exact: true })).toBeVisible();
    await expect(page.getByRole('alert')).toContainText('Unknown catalog');
    await expect(page.getByRole('button', { name: 'Save agent', exact: true })).toHaveCount(0);
    expect(errors).toEqual([]);
  } finally {
    await application?.close().catch(() => {});
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 150 });
  }
});
