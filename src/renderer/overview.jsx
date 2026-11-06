import { useEffect, useState } from 'react';
import { Activity, ArrowRight, Bell, BookOpen, Bot, CalendarClock, Check, ChevronRight, CircleDollarSign, Clock3, Code2, Cpu, ExternalLink, FolderKanban, GitBranch, History, Import, Layers3, LayoutDashboard, Plug, Plus, Save, Settings2, Sparkles, Terminal, Trash2 } from 'lucide-react';
import { Button, Confirm, DateTime, Empty, ErrorNotice, Field, IconButton, ItemRow, Modal, ModelField, PageHeading, Panel, SearchBox, Status, Tag, active, go, href, invoke, nameOf, part, slug, split, useApp, useEffortLabel, useText } from './ui.jsx';

const categories = [
  { key: 'workflows', label: 'Workflows', icon: GitBranch, tone: 'copper' },
  { key: 'agents', label: 'Agents', icon: Bot, tone: 'blue' },
  { key: 'libraries', label: 'Libraries', icon: BookOpen, tone: 'violet' },
  { key: 'workspaces', label: 'Workspaces', icon: FolderKanban, tone: 'green' },
];

function orderedRuns(state) {
  const connectorIds = new Set(state.connectors.map(({ id }) => id));
  return [...state.agentRuns.map((run) => ({ ...run, kind: connectorIds.has(run.agentId) ? 'connector' : 'agent' })), ...state.workflowRuns.map((run) => ({ ...run, kind: 'workflow' }))]
    .sort((a, b) => (b.startedAt || '').localeCompare(a.startedAt || ''));
}

function runTitle(run, state) {
  const id = run.kind === 'workflow' ? run.workflowId : run.agentId;
  return nameOf(state[`${run.kind}s`].find((item) => item.id === id)) || id || run.id;
}

function RunList({ runs, compact = false }) {
  const { state } = useApp();
  return <div className="row-list">{runs.map((run) => {
    const id = run.kind === 'workflow' ? run.workflowId : run.agentId;
    const route = `/${run.kind}s/${part(id)}?run=${part(run.id)}${run.catalogId ? `&catalog=${part(run.catalogId)}` : ''}`;
    const presentation = { agent: { label: 'Agent', icon: Bot }, connector: { label: 'Connector', icon: Plug }, workflow: { label: 'Workflow', icon: GitBranch } }[run.kind];
    return <ItemRow key={`${run.kind}/${run.id}`} to={route} icon={presentation.icon} title={runTitle(run, state)} detail={!compact && <>{presentation.label} <span className="middot">·</span> <DateTime value={run.startedAt} /></>}><Status status={run.status} /></ItemRow>;
  })}</div>;
}

export function Dashboard() {
  const { state } = useApp();
  const t = useText();
  const [period, setPeriod] = useState('7');
  const runs = orderedRuns(state);
  const running = runs.filter((run) => active(run.status));
  const consoles = state.consoles.filter((session) => session.status === 'Running');
  const scheduled = state.workflows.filter((workflow) => workflow.enabled !== false && ['cron', '1'].includes(String(workflow.trigger?.kind).toLowerCase()));
  const tracked = state.agentRuns.filter((run) => period === 'all' || Date.parse(run.startedAt) >= Date.now() - Number(period) * 86400000);
  const usageByProvider = Object.groupBy(tracked, (run) => run.providerId || 'Unknown');
  const summarize = (rows) => rows.reduce((total, run) => {
    const usage = run.usage || {};
    total.cost += Number(usage.totalCostUsd || 0);
    total.tokens += Number(usage.totalTokens || Number(usage.inputTokens || 0) + Number(usage.outputTokens || 0));
    total.reported ||= typeof usage.totalCostUsd === 'number';
    return total;
  }, { cost: 0, tokens: 0, reported: false });
  const usage = summarize(tracked);
  const money = (number) => new Intl.NumberFormat(undefined, { style: 'currency', currency: 'USD', maximumFractionDigits: 4 }).format(number);
  const figures = {
    workflows: `${state.workflows.filter(({ enabled }) => enabled !== false).length} ${t('Home.Enabled', 'enabled')}`,
    agents: `${state.agentRuns.length} ${t('Home.Runs', 'runs')}`,
    libraries: `${state.libraries.reduce((count, library) => count + (library.memories?.length || 0), 0)} ${t('Home.MemoryEntriesTotal', 'memory entries')}`,
    workspaces: `${consoles.length} ${t('Home.LiveConsolesCount', 'live consoles')}`,
  };

  return <div className="page dashboard">
    <PageHeading title={t('Home.Title', 'Dashboard')} subtitle={t('Home.Subtitle', 'See what is running, queued and recently completed.')} icon={LayoutDashboard} eyebrow={t('Chrome.Overview', 'WORKBENCH OVERVIEW')} actions={<Status status={running.length || consoles.length ? 'Active' : 'Ready'} />} />
    <section className="hero">
      <div className="hero-orbit" aria-hidden="true"><div /><div /><div /><span><GitBranch size={42} strokeWidth={1.2} /></span></div>
      <div className="hero-copy"><span className="eyebrow"><Sparkles size={14} /> {t('Home.QuickStart', 'QUICK START')}</span>
        <h2>{t('Home.Welcome', 'Welcome to DoxieOS')}</h2><p>{t('Home.WelcomeBody', 'Build an agent, connect a workflow, or start in a workspace.')}</p>
        <div className="actions hero-actions"><Button to="/agents/new" icon={Sparkles} variant="primary">{t('Home.BuildWithAi', 'Build with AI')}</Button><Button to="/workflows/new" icon={GitBranch}>{t('Home.ComposeWorkflow', 'Compose workflow')}</Button><Button to="/workspaces?new=1" variant="ghost" icon={Plus}>{t('Home.CreateWorkspace', 'Create workspace')}</Button></div>
      </div>
    </section>
    <div className="metrics">{categories.map(({ key, label, icon: Icon, tone }) => <a className={`metric ${tone}`} key={key} href={href(`/${key}`)}>
      <div className="metric-top"><span className="metric-icon"><Icon size={19} strokeWidth={1.65} /></span><ChevronRight size={17} className="row-caret" /></div>
      <div className="metric-count">{state[key].length}<span>{t(`Home.${label}`, label)}</span></div><p>{figures[key]}</p>
    </a>)}</div>
    <div className="dashboard-grid">
      <Panel title={t('Home.LiveNow', 'Live now')} icon={Activity} actions={<Tag>{running.length + consoles.length} active</Tag>}>
        {!running.length && !consoles.length ? <Empty compact icon={Activity} title={t('Home.Idle', 'Nothing running')} detail={t('Home.IdleBody', 'Agent runs, workflows and consoles appear here as they start.')} /> : <>
          <RunList runs={running.slice(0, 6)} compact />
          <div className="row-list">{consoles.slice(0, 6).map((session) => <ItemRow key={session.id} icon={Terminal} title={session.label || session.name || session.id} to={`/consoles?session=${part(session.id)}`}><Status status={session.status} /></ItemRow>)}</div>
        </>}
      </Panel>
      <Panel title={t('Home.NextCronFirings', 'Next scheduled runs')} icon={CalendarClock}>
        {!scheduled.length ? <Empty compact icon={Clock3} title={t('Home.NoSchedules', 'No scheduled workflows')} detail={t('Home.NoSchedulesBody', 'Enable a cron trigger to run a workflow on a schedule.')} /> : <div className="row-list">{scheduled.map((workflow) => <ItemRow key={`${workflow.catalogId}/${workflow.id}`} icon={Clock3} title={nameOf(workflow)} detail={workflow.nextRunAt ? <DateTime value={workflow.nextRunAt} /> : workflow.trigger.cronExpression} to={`/workflows/${part(workflow.id)}?catalog=${part(workflow.catalogId || 'default')}`} />)}</div>}
      </Panel>
      <Panel title={t('Home.RecentRuns', 'Recent runs')} icon={History} actions={<Button variant="ghost" to="/history">{t('Home.FullHistory', 'Full history')} <ArrowRight size={14} /></Button>}>
        {runs.length ? <RunList runs={runs.slice(0, 5)} /> : <Empty compact icon={History} title={t('Home.NoRecent', 'No recent runs')} detail={t('Home.NoRecentBody', 'Start an agent or workflow to see its execution here.')} />}
      </Panel>
      <Panel title={t('Home.Spend', 'Usage and spend')} icon={CircleDollarSign} actions={<select aria-label="Usage date range" value={period} onChange={(event) => setPeriod(event.target.value)}><option value="7">Last 7 days</option><option value="30">Last 30 days</option><option value="all">All time</option></select>}>
        {tracked.length ? <><div className="usage-grid"><div><small>{t('Home.TotalCost', 'Total cost')}</small><strong>{usage.reported ? money(usage.cost) : '—'}</strong></div><div><small>{t('Home.TotalTokens', 'Total tokens')}</small><strong>{usage.tokens.toLocaleString()}</strong></div><div><small>{t('Home.RunsCounted', 'Runs')}</small><strong>{tracked.length}</strong></div></div>
          <div className="row-list">{Object.entries(usageByProvider).map(([id, rows]) => { const provider = state.providers.find((item) => item.id === id); const summary = summarize(rows); return <ItemRow key={id} icon={Cpu} title={provider?.displayName || provider?.name || id} detail={`${rows.length} runs · ${summary.tokens.toLocaleString()} tokens`}>{summary.reported ? money(summary.cost) : '—'}</ItemRow>; })}</div></> : <Empty compact icon={CircleDollarSign} title={t('Home.NoUsage', 'No usage in this period')} detail={t('Home.NoUsageBody', 'Provider usage and reported costs appear after a run.')} />}
      </Panel>
    </div>
  </div>;
}

export function HistoryPage() {
  const { state, perform } = useApp();
  const t = useText();
  const [search, setSearch] = useState('');
  const [kind, setKind] = useState('all');
  const [confirm, setConfirm] = useState(false);
  const runs = orderedRuns(state).filter((run) => (kind === 'all' || run.kind === kind) && `${runTitle(run, state)} ${run.id} ${run.status}`.toLowerCase().includes(search.toLowerCase()));
  return <div className="page"><PageHeading icon={History} title={t('History.Title', 'History')} subtitle={t('History.Subtitle', 'Review agent and workflow runs, most recent first.')} actions={<Button icon={Trash2} disabled={!runs.length} onClick={() => setConfirm(true)}>{t('History.Clear', 'Clear history')}</Button>} />
    <div className="toolbar"><SearchBox value={search} setValue={setSearch} placeholder="Search runs…" /><select aria-label="Run type" value={kind} onChange={(event) => setKind(event.target.value)}><option value="all">All runs</option><option value="agent">Agents</option><option value="connector">Connectors</option><option value="workflow">Workflows</option></select></div>
    <Panel title={`${runs.length} ${runs.length === 1 ? 'run' : 'runs'}`} icon={History}>{runs.length ? <RunList runs={runs} /> : <Empty title="No matching runs" detail={t('History.Empty', 'Your executions appear here once an agent or workflow starts.')} />}</Panel>
    {confirm && <Confirm title="Clear run history" detail="Remove completed runs from history?" label="Clear history" close={() => setConfirm(false)} confirm={async () => { await perform('history:clear', [], 'History cleared'); setConfirm(false); }} />}
  </div>;
}

export function NotificationsPage() {
  const { state, perform } = useApp();
  const [unreadOnly, setUnreadOnly] = useState(false);
  const isUnread = (item) => !item.read && !item.readAt && !item.isRead;
  const visible = unreadOnly ? state.notifications.filter(isUnread) : state.notifications;
  const open = (link) => link?.startsWith('/') && !link.startsWith('//') ? go(link) : perform('shell:openExternal', [link]);
  return <div className="page"><PageHeading icon={Bell} title="Notifications" subtitle="Run outcomes and updates from agents and workflows." actions={<Button icon={Check} disabled={!state.notifications.some(isUnread)} onClick={() => perform('notifications:read')}>Mark all read</Button>} />
    <div className="toolbar"><label className="check"><input type="checkbox" checked={unreadOnly} onChange={(event) => setUnreadOnly(event.target.checked)} /> Unread only</label></div>
    <div className="stack">{visible.length ? visible.map((item, index) => <Panel key={item.id || `${item.timestamp}-${index}`} title={item.title} icon={Bell} hint={<DateTime value={item.timestamp || item.createdAt} />} actions={<>{isUnread(item) && <Tag>Unread</Tag>}<Status status={item.severity || 'Info'} /></>}>
      <div className="panel-body"><p>{item.body}</p>{item.content && <details><summary>Details</summary><pre>{item.content}</pre></details>}<div className="actions">{item.href && <Button icon={ExternalLink} onClick={() => open(item.href)}>View source</Button>}{item.actions?.map((action, actionIndex) => <Button key={`${action.href}/${actionIndex}`} icon={ExternalLink} onClick={() => open(action.href)}>{action.label}</Button>)}{item.sourcePath && <Button icon={FolderKanban} onClick={() => perform('notifications:openSource', [item.id])}>Open source folder</Button>}{isUnread(item) && <Button icon={Check} onClick={() => perform('notifications:read', [item.id])}>Mark read</Button>}</div></div>
    </Panel>) : <Panel><Empty title="You're all caught up" icon={Bell} detail="Notifications from the workbench appear here." /></Panel>}</div>
  </div>;
}

function JsonInput({ value, onChange, rows = 5, placeholder }) {
  return <textarea className="monospace" value={value} onChange={(event) => onChange(event.target.value)} rows={rows} spellCheck={false} placeholder={placeholder} />;
}

function McpEditor({ server, close, save }) {
  const [draft, setDraft] = useState({ id: '', name: '', transport: 'stdio', command: '', arguments: [], url: '', providers: ['claude-code', 'codex'], enabled: true, ...server });
  const [error, setError] = useState('');
  const [environment, setEnvironment] = useState('');
  const [replaceEnvironment, setReplaceEnvironment] = useState(!server?.environment);
  const [headers, setHeaders] = useState(() => {
    try { return JSON.stringify(draft.headers || JSON.parse(draft.extraJson || '{}').headers || {}, null, 2); } catch { return '{}'; }
  });
  const set = (key, value) => setDraft((old) => ({ ...old, [key]: value }));
  async function submit(event) {
    event.preventDefault();
    try {
      const metadata = JSON.parse(draft.extraJson || '{}');
      const parsedHeaders = JSON.parse(headers || '{}');
      if (!parsedHeaders || typeof parsedHeaders !== 'object' || Array.isArray(parsedHeaders)) throw new Error('Headers must be a JSON object.');
      const next = { ...draft, id: slug(draft.id || draft.name), headers: parsedHeaders, extraJson: JSON.stringify({ ...metadata, headers: parsedHeaders }) };
      if (replaceEnvironment) next.environment = environment;
      if (!next.id) throw new Error('Enter a valid server ID.');
      await save(next);
    } catch (problem) { setError(problem.message); }
  }
  return <Modal title={server ? 'Edit MCP server' : 'Add MCP server'} icon={Layers3} close={close}>
    <form onSubmit={submit}><div className="modal-body form-grid">
      <Field label="Server ID"><input value={draft.id} onChange={(event) => set('id', event.target.value)} placeholder="my-mcp-server" /></Field>
      <Field label="Display name"><input required value={draft.name} onChange={(event) => set('name', event.target.value)} /></Field>
      <Field label="Transport"><select value={draft.transport} onChange={(event) => set('transport', event.target.value)}><option value="stdio">stdio</option><option value="http">http</option></select></Field>
      {draft.transport === 'http' ? <Field label="URL"><input required type="url" value={draft.url || ''} onChange={(event) => set('url', event.target.value)} /></Field> : <>
        <Field label="Command"><input required value={draft.command || ''} onChange={(event) => set('command', event.target.value)} /></Field>
        <Field label="Arguments"><input value={Array.isArray(draft.arguments) ? draft.arguments.join(' ') : draft.arguments || ''} onChange={(event) => set('arguments', event.target.value)} /></Field>
        {replaceEnvironment ? <Field label="Environment / secrets"><input type="password" autoComplete="off" value={environment} onChange={(event) => setEnvironment(event.target.value)} placeholder="TOKEN=…" /></Field> : <Button onClick={() => setReplaceEnvironment(true)}>Replace environment secrets</Button>}
      </>}
      <Field label="HTTP headers JSON"><JsonInput value={headers} onChange={setHeaders} rows={3} /></Field>
      <div className="actions"><label className="check"><input type="checkbox" checked={draft.enabled} onChange={(event) => set('enabled', event.target.checked)} /> Enabled</label>{['claude-code', 'codex'].map((id) => <label className="check" key={id}><input type="checkbox" checked={draft.providers?.includes(id) || false} onChange={(event) => set('providers', event.target.checked ? [...draft.providers, id] : draft.providers.filter((value) => value !== id))} /> {id === 'codex' ? 'Codex' : 'Claude'}</label>)}</div>
      <ErrorNotice error={error} />
    </div><footer className="modal-footer"><Button onClick={close}>Cancel</Button><Button type="submit" variant="primary" icon={Save}>Save server</Button></footer></form>
  </Modal>;
}

function ProviderEditor({ provider, config, close, save }) {
  const [draft, setDraft] = useState({ ...config, executable: config?.executable || provider.executable || '', models: (Array.isArray(config?.models) ? config.models : []).join(', ') });
  const [environment, setEnvironment] = useState('');
  const [replaceEnvironment, setReplaceEnvironment] = useState(false);
  const [error, setError] = useState('');
  return <Modal title={`Configure ${provider.displayName || provider.name || provider.id}`} icon={Cpu} close={close}><form onSubmit={async (event) => {
    event.preventDefault();
    try {
      const next = { ...draft, models: split(draft.models) };
      if (replaceEnvironment) {
        const parsed = JSON.parse(environment || '{}');
        if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed) || Object.values(parsed).some((value) => typeof value !== 'string')) throw new Error('Environment must be a JSON object with string values.');
        next.environment = parsed;
      }
      await save(next);
    } catch (problem) { setError(problem.message); }
  }}><div className="modal-body stack">
    <Field label="CLI executable"><input value={draft.executable} onChange={(event) => setDraft({ ...draft, executable: event.target.value })} /></Field>
    <ModelField label="Default model" providerId={provider.id} value={draft.model} suggestions={split(draft.models)} hint="Used when a run inherits this provider’s model." placeholder="CLI default"
      onChange={(model, effort) => setDraft({ ...draft, model, effort })}
      effort={{ levels: provider.reasoningEfforts, value: draft.effort, onChange: (effort) => setDraft({ ...draft, effort }) }} />
    <Field label="Model names" hint="Separate models with commas."><input value={draft.models} onChange={(event) => setDraft({ ...draft, models: event.target.value })} /></Field>
    {replaceEnvironment ? <Field label="Environment variables" hint="Enter a JSON object with string values."><JsonInput value={environment} onChange={setEnvironment} /></Field> : <Button onClick={() => setReplaceEnvironment(true)}>Set environment variables</Button>}
    <ErrorNotice error={error} />
  </div><footer className="modal-footer"><Button onClick={close}>Cancel</Button><Button type="submit" variant="primary" icon={Save}>Save provider</Button></footer></form></Modal>;
}

const cleanupCopy = {
  en: {
    action: 'Clean temporary files', busy: 'Cleaning…', cancel: 'Cancel',
    detail: 'Permanently remove eligible finished run output and temporary sandboxes. Workspaces, catalogs, the database, resumable builder drafts and active jobs are preserved.',
    empty: 'No eligible temporary folders were found.', one: 'Removed 1 temporary folder.', many: (count) => `Removed ${count} temporary folders.`,
  },
  'pt-BR': {
    action: 'Limpar arquivos temporários', busy: 'Limpando…', cancel: 'Cancelar',
    detail: 'Remove permanentemente os arquivos elegíveis de execuções finalizadas e os sandboxes temporários. Áreas de trabalho, catálogos, banco de dados, rascunhos retomáveis dos builders e execuções ativas são preservados.',
    empty: 'Nenhuma pasta temporária elegível foi encontrada.', one: '1 pasta temporária removida.', many: (count) => `${count} pastas temporárias removidas.`,
  },
  es: {
    action: 'Limpiar archivos temporales', busy: 'Limpiando…', cancel: 'Cancelar',
    detail: 'Elimina de forma permanente los archivos elegibles de ejecuciones finalizadas y los entornos temporales. Se conservan los espacios de trabajo, los catálogos, la base de datos, los borradores reanudables y las ejecuciones activas.',
    empty: 'No se encontraron carpetas temporales elegibles.', one: 'Se eliminó 1 carpeta temporal.', many: (count) => `Se eliminaron ${count} carpetas temporales.`,
  },
};

function StoragePanel() {
  const { state, perform } = useApp();
  const t = useText();
  const [showCleanup, setShowCleanup] = useState(false);
  const [cleaning, setCleaning] = useState(false);
  const [cleanupError, setCleanupError] = useState('');
  const [removed, setRemoved] = useState(null);
  const root = typeof state.root === 'string' ? state.root : state.root?.path;
  const copy = cleanupCopy[state.settings.language] || cleanupCopy.en;
  const close = () => { if (!cleaning) setShowCleanup(false); };
  const clean = async () => {
    setCleaning(true);
    setCleanupError('');
    try {
      const result = await invoke('storage:cleanup');
      setRemoved(result.removed);
      setShowCleanup(false);
    } catch (error) {
      setCleanupError(error.message);
    } finally {
      setCleaning(false);
    }
  };
  return <Panel title={t('Settings.StoragePaths', 'Storage paths')} icon={FolderKanban} actions={<>
    <Button icon={Code2} onClick={() => perform('catalog:regenerate', [], 'Provider files regenerated')}>Regenerate provider files</Button>
    <Button icon={Trash2} onClick={() => { setCleanupError(''); setShowCleanup(true); }}>{copy.action}</Button>
  </>}>
    <div className="row-list">{Object.entries(state.storage || (root ? { 'Data directory': root } : {})).map(([label, value]) => <ItemRow key={label} icon={FolderKanban} title={label} detail={<span title={String(value)}>{String(value)}</span>} />)}</div>
    {removed !== null && <p className="panel-body muted" role="status">{removed === 0 ? copy.empty : removed === 1 ? copy.one : copy.many(removed)}</p>}
    {showCleanup && <Modal title={copy.action} icon={Trash2} close={close}>
      <div className="modal-body stack"><p>{copy.detail}</p><ErrorNotice error={cleanupError} /></div>
      <footer className="modal-footer"><Button disabled={cleaning} onClick={close}>{copy.cancel}</Button><Button variant="danger" icon={Trash2} disabled={cleaning} onClick={clean}>{cleaning ? copy.busy : copy.action}</Button></footer>
    </Modal>}
  </Panel>;
}

export function SettingsPage() {
  const effortLabel = useEffortLabel();
  const { state, perform } = useApp();
  const t = useText();
  const [editMcp, setEditMcp] = useState(null);
  const [editProvider, setEditProvider] = useState(null);
  const [removeMcp, setRemoveMcp] = useState(null);
  const [removeCatalog, setRemoveCatalog] = useState(null);
  const settings = state.settings;
  const catalogs = settings.catalogs || state.catalogs || [];
  const servers = settings.mcpServers || [];
  const save = (patch, message = 'Settings saved') => perform('settings:save', [{ ...settings, ...patch }], message);
  const addCatalog = async () => {
    const selected = await perform('workspace:pickDirectory');
    const directory = typeof selected === 'string' ? selected : selected?.path;
    if (!directory) return;
    const name = directory.split(/[\\/]/).filter(Boolean).at(-1);
    await save({ catalogs: [...catalogs, { id: slug(name), name, path: directory }] }, 'Catalog added');
  };
  return <div className="page"><PageHeading icon={Settings2} title={t('Settings.Title', 'Settings')} subtitle={t('Settings.ShortSubtitle', 'Manage local providers, MCP servers, catalogs and preferences.')} />
    <Panel title={t('Settings.LanguageTitle', 'Language')} icon={Settings2}><div className="panel-body"><Field label={t('Settings.LanguageLabel', 'Application language')}><select value={settings.language || 'en'} onChange={(event) => save({ language: event.target.value })}><option value="en">English</option><option value="pt-BR">Português (Brasil)</option><option value="es">Español</option></select></Field></div></Panel>
    <Panel title={t('Settings.AgentProviders', 'Agent CLI providers')} icon={Cpu} hint="Agents, workflow nodes and individual runs can override these defaults.">
      <div className="row-list">{state.providers.map((provider) => <ItemRow key={provider.id} icon={Terminal} title={provider.displayName || provider.name || provider.id} detail={[provider.executable || provider.id, provider.model, provider.effort && effortLabel(provider.effort)].filter(Boolean).join(' · ')}>
        {settings.defaultProvider === provider.id ? <Tag>Default</Tag> : <Button variant="ghost" onClick={() => save({ defaultProvider: provider.id })}>Set as default</Button>}
        <Status status={provider.status || (provider.available === false ? 'Not found' : 'Available')} />
        {provider.requiresAuth !== false && <Button onClick={async () => { const session = await perform('settings:auth', [provider.id]); if (session) go(`/consoles${session.id ? `?session=${part(session.id)}` : ''}`); }}>Authenticate</Button>}
        <IconButton icon={Settings2} label="Configure provider" onClick={() => setEditProvider(provider)} />
      </ItemRow>)}</div>
    </Panel>
    <Panel title={t('Settings.McpServers', 'MCP servers')} icon={Layers3} hint="Configure tools available to your provider CLIs." actions={<><Button icon={Import} onClick={() => perform('settings:importMcp', [], 'Provider MCP configuration imported')}>{t('Settings.ImportFromProviders', 'Import from providers')}</Button><Button variant="primary" icon={Plus} onClick={() => setEditMcp({})}>{t('Settings.AddMcp', 'Add MCP')}</Button></>}>
      {servers.length ? <div className="row-list">{servers.map((server) => <ItemRow key={server.id} icon={Layers3} title={server.name || server.id} detail={[server.transport, ...(server.providers || [])].filter(Boolean).join(' · ')}><Status status={server.enabled ? 'Enabled' : 'Disabled'} /><IconButton icon={Settings2} label="Edit MCP server" onClick={() => setEditMcp(server)} /><IconButton icon={Trash2} label="Remove MCP server" onClick={() => setRemoveMcp(server)} /></ItemRow>)}</div> : <Empty compact title="No MCP servers configured" icon={Layers3} />}
    </Panel>
    <Panel title={t('Settings.DoxieCatalogs', 'Doxie catalogs')} icon={BookOpen} hint="Add a folder to copy its catalog into DoxieOS app data." actions={<Button icon={Plus} onClick={addCatalog}>{t('Settings.AddCatalog', 'Add catalog')}</Button>}>
      <div className="row-list">{catalogs.map((catalog) => <ItemRow key={catalog.id} icon={BookOpen} title={nameOf(catalog)} detail={catalog.path || catalog.root}>{catalog.isDefault || catalog.id === 'default' ? <Tag>Default</Tag> : <IconButton icon={Trash2} label="Remove catalog" onClick={() => setRemoveCatalog(catalog)} />}</ItemRow>)}</div>
    </Panel>
    <StoragePanel />
    {editMcp && <McpEditor server={editMcp.id ? editMcp : null} close={() => setEditMcp(null)} save={async (server) => { const result = await save({ mcpServers: [...servers.filter((item) => item.id !== editMcp.id && item.id !== server.id), server] }); if (result !== false) setEditMcp(null); }} />}
    {editProvider && <ProviderEditor provider={editProvider} config={settings.providers?.[editProvider.id]} close={() => setEditProvider(null)} save={async (provider) => { const result = await save({ providers: { ...settings.providers, [editProvider.id]: provider } }); if (result !== false) setEditProvider(null); }} />}
    {removeMcp && <Confirm title="Remove MCP server" detail={`Remove ${nameOf(removeMcp)} from provider configuration?`} close={() => setRemoveMcp(null)} confirm={async () => { await save({ mcpServers: servers.filter((item) => item.id !== removeMcp.id) }); setRemoveMcp(null); }} />}
    {removeCatalog && <Confirm title="Remove catalog" detail={`Disconnect ${nameOf(removeCatalog)} from this workbench?`} close={() => setRemoveCatalog(null)} confirm={async () => { await save({ catalogs: catalogs.filter((item) => item.id !== removeCatalog.id) }); setRemoveCatalog(null); }} />}
  </div>;
}
