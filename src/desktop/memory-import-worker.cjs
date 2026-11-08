const { parentPort, workerData } = require('node:worker_threads');
const { discoverMemorySources, previewMemorySources, prepareMemoryImport } = require('./memory-import.cjs');

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
  const { action, options = {} } = workerData;
  const input = { ...options, onProgress };
  let result;
  if (action === 'discover') result = discoverMemorySources(input);
  else if (action === 'preview') result = previewMemorySources(input);
  else if (action === 'import') {
    result = prepareMemoryImport(input);
  } else throw new Error('Unknown memory import operation.');
  parentPort.postMessage({ type: 'result', result });
} catch (error) {
  parentPort.postMessage({ type: 'error', error: { name: error.name, message: error.message } });
} finally {
  parentPort.close();
}
