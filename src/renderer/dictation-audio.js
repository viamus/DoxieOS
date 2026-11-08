import captureModule from './dictation-worklet.js?url&no-inline';

export const dictationSeconds = 60;
const sampleRate = 16_000;

export async function startDictationCapture(onLimit, signal) {
  const stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true }, video: false });
  let context;
  let source;
  let capture;
  let silent;
  let timeout;
  let active = true;
  let limited = false;
  let frames = 0;
  const chunks = [];

  async function waitForStartup(promise) {
    let startupTimeout;
    try {
      return await Promise.race([
        promise,
        new Promise((_, reject) => { startupTimeout = setTimeout(() => reject(new DOMException('The microphone took too long to start.', 'TimeoutError')), 8000); }),
      ]);
    } finally { clearTimeout(startupTimeout); }
  }

  function limit() {
    if (!active || limited) return;
    limited = true;
    onLimit();
  }

  function close() {
    active = false;
    clearTimeout(timeout);
    signal.removeEventListener('abort', cancel);
    for (const track of stream.getAudioTracks()) track.removeEventListener('ended', limit);
    if (capture) capture.port.onmessage = null;
    source?.disconnect();
    capture?.disconnect();
    silent?.disconnect();
    for (const track of stream.getTracks()) track.stop();
    return context && context.state !== 'closed' ? context.close().catch(() => {}) : Promise.resolve();
  }

  function cancel() {
    chunks.length = 0;
    void close();
  }

  try {
    signal.throwIfAborted();
    signal.addEventListener('abort', cancel, { once: true });
    for (const track of stream.getAudioTracks()) track.addEventListener('ended', limit, { once: true });
    context = new AudioContext({ sampleRate, latencyHint: 'interactive' });
    await waitForStartup(context.audioWorklet.addModule(captureModule));
    if (!active) throw new DOMException('Dictation was cancelled.', 'AbortError');
    source = context.createMediaStreamSource(stream);
    capture = new AudioWorkletNode(context, 'doxie-dictation', { channelCount: 1, channelCountMode: 'explicit' });
    silent = context.createGain();
    silent.gain.value = 0;
    capture.port.onmessage = ({ data }) => {
      if (!active || !(data instanceof Float32Array)) return;
      const remaining = Math.max(0, Math.floor(context.sampleRate * dictationSeconds) - frames);
      const chunk = data.length > remaining ? data.slice(0, remaining) : data;
      if (chunk.length) {
        chunks.push(chunk);
        frames += chunk.length;
      }
      if (frames >= context.sampleRate * dictationSeconds) limit();
    };
    source.connect(capture);
    capture.connect(silent);
    silent.connect(context.destination);
    await waitForStartup(context.resume());
    signal.throwIfAborted();
    if (!stream.getAudioTracks().some(track => track.readyState === 'live')) throw new DOMException('The microphone stopped before recording began.', 'NotReadableError');
    timeout = setTimeout(limit, dictationSeconds * 1000);
  } catch (error) {
    await close();
    throw error;
  }

  return {
    cancel() {
      chunks.length = 0;
      return close();
    },
    async stop() {
      await close();
      const samples = new Float32Array(frames);
      let offset = 0;
      for (const chunk of chunks) {
        samples.set(chunk, offset);
        offset += chunk.length;
      }
      chunks.length = 0;
      if (!frames || context.sampleRate === sampleRate) return samples;
      const output = new OfflineAudioContext(1, Math.round(frames * sampleRate / context.sampleRate), sampleRate);
      const buffer = output.createBuffer(1, frames, context.sampleRate);
      buffer.copyToChannel(samples, 0);
      const node = output.createBufferSource();
      node.buffer = buffer;
      node.connect(output.destination);
      node.start();
      return (await output.startRendering()).getChannelData(0).slice();
    },
  };
}
