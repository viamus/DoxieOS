const { lstat, readdir, rm } = require('node:fs/promises');
const path = require('node:path');
const { safePath } = require('./storage.cjs');

const retention = 7 * 24 * 60 * 60 * 1000;
const initialDelay = 30_000;
const sweepInterval = 6 * 60 * 60 * 1000;
const builderId = /^builder-(agent|workflow)-[a-z0-9-]+$/;

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

async function sweepScratch({ storage, builders, clock = Date.now, shouldStop = () => false }) {
  const { root, db } = storage;
  const runs = safePath(root, '.runs');
  const sandboxes = safePath(root, '.sandbox');
  const standalone = safePath(runs, 'agents');
  const runningAgents = db.prepare("SELECT output_dir FROM agent_runs WHERE status IN ('Queued', 'Running')");
  const runningWorkflows = db.prepare("SELECT id, triggered_by FROM workflow_runs WHERE status IN ('Queued', 'Running', 'AwaitingApproval')");
  const cutoff = clock() - retention;

  function inUse(folder) {
    const agentUsesFolder = runningAgents.all().some(run => run.output_dir && contains(folder, run.output_dir));
    const workflowUsesFolder = runningWorkflows.all().some(run => {
      const sourceId = /^rerun:([^:]+):/.exec(run.triggered_by)?.[1];
      return [run.id, sourceId].some(id => id && contains(folder, path.join(runs, id)));
    });
    return agentUsesFolder || workflowUsesFolder;
  }

  let removed = 0;
  for (const directory of [runs, standalone, sandboxes]) {
    for (const id of await directories(directory)) {
      if (shouldStop()) return removed;
      if (directory === runs && id.toLowerCase() === 'agents') continue;
      try {
        const folder = safePath(directory, id);
        const info = await lstat(folder);
        if (!info.isDirectory() || info.mtimeMs >= cutoff || shouldStop() || inUse(folder)) continue;
        if (directory === sandboxes && builderId.test(id) && builders?.get(id)) continue;
        await rm(folder, { recursive: true, force: true, maxRetries: 2, retryDelay: 100 });
        removed++;
      } catch (error) {
        if (error.code !== 'ENOENT') console.error(`Could not prune DoxieOS scratch folder '${id}': ${error.message}`);
      }
    }
  }
  return removed;
}

function startHousekeeping(options) {
  let stopped = false;
  let current;
  async function run() {
    if (stopped) return;
    current = sweepScratch({ ...options, shouldStop: () => stopped });
    try { await current; }
    catch (error) { console.error(`DoxieOS scratch retention check failed: ${error.message}`); }
    finally { current = undefined; }
    if (!stopped) timer = setTimeout(run, sweepInterval).unref();
  }
  let timer = setTimeout(run, initialDelay).unref();
  return async function stop() {
    stopped = true;
    clearTimeout(timer);
    await Promise.allSettled([current]);
  };
}

module.exports = { startHousekeeping, sweepScratch };
