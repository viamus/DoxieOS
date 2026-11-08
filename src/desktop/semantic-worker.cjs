const { parentPort } = require('node:worker_threads');
const { availableParallelism } = require('node:os');
const { isAbsolute, resolve } = require('node:path');
const { stat } = require('node:fs/promises');
const { env, pipeline } = require('@huggingface/transformers');

env.allowRemoteModels = false;
env.allowLocalModels = true;
env.useBrowserCache = false;
env.useFSCache = false;

let extractor;
let loadedDirectory;
let busy = false;

async function embed({ id, modelDirectory, texts }) {
  if (typeof id !== 'string' || !id || id.length > 100) throw new Error('Invalid embedding request.');
  if (!Array.isArray(texts) || !texts.length || texts.length > 25
    || texts.some(text => typeof text !== 'string' || !text.trim() || Buffer.byteLength(text) > 300 * 1024)) {
    throw new Error('Embedding requests accept up to 25 nonempty texts of at most 300 KiB each.');
  }
  if (typeof modelDirectory !== 'string' || !isAbsolute(modelDirectory)) throw new Error('Invalid local embedding model directory.');
  const directory = resolve(modelDirectory);
  if (!(await stat(directory)).isDirectory()) throw new Error('The local embedding model is missing.');
  if (!extractor || directory !== loadedDirectory) {
    parentPort.postMessage({ id, type: 'progress', phase: 'loading' });
    await extractor?.dispose();
    extractor = null;
    loadedDirectory = null;
    extractor = await pipeline('feature-extraction', directory, {
      dtype: 'q8', device: 'cpu', local_files_only: true,
      session_options: { intraOpNumThreads: Math.max(1, Math.min(4, availableParallelism())),
        interOpNumThreads: 1, executionMode: 'sequential', logSeverityLevel: 3 },
    });
    loadedDirectory = directory;
  }
  const windows = [];
  const owners = [];
  const weights = [];
  for (const [index, text] of texts.entries()) {
    const ids = extractor.tokenizer.encode(text, { add_special_tokens: false });
    // Mean-pool all token windows rather than silently indexing only the first
    // model-length prefix of a longer memory chunk.
    for (let offset = 0; offset < ids.length;) {
      let count = Math.min(384, ids.length - offset);
      let window;
      while (true) {
        window = extractor.tokenizer.decode(ids.slice(offset, offset + count), { skip_special_tokens: true });
        if (extractor.tokenizer.encode(window).length <= 512 || count === 1) break;
        count = Math.max(1, Math.floor(count / 2));
      }
      if (window.trim()) { windows.push(window); owners.push(index); weights.push(count); }
      offset += count;
    }
  }
  const sums = texts.map(() => null);
  for (let offset = 0; offset < windows.length; offset += 8) {
    parentPort.postMessage({ id, type: 'progress', phase: 'embedding', completed: offset, total: windows.length });
    const output = await extractor(windows.slice(offset, offset + 8), { pooling: 'mean', normalize: true });
    for (const [local, vector] of output.tolist().entries()) {
      const owner = owners[offset + local];
      sums[owner] ??= new Float64Array(vector.length);
      for (let dimension = 0; dimension < vector.length; dimension++) sums[owner][dimension] += vector[dimension] * weights[offset + local];
    }
  }
  return sums.map(sum => {
    if (!sum) throw new Error('The memory did not contain embeddable text.');
    const norm = Math.hypot(...sum);
    if (!Number.isFinite(norm) || norm === 0) throw new Error('The local model produced an invalid embedding.');
    return Array.from(sum, value => value / norm);
  });
}

parentPort.on('message', request => {
  if (busy) { parentPort.postMessage({ id: request?.id, type: 'error', message: 'The local embedding worker is busy.' }); return; }
  busy = true;
  embed(request).then(vectors => parentPort.postMessage({ id: request.id, type: 'result', vectors }),
    error => parentPort.postMessage({ id: request?.id, type: 'error', message: String(error.message || 'Local embedding failed.').slice(0, 500) }))
    .finally(() => { busy = false; });
});
