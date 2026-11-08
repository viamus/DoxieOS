const feedLimits = { maxFiles: 4096, maxTotalBytes: 64 * 1024 * 1024 };

function createMemoryFeed({ imports, emit = () => {}, codexHome, claudeHome, enabled = true, intervalMs = 300000 } = {}) {
  if (!imports || ['discover', 'preview', 'import'].some(method => typeof imports[method] !== 'function')) throw new Error('Automatic memory feed requires background import jobs.');
  const period = Number.isSafeInteger(intervalMs) && intervalMs > 0 ? intervalMs : 300000;
  const homeOptions = { ...(codexHome ? { codexHome } : {}), ...(claudeHome ? { claudeHome } : {}), ...feedLimits };
  const state = { state: enabled ? 'idle' : 'disabled', lastRunAt: null, lastResult: null, sources: [] };
  let current;
  let timer;
  let started = false;
  let closed = false;
  const status = () => structuredClone(state);
  function publish(type = 'memory:feedChanged') {
    if (closed) return;
    try { emit(type === 'memory:feedChanged' ? { type, status: status() } : { type }); }
    catch (error) { console.error('Could not publish automatic memory feed status:', error.message); }
  }
  function sources(items) {
    return items.filter(source => ['codex', 'claude-code'].includes(source.provider)).map(source => ({
      id: source.id, provider: source.provider, path: source.path, name: source.name,
      fileCount: source.fileCount || 0, bytes: source.bytes || 0,
    }));
  }
  async function execute() {
    if (closed || !enabled) return status();
    try {
      delete state.error;
      for (let attempt = 0; attempt < 2; attempt++) {
        state.state = 'scanning';
        publish();
        const discovered = await imports.discover(homeOptions);
        if (closed) return status();
        state.sources = sources(discovered.sources || []);
        const preview = await imports.preview({ sources: state.sources, ...feedLimits });
        if (closed) return status();
        state.sources = sources(preview.sources || state.sources);
        state.state = 'importing';
        publish();
        let result;
        try {
          result = await imports.import({ sources: state.sources,
            expectedFiles: (preview.files || []).map(({ id, contentHash }) => ({ id, contentHash })), ...feedLimits });
        } catch (error) {
          if (attempt === 0 && /Memory sources changed\. Preview again before importing\./.test(error.message) && !closed) continue;
          throw error;
        }
        if (closed) return status();
        state.lastRunAt = new Date().toISOString();
        state.lastResult = { imported: result.imported || 0, duplicates: result.duplicates || 0, skipped: result.skipped || 0,
          files: result.files ?? preview.files?.length ?? 0, warnings: [...(discovered.warnings || []), ...(result.warnings || [])] };
        state.state = 'ready';
        publish();
        if (state.lastResult.imported > 0) publish('memory:changed');
        return status();
      }
    } catch (error) {
      if (!closed) {
        state.state = 'error';
        state.error = String(error.message || 'The automatic memory feed could not finish.').slice(0, 600);
        state.lastRunAt = new Date().toISOString();
        publish();
      }
    }
    return status();
  }
  function refresh() {
    if (!enabled || closed) return Promise.resolve(status());
    if (current) return current;
    state.state = 'scanning';
    const job = Promise.resolve().then(execute).finally(() => { if (current === job) current = null; });
    current = job;
    return job;
  }
  function start() {
    if (started || closed || !enabled) return status();
    started = true;
    timer = setInterval(() => { void refresh(); }, period);
    timer.unref?.();
    void refresh();
    return status();
  }
  function close() {
    closed = true;
    clearInterval(timer);
    timer = undefined;
    // Import jobs are shared with the application. Their owner terminates
    // workers separately; closing a feed never waits indefinitely for one.
    return Promise.resolve();
  }
  return { start, refresh, status, close };
}

module.exports = { createMemoryFeed };
