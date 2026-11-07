const { randomUUID, createHash } = require('node:crypto');
const { createReadStream } = require('node:fs');
const { lstat, mkdir, mkdtemp, open, rename, rm } = require('node:fs/promises');
const { dirname, join, resolve, sep } = require('node:path');
const { Worker } = require('node:worker_threads');
const { safePath } = require('./storage.cjs');
const bundledModel = require('./dictation-model.json');

async function digest(file) {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(file)) hash.update(chunk);
  return hash.digest('hex');
}

function allowedDownload(url) {
  return url.protocol === 'https:' && !url.username && !url.password && !url.port
    && (url.hostname === 'huggingface.co' || url.hostname.endsWith('.huggingface.co') || url.hostname.endsWith('.hf.co'));
}

function createDictation({ root, emit = () => {}, model = bundledModel, request = fetch,
  createWorker = () => new Worker(join(__dirname, 'dictation-worker.cjs'), { execArgv: [] }) }) {
  if (!/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(model.id)) throw new Error('Invalid dictation model.');
  const base = safePath(root, '.models/dictation');
  const directory = safePath(base, model.id);
  const totalBytes = model.files.reduce((total, file) => total + file.bytes, 0);
  const abort = new AbortController();
  let ready = false;
  let receivedBytes = 0;
  let installing;
  let verification;
  let worker;
  let pending;
  let closed = false;
  const snapshot = () => ({
    state: installing ? 'downloading' : ready ? 'ready' : 'missing',
    receivedBytes: ready ? totalBytes : receivedBytes, totalBytes, busy: Boolean(pending),
    model: model.name,
  });
  const changed = () => emit({ type: 'speech:changed', status: snapshot() });

  async function verify() {
    try {
      for (const file of model.files) {
        const path = safePath(directory, file.path);
        const information = await lstat(path);
        if (!information.isFile() || information.size !== file.bytes || await digest(path) !== file.sha256) return false;
      }
      return true;
    } catch { return false; }
  }

  async function status() {
    if (!installing && !ready) {
      verification ??= verify().finally(() => { verification = null; });
      ready = await verification;
    }
    return snapshot();
  }

  async function removeTemporary(path) {
    const absolute = resolve(path);
    if (!absolute.startsWith(resolve(base) + sep) || absolute === resolve(directory)) throw new Error('Invalid temporary model directory.');
    await rm(absolute, { recursive: true, force: true });
  }

  async function responseFor(path) {
    let url = new URL(`https://huggingface.co/${model.repository}/resolve/${model.revision}/${path}`);
    for (let redirects = 0; redirects < 6; redirects++) {
      if (!allowedDownload(url)) throw new Error('The voice model download left its trusted source.');
      const response = await request(url, { redirect: 'manual', signal: AbortSignal.any([abort.signal, AbortSignal.timeout(120_000)]) });
      if ([301, 302, 303, 307, 308].includes(response.status)) {
        const location = response.headers.get('location');
        await response.body?.cancel();
        if (!location) throw new Error('The voice model download returned an invalid redirect.');
        url = new URL(location, url);
      } else {
        if (!response.ok || !response.body) throw new Error('Could not download the voice model. Check your connection and retry.');
        return response;
      }
    }
    throw new Error('The voice model download returned too many redirects.');
  }

  async function download() {
    await mkdir(base, { recursive: true });
    const temporary = await mkdtemp(join(base, '.download-'));
    let backup;
    receivedBytes = 0;
    let lastUpdate = 0;
    try {
      for (const file of model.files) {
        const target = safePath(temporary, file.path);
        await mkdir(dirname(target), { recursive: true });
        const response = await responseFor(file.path);
        const handle = await open(target, 'wx', 0o600);
        const hash = createHash('sha256');
        let bytes = 0;
        try {
          for await (const chunk of response.body) {
            abort.signal.throwIfAborted();
            bytes += chunk.length;
            if (bytes > file.bytes) throw new Error('The voice model download has an unexpected size.');
            hash.update(chunk);
            await handle.writeFile(chunk);
            receivedBytes += chunk.length;
            if (Date.now() - lastUpdate >= 120) { changed(); lastUpdate = Date.now(); }
          }
        } finally { await handle.close(); }
        if (bytes !== file.bytes || hash.digest('hex') !== file.sha256) throw new Error('Voice model integrity verification failed. Retry the download.');
      }
      abort.signal.throwIfAborted();
      if (await lstat(directory).catch(() => null)) {
        backup = safePath(base, `.replaced-${randomUUID()}`);
        await rename(directory, backup);
      }
      try { await rename(temporary, directory); }
      catch (error) { if (backup) await rename(backup, directory); backup = null; throw error; }
      ready = true;
      if (backup) await removeTemporary(backup).catch(error => console.error('Could not remove the previous voice model:', error.message));
    } finally {
      await removeTemporary(temporary);
      if (!ready) receivedBytes = 0;
    }
  }

  async function install() {
    if (closed) throw new Error('DoxieOS is closing.');
    if (!installing && (await status()).state === 'ready') return snapshot();
    if (!installing) {
      installing = download().finally(() => { installing = null; changed(); });
      changed();
    }
    await installing;
    return snapshot();
  }

  function failPending(message) {
    const job = pending;
    if (!job) return;
    pending = null;
    clearTimeout(job.timer);
    job.reject(new Error(message));
    changed();
  }

  function inferenceWorker() {
    if (worker) return worker;
    const instance = createWorker();
    worker = instance;
    instance.on('message', message => {
      if (!pending || pending.id !== message.id) return;
      if (message.type === 'error') { failPending(message.message || 'Local transcription could not finish.'); return; }
      if (message.type !== 'result') return;
      const job = pending;
      pending = null;
      clearTimeout(job.timer);
      job.resolve({ text: typeof message.text === 'string' ? message.text : '' });
      changed();
    });
    instance.on('error', () => {
      if (worker !== instance) return;
      worker = null;
      failPending('The local voice runtime could not start.');
    });
    instance.on('exit', () => {
      if (worker !== instance) return;
      worker = null;
      failPending('The local voice runtime stopped. Try dictation again.');
    });
    return instance;
  }

  async function transcribe({ samples, language } = {}) {
    if (closed) throw new Error('DoxieOS is closing.');
    if (!(samples instanceof Float32Array) || !samples.length || samples.length > model.sampleRate * model.maxSeconds
      || !samples.every(Number.isFinite) || !['pt', 'en', 'es'].includes(language)) throw new Error('Invalid voice recording.');
    if (pending) throw new Error('Another voice recording is being transcribed.');
    await status();
    if (!ready || installing) throw new Error('Download the local voice model before recording.');
    if (!worker && !await verify()) { ready = false; changed(); throw new Error('The voice model needs to be downloaded again.'); }
    if (pending) throw new Error('Another voice recording is being transcribed.');
    const instance = inferenceWorker();
    return new Promise((resolve, reject) => {
      const id = randomUUID();
      const timer = setTimeout(() => {
        if (pending?.id !== id) return;
        worker = null;
        failPending('Local transcription took too long. Try a shorter recording.');
        void instance.terminate();
      }, 120_000);
      pending = { id, resolve, reject, timer };
      changed();
      try { instance.postMessage({ id, modelDirectory: directory, samples, language }, [samples.buffer]); }
      catch { failPending('The voice recording could not be processed.'); }
    });
  }

  async function close() {
    closed = true;
    abort.abort();
    failPending('DoxieOS is closing.');
    await Promise.allSettled([installing, worker?.terminate()]);
    worker = null;
  }

  return { status, install, transcribe, close };
}

module.exports = { createDictation };
