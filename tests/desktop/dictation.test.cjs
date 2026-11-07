const assert = require('node:assert/strict');
const { createHash } = require('node:crypto');
const { EventEmitter } = require('node:events');
const { mkdtemp, readFile, readdir, rm, writeFile } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { test } = require('node:test');
const { Worker } = require('node:worker_threads');
const { createDictation } = require('../../src/desktop/dictation.cjs');

async function fixture(t, options = {}) {
  const root = await mkdtemp(join(tmpdir(), 'doxie-dictation-'));
  const contents = new Map([['config.json', Buffer.from('{"local":true}')], ['onnx/fixture.onnx', Buffer.from('verified fixture weights')]]);
  const model = { id: 'test-model', name: 'Fixture model', repository: 'test/model', revision: 'immutable', sampleRate: 16000, maxSeconds: 60,
    files: [...contents].map(([path, bytes]) => ({ path, bytes: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') })) };
  const requested = [];
  const speech = createDictation({ root, model, request: async url => {
    requested.push(url.href);
    return new Response(contents.get(url.pathname.split('/immutable/')[1]));
  }, ...options });
  t.after(async () => { await speech.close(); await rm(root, { recursive: true, force: true }); });
  return { speech, root, contents, model, requested };
}

test('installs verified model files once and refuses a changed model before inference', async t => {
  const { speech, root, requested } = await fixture(t);
  assert.equal((await speech.status()).state, 'missing');
  const states = await Promise.all([speech.install(), speech.install()]);
  assert.ok(states.every(state => state.state === 'ready'));
  assert.equal(requested.length, 2);
  await speech.install();
  assert.equal(requested.length, 2);
  const installed = join(root, '.models', 'dictation', 'test-model');
  assert.equal(await readFile(join(installed, 'onnx', 'fixture.onnx'), 'utf8'), 'verified fixture weights');
  await writeFile(join(installed, 'config.json'), '{"local":null}');
  await assert.rejects(speech.transcribe({ samples: new Float32Array(1600), language: 'pt' }), /downloaded again/);
  assert.equal((await speech.status()).state, 'missing');
});

test('never publishes an incomplete or mismatched voice model and can retry the download', async t => {
  let correct = false;
  let contents;
  const fixtureData = await fixture(t, { request: async url => new Response(correct ? contents.get(url.pathname.split('/immutable/')[1]) : Buffer.from('wrong')) });
  contents = fixtureData.contents;
  const { speech, root } = fixtureData;
  await assert.rejects(speech.install(), /integrity verification|unexpected size/);
  assert.equal((await speech.status()).state, 'missing');
  assert.deepEqual(await readdir(join(root, '.models', 'dictation')), []);
  correct = true;
  assert.equal((await speech.install()).state, 'ready');
});

test('rejects redirects outside HTTPS model sources', async t => {
  let requests = 0;
  const { speech } = await fixture(t, { request: async () => {
    requests++;
    return new Response(null, { status: 302, headers: { location: 'http://127.0.0.1/private' } });
  } });
  await assert.rejects(speech.install(), /trusted source/);
  assert.equal(requests, 1);
  assert.equal((await speech.status()).state, 'missing');
});

test('bounds recordings and serializes worker jobs while keeping transcript text in memory', async t => {
  const worker = new EventEmitter();
  let sent;
  worker.postMessage = request => { sent = request; };
  worker.terminate = async () => { worker.emit('exit', 0); return 0; };
  const { speech } = await fixture(t, { createWorker: () => worker });
  await speech.install();
  for (const recording of [
    { samples: [], language: 'pt' }, { samples: new Float32Array(960001), language: 'pt' },
    { samples: new Float32Array([NaN]), language: 'pt' }, { samples: new Float32Array([0.1]), language: 'invalid' },
  ]) await assert.rejects(speech.transcribe(recording), /Invalid voice recording/);
  const result = speech.transcribe({ samples: new Float32Array([0.1]), language: 'pt' });
  while (!sent) await new Promise(resolve => setImmediate(resolve));
  assert.equal((await speech.status()).busy, true);
  await assert.rejects(speech.transcribe({ samples: new Float32Array([0.1]), language: 'pt' }), /Another voice recording/);
  assert.equal(sent.language, 'pt');
  worker.emit('message', { id: sent.id, type: 'result', text: 'Texto reconhecido.' });
  assert.deepEqual(await result, { text: 'Texto reconhecido.' });
  assert.equal((await speech.status()).busy, false);
});

test('starts the packaged CPU runtime and handles silence without downloading a model', { timeout: 15000 }, async () => {
  const worker = new Worker(join(__dirname, '../../src/desktop/dictation-worker.cjs'));
  try {
    const response = new Promise((resolve, reject) => { worker.once('message', resolve); worker.once('error', reject); });
    worker.postMessage({ id: 'silence', modelDirectory: tmpdir(), samples: new Float32Array(1600), language: 'pt' });
    assert.deepEqual(await response, { id: 'silence', type: 'result', text: '' });
  } finally { await worker.terminate(); }
});
