import { useEffect, useRef, useState } from 'react';
import { Terminal as Xterm } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { BookOpen, ChevronDown, ChevronRight, Code2, Copy, Download, ExternalLink, File, FileText, Folder, FolderKanban, Import, Maximize2, Minimize2, Pencil, Plus, Save, Search, Square, Terminal, Trash2, X } from 'lucide-react';
import { Button, Confirm, Empty, ErrorNotice, Field, IconButton, ItemRow, Loading, Modal, PageHeading, Panel, SearchBox, Status, Tag, VoiceInput, go, invoke, nameOf, part, slug, useApp, useAsync, useText } from './ui.jsx';
import '@xterm/xterm/css/xterm.css';

const fileRoute = (id, path = '') => `/workspaces/${part(id)}/files${path ? `/${path.split('/').map(part).join('/')}` : ''}`;
const entityKey = (item) => `${item.catalogId || 'default'}/${item.id}`;

function WorkspaceEditor({ workspace, close }) {
  const { perform } = useApp();
  const t = useText();
  const [draft, setDraft] = useState({ id: workspace?.id || '', name: workspace?.name || '', description: workspace?.description || '' });
  const [pending, setPending] = useState(false);
  const set = (key, value) => setDraft((previous) => ({ ...previous, [key]: value }));
  const submit = async (event) => {
    event.preventDefault();
    setPending(true);
    const result = await perform('catalog:save', ['workspaces', { ...draft, id: draft.id || slug(draft.name) }], workspace ? 'Workspace updated' : 'Workspace created');
    setPending(false);
    if (result) close();
  };
  return <Modal title={workspace ? `Edit ${nameOf(workspace)}` : t('Workspaces.New', 'New workspace')} icon={FolderKanban} close={close}><form onSubmit={submit}>
    <div className="modal-body stack">
      <div className="form-grid"><Field label="Name"><input required value={draft.name} onChange={(event) => set('name', event.target.value)} /></Field><Field label="Workspace ID"><input pattern="[a-z0-9]+(-[a-z0-9]+)*" disabled={!!workspace} value={draft.id} placeholder={slug(draft.name) || 'my-workspace'} onChange={(event) => set('id', event.target.value)} /></Field></div>
      <Field label="Description"><textarea rows={2} value={draft.description} onChange={(event) => set('description', event.target.value)} /></Field>
      {!workspace && <div className="field">
        <span>Copy files from a folder (optional)</span>
        <div className="input-action">
          <input aria-label="Source folder" readOnly value={draft.path || ''} placeholder="Start with an empty workspace" />
          <Button icon={Folder} onClick={async () => { const selected = await perform('workspace:pickDirectory'); if (selected) set('path', typeof selected === 'string' ? selected : selected.path); }}>Choose folder</Button>
          {draft.path && <IconButton label="Clear source folder" icon={X} onClick={() => set('path', '')} />}
        </div>
        <small>DoxieOS copies the files into the new workspace in its application data folder.</small>
      </div>}
      <p className="muted">Workspaces hold run inputs and outputs. Choose the agent or workflow when you run it; reusable knowledge stays in Brain.</p>
    </div><footer className="modal-footer"><Button onClick={close}>Cancel</Button><Button type="submit" variant="primary" disabled={pending}>{workspace ? 'Save workspace' : 'Create workspace'}</Button></footer>
  </form></Modal>;
}

export function WorkspacesPage() {
  const { state, perform } = useApp();
  const t = useText();
  const [search, setSearch] = useState('');
  const [edit, setEdit] = useState(() => window.location.hash.includes('new=1') ? {} : null);
  const [remove, setRemove] = useState(null);
  const workspaces = state.workspaces.filter((workspace) => `${nameOf(workspace)} ${workspace.id} ${workspace.description || ''}`.toLowerCase().includes(search.toLowerCase()));
  const newWorkspace = () => setEdit({});
  return <div className="page"><PageHeading icon={FolderKanban} title={t('Workspaces.Title', 'Workspaces')} subtitle={t('Workspaces.IOSubtitle', 'Folders for run inputs and outputs. Knowledge is shared through Brain.')} actions={<><Button icon={Import} onClick={() => perform('catalog:import', ['workspaces'], 'Workspace imported')}>Import</Button><Button variant="primary" icon={Plus} onClick={newWorkspace}>{t('Workspaces.New', 'New workspace')}</Button></>} />
    <div className="toolbar"><SearchBox value={search} setValue={setSearch} placeholder={t('Workspaces.SearchPlaceholder', 'Search by name or ID…')} /></div>
    {workspaces.length ? <div className="cards">{workspaces.map((workspace) => <Panel key={workspace.id} title={nameOf(workspace)} icon={FolderKanban} hint={workspace.path} actions={<IconButton icon={Trash2} label="Delete workspace" onClick={() => setRemove(workspace)} />}>
      <div className="panel-body stack"><p>{workspace.description || 'Run inputs and outputs'}</p><div className="actions start"><Tag>Input / output</Tag></div><div className="actions start">
        <Button to={fileRoute(workspace.id)} icon={Folder}>Files</Button><Button icon={Terminal} onClick={async () => { const session = await perform('console:start', [{ workspaceId: workspace.id }]); if (session?.id) go(`/consoles?session=${part(session.id)}`); }}>Console</Button><Button icon={ExternalLink} onClick={() => perform('workspace:open', [workspace.id])}>Folder</Button><IconButton icon={Code2} label="Open in editor" onClick={() => perform('workspace:open', [workspace.id, 'editor'])} /><IconButton icon={Download} label="Export workspace" onClick={() => perform('catalog:export', ['workspaces', workspace.id])} /><Button onClick={() => setEdit(workspace)}>Edit</Button>
      </div></div>
    </Panel>)}</div> : <Panel><Empty icon={search ? Search : FolderKanban} title={search ? 'No matching workspaces' : 'No workspaces yet'} detail={t('Workspaces.IOEmpty', 'Create a folder for the files your runs read and produce.')} action={!search && <Button icon={Plus} onClick={newWorkspace}>Create workspace</Button>} /></Panel>}
    {edit && <WorkspaceEditor workspace={edit.id ? edit : null} close={() => setEdit(null)} />}
    {remove && <Confirm title="Delete workspace" detail={`Remove ${nameOf(remove)} from DoxieOS?`} close={() => setRemove(null)} confirm={async () => { const result = await perform('catalog:remove', ['workspaces', remove.id], 'Workspace removed'); if (result !== false) setRemove(null); }} />}
  </div>;
}

function Diagram({ source }) {
  const result = useAsync(async () => {
    const { default: mermaid } = await import('mermaid');
    mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: 'dark' });
    const { svg } = await mermaid.render(`doxie-diagram-${crypto.randomUUID()}`, source);
    return `data:image/svg+xml,${encodeURIComponent(svg)}`;
  }, [source]);
  if (result.loading) return <Loading />;
  if (result.error) return <><ErrorNotice error="This diagram could not be rendered." /><pre>{source}</pre></>;
  return <img className="diagram" src={result.value} alt="Diagram" />;
}

function MarkdownImage({ target, alt, resolve }) {
  const image = useAsync(() => target && resolve?.(target), [target, resolve]);
  return image.value?.dataUrl?.startsWith('data:image/') ? <img className="document-image" src={image.value.dataUrl} alt={alt || ''} /> : alt ? <em>{alt}</em> : null;
}

export function Markdown({ children, resolveImage }) {
  const { perform } = useApp();
  return <div className="markdown"><ReactMarkdown remarkPlugins={[remarkGfm]} skipHtml components={{
    a: ({ href: target, children: label }) => target ? <a href={target} onClick={(event) => { event.preventDefault(); if (/^https?:\/\//i.test(target)) perform('shell:openExternal', [target]); else if (target.startsWith('#')) document.getElementById(target.slice(1))?.scrollIntoView(); }}>{label}</a> : <span>{label}</span>,
    img: ({ alt, src }) => resolveImage ? <MarkdownImage target={src} alt={alt} resolve={resolveImage} /> : alt ? <em>{alt}</em> : null,
    pre: ({ node, children: content }) => {
      const code = node?.children?.[0];
      return code?.properties?.className?.includes('language-mermaid') ? <Diagram source={code.children?.map((child) => child.value || '').join('') || ''} /> : <pre>{content}</pre>;
    },
  }}>{String(children || '')}</ReactMarkdown></div>;
}

function FileTree({ api, id, catalogId, folder = '', selected, onSelect, depth = 0, revision = 0 }) {
  const result = useAsync(() => invoke(`${api}:list`, id, folder, catalogId), [api, id, catalogId, folder, revision]);
  const [expanded, setExpanded] = useState({});
  if (result.loading) return <Loading />;
  if (result.error) return <ErrorNotice error={result.error} />;
  const entries = result.value?.entries || result.value || [];
  if (!entries.length && !depth) return <Empty compact title="No files" icon={Folder} />;
  return <ul className="file-tree">{entries.map((file) => {
    const relative = file.relativePath || `${folder ? `${folder}/` : ''}${file.name}`;
    const directory = file.isDirectory ?? file.directory;
    const open = expanded[relative] ?? selected?.startsWith(`${relative}/`);
    const Icon = directory ? Folder : File;
    const Caret = open ? ChevronDown : ChevronRight;
    return <li key={relative}>
      <button type="button" className={`file-row ${selected === relative ? 'selected' : ''}`} style={{ '--depth': depth }} aria-expanded={directory ? !!open : undefined} title={file.name} onClick={() => directory ? setExpanded((old) => ({ ...old, [relative]: !open })) : onSelect(relative)}>{directory ? <Caret size={13} /> : <span className="tree-indent" />}<Icon size={15} /> <span>{file.name}</span></button>
      {directory && open && <FileTree api={api} id={id} catalogId={catalogId} folder={relative} selected={selected} onSelect={onSelect} depth={depth + 1} revision={revision} />}
    </li>;
  })}</ul>;
}

function CsvPreview({ content, delimiter }) {
  const rows = [];
  let row = [];
  let field = '';
  let quoted = false;
  for (let index = 0; index < content.length && rows.length < 101; index += 1) {
    const char = content[index];
    if (char === '"') {
      if (quoted && content[index + 1] === '"') { field += '"'; index += 1; }
      else quoted = !quoted;
    } else if (!quoted && char === delimiter) { row.push(field); field = ''; }
    else if (!quoted && (char === '\n' || char === '\r')) {
      if (char === '\r' && content[index + 1] === '\n') index += 1;
      rows.push([...row, field]); row = []; field = '';
    } else field += char;
  }
  if (rows.length < 101 && (row.length || field)) rows.push([...row, field]);
  if (!rows.length) return <Empty compact title="No rows" icon={FileText} />;
  const [header, ...data] = rows;
  return <table className="preview-table"><caption>First {data.length} data {data.length === 1 ? 'row' : 'rows'}</caption><thead><tr>{header.map((cell, index) => <th scope="col" key={index}>{cell}</th>)}</tr></thead><tbody>{data.map((cells, rowIndex) => <tr key={rowIndex}>{cells.map((cell, index) => <td key={index}>{cell}</td>)}</tr>)}</tbody></table>;
}

function relativeImage(path, target) {
  if (/^(?:[a-z]+:|\/|\\)/i.test(target)) return null;
  const pieces = path.split('/').slice(0, -1);
  for (const encoded of target.split('/')) {
    let piece;
    try { piece = decodeURIComponent(encoded); } catch { return null; }
    if (piece === '.') continue;
    if (piece === '..') { if (!pieces.length) return null; pieces.pop(); }
    else if (piece && !/[\\/]/.test(piece)) pieces.push(piece);
    else return null;
  }
  return pieces.join('/');
}

function Preview({ file, path, api, id, catalogId }) {
  const [raw, setRaw] = useState(false);
  const ext = path.split('.').at(-1)?.toLowerCase();
  const content = file.content ?? file.text;
  const url = file.dataUrl;
  const markdown = ['md', 'markdown', 'mdx'].includes(ext);
  const spreadsheet = ext === 'csv' || ext === 'tsv';
  if (url && ['png', 'jpg', 'jpeg', 'webp', 'gif', 'bmp', 'ico', 'avif'].includes(ext)) return <div className="file-preview centered"><img className="document-image" src={url} alt={file.name || path} /></div>;
  if (url && ext === 'pdf') return <div className="file-preview"><iframe title={`PDF preview of ${file.name || path}`} src={url} /></div>;
  if (file.isBinary || content == null) return <Empty title="Preview unavailable" detail="Open the file in its native application or download a copy." icon={File} />;
  const resolveImage = (target) => {
    const relative = relativeImage(path, target);
    return relative ? invoke(`${api}:read`, id, relative, catalogId) : null;
  };
  const firstRow = content.split(/[\r\n]/, 1)[0];
  const delimiter = ext === 'tsv' ? '\t' : firstRow.includes(';') && !firstRow.includes(',') ? ';' : ',';
  return <div className="file-preview">{file.truncated && <p className="muted">Showing the available preview. Download the file to view it in full.</p>}{(markdown || spreadsheet || ext === 'mmd') && <div className="actions"><Button variant={raw ? 'ghost' : 'secondary'} onClick={() => setRaw(false)}>Preview</Button><Button variant={raw ? 'secondary' : 'ghost'} onClick={() => setRaw(true)}>Source</Button></div>}
    {markdown && !raw ? <Markdown resolveImage={resolveImage}>{content}</Markdown> : ext === 'mmd' && !raw ? <Diagram source={content} /> : spreadsheet && !raw ? <CsvPreview content={content} delimiter={delimiter} /> : <pre className="code-view">{content}</pre>}
  </div>;
}

export function FilesBrowser({ api = 'files', id, catalogId, path: initialPath = '', navigate, compact = false }) {
  const { perform } = useApp();
  const [selected, setSelected] = useState(initialPath);
  const [fullscreen, setFullscreen] = useState(false);
  const [revision, setRevision] = useState(0);
  const path = navigate ? initialPath : selected;
  const onSelect = navigate || setSelected;
  const result = useAsync(() => path && invoke(`${api}:read`, id, path, catalogId), [api, id, path, catalogId, revision]);
  useEffect(() => {
    if (!fullscreen) return;
    const handle = (event) => { if (event.key === 'Escape') setFullscreen(false); };
    window.addEventListener('keydown', handle);
    return () => window.removeEventListener('keydown', handle);
  }, [fullscreen]);
  return <div className={`split-view files-view ${fullscreen ? 'fullscreen' : ''} ${compact ? 'compact' : ''}`}>
    <Panel title="Files" icon={Folder} actions={<Button variant="ghost" onClick={() => setRevision((old) => old + 1)}>Refresh</Button>}><div className="file-tree-wrap"><FileTree api={api} id={id} catalogId={catalogId} selected={path} onSelect={onSelect} revision={revision} /></div></Panel>
    <Panel title={path || 'File preview'} icon={FileText} actions={<>{path && <><IconButton icon={ExternalLink} label="Open file" onClick={() => perform(`${api}:open`, [id, path, catalogId])} /><IconButton icon={Download} label="Download file" onClick={() => perform(`${api}:download`, [id, path, catalogId])} /></>}<IconButton icon={fullscreen ? Minimize2 : Maximize2} label={fullscreen ? 'Exit fullscreen' : 'Fullscreen'} onClick={() => setFullscreen(!fullscreen)} /></>}>
      {!path ? <Empty title="Select a file" detail="Choose a file from the tree to see its contents." icon={FileText} /> : result.loading ? <Loading /> : result.error ? <ErrorNotice error={result.error} /> : result.value ? <Preview key={path} path={path} file={result.value} api={api} id={id} catalogId={catalogId} /> : <Empty title="File unavailable" icon={File} />}
    </Panel>
  </div>;
}

export function WorkspaceFilesPage({ id, path }) {
  const { state, perform } = useApp();
  const workspace = state.workspaces.find((item) => item.id === id);
  return <div className="page"><PageHeading title={nameOf(workspace) || id} icon={FolderKanban} subtitle={workspace?.path} back={{ to: '/workspaces', label: 'Workspaces' }} actions={<Button icon={ExternalLink} onClick={() => perform('workspace:open', [id])}>Open folder</Button>} />
    {workspace ? <FilesBrowser id={id} path={path} navigate={(next) => go(fileRoute(id, next))} /> : <Panel><Empty title="Workspace not found" icon={Folder} /></Panel>}
  </div>;
}

function ConsoleTerminal({ id, fullscreen }) {
  const host = useRef(null);
  const fitAddon = useRef(null);
  const { notify } = useApp();
  useEffect(() => {
    const node = host.current;
    const term = new Xterm({ cursorBlink: true, convertEol: false, allowProposedApi: false, fontFamily: 'Cascadia Code, Consolas, monospace', fontSize: 13, scrollback: 10000, theme: { background: '#121210', foreground: '#eeeae3', cursor: '#e28d72', selectionBackground: '#634238' } });
    const fit = new FitAddon();
    fitAddon.current = fit;
    term.loadAddon(fit);
    term.open(node);
    const resize = () => {
      if (node.clientWidth > 0 && node.clientHeight > 0) fit.fit();
    };
    const input = term.onData((data) => invoke('console:write', id, data).catch((error) => notify(error.message, 'error')));
    const size = term.onResize(({ cols, rows }) => invoke('console:resize', id, cols, rows).catch(() => {}));
    const observer = new ResizeObserver(resize);
    observer.observe(node);
    let current = true;
    let attached = false;
    const buffered = [];
    const unsubscribe = window.doxie.onEvent((event) => {
      if (event.type !== 'console:output' || (event.id || event.sessionId) !== id) return;
      if (attached) term.write(event.data || event.output || '');
      else buffered.push(event);
    });
    invoke('console:snapshot', id).then((snapshot) => {
      if (!current) return;
      const output = snapshot?.output ?? snapshot?.buffer;
      if (output) term.write(Array.isArray(output) ? output.join('') : output);
      for (const event of buffered) if (snapshot?.sequence != null && event.sequence > snapshot.sequence) term.write(event.data || event.output || '');
      attached = true;
      resize();
      invoke('console:resize', id, term.cols, term.rows).catch(() => {});
    }).catch((error) => { if (current) term.writeln(error.message); });
    return () => { current = false; unsubscribe?.(); observer.disconnect(); input.dispose(); size.dispose(); term.dispose(); fitAddon.current = null; };
  }, [id, notify]);
  useEffect(() => { const frame = requestAnimationFrame(() => fitAddon.current?.fit()); return () => cancelAnimationFrame(frame); }, [fullscreen]);
  return <div className="terminal-surface" ref={host} />;
}

function NewConsole({ close }) {
  const { state, perform } = useApp();
  const [workspaceId, setWorkspaceId] = useState(state.workspaces[0]?.id || '');
  const [providerId, setProviderId] = useState('');
  const [label, setLabel] = useState('');
  const [starting, setStarting] = useState(false);
  return <Modal title="New console" icon={Terminal} close={close}><form onSubmit={async (event) => {
    event.preventDefault(); setStarting(true);
    const session = await perform('console:start', [{ workspaceId: workspaceId || undefined, providerId: providerId || undefined, label: label || undefined }]);
    setStarting(false);
    if (session?.id) { close(); go(`/consoles?session=${part(session.id)}`); }
  }}><div className="modal-body stack">
    <Field label="Workspace"><select value={workspaceId} onChange={(event) => setWorkspaceId(event.target.value)}><option value="">DoxieOS data directory</option>{state.workspaces.map((workspace) => <option key={workspace.id} value={workspace.id}>{nameOf(workspace)}</option>)}</select></Field>
    <Field label="Session type"><select value={providerId} onChange={(event) => setProviderId(event.target.value)}><option value="">Local shell</option>{state.providers.map((provider) => <option key={provider.id} value={provider.id}>{provider.displayName || provider.name || provider.id}</option>)}</select></Field>
    <Field label="Label"><input value={label} onChange={(event) => setLabel(event.target.value)} placeholder="Optional session name" /></Field>
  </div><footer className="modal-footer"><Button onClick={close}>Cancel</Button><Button type="submit" variant="primary" icon={Terminal} disabled={starting}>Open console</Button></footer></form></Modal>;
}

export function ConsolesPage({ sessionId, builderReturn }) {
  const { state, perform } = useApp();
  const t = useText();
  const [showNew, setShowNew] = useState(false);
  const [fullscreen, setFullscreen] = useState(false);
  const session = state.consoles.find(({ id }) => id === sessionId) || state.consoles[0];
  useEffect(() => {
    if (!fullscreen) return;
    const handle = (event) => { if (event.key === 'Escape') setFullscreen(false); };
    window.addEventListener('keydown', handle);
    return () => window.removeEventListener('keydown', handle);
  }, [fullscreen]);
  return <div className="page"><PageHeading title={t('Consoles.Title', 'Consoles')} icon={Terminal} subtitle={t('Consoles.Subtitle', 'Open a live shell or provider session in a workspace.')} back={builderReturn ? { to: builderReturn, label: 'Back to builder' } : undefined} actions={<Button variant="primary" icon={Plus} onClick={() => setShowNew(true)}>{t('Consoles.NewSession', 'New session')}</Button>} />
    <div className={`split-view terminal-view ${fullscreen ? 'fullscreen' : ''}`}>
      <Panel title={t('Consoles.Sessions', 'Sessions')} icon={Terminal} className="terminal-sessions">{state.consoles.length ? <div className="row-list">{state.consoles.map((item) => <ItemRow key={item.id} title={item.label || item.name || item.id} detail={item.workspaceId || item.kind} to={`/consoles?session=${part(item.id)}`}><Status status={item.status} /></ItemRow>)}</div> : <Empty compact title="No sessions" icon={Terminal} />}</Panel>
      <Panel title={session?.label || session?.name || 'Terminal'} icon={Terminal} actions={<>{session?.status === 'Running' && <><VoiceInput append={(text) => invoke('console:write', session.id, text).catch(() => {})} /><Button icon={Square} onClick={() => perform('console:stop', [session.id], 'Console stopped')}>Stop</Button></>}{session && session.status !== 'Running' && <IconButton icon={Trash2} label="Remove console" onClick={() => perform('console:remove', [session.id])} />}{session && <IconButton icon={fullscreen ? Minimize2 : Maximize2} label={fullscreen ? 'Exit fullscreen' : 'Fullscreen'} onClick={() => setFullscreen(!fullscreen)} />}</>}>
        {session ? <ConsoleTerminal key={session.id} id={session.id} fullscreen={fullscreen} /> : <Empty title={t('Consoles.NoSelected', 'No console selected')} detail={t('Consoles.NoSelectedBody', 'Start a session to open an interactive terminal.')} icon={Terminal} action={<Button icon={Plus} onClick={() => setShowNew(true)}>New session</Button>} />}
      </Panel>
    </div>{showNew && <NewConsole close={() => setShowNew(false)} />}
  </div>;
}
