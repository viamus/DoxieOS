const assert = require('node:assert/strict');
const { lstat, mkdir, mkdtemp, readFile, rm, symlink, utimes, writeFile } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const path = require('node:path');
const { DatabaseSync } = require('node:sqlite');
const test = require('node:test');
const { startHousekeeping, sweepScratch } = require('../../src/desktop/housekeeping.cjs');

test('prunes expired scratch while preserving active runs, replay inputs, builders and symlinks', async t => {
  const root = await mkdtemp(path.join(tmpdir(), 'doxie-retention-'));
  const db = new DatabaseSync(':memory:');
  t.after(async () => {
    db.close();
    await rm(root, { recursive: true, force: true });
  });
  const paths = Object.fromEntries(Object.entries({
    staleRun: '.runs/stale-run', currentRun: '.runs/current-run', parkedRun: '.runs/parked-run',
    replayInput: '.runs/replay-input', runningAgent: '.runs/agents/running', oldAgent: '.runs/agents/old',
    oldSandbox: '.sandbox/old', draft: '.sandbox/builder-agent-saved', outside: 'outside',
    link: '.sandbox/old-link', agents: '.runs/agents',
  }).map(([key, relative]) => [key, path.join(root, relative)]));
  const date = new Date();
  const expired = new Date(date.getTime() - 8 * 24 * 60 * 60 * 1000);
  for (const [key, folder] of Object.entries(paths)) {
    if (key === 'link') continue;
    await mkdir(folder, { recursive: true });
    if (key !== 'currentRun') await utimes(folder, expired, expired);
  }
  await symlink(paths.outside, paths.link, 'junction');

  db.exec(`
    CREATE TABLE agent_runs (output_dir TEXT, status TEXT);
    CREATE TABLE workflow_runs (id TEXT, triggered_by TEXT, status TEXT);
    INSERT INTO workflow_runs VALUES ('parked-run', 'manual', 'AwaitingApproval');
    INSERT INTO workflow_runs VALUES ('current-run', 'rerun:replay-input:start', 'Running');
  `);
  db.prepare("INSERT INTO agent_runs VALUES (?, 'Running')").run(paths.runningAgent);
  const builders = { get: id => id === 'builder-agent-saved' ? { id } : null };
  assert.equal(await sweepScratch({ storage: { root, db }, builders, clock: () => date.getTime() }), 3);
  for (const folder of [paths.staleRun, paths.oldSandbox, paths.oldAgent]) {
    await assert.rejects(lstat(folder), { code: 'ENOENT' });
  }
  for (const folder of [paths.currentRun, paths.parkedRun, paths.replayInput, paths.runningAgent, paths.draft, paths.outside, paths.agents]) {
    assert((await lstat(folder)).isDirectory());
  }
  assert((await lstat(paths.link)).isSymbolicLink());
});

test('manual cleanup removes fresh scratch once and preserves active runs and links', async t => {
  const root = await mkdtemp(path.join(tmpdir(), 'doxie-manual-cleanup-'));
  const db = new DatabaseSync(':memory:');
  const storage = { root, db };
  const builders = { get: id => id === 'builder-workflow-draft' ? { id } : null };
  const stop = startHousekeeping({ storage, builders });
  t.after(async () => {
    await stop();
    db.close();
    await rm(root, { recursive: true, force: true });
  });

  db.exec(`
    CREATE TABLE agent_runs (output_dir TEXT, status TEXT);
    CREATE TABLE workflow_runs (id TEXT, triggered_by TEXT, status TEXT);
    INSERT INTO workflow_runs VALUES ('finished', 'manual', 'Succeeded');
    INSERT INTO workflow_runs VALUES ('approval', 'manual', 'AwaitingApproval');
    INSERT INTO workflow_runs VALUES ('replay', 'rerun:source:next', 'Queued');
  `);
  const removed = ['.runs/finished', '.runs/agents/completed', '.sandbox/future-date'];
  const preserved = ['.runs/approval', '.runs/source', '.runs/replay', '.runs/agents/active',
    '.sandbox/builder-workflow-draft', '.sandbox/linked', '.workspace/demo'];
  for (const relative of [...removed, ...preserved]) await mkdir(path.join(root, relative), { recursive: true });
  db.prepare("INSERT INTO agent_runs VALUES (?, 'Running')").run(path.join(root, '.runs', 'agents', 'active'));
  const marker = path.join(root, '.workspace', 'demo', 'user.txt');
  await writeFile(marker, 'keep');
  const link = path.join(root, '.sandbox', 'linked', 'external');
  await symlink(path.dirname(marker), link, 'junction');
  const future = new Date(Date.now() + 24 * 60 * 60 * 1000);
  await utimes(path.join(root, '.sandbox', 'future-date'), future, future);

  const options = { storage, builders, retentionMs: 0 };
  assert.deepEqual(await Promise.all([sweepScratch(options), sweepScratch(options)]), [3, 0]);
  for (const relative of removed) await assert.rejects(lstat(path.join(root, relative)), { code: 'ENOENT' });
  for (const relative of preserved) assert((await lstat(path.join(root, relative))).isDirectory());
  assert((await lstat(link)).isSymbolicLink());
  assert.equal(await readFile(marker, 'utf8'), 'keep');
  assert.equal(db.prepare("SELECT status FROM workflow_runs WHERE id = 'finished'").get().status, 'Succeeded');

  const queued = sweepScratch(options);
  await stop();
  assert.equal(await queued, 0);
});
