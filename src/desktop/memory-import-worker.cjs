const { parentPort, workerData } = require('node:worker_threads');
const { createMemoryStore } = require('./memory-store.cjs');
const { discoverMemorySources, previewMemorySources, importMemorySources } = require('./memory-import.cjs');

let store;
let lastProgress = 0;
let lastPhase;
function onProgress(progress) {
  const timestamp = Date.now();
  if (progress.phase !== lastPhase || progress.completed === progress.total || timestamp - lastProgress >= 100) {
    parentPort.postMessage({ type: 'progress', progress });
    lastProgress = timestamp;
    lastPhase = progress.phase;
  }
}

try {
  const { action, root, options = {} } = workerData;
  const input = { ...options, onProgress };
  let result;
  if (action === 'discover') result = discoverMemorySources(input);
  else if (action === 'preview') result = previewMemorySources(input);
  else if (action === 'import') {
    store = createMemoryStore({ root });
    result = importMemorySources(store, input);
  } else throw new Error('Unknown memory import operation.');
  store?.close();
  store = null;
  parentPort.postMessage({ type: 'result', result });
} catch (error) {
  parentPort.postMessage({ type: 'error', error: { name: error.name, message: error.message } });
} finally {
  store?.close();
  parentPort.close();
}
