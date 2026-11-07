import { useEffect, useRef, useState } from 'react';
import { Activity, BookOpen, Bot, Clock3, Code2, Cpu, Download, FileText, GitBranch, Import, LoaderCircle, MessageSquare, Pencil, Play, Plug, Plus, Search, Send, ShieldCheck, Sparkles, Square, Terminal, Trash2 } from 'lucide-react';
import { Button, Confirm, DateTime, Empty, ErrorNotice, ExecutionFields, Field, FieldInput, IconButton, ItemRow, Loading, Modal, PageHeading, Panel, SearchBox, Status, Tag, VoiceInput, active, go, href, invoke, nameOf, part, selectModes, useApp, useAsync, useEffortLabel, useText } from './ui.jsx';
import { FilesBrowser, Markdown } from './workbench.jsx';

export const recordKey = (item) => `${item.catalogId || 'default'}/${item.id}`;
export function recordRoute(kind, item, child = '', runId) {
  const query = new URLSearchParams();
  if (item.catalogId && item.catalogId !== 'default') query.set('catalog', item.catalogId);
  if (runId) query.set('run', runId);
  return `/${kind}/${part(item.id)}${child ? `/${child}` : ''}${query.size ? `?${query}` : ''}`;
}

const categoryOf = (item) => {
  const category = item.displayCategory || item.customCategory || item.category || 'Other';
  return category === 'Connector' ? 'Other' : category;
};
const iconOptions = { SmartToy: Bot, AutoAwesome: Sparkles, Code: Code2, Terminal, AccountTree: GitBranch, MenuBook: BookOpen, Shield: ShieldCheck, Search };
const indexedText = (item) => [item.id, nameOf(item), item.description, categoryOf(item), ...(item.nodes || []).flatMap((node) => [node.label, node.agentId, node.toolId])].filter(Boolean).join(' ').toLowerCase();

function builderSessionName(session, items) {
  const draft = session.draft?.manifest || session.draft?.definition || session.draft;
  const named = [draft?.displayName, draft?.name, session.label].find((value) => typeof value === 'string' && value.trim());
  if (named) return named.trim();
  const editing = items.find((item) => item.id === session.editingId && (item.catalogId || 'default') === (session.catalogId || 'default'));
  if (editing) return `Edit ${nameOf(editing)}`;
  const request = session.messages?.find((message) => message.role === 'user')?.text;
  if (request?.trim()) {
    const title = request.trim().replace(/\s+/g, ' ');
    return title.length > 80 ? `${title.slice(0, 77)}…` : title;
  }
  return `New ${session.kind === 'workflow' ? 'workflow' : 'agent'} session`;
}

function Recommendations({ items, kind }) {
  const [context, setContext] = useState('');
  const [submitted, setSubmitted] = useState('');
  const terms = [...new Set(submitted.toLowerCase().match(/[\p{L}\p{N}][\p{L}\p{N}-]{2,}/gu) || [])];
  const ranked = items.map((item) => ({ item, score: terms.reduce((total, word) => total + (indexedText(item).includes(word) ? 1 : 0) + (nameOf(item).toLowerCase().includes(word) ? 3 : 0), 0) })).filter(({ score }) => score > 0).sort((a, b) => b.score - a.score).slice(0, 6);
  return <details className="recommendations"><summary><Sparkles size={16} /> Find {kind} for a task</summary><div className="stack">
    <form className="input-action" onSubmit={(event) => { event.preventDefault(); setSubmitted(context); }}><input aria-label="Describe the task" placeholder="Describe what you want to do…" value={context} onChange={(event) => setContext(event.target.value)} /><Button type="submit" icon={Sparkles} disabled={!context.trim()}>Recommend</Button></form>
    {submitted && (ranked.length ? <div className="row-list">{ranked.map(({ item, score }) => <ItemRow key={recordKey(item)} to={recordRoute(kind, item)} title={nameOf(item)} detail={item.description}><Tag>{score} match score</Tag></ItemRow>)}</div> : <Empty compact title="No matching catalog entries" icon={Search} />)}
  </div></details>;
}

function CatalogPage({ kind }) {
  const { state, perform } = useApp();
  const t = useText();
  const [search, setSearch] = useState('');
  const [catalog, setCatalog] = useState('');
  const [category, setCategory] = useState('');
  const [remove, setRemove] = useState(null);
  const agent = kind === 'agents';
  const scope = agent ? 'Agents' : 'Workflows';
  const singular = agent ? 'agent' : 'workflow';
  const Icon = agent ? Bot : GitBranch;
  const items = state[kind];
  const categories = [...new Set(items.map(categoryOf))].sort((a, b) => String(a).localeCompare(String(b)));
  const catalogs = state.catalogs || state.settings.catalogs || [];
  const visible = items.filter((item) => (!catalog || item.catalogId === catalog) && (!category || categoryOf(item) === category) && indexedText(item).includes(search.trim().toLowerCase()));
  return <div className="page"><PageHeading title={t(`${scope}.Title`, scope)} icon={Icon} subtitle={t(`${scope}.Subtitle`, agent ? 'Run agents from your catalogs and build new ones with AI.' : 'Compose repeatable agent workflows and review their runs.')} actions={<>
    <Button icon={Import} onClick={() => perform('catalog:import', [kind, catalog || undefined], `${agent ? 'Agent' : 'Workflow'} imported`)}>Import</Button>
    {!agent && <Button to="/workflows/new/chat" icon={Sparkles}>Build with AI</Button>}
    <Button to={`/${kind}/new`} icon={agent ? Sparkles : Plus} variant="primary">{agent ? 'Build agent' : t('Workflows.New', 'New workflow')}</Button>
  </>} />
  <div className="toolbar"><SearchBox value={search} setValue={setSearch} placeholder={t(`${scope}.SearchPlaceholder`, `Search ${kind}…`)} />
    <select aria-label="Catalog" value={catalog} onChange={(event) => setCatalog(event.target.value)}><option value="">All catalogs</option>{catalogs.map((item) => <option key={item.id} value={item.id}>{nameOf(item)}</option>)}</select>
    <select aria-label="Category" value={category} onChange={(event) => setCategory(event.target.value)}><option value="">All categories</option>{categories.map((value) => <option key={value} value={value}>{value}</option>)}</select>
    {agent && <Button to="/agents/new?sessions=1" icon={MessageSquare}>Sessions</Button>}
  </div>
  {items.length > 0 && <Recommendations items={items} kind={kind} />}
  {visible.length ? <div className="cards catalog-cards">{visible.map((item) => <Panel key={recordKey(item)} title={<a className="panel-title-link" href={href(recordRoute(kind, item))}>{nameOf(item)}</a>} icon={iconOptions[item.icon] || Icon} hint={item.catalogName || item.id} actions={<><IconButton icon={Download} label={`Export ${singular}`} onClick={() => perform('catalog:export', [kind, item.id, item.catalogId])} />{!item.categorySealed && !item.builtIn && <IconButton icon={Trash2} label={`Delete ${singular}`} onClick={() => setRemove(item)} />}</>}>
    <div className="panel-body catalog-card-body"><p>{item.description || 'No description provided.'}</p><div className="actions start"><Tag>{categoryOf(item)}</Tag>{item.isPrivate && <Tag>Private</Tag>}{!agent && <><Tag>{item.nodes?.length || 0} nodes</Tag><Status status={item.enabled === false ? 'Disabled' : 'Enabled'} /></>}</div><div className="actions start"><Button to={recordRoute(kind, item)} icon={agent ? Play : GitBranch}>{agent ? 'Configure run' : 'Open workflow'}</Button></div></div>
  </Panel>)}</div> : <Panel><Empty icon={Icon} title={items.length ? `No matching ${kind}` : `No ${kind} yet`} detail={agent ? 'Build an agent or import one from an existing catalog.' : 'Compose a workflow manually or build one with AI.'} action={!items.length && <Button to={`/${kind}/new`} icon={Plus}>New {singular}</Button>} /></Panel>}
  {remove && <Confirm title={`Delete ${singular}`} detail={`Remove ${nameOf(remove)} from its catalog?`} close={() => setRemove(null)} confirm={async () => { const result = await perform('catalog:remove', [kind, remove.id, remove.catalogId], `${scope.slice(0, -1)} removed`); if (result !== false) setRemove(null); }} />}
  </div>;
}

export function AgentsPage() { return <CatalogPage kind="agents" />; }
export function WorkflowsPage() { return <CatalogPage kind="workflows" />; }

export function LogOutput({ lines, label = 'Execution output' }) {
  const log = useRef(null);
  const follow = useRef(true);
  useEffect(() => { if (log.current && follow.current) log.current.scrollTop = log.current.scrollHeight; }, [lines]);
  if (!lines?.length) return <Empty compact title="No output yet" icon={Terminal} />;
  const formatted = Array.isArray(lines) ? lines : String(lines).split(/\r?\n/);
  return <div ref={log} className="log-output" aria-label={label} tabIndex={0} onScroll={(event) => { const node = event.currentTarget; follow.current = node.scrollHeight - node.scrollTop - node.clientHeight < 40; }}><pre>{formatted.map((line, index) => typeof line === 'string' ? <span key={index}>{line}{'\n'}</span> : <span key={index} className={String(line.source || '').toLowerCase()}>{line.text ?? line.message ?? JSON.stringify(line)}{'\n'}</span>)}</pre></div>;
}

export function Guidance({ title, submit }) {
  const [value, setValue] = useState('');
  const [sending, setSending] = useState(false);
  return <form className="guidance input-action" onSubmit={async (event) => { event.preventDefault(); setSending(true); const ok = await submit(value.trim()); setSending(false); if (ok !== false) setValue(''); }}><input aria-label={title} placeholder={title} value={value} onChange={(event) => setValue(event.target.value)} /><Button type="submit" icon={Send} disabled={!value.trim() || sending}>Send</Button></form>;
}

function AgentRunForm({ agent, launch, connector }) {
  const { state } = useApp();
  const effortLabel = useEffortLabel();
  const modes = selectModes(agent);
  const [mode, setMode] = useState(Object.keys(modes)[0] || '');
  const [workspaceId, setWorkspaceId] = useState('');
  const [execution, setExecution] = useState({});
  const [values, setValues] = useState({});
  const [extra, setExtra] = useState('');
  const [environment, setEnvironment] = useState([]);
  const [error, setError] = useState('');
  const [pending, setPending] = useState(false);
  const fields = modes[mode]?.fields || [];
  const selectedWorkspaceRequired = !!modes[mode]?.requiresWorkspace;
  const savedProviderId = agent.execution?.providerId || state.settings.defaultProvider;
  const savedProvider = state.providers.find(({ id }) => id === savedProviderId);
  const providerDefaults = state.settings.providers?.[savedProviderId] || savedProvider;
  const savedModel = agent.execution?.model || providerDefaults?.model;
  const savedEffort = agent.execution?.effort || ((!providerDefaults?.model || providerDefaults.model === savedModel) && providerDefaults?.effort);
  return <form className="panel-body stack" onSubmit={async (event) => {
    event.preventDefault();
    setError('');
    const names = environment.map(({ name }) => name.trim());
    if (new Set(names).size !== names.length) { setError('Environment variable names must be unique.'); return; }
    setPending(true);
    try {
      const inputs = Object.fromEntries(fields.map((field) => [field.id, values[field.id] ?? field.defaultValue ?? '']));
      const env = Object.fromEntries(environment.map(({ name, value }) => [name.trim(), value]));
      const overrides = connector ? { providerId: execution.providerId || undefined, model: execution.model || undefined, effort: execution.effort || undefined } : {};
      const result = await launch({ mode: mode || undefined, workspaceId: workspaceId || undefined, ...overrides, inputs, arguments: extra || undefined, catalogId: agent.catalogId, env });
      if (result) setEnvironment([]);
    } finally { setPending(false); }
  }}>
    <div className="form-grid">
      {!!Object.keys(modes).length && <Field label="Mode"><select aria-label="Mode" value={mode} onChange={(event) => { setMode(event.target.value); setValues({}); }}>{Object.entries(modes).map(([id, item]) => <option key={id} value={id}>{item.label || item.name || id}</option>)}</select></Field>}
      <Field label={`Workspace${selectedWorkspaceRequired ? ' *' : ''}`}><select required={selectedWorkspaceRequired} value={workspaceId} onChange={(event) => setWorkspaceId(event.target.value)}><option value="">{selectedWorkspaceRequired ? 'Select a workspace' : 'DoxieOS data directory'}</option>{state.workspaces.map((workspace) => <option key={workspace.id} value={workspace.id}>{nameOf(workspace)}</option>)}</select></Field>
      {connector && <ExecutionFields value={execution} onChange={setExecution} inherited={agent.execution} inheritedLabel={agent.execution?.providerId ? 'Connector default' : 'Application default'} />}
    </div>
    {connector ? <p className="muted">Provider and model selections in this form apply to this run.</p> : <section className="stack" aria-label="Agent execution"><strong>Agent execution</strong><div className="actions start"><Tag>{[nameOf(savedProvider) || savedProviderId, savedModel || 'Provider default model', savedEffort && effortLabel(savedEffort)].filter(Boolean).join(' · ')}</Tag></div><p className="muted">Uses the agent’s saved execution defaults. Edit Execution defaults above to change them.</p></section>}
    {modes[mode]?.description && <p className="muted">{modes[mode].description}</p>}
    {fields.map((field) => <FieldInput key={field.id} field={field} catalogId={agent.catalogId} value={values[field.id]} onChange={(value) => setValues((old) => ({ ...old, [field.id]: value }))} />)}
    {!fields.length && <Field label="Arguments / prompt"><textarea rows={3} value={extra} onChange={(event) => setExtra(event.target.value)} placeholder="Describe what this agent should do…" /></Field>}
    <details className="inline-panel panel"><summary>Environment for this run {environment.length > 0 && <Tag>{environment.length}</Tag>}</summary><div className="stack check-grid">
      <p className="muted">Values are passed to the provider process for this run and cleared after it starts.</p>
      {environment.map((entry) => <div className="input-action" key={entry.id}>
        <input aria-label="Variable name" value={entry.name} required pattern="[A-Za-z_][A-Za-z0-9_]*" placeholder="VARIABLE_NAME" onChange={(event) => setEnvironment((old) => old.map((item) => item.id === entry.id ? { ...item, name: event.target.value } : item))} />
        <input aria-label="Variable value" value={entry.value} type="password" autoComplete="off" placeholder="Value" onChange={(event) => setEnvironment((old) => old.map((item) => item.id === entry.id ? { ...item, value: event.target.value } : item))} />
        <IconButton icon={Trash2} label="Remove variable" onClick={() => setEnvironment((old) => old.filter((item) => item.id !== entry.id))} />
      </div>)}
      <div className="actions start"><Button icon={Plus} onClick={() => setEnvironment((old) => [...old, { id: crypto.randomUUID(), name: '', value: '' }])}>Add variable</Button></div>
    </div></details>
    <ErrorNotice error={error} />
    <div className="actions start"><Button type="submit" variant="primary" icon={Play} disabled={pending}>{connector ? 'Run connector' : 'Run agent'}</Button></div>
  </form>;
}

function AgentDefaults({ agent, connector }) {
  const { state, refresh, notify } = useApp();
  const effortLabel = useEffortLabel();
  const [execution, setExecution] = useState(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const effectiveId = agent.execution?.providerId || state.settings.defaultProvider;
  const provider = state.providers.find(({ id }) => id === effectiveId);
  const model = agent.execution?.model || provider?.model;
  const effort = agent.execution?.effort || ((!provider?.model || provider.model === model) && provider?.effort);
  const close = () => { if (!saving) setExecution(null); };
  return <>
    <Tag>{[nameOf(provider) || effectiveId, model, effort && effortLabel(effort)].filter(Boolean).join(' · ') || 'Application default'}</Tag>
    <Button icon={Cpu} onClick={() => { setError(''); setExecution(agent.execution || {}); }}>Execution defaults</Button>
    {execution && <Modal title={connector ? 'Connector execution defaults' : 'Agent execution defaults'} icon={Cpu} close={close}><form onSubmit={async (event) => {
      event.preventDefault();
      setSaving(true);
      setError('');
      const { providerId, model, effort } = execution;
      const next = providerId ? { providerId, ...(model ? { model } : {}), ...(effort ? { effort } : {}) } : null;
      try {
        if (connector) await invoke('connector:saveExecution', agent.id, next, agent.catalogId);
        else await invoke('catalog:save', 'agents', { id: agent.id, catalogId: agent.catalogId, execution: next });
        await refresh();
        setExecution(null);
        notify('Execution defaults saved');
      } catch (problem) { setError(problem.message); }
      finally { setSaving(false); }
    }}><div className="modal-body stack">
      <p>These defaults apply to direct runs and workflow nodes that inherit them.</p>
      <ExecutionFields value={execution} onChange={setExecution} requireProvider />
      <ErrorNotice error={error} />
    </div><footer className="modal-footer"><Button disabled={saving} onClick={close}>Cancel</Button><Button variant="primary" type="submit" disabled={saving}>Save defaults</Button></footer></form></Modal>}
  </>;
}

function AgentMetadata({ agent }) {
  const { perform } = useApp();
  const [show, setShow] = useState(false);
  const [category, setCategory] = useState(categoryOf(agent));
  const sealed = agent.categorySealed || categoryOf(agent) === 'Doxie';
  return <><div className="actions start"><Tag>{categoryOf(agent)}</Tag>{agent.isPrivate && <Tag>Private</Tag>}{agent.catalogName && <Tag>{agent.catalogName}</Tag>}<AgentDefaults agent={agent} />{!sealed && <Button variant="ghost" icon={Pencil} onClick={() => setShow(true)}>Change category</Button>}</div>
    {!!agent.requirements?.length && <details className="panel inline-panel"><summary>Requirements</summary><ul>{agent.requirements.map((requirement, index) => <li key={index}>{typeof requirement === 'string' ? requirement : requirement.label || requirement.description || requirement.name || requirement.id || JSON.stringify(requirement)}</li>)}</ul></details>}
    {show && <Modal title="Change category" close={() => setShow(false)}><form onSubmit={async (event) => { event.preventDefault(); const result = await perform('catalog:save', ['agents', { ...agent, category }], 'Category updated'); if (result) setShow(false); }}><div className="modal-body"><Field label="Category"><input required value={category} onChange={(event) => setCategory(event.target.value)} /></Field></div><footer className="modal-footer"><Button onClick={() => setShow(false)}>Cancel</Button><Button type="submit" variant="primary">Save</Button></footer></form></Modal>}
  </>;
}

function useAgentRun(id) {
  const [result, setResult] = useState({ id, loading: true });
  useEffect(() => {
    if (!id) return;
    let open = true;
    let timer;
    let revision = 0;
    const load = async () => {
      timer = undefined;
      const request = ++revision;
      try {
        const value = await invoke('agent:run', id);
        if (open && request === revision) setResult({ id, value });
      } catch (error) {
        if (open && request === revision) setResult({ id, error: error.message });
      }
    };
    const unsubscribe = window.doxie.onEvent((event) => {
      if ((event.run?.id || event.id) !== id || !['agent:output', 'agent:changed'].includes(event.type)) return;
      if (timer === undefined) timer = setTimeout(load, 150);
    });
    load();
    return () => { open = false; unsubscribe?.(); clearTimeout(timer); };
  }, [id]);
  return result.id === id ? result : { loading: !!id };
}

export function AgentPage({ id, catalogId, runId, connector = false }) {
  const effortLabel = useEffortLabel();
  const { state, perform } = useApp();
  const [files, setFiles] = useState(false);
  const section = connector ? 'connectors' : 'agents';
  const back = { to: `/${section}`, label: connector ? 'Connectors' : 'Agents' };
  const agent = state[section].find((item) => item.id === id && (!catalogId || item.catalogId === catalogId));
  const history = state.agentRuns.filter((run) => run.agentId === id && (!catalogId || !run.catalogId || catalogId === run.catalogId)).sort((a, b) => (b.startedAt || '').localeCompare(a.startedAt || ''));
  const selectedRun = runId || history[0]?.id;
  const detail = useAgentRun(selectedRun);
  const run = detail.value || history.find((item) => item.id === selectedRun);
  const buildQuery = new URLSearchParams({ edit: id });
  if (catalogId) buildQuery.set('catalog', catalogId);
  if (!agent) return <div className="page"><PageHeading title="Agent not found" icon={Bot} back={back} />{run && <Panel title={`Run ${run.id}`} icon={Terminal}><LogOutput lines={run.output} /></Panel>}</div>;
  return <div className="page"><PageHeading title={nameOf(agent)} icon={connector ? Plug : iconOptions[agent.icon] || Bot} subtitle={agent.description} back={back} actions={!connector && <><Button to={`/agents/new?${buildQuery}`} icon={Pencil}>Edit with AI</Button><Button icon={Download} onClick={() => perform('catalog:export', ['agents', id, agent.catalogId])}>Export</Button></>} />
    {connector ? <div className="actions start"><Tag>Model-backed connector</Tag><AgentDefaults agent={agent} connector /></div> : <AgentMetadata agent={agent} />}
    <div className="dashboard-grid detail-grid">
      <Panel title="Configure run" icon={Play}><AgentRunForm agent={agent} connector={connector} launch={async (params) => { const result = await perform(connector ? 'connector:start' : 'agent:start', [id, params]); if (result?.id) go(recordRoute(section, agent, '', result.id)); return result; }} /></Panel>
      <Panel title={run ? 'Execution' : 'Latest run'} icon={Terminal} actions={run && <><Status status={run.status} />{active(run.status) && <Button icon={Square} onClick={() => perform('agent:stop', [run.id])}>Stop</Button>}</>}>
        {run ? <><div className="panel-body execution-meta"><DateTime value={run.startedAt} />{run.providerId && <Tag>{nameOf(state.providers.find(({ id }) => id === run.providerId)) || run.providerId}</Tag>}{run.model && <Tag>{run.model}</Tag>}{run.effort && <Tag>{effortLabel(run.effort)}</Tag>}{run.exitCode !== null && run.exitCode !== undefined && <Tag>Exit {run.exitCode}</Tag>}</div>
          <LogOutput lines={run.output || run.logs} /><ErrorNotice error={detail.error} />
          {active(run.status) && <div className="panel-body"><Guidance title={connector ? 'Guide the running connector…' : 'Guide the running agent…'} submit={(text) => perform('agent:guide', [run.id, text], 'Guidance sent')} /></div>}
          {run.usage && <div className="usage-grid"><div><small>Input tokens</small><strong>{Number(run.usage.inputTokens || 0).toLocaleString()}</strong></div><div><small>Output tokens</small><strong>{Number(run.usage.outputTokens || 0).toLocaleString()}</strong></div><div><small>Cost (USD)</small><strong>{run.usage.totalCostUsd ?? '—'}</strong></div></div>}
        </> : detail.loading && selectedRun ? <Loading /> : <Empty title="No run selected" detail={connector ? 'Run this connector to see its output here.' : 'Run this agent to see its output here.'} icon={Terminal} />}
      </Panel>
    </div>
    {run && (run.outputDir || run.artifacts?.length) && <Panel title="Files produced" icon={FileText} actions={<Button onClick={() => setFiles(!files)}>{files ? 'Hide files' : 'Browse files'}</Button>}>{files && <FilesBrowser key={run.id} api="run-files" id={run.id} compact />}</Panel>}
    {agent.memories?.length > 0 && <Panel title="Memories" icon={BookOpen}><div className="panel-body stack">{agent.memories.map((memory) => <details key={memory.id || memory.name}><summary>{nameOf(memory)}</summary><Markdown>{memory.content}</Markdown></details>)}</div></Panel>}
    <Panel title="History" icon={Clock3}>{history.length ? <div className="row-list">{history.map((item) => <ItemRow key={item.id} icon={Activity} to={recordRoute(section, agent, '', item.id)} title={<DateTime value={item.startedAt} />} detail={item.id}><Status status={item.status} /></ItemRow>)}</div> : <Empty compact title="No previous runs" icon={Clock3} />}</Panel>
  </div>;
}

export function BuilderPage({ kind, query }) {
  const { state, refresh, notify, lastEvent } = useApp();
  const effortLabel = useEffortLabel();
  const section = `${kind}s`;
  const builderPath = kind === 'agent' ? '/agents/new' : '/workflows/new/chat';
  const editorId = query.get('edit') || undefined;
  const initialCatalogId = query.get('catalog') || 'default';
  const [catalogId, setCatalogId] = useState(initialCatalogId);
  const [workspaceId, setWorkspaceId] = useState(() => query.get('workspace') || '');
  const [execution, setExecution] = useState(() => ({ providerId: query.get('provider') || '', model: query.get('model') || '', effort: query.get('effort') || '' }));
  const sessionId = query.get('session') || '';
  const [sessionResult, setSessionResult] = useState(null);
  const [executionDraft, setExecutionDraft] = useState(null);
  const [draftExecution, setDraftExecution] = useState(null);
  const [draftText, setDraftText] = useState('');
  const [pending, setPending] = useState('');
  const [error, setError] = useState('');
  const [draftTab, setDraftTab] = useState('preview');
  const [chosenIcon, setChosenIcon] = useState('');
  const [sessionsRevision, setSessionsRevision] = useState(0);
  const [removeRequest, setRemoveRequest] = useState(null);
  const [removeError, setRemoveError] = useState('');
  const [removing, setRemoving] = useState(false);
  const chat = useRef(null);
  const listEvent = (lastEvent?.type === 'builder:changed' && lastEvent.session?.kind === kind) || (lastEvent?.type === 'builder:removed' && lastEvent.kind === kind) ? lastEvent : null;
  const list = useAsync(() => invoke('builder:list', kind), [kind, sessionId, sessionsRevision, listEvent]);
  const catalogOptions = state.catalogs || state.settings.catalogs || [];
  const editing = state[section].find((item) => item.id === editorId && (!initialCatalogId || (item.catalogId || 'default') === initialCatalogId));
  const session = sessionResult?.id === sessionId ? sessionResult.value : null;
  const sessionError = sessionResult?.id === sessionId && sessionResult.error;
  const busy = Boolean(pending) || removing || active(session?.status);
  const providerId = (sessionId ? session?.providerId : execution.providerId) || state.settings.defaultProvider;
  const provider = state.providers.find((item) => item.id === providerId);
  const canAuthenticate = provider && provider.available !== false && provider.requiresAuth !== false;
  const localCodex = providerId === 'codex';
  const signInNeeded = canAuthenticate && (localCodex ? provider.localAuthenticated : provider.authenticated) === false;
  const failureMatchesProvider = !session?.failure?.providerId || session.failure.providerId === providerId;

  function routeTo(id, params = new URLSearchParams(query)) {
    if (id) {
      params.set('session', id);
      params.delete('sessions');
    } else params.delete('session');
    return `${builderPath}${params.size ? `?${params}` : ''}`;
  }

  function showSession(value) {
    setSessionResult(value ? { id: value.id, value } : null);
    setError('');
    setDraftText('');
    setExecutionDraft(null);
    setDraftExecution(null);
    setChosenIcon('');
    const params = new URLSearchParams(query);
    if (value) {
      for (const [key, setting] of Object.entries({ edit: value.editingId, catalog: value.catalogId, workspace: value.workspaceId })) {
        if (setting) params.set(key, setting);
        else params.delete(key);
      }
      setCatalogId(value.catalogId || 'default');
      setWorkspaceId(value.workspaceId || '');
    } else params.set('sessions', '1');
    go(routeTo(value?.id, params));
  }

  async function runAction(label, action, args) {
    setPending(label);
    setError('');
    try {
      return await invoke(action, ...args);
    } catch (problem) {
      setError(problem.message.replace(/^Error invoking remote method '[^']+': (?:Error: )?/, ''));
      return null;
    } finally { setPending(''); }
  }

  async function begin(event) {
    event.preventDefault();
    if (busy) return;
    const result = await runAction('Starting session…', 'builder:start', [kind, { workspaceId: workspaceId || undefined, providerId: execution.providerId || undefined, model: execution.model || undefined, effort: execution.effort || undefined, catalogId, editingId: editorId, initialize: kind === 'agent' }]);
    if (result?.id) showSession(result);
  }

  useEffect(() => {
    if (!sessionId) return;
    let open = true;
    const load = () => invoke('builder:get', sessionId).then((value) => {
      if (open) setSessionResult(value ? { id: sessionId, value } : { id: sessionId, error: 'Builder session not found.' });
    }).catch((problem) => { if (open) setSessionResult({ id: sessionId, error: problem.message }); });
    load();
    const interval = setInterval(load, 1400);
    return () => { open = false; clearInterval(interval); };
  }, [sessionId, lastEvent?.type === 'builder:changed' && lastEvent.session?.id === sessionId ? lastEvent : null]);
  useEffect(() => { if (chat.current) chat.current.scrollTop = chat.current.scrollHeight; }, [session?.messages?.length]);

  async function authenticate() {
    if (!canAuthenticate || busy) return;
    const result = await runAction('Opening authentication…', 'settings:auth', localCodex ? [provider.id, { localCodex: true }] : [provider.id]);
    if (!result?.id) return;
    const params = new URLSearchParams(query);
    if (!sessionId) {
      for (const [key, value] of Object.entries({ provider: provider.id, model: execution.model, effort: execution.effort, workspace: workspaceId, catalog: catalogId })) {
        if (value) params.set(key, value);
        else params.delete(key);
      }
    }
    await refresh();
    go(`/consoles?${new URLSearchParams({ session: result.id, returnTo: routeTo(sessionId, params) })}`);
  }

  async function submitMessage(event) {
    event.preventDefault();
    if (!draftText.trim() || !session || busy) return;
    const text = draftText.trim();
    setDraftText('');
    const result = await runAction('Builder is working…', 'builder:message', [sessionId, text]);
    if (result?.id) setSessionResult({ id: result.id, value: result });
    else setDraftText(text);
  }

  async function retry() {
    const result = await runAction('Retrying last message…', 'builder:retry', [sessionId]);
    if (result?.id) setSessionResult({ id: result.id, value: result });
  }

  async function updateExecution(event) {
    event.preventDefault();
    if (busy) return;
    const result = await runAction('Updating execution…', 'builder:updateExecution', [sessionId, executionDraft]);
    if (result?.id) {
      setSessionResult({ id: result.id, value: result });
      setExecutionDraft(null);
    }
  }

  async function promote() {
    const result = await runAction('Saving to catalog…', 'builder:promote', [sessionId, chosenIcon ? { icon: chosenIcon } : {}]);
    if (!result) return;
    await refresh();
    notify(`${kind === 'agent' ? 'Agent' : 'Workflow'} saved to catalog`);
    go(result.id ? recordRoute(section, result) : `/${section}`);
  }

  async function updateDraftExecution(event) {
    event.preventDefault();
    if (busy || !draftExecution) return;
    const { providerId, model, effort } = draftExecution;
    const next = providerId ? { providerId, ...(model ? { model } : {}), ...(effort ? { effort } : {}) } : null;
    const result = await runAction('Updating agent execution…', 'builder:updateDraftExecution', [sessionId, next]);
    if (result?.id) {
      setSessionResult({ id: result.id, value: result });
      setDraftExecution(null);
    }
  }

  async function removeSessions() {
    if (removing || !removeRequest) return;
    setRemoving(true);
    setRemoveError('');
    const failed = [];
    try {
      for (const item of removeRequest.items) {
        try { await invoke('builder:remove', item.id); }
        catch (problem) { failed.push({ item, message: problem.message.replace(/^Error invoking remote method '[^']+': (?:Error: )?/, '') }); }
      }
      if (failed.length) {
        setRemoveRequest({ ...removeRequest, items: failed.map(({ item }) => item) });
        setRemoveError(failed.map(({ message }) => message).join(' '));
      } else setRemoveRequest(null);
    } finally {
      setSessionsRevision((revision) => revision + 1);
      setRemoving(false);
    }
  }

  const draft = session?.draft;
  const resume = Array.isArray(list.value) ? list.value : list.value?.sessions || [];
  const removableSessions = resume.filter((item) => !active(item.status));
  const loginAction = canAuthenticate && <Button onClick={authenticate} disabled={busy}>Authenticate</Button>;
  const providerHint = provider?.available === false ? <div className="stack"><p className="muted">{nameOf(provider)} CLI is unavailable. Choose an available provider or configure its executable.</p><div className="actions start"><Button to="/settings">Configure providers</Button></div></div>
    : signInNeeded && <div className="stack"><p className="muted">{localCodex ? 'The builder uses the local Codex CLI sign-in. Sign in to Codex to continue.' : `Sign in to ${nameOf(provider)} in DoxieOS to use the builder.`}</p><div className="actions start">{loginAction}</div></div>;
  const progress = pending || (active(session?.status) ? session.initializing ? 'Preparing the first response…' : 'Builder is working…' : '');
  const progressIndicator = progress && <div className="actions start" role="status" aria-live="polite"><LoaderCircle size={16} className="spinner" aria-hidden="true" /><span>{progress}</span></div>;
  return <div className="page"><PageHeading title={editing ? `Edit ${nameOf(editing)} with AI` : `Build a${kind === 'agent' ? 'n agent' : ' workflow'}`} icon={Sparkles} subtitle="Describe what you need, inspect the draft and save it to a catalog when ready." back={{ to: `/${section}`, label: kind === 'agent' ? 'Agents' : 'Workflows' }} actions={kind === 'workflow' && !editing && <Button to="/workflows/new" icon={Pencil}>Create manually</Button>} />
    {!sessionId ? <div className="dashboard-grid"><Panel title="Start a builder session" icon={Sparkles}><form className="panel-body stack" onSubmit={begin}>
      <div className="form-grid"><ExecutionFields value={execution} onChange={setExecution} /></div>
      {providerHint}
      <Field label="Attach workspace"><select value={workspaceId} onChange={(event) => setWorkspaceId(event.target.value)}><option value="">No workspace</option>{state.workspaces.map((workspace) => <option key={workspace.id} value={workspace.id}>{nameOf(workspace)}</option>)}</select></Field>
      <Field label="Destination catalog"><select value={catalogId} onChange={(event) => setCatalogId(event.target.value)}>{!catalogOptions.some((item) => item.id === 'default') && <option value="default">Default catalog</option>}{catalogOptions.map((item) => <option key={item.id} value={item.id}>{nameOf(item)}</option>)}</select></Field>
      <ErrorNotice error={error} />
      {progressIndicator}
      <Button variant="primary" icon={Sparkles} disabled={busy} type="submit">Start session</Button>
    </form></Panel><Panel title="Resume a session" icon={MessageSquare} actions={<Button icon={Trash2} disabled={busy || list.loading || !removableSessions.length} onClick={() => { setRemoveError(''); setRemoveRequest({ all: true, items: removableSessions }); }}>Clear sessions</Button>}>{list.loading ? <Loading /> : list.error ? <div className="panel-body"><ErrorNotice error={list.error} /></div> : resume.length ? <div className="row-list">{resume.map((item) => <div className="builder-session-row" key={item.id}><ItemRow icon={MessageSquare} title={builderSessionName(item, state[section])} detail={<DateTime value={item.updatedAt || item.createdAt} />} onClick={() => { if (!busy) showSession(item); }}><Status status={item.status} /></ItemRow><IconButton icon={Trash2} label="Remove session" title={`Remove ${builderSessionName(item, state[section])}`} disabled={busy || active(item.status)} onClick={() => { setRemoveError(''); setRemoveRequest({ all: false, items: [item] }); }} /></div>)}</div> : <Empty title="No previous sessions" detail="Your builder conversations and drafts appear here after you start a session." icon={MessageSquare} />}</Panel></div> :
      <div className="dashboard-grid detail-grid"><Panel title={session ? builderSessionName(session, state[section]) : 'Builder chat'} icon={MessageSquare} actions={<>{session && <Status status={session.status} />}<Button onClick={() => showSession(null)} disabled={Boolean(pending)}>Sessions</Button></>}>
        {!session ? sessionError ? <div className="panel-body"><ErrorNotice error={sessionError} /></div> : <Loading /> : <>
        <div className="panel-body stack">
          <div className="actions start"><Tag>{[nameOf(provider) || providerId, session.model, session.effort && effortLabel(session.effort)].filter(Boolean).join(' · ')}</Tag><Button onClick={() => setExecutionDraft({ providerId: session.providerId, model: session.model, effort: session.effort })} disabled={busy || !!executionDraft}>Change execution</Button></div>
          {executionDraft && <form className="stack" onSubmit={updateExecution}><div className="form-grid"><ExecutionFields value={executionDraft} onChange={setExecutionDraft} /></div><div className="actions start"><Button type="submit" disabled={busy}>Apply execution</Button><Button onClick={() => setExecutionDraft(null)} disabled={busy}>Cancel</Button></div></form>}
          {(!session.failure || !failureMatchesProvider) && providerHint}
          <ErrorNotice error={error || session.failure?.message} />
          {session.failure && <div className="actions start">{session.failure.kind === 'authentication' && failureMatchesProvider && loginAction}<Button onClick={retry} disabled={busy}>Retry last message</Button></div>}
        </div>
        <div className="chat" ref={chat} aria-busy={busy}>{session?.messages?.length ? session.messages.map((message, index) => <article className={`chat-message ${message.role || 'assistant'}`} key={message.id || index}><small>{message.role === 'user' ? 'You' : 'Builder'}</small><Markdown>{message.text || message.content}</Markdown></article>) : !busy && !session.failure && <Empty title="Start the conversation" detail="Tell the builder what the finished result should do." icon={MessageSquare} />}{progressIndicator && <article className="chat-message assistant"><small>Builder</small>{progressIndicator}</article>}</div>
        {!busy && !session.failure && <p className="panel-body muted" role="status" aria-live="polite">Waiting for your message.</p>}
        <form className="panel-body input-action chat-composer" onSubmit={submitMessage}><textarea aria-label="Message to builder" aria-describedby="builder-message-shortcut" rows={2} placeholder="Describe a goal or ask for a change…" value={draftText} onChange={(event) => setDraftText(event.target.value)} onKeyDown={(event) => { if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) { event.preventDefault(); if (!busy && draftText.trim()) event.currentTarget.form.requestSubmit(); } }} /><VoiceInput append={(text) => setDraftText((old) => `${old}${old ? ' ' : ''}${text}`)} /><Button type="submit" icon={Send} variant="primary" disabled={busy || !draftText.trim()}>Send</Button></form><small className="panel-body muted" id="builder-message-shortcut">Enter to send · Shift+Enter for a new line</small></>}
      </Panel><Panel title={kind === 'agent' ? 'Draft manifest' : 'Draft workflow'} icon={FileText} actions={draft && <>{kind === 'agent' && <Button icon={Cpu} disabled={busy || !!draftExecution} onClick={() => { setError(''); setDraftExecution(draft.execution || {}); }}>Change agent execution</Button>}<Button variant="primary" onClick={promote} disabled={busy || !!draftExecution}>Save to catalog</Button></>}>
        {draft ? <div className="panel-body stack"><div className="segmented"><Button variant={draftTab === 'preview' ? 'secondary' : 'ghost'} onClick={() => setDraftTab('preview')}>Preview</Button><Button variant={draftTab === 'source' ? 'secondary' : 'ghost'} onClick={() => setDraftTab('source')}>Source</Button></div>
          {kind === 'agent' && <section className="stack" aria-label="Agent execution defaults"><strong>Agent execution defaults</strong><div className="actions start"><Tag>{draft.execution?.providerId ? [nameOf(state.providers.find((item) => item.id === draft.execution.providerId)) || draft.execution.providerId, draft.execution.model || 'Provider default model', draft.execution.effort && effortLabel(draft.execution.effort)].filter(Boolean).join(' · ') : 'Application default'}</Tag></div></section>}
          {draftTab === 'source' ? <pre>{typeof draft === 'string' ? draft : JSON.stringify(draft, null, 2)}</pre> : <><h3>{nameOf(draft.manifest || draft.definition || draft) || 'Draft'}</h3><Markdown>{draft.description || draft.manifest?.description || draft.definition?.description || draft.body || (typeof draft === 'string' ? draft : '')}</Markdown>{(draft.nodes || draft.definition?.nodes)?.map((node) => <ItemRow key={node.id} icon={GitBranch} title={node.label || node.id} detail={node.kind} />)}{kind === 'agent' && <div className="stack"><strong>Agent icon</strong><div className="actions start">{Object.entries(iconOptions).map(([key, icon]) => <Button key={key} className="icon-button" variant={(chosenIcon || draft.icon || 'SmartToy') === key ? 'primary' : 'secondary'} icon={icon} title={key} aria-label={key} aria-pressed={(chosenIcon || draft.icon || 'SmartToy') === key} onClick={() => setChosenIcon(key)} />)}</div></div>}</>}
        </div> : <Empty title="Waiting for a draft" detail="The builder’s proposed files and manifest will appear here." icon={FileText} />}
      </Panel></div>}
    {draftExecution && <Modal title="Agent execution defaults" icon={Cpu} close={() => { if (!busy) setDraftExecution(null); }}><form onSubmit={updateDraftExecution}><div className="modal-body stack"><p>These settings are saved in the agent manifest and used for future runs.</p><div className="form-grid"><ExecutionFields value={draftExecution} onChange={setDraftExecution} requireProvider /></div><ErrorNotice error={error} /></div><footer className="modal-footer"><Button disabled={busy} onClick={() => setDraftExecution(null)}>Cancel</Button><Button variant="primary" type="submit" disabled={busy}>Apply agent execution</Button></footer></form></Modal>}
    {removeRequest && <Modal title={removeRequest.all ? 'Clear sessions' : 'Remove session'} icon={Trash2} close={() => { if (!removing) setRemoveRequest(null); }}><div className="modal-body stack"><p>{removeRequest.all ? `Permanently remove ${removeRequest.items.length} inactive builder session${removeRequest.items.length === 1 ? '' : 's'}, including their conversations and drafts?` : `Permanently remove “${builderSessionName(removeRequest.items[0], state[section])}”, including its conversation and draft?`} Saved catalog items are kept.</p><ErrorNotice error={removeError} /></div><footer className="modal-footer"><Button disabled={removing} onClick={() => setRemoveRequest(null)}>Cancel</Button><Button variant="danger" disabled={removing} onClick={removeSessions}>{removing ? 'Removing…' : removeRequest.all ? 'Clear sessions' : 'Remove session'}</Button></footer></Modal>}
  </div>;
}
