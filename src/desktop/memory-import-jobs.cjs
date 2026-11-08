const { randomUUID } = require('node:crypto');
const { join, resolve } = require('node:path');
const { Worker } = require('node:worker_threads');

function createMemoryImportJobs({ root, emit = () => {} } = {}) {
  if (typeof root !== 'string' || !root.trim()) throw new Error('A central memory root is required.');
  root = resolve(root);
  const queue = [];
  let current;
  let closed = false;
  let closing;
  function next() {
    if (closed || current || !queue.length) return;
    const job = queue.shift();
    current = job;
    try {
      job.worker = new Worker(join(__dirname, 'memory-import-worker.cjs'), { workerData: { root, action: job.action, options: job.options } });
    } catch (error) {
      current = null;
      job.reject(error);
      next();
      return;
    }
    job.worker.on('message', message => {
      if (message.type === 'result') { job.result = message.result; job.hasResult = true; }
      else if (message.type === 'error') job.error = Object.assign(new Error(message.error.message), { name: message.error.name });
      else if (message.type === 'progress' && !closed) {
        try { emit({ type: 'memory:importProgress', jobId: job.id, action: job.action, ...message.progress }); }
        catch (error) { console.error('Could not publish memory import progress:', error.message); }
      }
    });
    job.worker.once('error', error => { job.error = error; });
    job.worker.once('exit', code => {
      current = null;
      if (job.cancelled) job.reject(new Error('Memory import cancelled because the application is closing.'));
      else if (job.error) job.reject(job.error);
      else if (code !== 0 || !job.hasResult) job.reject(new Error(`Memory import worker exited before completing the operation (${code}).`));
      else job.resolve(job.result);
      next();
    });
  }
  function run(action, options = {}) {
    return new Promise((resolveJob, reject) => {
      if (closed) { reject(new Error('The memory import service is closed.')); return; }
      let copy;
      try { copy = structuredClone(options); }
      catch { reject(new Error('Memory import options must contain only serializable values.')); return; }
      queue.push({ id: randomUUID(), action, options: copy, resolve: resolveJob, reject });
      next();
    });
  }
  function close() {
    if (closing) return closing;
    closed = true;
    for (const job of queue.splice(0)) job.reject(new Error('The memory import service is closed.'));
    const job = current;
    if (job) job.cancelled = true;
    closing = job?.worker ? job.worker.terminate().then(() => undefined) : Promise.resolve();
    return closing;
  }
  return { discover: options => run('discover', options), preview: options => run('preview', options),
    import: options => run('import', options), close };
}

module.exports = { createMemoryImportJobs };
