import { useCallback, useEffect, useRef, useState } from 'react';
import { BookOpen, CircleDot, FileText, FolderOpen, GitBranch, Link2, Network, RefreshCw, Settings2, Sparkles } from 'lucide-react';
import { Button, DateTime, Empty, ErrorNotice, Loading, PageHeading, Panel, SearchBox, Tag, invoke } from './ui.jsx';
import { Markdown } from './workbench.jsx';
import { MemoryGraph } from './memory-graph.jsx';
import './memory.css';

const kinds = [['memory', 'Memory'], ['entity', 'Entity'], ['parameter', 'Learned parameter']];
const labelOfKind = kind => kinds.find(([value]) => value === kind)?.[1] || 'Memory';
const readable = value => String(value || '').replace(/[_-]/g, ' ');
const nodeIcon = kind => kind === 'entity' ? CircleDot : kind === 'parameter' ? Settings2 : FileText;
const valueText = value => typeof value === 'string' ? value : JSON.stringify(value, null, 2);

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
    : status.state === 'indexing' ? `Preparing your memories for search · ${status.indexed || 0} indexed${status.pending ? ` · ${status.pending} remaining` : ''}`
      : ready ? `Search is ready · ${status.indexed || 0} memories indexed · runs locally on your CPU`
        : 'Find related knowledge even when the words differ. Optional local model download: about 135 MB. Runs on your CPU.';
  return <section className="memory-semantic" aria-label="Search by meaning"><Sparkles size={21} /><div className="memory-semantic-copy"><strong>Search by meaning{ready && <span className="memory-semantic-ready">Ready</span>}</strong><p>{description}</p>{preparing && <progress aria-label="Preparing search by meaning" max={status.state === 'downloading' ? totalBytes : Math.max(1, (status.indexed || 0) + (status.pending || 0))} value={status.state === 'downloading' ? status.receivedBytes || 0 : status.indexed || 0} />}{(error || status.error) && <ErrorNotice error={error || status.error} />}</div><Button icon={ready ? RefreshCw : Sparkles} disabled={busy || preparing || status.state === 'loading'} onClick={async () => {
    setBusy(true); setError('');
    try { await invoke(ready ? 'memory:semanticReindex' : 'memory:semanticInstall'); await update(); }
    catch (problem) { setError(problem.message); } finally { setBusy(false); }
  }}>{preparing || busy ? 'Preparing search…' : ready ? 'Reindex memories' : 'Enable semantic search'}</Button></section>;
}

function AgentMemorySources() {
  const [status, setStatus] = useState({ state: 'loading', sources: [] });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const update = useCallback(async () => {
    try { setStatus(await invoke('memory:feedStatus')); }
    catch (problem) { setError(problem.message); }
  }, []);
  useEffect(() => {
    update();
    const timer = setInterval(() => { if (document.visibilityState === 'visible') update(); }, 5000);
    const unsubscribe = window.doxie.onEvent(event => {
      if (event.type === 'memory:feedChanged' && event.status) setStatus(event.status);
      else if (['memory:feedChanged', 'memory:changed'].includes(event.type)) update();
    });
    const visible = () => { if (document.visibilityState === 'visible') update(); };
    document.addEventListener('visibilitychange', visible);
    return () => { clearInterval(timer); unsubscribe?.(); document.removeEventListener('visibilitychange', visible); };
  }, [update]);
  const active = ['scanning', 'importing'].includes(status.state);
  const labels = { loading: 'Checking sources', disabled: 'Disabled for this profile', idle: 'Waiting for the next sync', scanning: 'Checking agent memories', importing: 'Updating the library', ready: 'Up to date', error: 'Sync needs attention' };
  const sources = Array.isArray(status.sources) ? status.sources : [];
  const result = status.lastResult;
  const provider = value => value === 'codex' ? 'Codex' : ['claude-code', 'claude'].includes(value) ? 'Claude' : 'Agent memory';
  const failure = typeof status.error === 'string' ? status.error : status.error?.message;
  return <section className="memory-feed" aria-label="Agent memory sources"><div className="memory-feed-header"><FolderOpen size={20} /><div className="memory-feed-copy"><h2>Agent memory sources</h2><p>Codex and Claude memories are kept in sync automatically by your agent harness.</p></div><span className={`memory-feed-state ${status.state}`} role="status">{labels[status.state] || status.state}</span><Button icon={RefreshCw} disabled={busy || active || ['disabled', 'loading'].includes(status.state)} onClick={async () => {
    setBusy(true); setError('');
    try { const next = await invoke('memory:refreshSources'); if (next?.state) setStatus(next); else await update(); }
    catch (problem) { setError(problem.message); } finally { setBusy(false); }
  }}>{busy || active ? 'Refreshing agent memories…' : 'Refresh agent memories'}</Button></div>{(status.lastRunAt || result) && <div className="memory-feed-result">{status.lastRunAt && <span>Last checked <DateTime value={status.lastRunAt} /></span>}{result && <span>{Number(result.imported || 0).toLocaleString()} added · {Number(result.duplicates || 0).toLocaleString()} already known · {Number(result.skipped || 0).toLocaleString()} skipped</span>}</div>}{sources.length > 0 && <details className="memory-feed-sources"><summary>{sources.length} {sources.length === 1 ? 'agent memory source' : 'agent memory sources'}</summary><div className="memory-feed-list">{sources.map((source, index) => <div className="memory-feed-source" key={source.id || source.path || index}><div><strong>{provider(source.provider)}</strong>{source.fileCount != null && <span>{source.fileCount} {source.fileCount === 1 ? 'file' : 'files'}</span>}</div><span className="memory-source-path">{source.path}</span></div>)}</div></details>}{status.state === 'disabled' && <p className="memory-feed-note">Automatic source sync is disabled for this profile. Agent runs can still contribute verified learnings directly.</p>}{status.state === 'ready' && !sources.length && <p className="memory-feed-note">No Codex or Claude memory folders were found yet. New agent learnings will appear here as they are produced.</p>}{result?.warnings?.length > 0 && <details className="memory-feed-sources"><summary>{result.warnings.length} source {result.warnings.length === 1 ? 'note' : 'notes'}</summary><div className="memory-feed-list">{result.warnings.map((warning, index) => <p key={index}>{typeof warning === 'string' ? warning : warning.reason || warning.message || 'A source file was skipped.'}</p>)}</div></details>}{(error || failure) && <ErrorNotice error={error || failure} />}</section>;
}
function SourceDetails({ memory, onSelect }) {
  const origins = Array.isArray(memory.provenance) ? memory.provenance : [];
  const source = typeof memory.source === 'string' ? { path: memory.source } : memory.source || origins[0] || {};
  const providerLabel = provider => provider === 'codex' ? 'Codex' : ['claude-code', 'claude'].includes(provider) ? 'Claude' : provider === 'catalog' ? 'Library catalog' : provider === 'doxie-agent' ? 'Agent run' : provider === 'doxie' || !provider ? 'DoxieOS' : readable(provider);
  const path = source.path || source.sourcePath;
  const previous = path?.startsWith('memory:') ? path.slice(7) : null;
  const learnedAt = source.importedAt || memory.createdAt;
  return <div className="memory-detail-section"><h4>Origin</h4><dl className="memory-provenance"><div><dt>From</dt><dd>{providerLabel(source.provider)}</dd></div>{path && <div><dt>{previous ? 'Based on' : 'Source file'}</dt><dd className="memory-source-path">{previous ? <Button variant="ghost" onClick={() => onSelect(previous)}>Open source version</Button> : path}</dd></div>}{source.runId && <div><dt>Run</dt><dd className="memory-source-path">{source.runId}</dd></div>}{learnedAt && <div><dt>Remembered</dt><dd><DateTime value={learnedAt} /></dd></div>}{memory.updatedAt && memory.updatedAt !== memory.createdAt && <div><dt>Updated</dt><dd><DateTime value={memory.updatedAt} /></dd></div>}</dl>{origins.length > 1 && <details><summary>{origins.length} source records</summary><div className="stack">{origins.map((origin, index) => <dl className="memory-provenance" key={`${origin.path}/${index}`}><div><dt>From</dt><dd>{providerLabel(origin.provider)}</dd></div>{origin.path && <div><dt>Source</dt><dd className="memory-source-path">{origin.path}</dd></div>}{origin.importedAt && <div><dt>Imported</dt><dd><DateTime value={origin.importedAt} /></dd></div>}</dl>)}</div></details>}</div>;
}

function MemoryDetails({ id, revision, graph, onSelect }) {
  const detail = useRetainedAsync(() => id ? invoke('memory:get', id) : null, [id, revision]);
  const memory = detail.value?.id === id ? detail.value : null;
  const edges = (graph?.edges || []).filter(edge => [edge.fromId || edge.from, edge.toId || edge.to].includes(id));
  const previous = edges.find(edge => edge.type === 'supersedes' && (edge.fromId || edge.from) === id);
  const previousId = previous && (previous.toId || previous.to);
  return <Panel title="Memory details" icon={BookOpen} className="memory-details" actions={id && detail.loading && memory && <Tag>Updating…</Tag>}>{detail.error && <div className="panel-body"><ErrorNotice error={detail.error} /></div>}{id && detail.loading && !memory ? <Loading /> : memory ? <div className="panel-body stack"><div className="stack"><div className="memory-detail-meta"><span className={`memory-type ${memory.kind}`}>{labelOfKind(memory.kind)}</span>{memory.libraryId && <Tag>{readable(memory.libraryId)}</Tag>}</div><h3>{memory.title}</h3>{previousId && <Button variant="ghost" onClick={() => onSelect(previousId)}>Open previous version</Button>}{memory.archivedAt && <p className="muted">Archived · <DateTime value={memory.archivedAt} /></p>}{memory.supersededBy && <Button variant="ghost" onClick={() => onSelect(memory.supersededBy)}>Open current version</Button>}</div><div className="memory-detail-section"><h4>What to remember</h4><Markdown>{memory.content}</Markdown></div>{Object.keys(memory.parameters || {}).length > 0 && <div className="memory-detail-section"><h4>Learned parameters</h4><dl className="memory-parameters">{Object.entries(memory.parameters).map(([key, value]) => <div key={key}><dt>{readable(key)}</dt><dd>{valueText(value)}</dd></div>)}</dl></div>}{memory.tags?.length > 0 && <div className="memory-tag-list">{memory.tags.map(tag => <span className="memory-tag" key={tag}>{tag}</span>)}</div>}<SourceDetails memory={memory} onSelect={onSelect} />{edges.length > 0 && <div className="memory-detail-section"><h4>Connections</h4>{edges.map((edge, index) => { const target = (edge.fromId || edge.from) === id ? edge.toId || edge.to : edge.fromId || edge.from; const other = graph.nodes.find(node => node.id === target); return <Button key={edge.id || index} variant="ghost" icon={Link2} onClick={() => onSelect(target)}>{other?.title || 'Related memory'} · {readable(edge.type || 'related')}</Button>; })}</div>}</div> : <Empty compact title="Explore your knowledge" detail="Select a memory in the map or the list to see its content, learned values, and origin." />}</Panel>;
}

export function LibrariesPage() {
  const [search, setSearch] = useState('');
  const [query, setQuery] = useState('');
  const [kind, setKind] = useState('');
  const [selected, setSelected] = useState('');
  const [revision, setRevision] = useState(0);
  const selectionScope = useRef(null);
  useEffect(() => { const timer = setTimeout(() => setQuery(search.trim()), 180); return () => clearTimeout(timer); }, [search]);
  useEffect(() => window.doxie.onEvent(event => {
    if (['memory:changed', 'memory:semanticChanged'].includes(event.type) || (event.type === 'agent:changed' && !['Running', 'Queued'].includes(event.run?.status))) setRevision(value => value + 1);
  }), []);
  const data = useRetainedAsync(async () => {
    const filters = { ...(kind ? { kind } : {}), limit: 100 };
    const [stats, result, graph] = await Promise.all([invoke('memory:stats'), invoke(query ? 'memory:search' : 'memory:list', { ...filters, ...(query ? { query, maxBytes: 100_000 } : {}) }), invoke('memory:graph', { ...filters, limit: 36, ...(query ? { query } : {}) })]);
    return { stats, nodes: query ? result.results : result, graph, query, kind };
  }, [query, kind, revision]);
  const rows = data.value?.nodes || [];
  const graph = data.value?.graph || { nodes: [], edges: [] };
  const stats = data.value?.stats || {};
  const initialLoading = data.loading && !data.value;
  const unavailable = data.error && !data.value;
  useEffect(() => {
    if (!data.value) return;
    const scope = JSON.stringify([data.value.query, data.value.kind]);
    const changedScope = selectionScope.current !== scope;
    selectionScope.current = scope;
    if ((!selected || (changedScope && !data.value.graph.nodes.some(node => node.id === selected)))) setSelected(data.value.nodes[0]?.id || '');
  }, [data.value]);
  const change = (setter, value) => setter(value);
  return <div className="page memory-page"><PageHeading title="Library" icon={BookOpen} subtitle="Knowledge learned by your agents, with its sources and connections." />
    <div className="memory-learning-note"><CircleDot size={20} /><div><strong>Agents build this library as they work</strong><p>Verified learnings and useful parameters are recorded during agent runs. Explore the evidence, connections, and previous versions here.</p></div><Button to="/agents">Open agents</Button></div>
    <div className="memory-stats"><div className="memory-stat"><BookOpen size={20} /><div><strong>{stats.memories ?? '—'}</strong><small>Memories</small></div></div><div className="memory-stat"><CircleDot size={20} /><div><strong>{stats.entities ?? '—'}</strong><small>Entities</small></div></div><div className="memory-stat"><Settings2 size={20} /><div><strong>{stats.parameters ?? '—'}</strong><small>Learned parameters</small></div></div><div className="memory-stat"><GitBranch size={20} /><div><strong>{stats.relations ?? '—'}</strong><small>Connections</small></div></div></div>
    <div className="memory-toolbar"><SearchBox value={search} setValue={value => change(setSearch, value)} placeholder="Search memories…" /><select aria-label="Memory type" value={kind} onChange={event => change(setKind, event.target.value)}><option value="">All types</option>{kinds.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></div>
    <SemanticSearch />
    <AgentMemorySources />
    {data.error && <div className="stack"><ErrorNotice error={data.error} /><div className="actions start">{data.value && <span className="muted">Showing the last available view.</span>}<Button icon={RefreshCw} onClick={() => setRevision(value => value + 1)}>Retry library load</Button></div></div>}
    <div className="memory-layout"><div className="memory-main"><Panel title="Memory map" icon={Network} hint="Explore how your agents' knowledge connects." actions={<>{data.loading && data.value && <span role="status" className="actions"><RefreshCw size={13} className="spinner" />Updating…</span>}<Tag>{graph.nodes.length} nodes · {graph.edges.length} links</Tag></>}>{initialLoading ? <Loading /> : unavailable ? <Empty title="Library unavailable" detail="Retry loading shared agent knowledge." /> : <MemoryGraph graph={graph} selectedId={selected} onSelect={setSelected} />}</Panel><Panel title={query ? 'Search results' : 'Memories'} icon={FileText} actions={<Tag>{rows.length} shown</Tag>}>{initialLoading ? <Loading /> : unavailable ? <Empty compact title="Memories could not be loaded" /> : rows.length ? <div className="memory-results">{rows.map(memory => { const Icon = nodeIcon(memory.kind); return <button type="button" className={`memory-result${selected === memory.id ? ' selected' : ''}`} key={memory.id} aria-pressed={selected === memory.id} onClick={() => setSelected(memory.id)}><Icon size={17} /><span className="memory-result-copy"><span className="memory-result-heading"><strong>{memory.title}</strong><span className={`memory-type ${memory.kind}`}>{labelOfKind(memory.kind)}</span></span><span className="memory-result-excerpt">{memory.excerpt || memory.content}</span><span className="memory-result-caption">{memory.source?.provider === 'doxie-agent' ? 'Learned during an agent run' : memory.source?.provider === 'codex' ? 'Codex memory' : memory.source?.provider === 'claude-code' ? 'Claude memory' : 'Harness memory'}{memory.tags?.slice(0, 3).map(tag => <span key={tag}>#{tag}</span>)}</span></span></button>; })}</div> : <Empty compact title={query || kind ? 'No matching memories' : 'No agent learnings yet'} detail={query || kind ? 'Try another search or clear a filter.' : 'Run an agent to build shared knowledge. The harness also syncs existing Codex and Claude memories automatically.'} action={!query && !kind && <Button to="/agents">Open agents</Button>} />}</Panel></div><MemoryDetails id={selected} revision={revision} graph={graph} onSelect={setSelected} /></div>
  </div>;
}
