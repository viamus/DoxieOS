const assert = require('node:assert/strict');
const { lstat, mkdir, mkdtemp, rm, symlink, utimes } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const path = require('node:path');
const { DatabaseSync } = require('node:sqlite');
const test = require('node:test');
const { sweepScratch } = require('../../src/desktop/housekeeping.cjs');

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
