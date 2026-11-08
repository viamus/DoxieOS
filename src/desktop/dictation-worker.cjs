const { parentPort } = require('node:worker_threads');
const { availableParallelism } = require('node:os');
const { isAbsolute, join, resolve } = require('node:path');
const { stat } = require('node:fs/promises');
const { env, pipeline } = require('@huggingface/transformers');

env.allowRemoteModels = false;
env.allowLocalModels = true;
env.useBrowserCache = false;
env.useFSCache = false;

const sampleRate = 16_000;
const maximumSamples = sampleRate * 60;
const languages = new Set(['pt', 'en', 'es']);
let transcriber;
let loadedDirectory;
let busy = false;

async function transcribe({ id, modelDirectory, samples, language }) {
  if (typeof id !== 'string' || !id || id.length > 100) throw new Error('Invalid dictation request.');
  if (!(samples instanceof Float32Array) || !samples.length || samples.length > maximumSamples) {
    throw new Error('Dictation requires up to 60 seconds of 16 kHz audio.');
  }
  if (!languages.has(language)) throw new Error('Unsupported dictation language.');
  let energy = 0;
  for (const sample of samples) {
    if (!Number.isFinite(sample) || Math.abs(sample) > 1) throw new Error('The dictation audio is invalid.');
    energy += sample * sample;
  }
  if (energy / samples.length < 1e-8) return '';
  if (typeof modelDirectory !== 'string' || !isAbsolute(modelDirectory)) throw new Error('Invalid speech model directory.');
  const directory = resolve(modelDirectory);
  if (!(await stat(directory)).isDirectory()) throw new Error('The local speech model is missing.');
  if (!transcriber || loadedDirectory !== directory) {
    parentPort.postMessage({ id, type: 'progress', phase: 'loading' });
    await transcriber?.dispose();
    transcriber = null;
    loadedDirectory = null;
    env.cacheDir = join(directory, '.cache');
    transcriber = await pipeline('automatic-speech-recognition', directory, {
      dtype: 'q8', device: 'cpu', local_files_only: true,
      session_options: {
        intraOpNumThreads: Math.max(1, Math.min(4, availableParallelism())),
        interOpNumThreads: 1, executionMode: 'sequential', logSeverityLevel: 3,
      },
    });
    loadedDirectory = directory;
  }
  parentPort.postMessage({ id, type: 'progress', phase: 'transcribing' });
  const result = await transcriber(samples, {
    language, task: 'transcribe', chunk_length_s: 30, stride_length_s: 5,
    return_timestamps: false, do_sample: false,
  });
  return result.text.trim();
}

parentPort.on('message', request => {
  if (busy) {
    parentPort.postMessage({ id: request?.id, type: 'error', message: 'Dictation is already processing another recording.' });
    return;
  }
  busy = true;
  transcribe(request).then(
    text => parentPort.postMessage({ id: request.id, type: 'result', text }),
    error => parentPort.postMessage({ id: request?.id, type: 'error', message: String(error.message || 'Local transcription failed.').slice(0, 500) }),
  ).finally(() => { busy = false; });
});
