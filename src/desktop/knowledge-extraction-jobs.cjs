const defaultExtract = (...args) => require('./knowledge-extraction.cjs').extractKnowledge(...args);

const provider = 'doxie-extractor';
const lifecyclePrefix = 'Brain extraction: ';
const now = () => new Date().toISOString();
const turn = () => new Promise(resolve => setImmediate(resolve));
const sourcesOf = node => Array.isArray(node.provenance) && node.provenance.length ? node.provenance : node.source ? [node.source] : [];
const sourceIdOf = source => source?.provider === provider && typeof source.path === 'string' && source.path.startsWith('memory:') ? source.path.slice(7) : null;
const statusOf = node => node?.archivedAt ? 'archived' : node?.supersededBy ? 'superseded' : node?.lifecycle?.status || node?.status || 'current';
const eligible = node => node?.kind === 'memory' && !['archived', 'superseded'].includes(statusOf(node))
  && !sourcesOf(node).some(source => source.provider === provider);
const allRecords = { includeStale: true, includeArchived: true, includeSuperseded: true };
const identity = (sourceId, section) => `${sourceId}\0${section}`;
const sameFact = (node, fact) => node?.kind === fact.kind && node.title === fact.title && node.content === fact.content
  && JSON.stringify(node.parameters || null) === JSON.stringify(fact.parameters || null)
  && JSON.stringify([...(node.tags || [])].sort()) === JSON.stringify([...fact.tags].sort());

function createKnowledgeExtraction({ store, emit = () => {}, extract = defaultExtract, batchSize = 10 } = {}) {
  if (!store || ['records', 'get', 'remember', 'relate', 'review'].some(method => typeof store[method] !== 'function'))
    throw new Error('Knowledge extraction requires the owning Brain store.');
  const batch = Number.isInteger(batchSize) && batchSize > 0 ? Math.min(batchSize, 10) : 10;
  const state = { state: 'idle', total: 0, processed: 0, entities: 0, parameters: 0, created: 0, linked: 0,
    withdrawn: 0, reviewed: 0, truncated: false, lastRunAt: null, finishedAt: null };
  let current;
  let generation = 0;
  let rerun = false;
  let cancelling = false;
  let closed = false;
  const status = () => structuredClone(state);
  function publish() {
    try { emit({ type: 'memory:extractionChanged', status: status() }); }
    catch (error) { console.error('Could not publish Brain extraction status:', error.message); }
  }
  const valid = epoch => !closed && !cancelling && generation === epoch;

  async function execute(epoch) {
    const records = store.records(allRecords);
    const memories = records.filter(eligible);
    const knownIds = new Set(records.map(node => node.id));
    const known = new Map();
    for (const node of records.filter(node => ['entity', 'parameter'].includes(node.kind))) {
      for (const source of sourcesOf(node)) {
        const sourceId = sourceIdOf(source);
        if (!sourceId || !source.section) continue;
        const key = identity(sourceId, source.section);
        const prior = known.get(key);
        const rank = value => statusOf(value) === 'superseded' ? 0 : statusOf(value) === 'archived' ? 1 : 2;
        if (!prior || rank(node) > rank(prior) || (rank(node) === rank(prior) && node.updatedAt > prior.updatedAt)) known.set(key, node);
      }
    }
    const expected = new Map();
    Object.assign(state, { total: memories.length, processed: 0, entities: 0, parameters: 0, created: 0, linked: 0,
      withdrawn: 0, reviewed: 0, truncated: false });
    publish();
    for (const memory of memories) {
      if (!valid(epoch)) return;
      const extracted = await extract({ id: memory.id, title: memory.title, content: memory.content });
      if (!valid(epoch)) return;
      const live = store.get(memory.id);
      if (!eligible(live) || live.content !== memory.content || live.title !== memory.title) continue;
      const facts = [
        ...(extracted.entities || []).map(fact => ({ ...fact, kind: 'entity' })),
        ...(extracted.parameters || []).map(fact => ({ ...fact, kind: 'parameter' })),
      ].map(fact => ({ ...fact, tags: [...new Set(['locally-extracted', ...(fact.tags || [])])].sort(),
        parameters: fact.kind === 'parameter' ? fact.parameters || fact.values : null }));
      expected.set(memory.id, new Set(facts.map(fact => fact.key)));
      state.truncated ||= Boolean(extracted.truncated);
      const pending = facts.filter(fact => {
        const prior = known.get(identity(memory.id, fact.key));
        return !prior || statusOf(prior) === 'superseded' || !sameFact(prior, fact);
      });
      const apply = () => {
        for (const fact of pending) {
          if (!valid(epoch)) return;
          const key = identity(memory.id, fact.key);
          const prior = known.get(key);
          if (prior && statusOf(prior) !== 'superseded' && sameFact(prior, fact)) continue;
          const evidence = fact.evidence;
          if (!evidence || evidence.memoryId !== memory.id || typeof evidence.excerpt !== 'string'
            || !Number.isSafeInteger(evidence.start) || !Number.isSafeInteger(evidence.end)
            || evidence.start < 0 || evidence.end <= evidence.start || evidence.end > memory.content.length
            || memory.content.slice(evidence.start, evidence.end) !== evidence.excerpt)
            throw new Error('Extracted knowledge must retain exact literal source evidence.');
          const source = { provider, path: `memory:${memory.id}`, runId: memory.id, section: fact.key,
            label: `Literal extraction; not independently verified. Lines ${evidence.startLine}–${evidence.endLine}, characters ${evidence.start}–${evidence.end}.\n${evidence.excerpt}`,
            evidence: { ...evidence } };
          const derived = store.remember({ title: fact.title, content: fact.content, kind: fact.kind, origin: 'optimized',
            libraryId: live.libraryId, catalogId: live.catalogId, tags: fact.tags,
            ...(fact.parameters ? { parameters: fact.parameters } : {}), source,
            ...(live.reviewAfter ? { reviewAfter: live.reviewAfter } : {}), confidence: Math.min(live.confidence ?? 0.8, 0.65) });
          store.relate({ fromId: memory.id, toId: derived.id, type: fact.kind === 'entity' ? 'mentions' : 'supports', weight: 1 });
          if (statusOf(live) === 'stale' && statusOf(derived) === 'current'
            && !sourcesOf(derived).some(source => {
              const anchor = sourceIdOf(source);
              return anchor && statusOf(store.get(anchor)) === 'current' && eligible(store.get(anchor));
            })) {
            store.review({ id: derived.id, status: 'stale', evidence: `${lifecyclePrefix}Every supporting Brain source requires review.` });
            state.reviewed++;
          }
          if (!knownIds.has(derived.id)) { knownIds.add(derived.id); state.created++; }
          known.set(key, derived);
          state.linked++;
        }
      };
      if (pending.length) { if (store.batch) store.batch(apply); else apply(); }
      state.entities += facts.filter(fact => fact.kind === 'entity').length;
      state.parameters += facts.filter(fact => fact.kind === 'parameter').length;
      state.processed++;
      if (state.processed % batch === 0) { publish(); await turn(); }
    }
    if (!valid(epoch)) return;
    const derived = store.records(allRecords).filter(node => ['entity', 'parameter'].includes(node.kind)
      && statusOf(node) !== 'superseded' && sourcesOf(node).some(source => source.provider === provider));
    let reconciled = 0;
    for (const node of derived) {
      if (!valid(epoch)) return;
      const origins = sourcesOf(node);
      // Independent agent or imported provenance cannot be withdrawn by this
      // local extractor merely because an extraction anchor has gone away.
      if (origins.some(source => source.provider !== provider)) continue;
      const anchors = origins.filter(source => sourceIdOf(source) && expected.get(sourceIdOf(source))?.has(source.section))
        .map(source => store.get(sourceIdOf(source))).filter(eligible);
      const currentAnchors = anchors.filter(source => statusOf(source) === 'current');
      const desired = currentAnchors.length ? 'current' : anchors.length ? 'stale' : 'archived';
      const currentStatus = statusOf(node);
      const evidence = node.lifecycle?.evidence || '';
      const automatic = !evidence || evidence.startsWith(lifecyclePrefix);
      // User review decisions stay in force. Restore only lifecycle changes
      // this job made, or an expiry inherited from the source review date.
      if (['stale', 'archived'].includes(currentStatus) && !automatic) continue;
      const dates = (currentAnchors.length ? currentAnchors : anchors).map(source => source.reviewAfter)
        .filter(date => typeof date === 'string' && Number.isFinite(Date.parse(date))).sort();
      const reviewAfter = dates[0];
      if (currentStatus === desired && (!reviewAfter || node.reviewAfter === reviewAfter)) continue;
      if (currentStatus === 'archived' && desired === 'archived') continue;
      store.review({ id: node.id, status: desired,
        ...(reviewAfter ? { reviewAfter } : {}), evidence: `${lifecyclePrefix}${desired === 'current' ? 'A current Brain source contains this literal fact; extraction is not independent verification.' : desired === 'stale' ? 'Every supporting Brain source requires review.' : 'No active Brain source still contains this literal fact.'}` });
      if (desired === 'archived') state.withdrawn++; else state.reviewed++;
      reconciled++;
      if (reconciled % batch === 0) { publish(); await turn(); }
    }
  }

  function run() {
    if (closed) return Promise.reject(new Error('Brain knowledge extraction is closed.'));
    if (current) { if (!cancelling) rerun = true; return current; }
    if (cancelling) return Promise.resolve(status());
    const epoch = generation;
    rerun = true;
    state.state = 'running'; state.lastRunAt = now(); state.finishedAt = null; delete state.error;
    publish();
    current = (async () => {
      while (rerun && valid(epoch)) { rerun = false; await execute(epoch); }
      if (valid(epoch)) { state.state = 'ready'; state.finishedAt = now(); }
      return status();
    })().catch(error => {
      if (valid(epoch)) { state.state = 'error'; state.error = String(error.message || 'Local Brain extraction failed.').slice(0, 600); }
      throw error;
    }).finally(() => { current = null; publish(); });
    return current;
  }
  async function cancel() {
    generation++; rerun = false; cancelling = true;
    if (current) { state.state = 'cancelling'; publish(); await current.catch(() => {}); }
    state.state = closed ? 'closed' : 'idle'; state.finishedAt = now(); cancelling = false; publish();
    return status();
  }
  async function close() { closed = true; await cancel(); }
  return { status, run, cancel, close };
}

module.exports = { createKnowledgeExtraction };
