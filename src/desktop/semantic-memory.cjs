const { createHash, randomUUID } = require('node:crypto');
const { createReadStream } = require('node:fs');
const { lstat, mkdir, mkdtemp, open, rename, rm } = require('node:fs/promises');
const { dirname, join, resolve, sep } = require('node:path');
const { Worker } = require('node:worker_threads');
const { safePath } = require('./storage.cjs');
const bundledModel = require('./semantic-model.json');
const { boundedMemoryNode } = require('./memory-view.cjs');

const bytes = value => Buffer.byteLength(typeof value === 'string' ? value : JSON.stringify(value));
const bounded = (value, fallback, maximum) => Number.isSafeInteger(Number(value)) && Number(value) > 0 ? Math.min(Number(value), maximum) : fallback;
function shorten(text, maximum) {
  if (bytes(text) <= maximum) return text;
  let output = '';
  let used = 0;
  for (const character of text) {
    const cost = bytes(character);
    if (used + cost + 3 > maximum) break;
    output += character;
    used += cost;
  }
  return output + (maximum >= 3 ? '…' : '');
}
async function digest(file) {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(file)) hash.update(chunk);
  return hash.digest('hex');
}
function allowedDownload(url) {
  return url.protocol === 'https:' && !url.username && !url.password && !url.port
    && (url.hostname === 'huggingface.co' || url.hostname.endsWith('.huggingface.co') || url.hostname.endsWith('.hf.co'));
}
function cosine(left, right) {
  if (left.length !== right.length) return -1;
  let dot = 0;
  let leftNorm = 0;
  let rightNorm = 0;
  for (let index = 0; index < left.length; index++) {
    if (!Number.isFinite(left[index]) || !Number.isFinite(right[index])) return -1;
    dot += left[index] * right[index];
    leftNorm += left[index] ** 2;
    rightNorm += right[index] ** 2;
  }
  return leftNorm && rightNorm ? dot / Math.sqrt(leftNorm * rightNorm) : -1;
}

function createSemanticMemory({ root, store, emit = () => {}, model = bundledModel, request = fetch,
  createWorker = () => new Worker(join(__dirname, 'semantic-worker.cjs'), { execArgv: [] }), timeoutMs = 300_000 }) {
  if (!/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(model.id) || !/^[\w.-]+\/[\w.-]+$/.test(model.repository)
    || !Array.isArray(model.files) || !model.files.length || model.files.some(file => !Number.isSafeInteger(file.bytes) || file.bytes < 1 || !/^[a-f0-9]{64}$/.test(file.sha256))) {
    throw new Error('Invalid local embedding model manifest.');
  }
  const modelKey = `${model.id}@${model.revision}`;
  const base = safePath(root, '.models/semantic');
  const directory = safePath(base, model.id);
  const totalBytes = model.files.reduce((total, file) => total + file.bytes, 0);
  for (const file of model.files) safePath(directory, file.path);
  const abort = new AbortController();
  let ready = false;
  let receivedBytes = 0;
  let installing;
  let indexing;
  let verification;
  let worker;
  let pending;
  let tail = Promise.resolve();
  let closed = false;
  let error;
  let phase;
  const counts = () => {
    if (store.embeddingStats) return store.embeddingStats({ model: modelKey });
    const indexed = store.getEmbeddings({ model: modelKey, limit: 10000 }).length;
    const stats = store.stats();
    return { indexed, pending: Math.max(0, (stats.memories + stats.entities + stats.parameters) - indexed) };
  };
  const snapshot = () => ({ state: installing ? 'downloading' : indexing ? 'indexing' : ready ? 'ready' : 'missing',
    model: model.name, modelId: modelKey, totalBytes, receivedBytes: ready ? totalBytes : receivedBytes,
    ...counts(), ...(error ? { error } : {}), ...(phase ? { phase } : {}) });
  const changed = () => emit({ type: 'memory:semanticChanged', status: snapshot() });
  async function verify() {
    try {
      for (const file of model.files) {
        const target = safePath(directory, file.path);
        const information = await lstat(target);
        if (!information.isFile() || information.isSymbolicLink() || information.size !== file.bytes || await digest(target) !== file.sha256) return false;
      }
      return true;
    } catch { return false; }
  }
  async function status() {
    if (!ready && !installing) {
      verification ??= verify().finally(() => { verification = null; });
      ready = await verification;
    }
    return snapshot();
  }
  async function removeTemporary(target) {
    const absolute = resolve(target);
    if (!absolute.startsWith(resolve(base) + sep) || absolute === resolve(directory)) throw new Error('Invalid temporary embedding directory.');
    await rm(absolute, { recursive: true, force: true });
  }
  async function responseFor(file) {
    let url = new URL(`https://huggingface.co/${model.repository}/resolve/${model.revision}/${file.path}`);
    for (let redirects = 0; redirects < 6; redirects++) {
      if (!allowedDownload(url)) throw new Error('The embedding model download left its trusted source.');
      const response = await request(url, { redirect: 'manual', signal: AbortSignal.any([abort.signal, AbortSignal.timeout(120_000)]) });
      if ([301, 302, 303, 307, 308].includes(response.status)) {
        const location = response.headers.get('location');
        await response.body?.cancel();
        if (!location) throw new Error('The embedding model returned an invalid redirect.');
        url = new URL(location, url);
      } else {
        if (!response.ok || !response.body) throw new Error('Could not download the local embedding model.');
        return response;
      }
    }
    throw new Error('The embedding model returned too many redirects.');
  }
  async function download() {
    await mkdir(base, { recursive: true });
    const temporary = await mkdtemp(join(base, '.download-'));
    let backup;
    receivedBytes = 0;
    let updated = 0;
    try {
      for (const file of model.files) {
        const target = safePath(temporary, file.path);
        await mkdir(dirname(target), { recursive: true });
        const response = await responseFor(file);
        const handle = await open(target, 'wx', 0o600);
        const hash = createHash('sha256');
        let size = 0;
        try {
          for await (const chunk of response.body) {
            abort.signal.throwIfAborted();
            size += chunk.length;
            if (size > file.bytes) throw new Error('The embedding model download has an unexpected size.');
            hash.update(chunk);
            await handle.writeFile(chunk);
            receivedBytes += chunk.length;
            if (Date.now() - updated > 120) { changed(); updated = Date.now(); }
          }
        } finally { await handle.close(); }
        if (size !== file.bytes || hash.digest('hex') !== file.sha256) throw new Error('Embedding model integrity verification failed.');
      }
      abort.signal.throwIfAborted();
      if (await lstat(directory).catch(() => null)) {
        backup = safePath(base, `.replaced-${randomUUID()}`);
        await rename(directory, backup);
      }
      try { await rename(temporary, directory); }
      catch (failure) { if (backup) await rename(backup, directory); backup = null; throw failure; }
      ready = true;
      error = undefined;
      if (backup) await removeTemporary(backup);
    } finally {
      await removeTemporary(temporary);
      if (!ready) receivedBytes = 0;
    }
  }
  function rejectPending(message) {
    if (!pending) return;
    const job = pending;
    pending = null;
    clearTimeout(job.timer);
    job.reject(new Error(message));
  }
  function inferenceWorker() {
    if (worker) return worker;
    const instance = createWorker();
    worker = instance;
    instance.on('message', message => {
      if (!pending || pending.id !== message.id) return;
      if (message.type === 'progress') { phase = message.phase; changed(); return; }
      if (message.type === 'error') { rejectPending(message.message || 'Local embedding failed.'); return; }
      if (message.type !== 'result') return;
      const job = pending;
      pending = null;
      clearTimeout(job.timer);
      job.resolve(message.vectors);
    });
    const failed = message => {
      if (worker !== instance) return;
      worker = null;
      rejectPending(message);
    };
    instance.on('error', () => failed('The local embedding runtime could not start.'));
    instance.on('exit', () => failed('The local embedding runtime stopped.'));
    return instance;
  }
  function embed(texts) {
    const task = tail.catch(() => {}).then(async () => {
      if (closed) throw new Error('DoxieOS is closing.');
      if (!worker && !await verify()) { ready = false; throw new Error('The embedding model must be downloaded again.'); }
      if (closed) throw new Error('DoxieOS is closing.');
      const instance = inferenceWorker();
      const vectors = await new Promise((resolve, reject) => {
        const id = randomUUID();
        const timer = setTimeout(() => {
          if (pending?.id !== id) return;
          worker = null;
          rejectPending('Local embedding took too long.');
          void instance.terminate();
        }, timeoutMs);
        pending = { id, resolve, reject, timer };
        try { instance.postMessage({ id, texts, modelDirectory: directory }); }
        catch (failure) { rejectPending(failure.message); }
      });
      if (!Array.isArray(vectors) || vectors.length !== texts.length || vectors.some(vector => !Array.isArray(vector)
        || vector.length !== model.dimensions || vector.some(value => !Number.isFinite(value)) || !vector.some(value => value !== 0))) throw new Error('The local model returned invalid embeddings.');
      error = undefined;
      return vectors;
    });
    const completed = task.finally(() => { if (!indexing) { phase = undefined; changed(); } });
    tail = completed.catch(() => {});
    return completed;
  }
  async function reindex({ batchSize = 25 } = {}) {
    if (closed) throw new Error('DoxieOS is closing.');
    if (installing) await installing;
    await status();
    if (closed) throw new Error('DoxieOS is closing.');
    if (!ready) throw new Error('Install the local embedding model before indexing.');
    if (!indexing) {
      error = undefined;
      indexing = (async () => {
        const embeddedIds = [];
        while (!closed) {
          const nodes = store.unembedded({ model: modelKey, limit: bounded(batchSize, 25, 25) });
          if (!nodes.length) break;
          const vectors = await embed(nodes.map(node => `${node.title}\n\n${node.content}`));
          if (closed) throw new Error('DoxieOS is closing.');
          const save = () => nodes.forEach((node, index) => store.setEmbedding(node.id, vectors[index], { model: modelKey }));
          if (store.batch) store.batch(save); else save();
          embeddedIds.push(...nodes.map(node => node.id));
          changed();
          await new Promise(resolve => setImmediate(resolve));
        }
        if (!closed && store.rebuildSemanticRelations) {
          phase = 'correlating';
          changed();
          // New embeddings only need their own nearest neighborhoods. A manual
          // reindex with no pending vectors refreshes all existing connections.
          const rebuild = store.rebuildSemanticRelationsAsync || store.rebuildSemanticRelations;
          await rebuild({ model: modelKey, ...(embeddedIds.length ? { ids: embeddedIds } : {}), threshold: 0.62, neighbors: 6,
            shouldContinue: () => !closed, onProgress: () => changed() });
          emit({ type: 'memory:changed' });
        }
      })().catch(failure => { error = failure.message; throw failure; }).finally(() => { indexing = null; phase = undefined; changed(); });
      changed();
    }
    await indexing;
    return snapshot();
  }
  async function install() {
    if (closed) throw new Error('DoxieOS is closing.');
    await status();
    if (closed) throw new Error('DoxieOS is closing.');
    if (!installing && !ready) {
      error = undefined;
      installing = download().catch(failure => { error = failure.message; throw failure; }).finally(() => { installing = null; changed(); });
      changed();
    }
    await installing;
    await reindex();
    return snapshot();
  }
  async function search(options = {}) {
    if (closed) throw new Error('DoxieOS is closing.');
    const limit = bounded(options.limit, 8, 100);
    const budget = bounded(options.maxBytes, 16000, 16000);
    const normalized = { ...options, limit, maxBytes: Math.max(1, budget - 32) };
    const lexical = store.search(normalized);
    if (!(await status()).state.match(/ready|indexing/) || !options.query?.trim()) return { ...lexical, mode: 'lexical' };
    try {
      const [queryVector] = await embed([options.query]);
      const scope = { libraryId: options.libraryId, catalogId: options.catalogId, kind: options.kind,
        includeStale: options.includeStale, includeArchived: options.includeArchived, includeSuperseded: options.includeSuperseded, status: options.status };
      const embeddings = store.nearest ? null : store.getEmbeddings({ model: modelKey, ...scope, limit: 10000 });
      const semantic = store.nearest ? store.nearest(queryVector, { model: modelKey, ...scope, limit: Math.max(40, limit * 4) })
        : embeddings.map(item => ({ id: item.id, similarity: cosine(queryVector, item.vector) }))
          .filter(item => item.similarity >= 0.15).sort((left, right) => right.similarity - left.similarity).slice(0, Math.max(40, limit * 4));
      if (!semantic.length && !lexical.results.length) return { ...lexical, mode: 'hybrid' };
      const ranks = new Map();
      lexical.results.forEach((item, index) => ranks.set(item.id, { id: item.id, lexicalRank: index + 1, score: 1 / (61 + index) }));
      semantic.filter(item => item.similarity >= 0.15).forEach((item, index) => {
        const rank = ranks.get(item.id) || { id: item.id, lexicalRank: Infinity, score: 0 };
        rank.score += 1 / (61 + index);
        rank.semanticScore = item.similarity;
        ranks.set(item.id, rank);
      });
      const candidates = [...ranks.values()].sort((left, right) => right.score - left.score || left.lexicalRank - right.lexicalRank);
      const responseQuery = shorten(options.query, Math.max(0, budget - 512));
      const queryMetadata = responseQuery === options.query ? {} : { queryTruncated: true };
      const resultBudget = budget - bytes({ query: responseQuery, results: [], truncated: false, bytes: budget, mode: 'hybrid', ...queryMetadata }) + 2;
      const results = [];
      let truncated = lexical.truncated || candidates.length > limit || responseQuery !== options.query
        || Boolean(embeddings && store.embeddingStats && store.embeddingStats({ model: modelKey, ...scope }).indexed > embeddings.length);
      for (const rank of candidates) {
        if (results.length >= limit) break;
        const node = store.get(rank.id);
        if (!node || (node.supersededBy && !options.includeSuperseded) || (node.archivedAt && !options.includeArchived)
          || (node.lifecycle?.status === 'stale' && options.includeStale !== true)) continue;
        const remaining = resultBudget - bytes(results) - 2;
        if (remaining < 256) { truncated = true; break; }
        const allowance = Math.min(remaining, Math.max(256, Math.floor(resultBudget / limit) - 4));
        const content = shorten(node.content, 2000);
        const hit = boundedMemoryNode({ ...node, content, excerpt: content, contentTruncated: content !== node.content,
          score: rank.score, ...(rank.semanticScore === undefined ? {} : { semanticScore: rank.semanticScore }) }, allowance);
        if (bytes([...results, hit]) > resultBudget) { truncated = true; break; }
        results.push(hit);
        truncated ||= hit.contentTruncated || hit.parametersTruncated || hit.provenanceTruncated || hit.tagsTruncated;
      }
      return { query: responseQuery, results, truncated, bytes: bytes(results), mode: 'hybrid', ...queryMetadata };
    } catch (failure) {
      error = failure.message;
      changed();
      return { ...lexical, mode: 'lexical' };
    }
  }
  async function close() {
    closed = true;
    abort.abort();
    rejectPending('DoxieOS is closing.');
    await Promise.allSettled([installing, indexing, verification, tail, worker?.terminate()]);
    worker = null;
  }
  return { status, install, reindex, search, close };
}

module.exports = { createSemanticMemory, cosine };
