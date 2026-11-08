const { randomUUID } = require('node:crypto');
const { lstatSync, renameSync } = require('node:fs');
const { lstat, readdir, rm } = require('node:fs/promises');
const path = require('node:path');
const { safePath } = require('./storage.cjs');

const retention = 7 * 24 * 60 * 60 * 1000;
const initialDelay = 30_000;
const sweepInterval = 6 * 60 * 60 * 1000;
const builderId = /^builder-(agent|workflow)-[a-z0-9-]+$/;
const sweeps = new WeakMap();

async function directories(directory) {
  try {
    if (!(await lstat(directory)).isDirectory()) return [];
    return (await readdir(directory, { withFileTypes: true })).filter(item => item.isDirectory()).map(item => item.name);
  } catch (error) {
    if (error.code !== 'ENOENT') throw error;
    return [];
  }
}

function contains(directory, file) {
  const relative = path.relative(directory, file);
  return !path.isAbsolute(relative) && relative !== '..' && !relative.startsWith(`..${path.sep}`);
}

async function containsSymlink(directory, shouldStop) {
  const entries = await readdir(directory, { withFileTypes: true });
  for (const entry of entries) {
    if (shouldStop() || entry.isSymbolicLink()) return true;
    if (entry.isDirectory() && await containsSymlink(safePath(directory, entry.name), shouldStop)) return true;
  }
  return false;
}

function sweepState(storage) {
  if (!sweeps.has(storage.db)) sweeps.set(storage.db, { stopped: false, tail: Promise.resolve() });
  return sweeps.get(storage.db);
}

function sweepScratch({ storage, builders, retentionMs = retention, clock = Date.now, shouldStop = () => false }) {
  if (!Number.isFinite(retentionMs) || retentionMs < 0) throw new RangeError('Scratch retention must be zero or a positive duration.');
  const state = sweepState(storage);
  const stopped = () => state.stopped || shouldStop();
  const task = state.tail.catch(() => {}).then(() => stopped() ? 0 : pruneScratch({ storage, builders, retentionMs, clock, stopped }));
  state.tail = task;
  return task;
}

async function pruneScratch({ storage, builders, retentionMs, clock, stopped }) {
  const { root, db } = storage;
  const runs = path.join(root, '.runs');
  const runningAgents = db.prepare("SELECT output_dir FROM agent_runs WHERE status IN ('Queued', 'Running', 'AwaitingInput')");
  const runningWorkflows = db.prepare("SELECT id, triggered_by FROM workflow_runs WHERE status IN ('Queued', 'Running', 'AwaitingApproval', 'AwaitingInput')");
  const cutoff = retentionMs === 0 ? Infinity : clock() - retentionMs;

  function inUse(folder) {
    const agentUsesFolder = runningAgents.all().some(run => run.output_dir && contains(folder, run.output_dir));
    const workflowUsesFolder = runningWorkflows.all().some(run => {
      const sourceId = /^rerun:([^:]+):/.exec(run.triggered_by)?.[1];
      return [run.id, sourceId].some(id => id && contains(folder, path.join(runs, id)));
    });
    return agentUsesFolder || workflowUsesFolder;
  }

  let removed = 0;
  for (const relativeRoot of ['.runs', '.runs/agents', '.sandbox']) {
    let directory;
    try { directory = safePath(root, relativeRoot); }
    catch (error) {
      console.error(`Could not inspect DoxieOS scratch root '${relativeRoot}': ${error.message}`);
      continue;
    }
    for (const id of await directories(directory)) {
      if (stopped()) return removed;
      if (relativeRoot === '.runs' && id.toLowerCase() === 'agents') continue;
      try {
        const relativeFolder = path.join(relativeRoot, id);
        const folder = safePath(root, relativeFolder);
        const info = await lstat(folder);
        const resumable = () => relativeRoot === '.sandbox' && builderId.test(id) && builders?.get(id);
        if (!info.isDirectory() || info.mtimeMs >= cutoff || stopped() || inUse(folder) || resumable()) continue;
        if (await containsSymlink(folder, stopped)) continue;
        const checked = safePath(root, relativeFolder);
        const current = lstatSync(checked);
        if (!current.isDirectory() || current.ino !== info.ino || current.dev !== info.dev || current.mtimeMs !== info.mtimeMs
          || stopped() || inUse(checked) || resumable()) continue;

        // Claim the folder without yielding after the final liveness check.
        // This keeps newly started runs from racing asynchronous removal.
        const retired = safePath(root, path.join(relativeRoot, `.doxie-cleanup-${randomUUID()}`));
        renameSync(checked, retired);
        await rm(safePath(root, path.relative(root, retired)), { recursive: true, force: true, maxRetries: 2, retryDelay: 100 });
        removed++;
      } catch (error) {
        if (error.code !== 'ENOENT') console.error(`Could not prune DoxieOS scratch folder '${id}': ${error.message}`);
      }
    }
  }
  return removed;
}

function startHousekeeping(options) {
  const state = sweepState(options.storage);
  async function run() {
    if (state.stopped) return;
    try { await sweepScratch(options); }
    catch (error) { console.error(`DoxieOS scratch retention check failed: ${error.message}`); }
    if (!state.stopped) timer = setTimeout(run, sweepInterval).unref();
  }
  let timer = setTimeout(run, initialDelay).unref();
  return async function stop() {
    state.stopped = true;
    clearTimeout(timer);
    await Promise.allSettled([state.tail]);
  };
}

module.exports = { startHousekeeping, sweepScratch };
