import { useCallback, useEffect, useRef, useState } from 'react';
import { Brain, Check, CircleDot, Clock3, FileText, FolderOpen, GitBranch, Link2, Network, RefreshCw, Settings2, Sparkles } from 'lucide-react';
import { Button, DateTime, Empty, ErrorNotice, Loading, PageHeading, Panel, SearchBox, Tag, invoke } from './ui.jsx';
import { Markdown } from './workbench.jsx';
import { MemoryGraph } from './memory-graph.jsx';
import './memory.css';

const kinds = [['memory', 'Memory'], ['entity', 'Entity'], ['parameter', 'Learned parameter']];
const labelOfKind = kind => kinds.find(([value]) => value === kind)?.[1] || 'Memory';
const readable = value => String(value || '').replace(/[_-]/g, ' ');
const nodeIcon = kind => kind === 'entity' ? CircleDot : kind === 'parameter' ? Settings2 : FileText;
const valueText = value => typeof value === 'string' ? value : JSON.stringify(value, null, 2);
const lifecycleOf = memory => memory.lifecycle?.status || (memory.archivedAt ? 'archived' : memory.supersededBy ? 'superseded' : 'current');
const lifecycleLabels = { current: 'Current', stale: 'Needs review', superseded: 'Previous version', archived: 'Archived' };
const originOf = memory => memory.origin || memory.source?.kind || (['codex', 'claude', 'claude-code'].includes(memory.source?.provider) ? 'imported' : 'learned');
const originLabels = { imported: 'Imported memory', learned: 'Agent learning', optimized: 'Optimized knowledge', derived: 'Derived knowledge' };
const originLabel = memory => originLabels[originOf(memory)] || 'Constructed knowledge';
const lifecycleMatches = (memory, scope) => !scope || (scope === 'history' ? ['archived', 'superseded'].includes(lifecycleOf(memory)) : lifecycleOf(memory) === scope);

// Background learning must not remove the view the user is reading or dragging.
function useRetainedAsync(load, keys) {
  const [result, setResult] = useState({ loading: true });
  useEffect(() => {
    let current = true;
    setResult(previous => ({ ...previous, loading: true, error: undefined }));
    Promise.resolve().then(load).then(
      value => { if (current) setResult({ value, loading: false }); },
      error => { if (current) setResult(previous => ({ ...previous, loading: false, error: error.message })); },
    );
    return () => { current = false; };
  }, keys);
  return result;
}

function SemanticSearch() {
  const [status, setStatus] = useState({ state: 'loading' });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const update = useCallback(async () => {
    try { setStatus(await invoke('memory:semanticStatus')); }
    catch (problem) { setError(problem.message); }
  }, []);
  useEffect(() => {
    update();
    const timer = setInterval(() => { if (document.visibilityState === 'visible') update(); }, 3000);
    const unsubscribe = window.doxie.onEvent(event => { if (event.type === 'memory:semanticChanged') update(); });
    const visible = () => { if (document.visibilityState === 'visible') update(); };
    document.addEventListener('visibilitychange', visible);
    return () => { clearInterval(timer); unsubscribe?.(); document.removeEventListener('visibilitychange', visible); };
  }, [update]);
  const preparing = ['downloading', 'indexing'].includes(status.state);
  const ready = status.state === 'ready';
  const totalBytes = status.totalBytes || 135 * 1024 * 1024;
  const description = status.state === 'downloading' ? `Downloading the local model · ${Math.round(100 * (status.receivedBytes || 0) / totalBytes)}%`
    : status.state === 'indexing' ? status.phase === 'correlating' ? 'Connecting related memories · comparing meaning and correlation locally' : `Preparing your memories for search · ${status.indexed || 0} indexed${status.pending ? ` · ${status.pending} remaining` : ''}`
      : ready ? `Search and semantic connections are ready · ${status.indexed || 0} memories indexed · runs locally on your CPU`
        : 'Find related knowledge even when the words differ. Optional local model download: about 135 MB. Runs on your CPU.';
  return <section className="memory-semantic" aria-label="Search by meaning"><Sparkles size={21} /><div className="memory-semantic-copy"><strong>Search by meaning{ready && <span className="memory-semantic-ready">Ready</span>}</strong><p>{description}</p>{preparing && <progress aria-label="Preparing search by meaning" max={status.state === 'downloading' ? totalBytes : Math.max(1, (status.indexed || 0) + (status.pending || 0))} value={status.state === 'downloading' ? status.receivedBytes || 0 : status.indexed || 0} />}{(error || status.error) && <ErrorNotice error={error || status.error} />}</div><Button icon={ready ? RefreshCw : Sparkles} disabled={busy || preparing || status.state === 'loading'} onClick={async () => {
    setBusy(true); setError('');
    try { await invoke(ready ? 'memory:semanticReindex' : 'memory:semanticInstall'); await update(); }
    catch (problem) { setError(problem.message); } finally { setBusy(false); }
  }}>{preparing || busy ? 'Preparing search…' : ready ? 'Reindex memories' : 'Enable semantic search'}</Button></section>;
}

function ImportMemories() {
  const [sources, setSources] = useState([]);
  const [sourceIds, setSourceIds] = useState(new Set());
  const [discovered, setDiscovered] = useState(false);
  const [preview, setPreview] = useState(null);
  const [fileIds, setFileIds] = useState(new Set());
  const [warnings, setWarnings] = useState([]);
  const [result, setResult] = useState(null);
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const providerLabel = value => value === 'codex' ? 'Codex' : ['claude-code', 'claude'].includes(value) ? 'Claude Code' : readable(value);
  const toggle = (values, value) => new Set(values.has(value) ? [...values].filter(item => item !== value) : [...values, value]);
  async function run(operation, action) {
    setBusy(operation); setError('');
    try { await action(); } catch (problem) { setError(problem.message); } finally { setBusy(''); }
  }
  return <section className="memory-feed memory-import" aria-label="Import memories">
    <div className="memory-feed-header"><FolderOpen size={20} /><div className="memory-feed-copy"><h2>Import memories</h2><p>Bring in existing provider memories when you choose. Preview the files and select what belongs in Brain.</p></div><Button icon={FolderOpen} disabled={Boolean(busy)} onClick={() => run('discover', async () => {
      const found = await invoke('memory:discoverSources');
      setSources(found.sources || []); setSourceIds(new Set()); setWarnings(found.warnings || []); setDiscovered(true); setPreview(null); setResult(null);
    })}>{busy === 'discover' ? 'Finding sources…' : 'Find import sources'}</Button></div>
    {discovered && !sources.length && <p className="memory-feed-note">No recognized provider memory folders were found on this device.</p>}
    {sources.length > 0 && <div className="memory-import-sources">{sources.map(source => <label className="memory-import-source" key={source.id}><input type="checkbox" checked={sourceIds.has(source.id)} disabled={Boolean(busy)} onChange={() => { setSourceIds(current => toggle(current, source.id)); setPreview(null); setResult(null); }} /><span><strong>{source.name || providerLabel(source.provider)}</strong><small>{providerLabel(source.provider)}{source.fileCount != null && ` · ${source.fileCount} files`}</small><span className="memory-source-path">{source.path}</span></span></label>)}<div className="actions start"><Button icon={FileText} disabled={Boolean(busy) || !sourceIds.size} onClick={() => run('preview', async () => {
      const next = await invoke('memory:importPreview', { sourceIds: [...sourceIds] });
      setPreview(next); setFileIds(new Set((next.files || []).map(file => file.id))); setWarnings(next.warnings || []); setResult(null);
    })}>{busy === 'preview' ? 'Preparing preview…' : 'Preview selected sources'}</Button><span className="muted">{sourceIds.size} selected</span></div></div>}
    {preview && <div className="memory-import-preview"><div className="memory-import-preview-heading"><strong>{preview.files?.length || 0} files ready to import</strong>{preview.files?.length > 0 && <Button variant="ghost" disabled={Boolean(busy)} onClick={() => setFileIds(new Set(fileIds.size === preview.files.length ? [] : preview.files.map(file => file.id)))}>{fileIds.size === preview.files.length ? 'Clear selection' : 'Select all'}</Button>}</div><div className="memory-import-files">{(preview.files || []).map(file => <label className="memory-import-file" key={file.id}><input type="checkbox" checked={fileIds.has(file.id)} disabled={Boolean(busy)} onChange={() => setFileIds(current => toggle(current, file.id))} /><span><strong>{file.title || file.name || file.relativePath}</strong><small>{file.relativePath || file.name}{file.bytes != null && ` · ${(file.bytes / 1024).toFixed(1)} KB`}</small>{file.contentPreview && <span className="memory-import-excerpt">{file.contentPreview}</span>}</span></label>)}</div>{preview.files?.length > 0 && <div className="actions start"><Button icon={Brain} variant="primary" disabled={Boolean(busy) || !fileIds.size} onClick={() => run('import', async () => {
      const imported = await invoke('memory:import', { previewId: preview.previewId, selectedFileIds: [...fileIds] });
      setResult(imported); setWarnings(imported.warnings || []); setPreview(null);
    })}>{busy === 'import' ? 'Importing memories…' : 'Import selected memories'}</Button><span className="muted">{fileIds.size} selected · original files stay intact</span></div>}</div>}
    {result && <p className="memory-import-result" role="status"><Check size={16} />{Number(result.imported || 0).toLocaleString()} memories added · {Number(result.duplicates || 0).toLocaleString()} already known · {Number(result.skipped || 0).toLocaleString()} skipped</p>}
    {warnings.length > 0 && <details className="memory-feed-sources"><summary>{warnings.length} import {warnings.length === 1 ? 'note' : 'notes'}</summary><div className="memory-feed-list">{warnings.map((warning, index) => <p key={index}>{typeof warning === 'string' ? warning : warning.reason || warning.message || 'A source file was skipped.'}{warning?.path && <span className="memory-source-path memory-feed-warning-path">{warning.path}</span>}</p>)}</div></details>}
    {error && <ErrorNotice error={error} />}
  </section>;
}
function SourceDetails({ memory, onSelect }) {
  const origins = Array.isArray(memory.provenance) ? memory.provenance : [];
  const source = typeof memory.source === 'string' ? { path: memory.source } : memory.source || origins[0] || {};
  const providerLabel = provider => provider === 'codex' ? 'Codex' : ['claude-code', 'claude'].includes(provider) ? 'Claude' : provider === 'catalog' ? 'Knowledge catalog' : provider === 'doxie-agent' ? 'Agent run' : provider === 'doxie' || !provider ? 'DoxieOS' : readable(provider);
  const path = source.path || source.sourcePath;
  const previous = path?.startsWith('memory:') ? path.slice(7) : null;
  const learnedAt = source.importedAt || memory.createdAt;
  return <div className="memory-detail-section"><h4>Origin & evidence</h4><dl className="memory-provenance"><div><dt>Built through</dt><dd>{originLabel(memory)}</dd></div><div><dt>Provider</dt><dd>{providerLabel(source.provider)}</dd></div>{path && <div><dt>{previous ? 'Based on' : 'Source file'}</dt><dd className="memory-source-path">{previous ? <Button variant="ghost" onClick={() => onSelect(previous)}>Open source version</Button> : path}</dd></div>}{source.label && <div><dt>Evidence</dt><dd>{source.label}</dd></div>}{source.runId && <div><dt>Run</dt><dd className="memory-source-path">{source.runId}</dd></div>}{learnedAt && <div><dt>Remembered</dt><dd><DateTime value={learnedAt} /></dd></div>}{memory.updatedAt && memory.updatedAt !== memory.createdAt && <div><dt>Updated</dt><dd><DateTime value={memory.updatedAt} /></dd></div>}</dl>{origins.length > 1 && <details><summary>{origins.length} source records</summary><div className="stack">{origins.map((origin, index) => <dl className="memory-provenance" key={`${origin.path}/${index}`}><div><dt>From</dt><dd>{providerLabel(origin.provider)}</dd></div>{origin.path && <div><dt>Source</dt><dd className="memory-source-path">{origin.path}</dd></div>}{origin.importedAt && <div><dt>Imported</dt><dd><DateTime value={origin.importedAt} /></dd></div>}</dl>)}</div></details>}</div>;
}

function MemoryDetails({ id, revision, graph, onSelect, onReviewed }) {
  const detail = useRetainedAsync(() => id ? invoke('memory:get', id) : null, [id, revision]);
  const neighborhood = useRetainedAsync(() => id ? invoke('memory:graph', { nodeId: id, limit: 24, maxBytes: 64000, includeStale: true, includeHistory: true }) : null, [id, revision]);
  const [reviewing, setReviewing] = useState(false);
  const [reviewError, setReviewError] = useState('');
  useEffect(() => setReviewError(''), [id]);
  const memory = detail.value?.id === id ? detail.value : null;
  const relatedGraph = neighborhood.value?.nodes?.some(node => node.id === id) ? neighborhood.value : graph;
  const edges = (relatedGraph?.edges || []).filter(edge => [edge.fromId || edge.from, edge.toId || edge.to].includes(id)).sort((left, right) => (right.weight ?? 1) - (left.weight ?? 1));
  const previous = edges.find(edge => edge.type === 'supersedes' && (edge.fromId || edge.from) === id);
  const previousId = previous && (previous.toId || previous.to);
  const lifecycle = memory ? lifecycleOf(memory) : 'current';
  const reviewAfter = memory?.lifecycle?.reviewAfter || memory?.reviewAfter;
  async function review(status) {
    setReviewing(true); setReviewError('');
    try {
      await invoke('memory:review', { id, status, evidence: status === 'current' ? 'Confirmed current by the user in Brain.' : status === 'stale' ? 'Flagged for review by the user in Brain.' : 'Archived by the user in Brain.' });
      onReviewed();
    } catch (problem) { setReviewError(problem.message); }
    finally { setReviewing(false); }
  }
  return <Panel title="Memory details" icon={Brain} className="memory-details" actions={id && detail.loading && memory && <Tag>Updating…</Tag>}>
    {detail.error && <div className="panel-body"><ErrorNotice error={detail.error} /></div>}
    {id && detail.loading && !memory ? <Loading /> : memory ? <div className="panel-body stack">
      <div className="stack">
        <div className="memory-detail-meta"><span className={`memory-type ${memory.kind}`}>{labelOfKind(memory.kind)}</span><span className={`memory-lifecycle ${lifecycle}`}>{lifecycleLabels[lifecycle] || readable(lifecycle)}</span></div>
        <h3>{memory.title}</h3>
        {previousId && <Button variant="ghost" onClick={() => onSelect(previousId)}>Open previous version</Button>}
        {memory.supersededBy && <Button variant="ghost" onClick={() => onSelect(memory.supersededBy)}>Open current version</Button>}
      </div>
      <div className={`memory-review ${lifecycle}`}>
        <Clock3 size={16} /><div><strong>{lifecycle === 'stale' ? 'Ready for another look' : lifecycle === 'archived' ? 'Retained as history' : lifecycle === 'superseded' ? 'A newer version is available' : 'Available to your agents'}</strong><p>{lifecycle === 'stale' ? 'Recheck the evidence before using this knowledge. Review keeps it useful without losing its history.' : lifecycle === 'archived' || lifecycle === 'superseded' ? 'This version is preserved for traceability and excluded from default agent retrieval.' : 'A review date helps Brain keep changing knowledge fresh.'}</p>{reviewAfter && <small>Review {lifecycle === 'stale' ? 'was due' : 'due'} <DateTime value={reviewAfter} /></small>}
        {lifecycle !== 'superseded' && <div className="actions start memory-review-actions">{lifecycle === 'current' ? <Button variant="ghost" disabled={reviewing} onClick={() => review('stale')}>Flag for review</Button> : <Button icon={Check} variant="ghost" disabled={reviewing} onClick={() => review('current')}>{lifecycle === 'archived' ? 'Restore as current' : 'Confirm current'}</Button>}{lifecycle === 'stale' && <Button variant="ghost" disabled={reviewing} onClick={() => review('archived')}>Archive</Button>}</div>}
        </div>
      </div>
      {reviewError && <ErrorNotice error={reviewError} />}
      <div className="memory-detail-section"><h4>What to remember</h4><Markdown>{memory.content}</Markdown></div>
      {Object.keys(memory.parameters || {}).length > 0 && <div className="memory-detail-section"><h4>Learned parameters</h4><dl className="memory-parameters">{Object.entries(memory.parameters).map(([key, value]) => <div key={key}><dt>{readable(key)}</dt><dd>{valueText(value)}</dd></div>)}</dl></div>}
      {memory.tags?.length > 0 && <div className="memory-tag-list">{memory.tags.map(tag => <span className="memory-tag" key={tag}>{tag}</span>)}</div>}
      <SourceDetails memory={memory} onSelect={onSelect} />
      {edges.length > 0 && <div className="memory-detail-section"><h4>Connections</h4><div className="memory-connections">{edges.map((edge, index) => {
        const target = (edge.fromId || edge.from) === id ? edge.toId || edge.to : edge.fromId || edge.from;
        const other = relatedGraph.nodes.find(node => node.id === target);
        const inferred = edge.inferred || ['semantic', 'similarity', 'similar'].includes(edge.type);
        return <div className="memory-connection" key={edge.id || index}><Button variant="ghost" icon={Link2} onClick={() => onSelect(target)}>{other?.title || 'Related memory'}</Button><span>{inferred ? 'Semantic similarity' : readable(edge.type || 'related')} · {Math.round((edge.weight ?? 1) * 100)}% strength</span><div className="memory-connection-scores">{Number.isFinite(edge.semanticScore) && <span>Meaning {Math.round(edge.semanticScore * 100)}%</span>}{Number.isFinite(edge.correlationScore) && <span>Correlation {Math.round(edge.correlationScore * 100)}%</span>}{Number.isFinite(edge.distance) && <span>Distance {edge.distance.toFixed(2)}</span>}</div></div>;
      })}</div></div>}
    </div> : <Empty compact title="Explore your knowledge" detail="Select a memory in the map or the list to see its content, learned values, and origin." />}
  </Panel>;
}

export function LibrariesPage() {
  const [search, setSearch] = useState('');
  const [query, setQuery] = useState('');
  const [kind, setKind] = useState('');
  const [lifecycle, setLifecycle] = useState('');
  const [selected, setSelected] = useState('');
  const [revision, setRevision] = useState(0);
  const selectionScope = useRef(null);
  useEffect(() => { const timer = setTimeout(() => setQuery(search.trim()), 180); return () => clearTimeout(timer); }, [search]);
  useEffect(() => window.doxie.onEvent(event => {
    if (['memory:changed', 'memory:semanticChanged'].includes(event.type) || (event.type === 'agent:changed' && !['Running', 'Queued'].includes(event.run?.status))) setRevision(value => value + 1);
  }), []);
  const data = useRetainedAsync(async () => {
    const filters = { ...(kind ? { kind } : {}), includeStale: true, ...(lifecycle === 'history' ? { includeArchived: true, includeSuperseded: true } : lifecycle ? { status: lifecycle } : {}), limit: 100 };
    const [stats, result, graph] = await Promise.all([
      invoke('memory:stats'),
      invoke(query ? 'memory:search' : 'memory:list', { ...filters, ...(query ? { query, maxBytes: 100_000 } : {}) }),
      invoke('memory:graph', { ...filters, limit: 48, maxBytes: 128000, ...(query ? { query } : {}) }),
    ]);
    return { stats, nodes: (query ? result.results : result).filter(memory => lifecycleMatches(memory, lifecycle)), graph, query, kind, lifecycle };
  }, [query, kind, lifecycle, revision]);
  const rows = data.value?.nodes || [];
  const graph = data.value?.graph || { nodes: [], edges: [] };
  const stats = data.value?.stats || {};
  const initialLoading = data.loading && !data.value;
  const unavailable = data.error && !data.value;
  useEffect(() => {
    if (!data.value) return;
    const scope = JSON.stringify([data.value.query, data.value.kind, data.value.lifecycle]);
    const changedScope = selectionScope.current !== scope;
    selectionScope.current = scope;
    if (!selected || (changedScope && !data.value.graph.nodes.some(node => node.id === selected))) setSelected(data.value.nodes[0]?.id || '');
  }, [data.value]);
  return <div className="page memory-page">
    <PageHeading title="Brain" icon={Brain} subtitle="A shared memory that connects knowledge, keeps its evidence, and evolves with your agents." />
    <div className="memory-learning-note"><Brain size={22} /><div><strong>Connected to every agent</strong><p>Agents consult Brain, connect related knowledge, and retain verified learnings as they work. Imported memories, derived insights, and optimized knowledge share one living graph.</p></div><Tag>Always connected</Tag><Button to="/agents">Open agents</Button></div>
    <div className="memory-stats">
      <div className="memory-stat"><Brain size={20} /><div><strong>{stats.memories ?? '—'}</strong><small>Memories</small></div></div>
      <div className="memory-stat"><CircleDot size={20} /><div><strong>{stats.entities ?? '—'}</strong><small>Entities</small></div></div>
      <div className="memory-stat"><Settings2 size={20} /><div><strong>{stats.parameters ?? '—'}</strong><small>Learned parameters</small></div></div>
      <div className="memory-stat"><GitBranch size={20} /><div><strong>{stats.relations ?? '—'}</strong><small>Connections</small></div></div>
      <button type="button" className="memory-stat memory-stat-review" onClick={() => setLifecycle('stale')}><Clock3 size={20} /><div><strong>{stats.stale ?? stats.needsReview ?? 0}</strong><small>Need review</small></div></button>
    </div>
    <div className="memory-toolbar"><SearchBox value={search} setValue={setSearch} placeholder="Search memories…" /><select aria-label="Memory type" value={kind} onChange={event => setKind(event.target.value)}><option value="">All types</option>{kinds.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select><select aria-label="Memory lifecycle" value={lifecycle} onChange={event => setLifecycle(event.target.value)}><option value="">Current & needs review</option><option value="current">Current</option><option value="stale">Needs review</option><option value="history">Archived & previous versions</option></select></div>
    <SemanticSearch />
    <ImportMemories />
    {data.error && <div className="stack"><ErrorNotice error={data.error} /><div className="actions start">{data.value && <span className="muted">Showing the last available view.</span>}<Button icon={RefreshCw} onClick={() => setRevision(value => value + 1)}>Retry Brain load</Button></div></div>}
    <div className="memory-layout"><div className="memory-main">
      <Panel title="Memory map" icon={Network} hint="Explore evidence, semantic similarity, and the strength of each connection." actions={<>{data.loading && data.value && <span role="status" className="actions"><RefreshCw size={13} className="spinner" />Updating…</span>}<Tag>{graph.nodes.length} nodes · {graph.edges.length} links</Tag></>}>
        {initialLoading ? <Loading /> : unavailable ? <Empty title="Brain unavailable" detail="Retry loading shared agent knowledge." /> : <MemoryGraph graph={graph} selectedId={selected} onSelect={setSelected} />}
      </Panel>
      <Panel title={query ? 'Search results' : lifecycle === 'stale' ? 'Memories to review' : lifecycle === 'history' ? 'Memory history' : 'Memories'} icon={FileText} actions={<Tag>{rows.length} shown</Tag>}>
        {initialLoading ? <Loading /> : unavailable ? <Empty compact title="Memories could not be loaded" /> : rows.length ? <div className="memory-results">{rows.map(memory => {
          const Icon = nodeIcon(memory.kind);
          const status = lifecycleOf(memory);
          return <button type="button" className={`memory-result${selected === memory.id ? ' selected' : ''}`} key={memory.id} aria-pressed={selected === memory.id} onClick={() => setSelected(memory.id)}>
            <Icon size={17} /><span className="memory-result-copy"><span className="memory-result-heading"><strong>{memory.title}</strong><span className={`memory-type ${memory.kind}`}>{labelOfKind(memory.kind)}</span></span><span className="memory-result-excerpt">{memory.excerpt || memory.content}</span><span className="memory-result-caption"><span>{originLabel(memory)}</span>{status !== 'current' && <span className={`memory-lifecycle ${status}`}>{lifecycleLabels[status] || readable(status)}</span>}{memory.tags?.slice(0, 3).map(tag => <span key={tag}>#{tag}</span>)}</span></span>
          </button>;
        })}</div> : <Empty compact title={query || kind || lifecycle ? 'No matching memories' : 'No agent learnings yet'} detail={query || kind || lifecycle ? 'Try another search or clear a filter.' : 'Run an agent to build shared knowledge. Import existing provider memories when you choose.'} action={!query && !kind && !lifecycle && <Button to="/agents">Open agents</Button>} />}
      </Panel>
    </div><MemoryDetails id={selected} revision={revision} graph={graph} onSelect={setSelected} onReviewed={() => setRevision(value => value + 1)} /></div>
  </div>;
}
