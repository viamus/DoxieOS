const { randomUUID } = require('node:crypto');
const { join, resolve } = require('node:path');
const { Worker } = require('node:worker_threads');

function createMemoryImportJobs({ root, store, emit = () => {} } = {}) {
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
      job.finish?.();
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
    job.worker.once('exit', async code => {
      job.worker = null;
      try {
        if (job.cancelled) throw new Error('Memory import cancelled because the application is closing.');
        if (job.error) throw job.error;
        if (code !== 0 || !job.hasResult) throw new Error(`Memory import worker exited before completing the operation (${code}).`);
        if (job.action === 'import') {
          if (!store) throw new Error('The Brain owner must commit imported memories.');
          const { preparedFiles, ...result } = job.result;
          let imported = 0;
          let duplicates = 0;
          let completed = 0;
          for (const file of preparedFiles) {
            const ids = [];
            for (let offset = 0; offset < file.memories.length; offset += 25) {
              if (closed) throw new Error('Memory import cancelled because the application is closing.');
              const apply = () => {
                for (const memory of file.memories.slice(offset, offset + 25)) {
                  const before = store.stats().total;
                  ids.push(store.remember(memory).id);
                  if (store.stats().total === before) duplicates++; else imported++;
                }
              };
              if (store.batch) store.batch(apply); else apply();
              await new Promise(resolve => setImmediate(resolve));
            }
            store.reconcileImport?.({ provider: file.provider, path: file.path, activeIds: ids });
            completed++;
            if (!closed) emit({ type: 'memory:importProgress', jobId: job.id, action: job.action, phase: 'import', completed, total: preparedFiles.length });
          }
          job.resolve({ ...result, imported, duplicates });
        } else job.resolve(job.result);
      } catch (error) { job.reject(error); }
      current = null;
      job.finish?.();
      next();
    });
  }
  function run(action, options = {}) {
    return new Promise((resolveJob, reject) => {
      if (closed) { reject(new Error('The memory import service is closed.')); return; }
      let copy;
      try { copy = structuredClone(options); }
      catch { reject(new Error('Memory import options must contain only serializable values.')); return; }
      const job = { id: randomUUID(), action, options: copy, resolve: resolveJob, reject };
      job.finished = new Promise(resolve => { job.finish = resolve; });
      queue.push(job);
      next();
    });
  }
  function close() {
    if (closing) return closing;
    closed = true;
    for (const job of queue.splice(0)) job.reject(new Error('The memory import service is closed.'));
    const job = current;
    if (job) job.cancelled = true;
    closing = job ? Promise.allSettled([job.worker?.terminate(), job.finished]).then(() => undefined) : Promise.resolve();
    return closing;
  }
  return { discover: options => run('discover', options), preview: options => run('preview', options),
    import: options => run('import', options), close };
}

module.exports = { createMemoryImportJobs };
