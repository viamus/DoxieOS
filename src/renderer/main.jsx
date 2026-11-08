import { StrictMode, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { Activity, Bell, BookOpen, Bot, CircleDot, Cpu, FolderKanban, GitBranch, History, LayoutDashboard, Menu, Plug, Settings2, Terminal, X } from 'lucide-react';
import { AppContext, Button, Empty, ErrorNotice, IconButton, Loading, Modal, Status, href, invoke, useText } from './ui.jsx';
import { Dashboard, HistoryPage, NotificationsPage, SettingsPage } from './overview.jsx';
import { AgentsPage, AgentPage, BuilderPage, WorkflowsPage } from './automation.jsx';
import { AgentEditor } from './agent-editor.jsx';
import { ConnectorPage, ConnectorsPage } from './connectors.jsx';
import { WorkflowEditor, WorkflowPage } from './workflow.jsx';
import { ConsolesPage, LibrariesPage, Markdown, WorkspaceFilesPage, WorkspacesPage } from './workbench.jsx';
import logo from './doxie-face.svg';
import './styles.css';

const navigation = [
  { to: '/', key: 'Nav.Dashboard', label: 'Dashboard', icon: LayoutDashboard },
  { to: '/workspaces', key: 'Nav.Workspaces', label: 'Workspaces', icon: FolderKanban },
  { to: '/consoles', key: 'Nav.Consoles', label: 'Consoles', icon: Terminal },
  { to: '/agents', key: 'Nav.Agents', label: 'Agents', icon: Bot },
  { to: '/connectors', key: 'Nav.Connectors', label: 'Connectors', icon: Plug },
  { to: '/workflows', key: 'Nav.Workflows', label: 'Workflows', icon: GitBranch },
  { to: '/libraries', key: 'Nav.Libraries', label: 'Libraries', icon: BookOpen },
  { to: '/history', key: 'Nav.History', label: 'History', icon: History },
];

const emptyState = { agents: [], connectors: [], workflows: [], workspaces: [], libraries: [], agentRuns: [], workflowRuns: [], notifications: [], consoles: [], settings: {}, providers: [] };

function currentRoute() {
  const [pathname, queryString = ''] = (window.location.hash.slice(1) || '/').split('?');
  return { pathname, query: new URLSearchParams(queryString) };
}

function decode(value) {
  try { return decodeURIComponent(value); } catch { return value; }
}

function Router({ route }) {
  const { pathname, query } = route;
  const [, section, encodedId, child, ...rest] = pathname.split('/');
  const id = encodedId ? decode(encodedId) : undefined;
  const catalogId = query.get('catalog') || undefined;
  const runId = query.get('run') || undefined;
  if (!section) return <Dashboard />;
  if (section === 'agents') {
    if (id === 'new') return <BuilderPage kind="agent" query={query} />;
    if (id === 'prompt') return <ConnectorPage id={id} runId={runId} />;
    if (id && child === 'edit') return <AgentEditor key={`${catalogId || 'default'}/${id}`} id={id} catalogId={catalogId} />;
    return id ? <AgentPage id={id} catalogId={catalogId} runId={runId} /> : <AgentsPage />;
  }
  if (section === 'connectors') return id ? <ConnectorPage id={id} runId={runId} /> : <ConnectorsPage />;
  if (section === 'workflows') {
    if (id === 'new') return child === 'chat' ? <BuilderPage kind="workflow" query={query} /> : <WorkflowEditor />;
    if (child === 'edit') return <WorkflowEditor id={id} catalogId={catalogId} />;
    return id ? <WorkflowPage id={id} catalogId={catalogId} runId={runId} /> : <WorkflowsPage />;
  }
  if (section === 'workspaces') return id && child === 'files' ? <WorkspaceFilesPage id={id} path={rest.map(decode).join('/')} /> : <WorkspacesPage />;
  if (section === 'consoles') {
    const returnTo = query.get('returnTo');
    const builderReturn = /^\/(?:agents\/new|workflows\/new\/chat)(?:\?|$)/.test(returnTo || '') ? returnTo : undefined;
    return <ConsolesPage sessionId={query.get('session') || id} builderReturn={builderReturn} />;
  }
  if (section === 'libraries') return <LibrariesPage />;
  if (section === 'history') return <HistoryPage />;
  if (section === 'notifications') return <NotificationsPage />;
  if (section === 'settings') return <SettingsPage />;
  return <Empty title="Page not found" action={<Button to="/">Go to dashboard</Button>} />;
}

function Chrome({ state, pathname, collapsed, onCollapse, onReleaseNotes, newRelease, children }) {
  const t = useText();
  const activeItem = navigation.find(({ to }) => to === '/' ? pathname === '/' : pathname.startsWith(to));
  const provider = state.providers.find(({ id }) => id === state.settings.defaultProvider);
  const live = state.consoles.filter(({ status }) => status === 'Running').length;
  const unread = state.notifications.filter((item) => !item.read && !item.readAt && !item.isRead).length;
  const selected = activeItem ? t(activeItem.key, activeItem.label) : pathname.startsWith('/settings') ? t('Nav.Settings', 'Settings') : 'Notifications';
  return <div className={`desktop ${collapsed ? 'rail-collapsed' : ''}`}>
    <aside className="sidebar">
      <a className="brand" href={href('/')} title="DoxieOS dashboard">
        <span className="brand-icon"><img src={logo} alt="" /></span>
        <span className="brand-copy"><strong>Doxie<span>OS</span></strong><small>WORKBENCH</small></span>
      </a>
      <nav aria-label="Workbench">
        <span className="nav-label">{t('Chrome.Workbench', 'WORKBENCH')}</span>
        {navigation.map(({ to, key, label, icon: Icon }) => <a key={to} href={href(to)} title={t(key, label)} className={`nav-link ${activeItem?.to === to ? 'selected' : ''}`} aria-current={activeItem?.to === to ? 'page' : undefined}>
          <Icon size={18} strokeWidth={1.7} /><span>{t(key, label)}</span>{to === '/consoles' && live > 0 && <small>{live}</small>}
        </a>)}
      </nav>
      <div className="sidebar-bottom">
        <a href={href('/settings')} title={t('Nav.Settings', 'Settings')} className={`nav-link ${pathname === '/settings' ? 'selected' : ''}`} aria-current={pathname === '/settings' ? 'page' : undefined}><Settings2 size={18} strokeWidth={1.7} /><span>{t('Nav.Settings', 'Settings')}</span></a>
        <button type="button" className="edition" title={t('App.ReleaseNotes', 'Release notes')} onClick={onReleaseNotes}><CircleDot size={15} /><span>{t('Chrome.Desktop', 'Desktop edition')}</span><small className={newRelease ? 'new-release' : ''}>{state.version ? `v${state.version}` : 'LOCAL'}</small></button>
      </div>
    </aside>
    <div className="desktop-main">
      <header className="topbar">
        <div className="topbar-left"><IconButton icon={Menu} label={t('Chrome.ToggleNav', 'Toggle navigation')} onClick={onCollapse} aria-expanded={!collapsed} /><span className="topbar-divider" /><span className="breadcrumb">{selected}</span></div>
        <div className="topbar-right">
          <a className="topbar-provider" href={href('/settings')} title={t('App.Provider', 'Agent provider')}><Cpu size={15} /><span>{provider?.displayName || provider?.name || t('Chrome.Local', 'Local runtime')}</span></a>
          <a className="topbar-live" href={href('/consoles')}><Activity size={15} /><span>{live} {t('App.Live', 'live')}</span></a>
          <span className="topbar-divider" />
          <IconButton icon={Bell} label={`Notifications${unread ? ` (${unread} unread)` : ''}`} to="/notifications" aria-current={pathname === '/notifications' ? 'page' : undefined}>{unread > 0 && <i className="notification-dot" />}</IconButton>
        </div>
      </header>
      <main className="content" id="main-content" tabIndex={-1}>{children}</main>
    </div>
  </div>;
}

function Workbench() {
  const [state, setState] = useState(emptyState);
  const [route, setRoute] = useState(currentRoute);
  const [collapsed, setCollapsed] = useState(() => localStorage.getItem('doxie-nav') === 'collapsed');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [toast, setToast] = useState(null);
  const [showRelease, setShowRelease] = useState(false);
  const [seenRelease, setSeenRelease] = useState(() => localStorage.getItem('doxie-seen-release'));
  const [lastEvent, setLastEvent] = useState(null);
  const updateTimer = useRef(null);
  const toastTimer = useRef(null);

  const refresh = useCallback(async () => {
    try {
      const data = await invoke('bootstrap');
      setState({ ...emptyState, ...data });
      setError('');
    } catch (problem) {
      setError(problem.message);
    } finally {
      setLoading(false);
    }
  }, []);

  const notify = useCallback((text, tone = 'success') => {
    setToast({ text, tone });
    clearTimeout(toastTimer.current);
    toastTimer.current = setTimeout(() => setToast(null), 4500);
  }, []);

  useEffect(() => {
    refresh();
    const navigate = () => setRoute(currentRoute());
    window.addEventListener('hashchange', navigate);
    const unsubscribe = window.doxie?.onEvent((event) => {
      if (event.type === 'navigate' && typeof event.href === 'string' && /^\/(?:agents|connectors|workflows|history|notifications)(?:[/?]|$)/.test(event.href) && !event.href.includes('\\')) {
        window.location.hash = event.href;
      }
      if (event.type === 'console:output' || event.type === 'agent:output') return;
      setLastEvent(event);
      if (event.type === 'notification' && event.notification?.title) notify(event.notification.title);
      clearTimeout(updateTimer.current);
      updateTimer.current = setTimeout(refresh, 180);
    });
    return () => { window.removeEventListener('hashchange', navigate); unsubscribe?.(); clearTimeout(updateTimer.current); clearTimeout(toastTimer.current); };
  }, [refresh, notify]);

  useEffect(() => { document.querySelector('main')?.scrollTo(0, 0); }, [route.pathname]);
  useEffect(() => { document.documentElement.lang = state.settings.language || 'en'; }, [state.settings.language]);

  const perform = useCallback(async (action, args = [], success = '') => {
    try {
      const result = await invoke(action, ...args);
      await refresh();
      if (result !== null && result !== false && success) notify(success);
      return result;
    } catch (problem) {
      notify(problem.message, 'error');
      return false;
    }
  }, [refresh, notify]);

  const context = useMemo(() => ({ state, refresh, perform, notify, lastEvent }), [state, refresh, perform, notify, lastEvent]);
  const toggleNav = () => setCollapsed((old) => { localStorage.setItem('doxie-nav', old ? 'expanded' : 'collapsed'); return !old; });
  const newRelease = Boolean(state.version && state.version !== seenRelease);
  const releaseNotes = () => { setShowRelease(true); if (state.version) { localStorage.setItem('doxie-seen-release', state.version); setSeenRelease(state.version); } };
  const routeKey = route.pathname.match(/^\/workspaces\/[^/]+\/files(?=\/|$)/)?.[0] || route.pathname;

  return <AppContext.Provider value={context}>
    <a href="#main-content" className="skip-link" onClick={(event) => { event.preventDefault(); document.getElementById('main-content')?.focus(); }}>Skip to content</a>
    <Chrome state={state} pathname={route.pathname} collapsed={collapsed} onCollapse={toggleNav} onReleaseNotes={releaseNotes} newRelease={newRelease}>
      {loading ? <Loading /> : error ? <><ErrorNotice error={error} /><Button onClick={refresh}>Retry</Button></> : <Router key={routeKey} route={route} />}
    </Chrome>
    {toast && <div className={`toast ${toast.tone}`} role="status"><span>{toast.text}</span><IconButton icon={X} label="Dismiss message" onClick={() => setToast(null)} /></div>}
    {showRelease && <Modal title="Release notes" icon={CircleDot} close={() => setShowRelease(false)} wide><div className="modal-body stack"><img className="about-logo" src={logo} alt="" /><h3>DoxieOS Desktop {state.version && `v${state.version}`}</h3><p>Local workbench for agents, workflows, workspaces and reusable memory.</p>{state.releaseNotes?.map((note) => <details className="memory" key={note.version} open={note.version === state.version}><summary><strong>{note.title || `v${note.version}`}</strong> <span className="tag">v{note.version}</span></summary><Markdown>{note.body}</Markdown></details>)}</div><footer className="modal-footer"><Button onClick={() => setShowRelease(false)}>Close</Button></footer></Modal>}
  </AppContext.Provider>;
}

createRoot(document.getElementById('root')).render(<StrictMode><Workbench /></StrictMode>);
