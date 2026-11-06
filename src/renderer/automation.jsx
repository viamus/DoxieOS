import { useEffect, useRef, useState } from 'react';
import { Activity, BookOpen, Bot, Clock3, Code2, Download, FileText, GitBranch, Import, MessageSquare, Pencil, Play, Plus, Search, Send, ShieldCheck, Sparkles, Square, Terminal, Trash2 } from 'lucide-react';
import { Button, Confirm, DateTime, Empty, ErrorNotice, Field, FieldInput, IconButton, ItemRow, Loading, Modal, PageHeading, Panel, SearchBox, Status, Tag, VoiceInput, active, go, href, invoke, nameOf, part, selectModes, useApp, useAsync, useText } from './ui.jsx';
import { FilesBrowser, Markdown } from './workbench.jsx';

export const recordKey = (item) => `${item.catalogId || 'default'}/${item.id}`;
export function recordRoute(kind, item, child = '', runId) {
  const query = new URLSearchParams();
  if (item.catalogId && item.catalogId !== 'default') query.set('catalog', item.catalogId);
  if (runId) query.set('run', runId);
  return `/${kind}/${part(item.id)}${child ? `/${child}` : ''}${query.size ? `?${query}` : ''}`;
}

const categoryOf = (item) => item.displayCategory || item.customCategory || item.category || 'Other';
const iconOptions = { SmartToy: Bot, AutoAwesome: Sparkles, Code: Code2, Terminal, AccountTree: GitBranch, MenuBook: BookOpen, Shield: ShieldCheck, Search };
const indexedText = (item) => [item.id, nameOf(item), item.description, categoryOf(item), item.skillName, ...(item.nodes || []).flatMap((node) => [node.label, node.agentId])].filter(Boolean).join(' ').toLowerCase();

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

function AgentRunForm({ agent, launch }) {
  const { state } = useApp();
  const modes = selectModes(agent);
  const [mode, setMode] = useState(Object.keys(modes)[0] || '');
  const [workspaceId, setWorkspaceId] = useState('');
  const [providerId, setProviderId] = useState(state.settings.defaultProvider || state.providers[0]?.id || '');
  const [model, setModel] = useState('');
  const [values, setValues] = useState({});
  const [extra, setExtra] = useState('');
  const [environment, setEnvironment] = useState([]);
  const [error, setError] = useState('');
  const [pending, setPending] = useState(false);
  const fields = modes[mode]?.fields || [];
  const provider = state.providers.find((item) => item.id === providerId);
  const models = state.settings.providers?.[providerId]?.models || provider?.models || [];
  const selectedWorkspaceRequired = !!modes[mode]?.requiresWorkspace;
  return <form className="panel-body stack" onSubmit={async (event) => {
    event.preventDefault();
    setError('');
    const names = environment.map(({ name }) => name.trim());
    if (new Set(names).size !== names.length) { setError('Environment variable names must be unique.'); return; }
    setPending(true);
    try {
      const inputs = Object.fromEntries(fields.map((field) => [field.id, values[field.id] ?? field.defaultValue ?? '']));
      const env = Object.fromEntries(environment.map(({ name, value }) => [name.trim(), value]));
      const result = await launch({ mode: mode || undefined, workspaceId: workspaceId || undefined, providerId: providerId || undefined, model: model || undefined, inputs, arguments: extra || undefined, catalogId: agent.catalogId, env });
      if (result) setEnvironment([]);
    } finally { setPending(false); }
  }}>
    <div className="form-grid">
      {!!Object.keys(modes).length && <Field label="Mode"><select value={mode} onChange={(event) => { setMode(event.target.value); setValues({}); }}>{Object.entries(modes).map(([id, item]) => <option key={id} value={id}>{item.label || item.name || id}</option>)}</select></Field>}
      <Field label={`Workspace${selectedWorkspaceRequired ? ' *' : ''}`}><select required={selectedWorkspaceRequired} value={workspaceId} onChange={(event) => setWorkspaceId(event.target.value)}><option value="">{selectedWorkspaceRequired ? 'Select a workspace' : 'DoxieOS data directory'}</option>{state.workspaces.map((workspace) => <option key={workspace.id} value={workspace.id}>{nameOf(workspace)}</option>)}</select></Field>
      <Field label="Provider"><select value={providerId} onChange={(event) => { setProviderId(event.target.value); setModel(''); }}><option value="">Default provider</option>{state.providers.map((item) => <option key={item.id} value={item.id}>{item.displayName || item.name || item.id}</option>)}</select></Field>
      <Field label="Model"><input list="agent-models" value={model} onChange={(event) => setModel(event.target.value)} placeholder="Provider default" /><datalist id="agent-models">{Array.isArray(models) && models.map((item) => <option key={item.id || item} value={item.id || item} />)}</datalist></Field>
    </div>
    {modes[mode]?.description && <p className="muted">{modes[mode].description}</p>}
    {fields.map((field) => <FieldInput key={field.id} field={field} value={values[field.id]} onChange={(value) => setValues((old) => ({ ...old, [field.id]: value }))} />)}
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
    <div className="actions start"><Button type="submit" variant="primary" icon={Play} disabled={pending}>Run agent</Button></div>
  </form>;
}

function AgentMetadata({ agent }) {
  const { perform } = useApp();
  const [show, setShow] = useState(false);
  const [category, setCategory] = useState(categoryOf(agent));
  const sealed = agent.categorySealed || ['Doxie', 'Connector'].includes(categoryOf(agent));
  return <><div className="actions start"><Tag>{categoryOf(agent)}</Tag>{agent.isPrivate && <Tag>Private</Tag>}{agent.catalogName && <Tag>{agent.catalogName}</Tag>}{!sealed && <Button variant="ghost" icon={Pencil} onClick={() => setShow(true)}>Change category</Button>}</div>
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

export function AgentPage({ id, catalogId, runId }) {
  const { state, perform } = useApp();
  const [files, setFiles] = useState(false);
  const agent = state.agents.find((item) => item.id === id && (!catalogId || item.catalogId === catalogId));
  const history = state.agentRuns.filter((run) => run.agentId === id && (!catalogId || !run.catalogId || catalogId === run.catalogId)).sort((a, b) => (b.startedAt || '').localeCompare(a.startedAt || ''));
  const selectedRun = runId || history[0]?.id;
  const detail = useAgentRun(selectedRun);
  const run = detail.value || history.find((item) => item.id === selectedRun);
  const buildQuery = new URLSearchParams({ edit: id });
  if (catalogId) buildQuery.set('catalog', catalogId);
  if (!agent) return <div className="page"><PageHeading title="Agent not found" icon={Bot} back={{ to: '/agents', label: 'Agents' }} />{run && <Panel title={`Run ${run.id}`} icon={Terminal}><LogOutput lines={run.output} /></Panel>}</div>;
  return <div className="page"><PageHeading title={nameOf(agent)} icon={iconOptions[agent.icon] || Bot} subtitle={agent.description} back={{ to: '/agents', label: 'Agents' }} actions={<><Button to={`/agents/new?${buildQuery}`} icon={Pencil}>Edit with AI</Button><Button icon={Download} onClick={() => perform('catalog:export', ['agents', id, agent.catalogId])}>Export</Button></>} />
    <AgentMetadata agent={agent} />
    <div className="dashboard-grid detail-grid">
      <Panel title="Configure run" icon={Play}><AgentRunForm agent={agent} launch={async (params) => { const result = await perform('agent:start', [id, params]); if (result?.id) go(recordRoute('agents', agent, '', result.id)); return result; }} /></Panel>
      <Panel title={run ? 'Execution' : 'Latest run'} icon={Terminal} actions={run && <><Status status={run.status} />{active(run.status) && <Button icon={Square} onClick={() => perform('agent:stop', [run.id])}>Stop</Button>}</>}>
        {run ? <><div className="panel-body execution-meta"><DateTime value={run.startedAt} />{run.providerId && <Tag>{run.providerId}</Tag>}{run.exitCode !== null && run.exitCode !== undefined && <Tag>Exit {run.exitCode}</Tag>}</div>
          <LogOutput lines={run.output || run.logs} /><ErrorNotice error={detail.error} />
          {active(run.status) && <div className="panel-body"><Guidance title="Guide the running agent…" submit={(text) => perform('agent:guide', [run.id, text], 'Guidance sent')} /></div>}
          {run.usage && <div className="usage-grid"><div><small>Input tokens</small><strong>{Number(run.usage.inputTokens || 0).toLocaleString()}</strong></div><div><small>Output tokens</small><strong>{Number(run.usage.outputTokens || 0).toLocaleString()}</strong></div><div><small>Cost (USD)</small><strong>{run.usage.totalCostUsd ?? '—'}</strong></div></div>}
        </> : detail.loading && selectedRun ? <Loading /> : <Empty title="No run selected" detail="Configure and run this agent to see its output here." icon={Terminal} />}
      </Panel>
    </div>
    {run && (run.outputDir || run.artifacts?.length) && <Panel title="Files produced" icon={FileText} actions={<Button onClick={() => setFiles(!files)}>{files ? 'Hide files' : 'Browse files'}</Button>}>{files && <FilesBrowser key={run.id} api="run-files" id={run.id} compact />}</Panel>}
    {agent.memories?.length > 0 && <Panel title="Memories" icon={BookOpen}><div className="panel-body stack">{agent.memories.map((memory) => <details key={memory.id || memory.name}><summary>{nameOf(memory)}</summary><Markdown>{memory.content}</Markdown></details>)}</div></Panel>}
    <Panel title="History" icon={Clock3}>{history.length ? <div className="row-list">{history.map((item) => <ItemRow key={item.id} icon={Activity} to={recordRoute('agents', agent, '', item.id)} title={<DateTime value={item.startedAt} />} detail={item.id}><Status status={item.status} /></ItemRow>)}</div> : <Empty compact title="No previous runs" icon={Clock3} />}</Panel>
  </div>;
}

export function BuilderPage({ kind, query }) {
  const { state, perform, lastEvent } = useApp();
  const section = `${kind}s`;
  const editorId = query.get('edit') || undefined;
  const initialCatalogId = query.get('catalog') || 'default';
  const [catalogId, setCatalogId] = useState(initialCatalogId);
  const [workspaceId, setWorkspaceId] = useState('');
  const [providerId, setProviderId] = useState(state.settings.defaultProvider || '');
  const [sessionId, setSessionId] = useState(query.get('session') || '');
  const [session, setSession] = useState(null);
  const [draftText, setDraftText] = useState('');
  const [pending, setPending] = useState(false);
  const [draftTab, setDraftTab] = useState('preview');
  const [chosenIcon, setChosenIcon] = useState('');
  const chat = useRef(null);
  const list = useAsync(() => invoke('builder:list', kind), [kind, sessionId]);
  const catalogOptions = state.catalogs || state.settings.catalogs || [];
  const editing = state[section].find((item) => item.id === editorId && (!initialCatalogId || (item.catalogId || 'default') === initialCatalogId));
  const begin = async () => {
    setPending(true);
    try {
      const result = await perform('builder:start', [kind, { workspaceId: workspaceId || undefined, providerId: providerId || undefined, catalogId, editingId: editorId }]);
      if (result?.id) { setSession(result); setSessionId(result.id); }
    } finally { setPending(false); }
  };
  useEffect(() => {
    if (!sessionId) return;
    let open = true;
    const refresh = () => invoke('builder:get', sessionId).then((value) => { if (open && value) setSession(value); }).catch(() => {});
    refresh();
    const interval = setInterval(refresh, 1400);
    return () => { open = false; clearInterval(interval); };
  }, [sessionId, lastEvent?.type === 'builder:changed' ? lastEvent : null]);
  useEffect(() => { if (chat.current) chat.current.scrollTop = chat.current.scrollHeight; }, [session?.messages?.length]);
  const submitMessage = async (event) => {
    event.preventDefault();
    if (!draftText.trim() || !sessionId) return;
    const text = draftText.trim();
    setDraftText('');
    setPending(true);
    const result = await perform('builder:message', [sessionId, text]);
    if (result === false) setDraftText(text);
    else if (result?.id) setSession(result);
    setPending(false);
  };
  const promote = async () => {
    const result = await perform('builder:promote', [sessionId, chosenIcon ? { icon: chosenIcon } : {}], `${kind === 'agent' ? 'Agent' : 'Workflow'} saved to catalog`);
    if (result?.id) go(recordRoute(section, result));
    else if (result) go(`/${section}`);
  };
  const draft = session?.draft;
  const resume = Array.isArray(list.value) ? list.value : list.value?.sessions || [];
  return <div className="page"><PageHeading title={editing ? `Edit ${nameOf(editing)} with AI` : `Build a${kind === 'agent' ? 'n agent' : ' workflow'}`} icon={Sparkles} subtitle="Describe what you need, inspect the draft and save it to a catalog when ready." back={{ to: `/${section}`, label: kind === 'agent' ? 'Agents' : 'Workflows' }} actions={kind === 'workflow' && !editing && <Button to="/workflows/new" icon={Pencil}>Create manually</Button>} />
    {!sessionId ? <div className="dashboard-grid"><Panel title="Start a builder session" icon={Sparkles}><div className="panel-body stack">
      <Field label="Provider"><select value={providerId} onChange={(event) => setProviderId(event.target.value)}><option value="">Default provider</option>{state.providers.map((item) => <option key={item.id} value={item.id}>{item.displayName || item.name || item.id}</option>)}</select></Field>
      <Field label="Attach workspace"><select value={workspaceId} onChange={(event) => setWorkspaceId(event.target.value)}><option value="">No workspace</option>{state.workspaces.map((workspace) => <option key={workspace.id} value={workspace.id}>{nameOf(workspace)}</option>)}</select></Field>
      <Field label="Destination catalog"><select value={catalogId} onChange={(event) => setCatalogId(event.target.value)}>{!catalogOptions.some((item) => item.id === 'default') && <option value="default">Default catalog</option>}{catalogOptions.map((item) => <option key={item.id} value={item.id}>{nameOf(item)}</option>)}</select></Field>
      <Button variant="primary" icon={Sparkles} disabled={pending} onClick={begin}>Start session</Button>
    </div></Panel><Panel title="Resume a session" icon={MessageSquare}>{resume.length ? <div className="row-list">{resume.map((item) => <ItemRow key={item.id} icon={MessageSquare} title={item.label || item.id} detail={<DateTime value={item.updatedAt || item.createdAt} />} onClick={() => { setSession(item); setSessionId(item.id); }}><Status status={item.status} /></ItemRow>)}</div> : <Empty title="No previous sessions" icon={MessageSquare} />}</Panel></div> :
      <div className="dashboard-grid detail-grid"><Panel title={session?.label || 'Builder chat'} icon={MessageSquare} actions={<Button onClick={() => { setSessionId(''); setSession(null); }}>Sessions</Button>}>
        <div className="chat" ref={chat}>{session?.messages?.length ? session.messages.map((message, index) => <article className={`chat-message ${message.role || 'assistant'}`} key={message.id || index}><small>{message.role === 'user' ? 'You' : 'Builder'}</small><Markdown>{message.text || message.content}</Markdown></article>) : <Empty title="Start the conversation" detail="Tell the builder what the finished result should do." icon={MessageSquare} />}</div>
        <form className="panel-body input-action chat-composer" onSubmit={submitMessage}><textarea aria-label="Message to builder" rows={2} placeholder="Describe a goal or ask for a change…" value={draftText} onChange={(event) => setDraftText(event.target.value)} onKeyDown={(event) => { if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) event.currentTarget.form.requestSubmit(); }} /><VoiceInput append={(text) => setDraftText((old) => `${old}${old ? ' ' : ''}${text}`)} /><Button type="submit" icon={Send} variant="primary" disabled={pending || !draftText.trim()}>Send</Button></form>
      </Panel><Panel title={kind === 'agent' ? 'Draft manifest' : 'Draft workflow'} icon={FileText} actions={draft && <Button variant="primary" onClick={promote} disabled={pending}>Save to catalog</Button>}>
        {draft ? <div className="panel-body stack"><div className="segmented"><Button variant={draftTab === 'preview' ? 'secondary' : 'ghost'} onClick={() => setDraftTab('preview')}>Preview</Button><Button variant={draftTab === 'source' ? 'secondary' : 'ghost'} onClick={() => setDraftTab('source')}>Source</Button></div>
          {draftTab === 'source' ? <pre>{typeof draft === 'string' ? draft : JSON.stringify(draft, null, 2)}</pre> : <><h3>{nameOf(draft.manifest || draft.definition || draft) || 'Draft'}</h3><Markdown>{draft.description || draft.manifest?.description || draft.definition?.description || draft.body || (typeof draft === 'string' ? draft : '')}</Markdown>{(draft.nodes || draft.definition?.nodes)?.map((node) => <ItemRow key={node.id} icon={GitBranch} title={node.label || node.id} detail={node.kind} />)}{kind === 'agent' && <div className="stack"><strong>Agent icon</strong><div className="actions start">{Object.entries(iconOptions).map(([key, icon]) => <Button key={key} className="icon-button" variant={(chosenIcon || draft.icon || 'SmartToy') === key ? 'primary' : 'secondary'} icon={icon} title={key} aria-label={key} aria-pressed={(chosenIcon || draft.icon || 'SmartToy') === key} onClick={() => setChosenIcon(key)} />)}</div></div>}</>}
        </div> : <Empty title="Waiting for a draft" detail="The builder’s proposed files and manifest will appear here." icon={FileText} />}
      </Panel></div>}
  </div>;
}
