const { app, BrowserWindow, Menu, dialog, ipcMain, net, protocol, session, shell } = require('electron');
const { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } = require('node:fs');
const { homedir } = require('node:os');
const { dirname, join, resolve, sep } = require('node:path');
const { pathToFileURL } = require('node:url');
const { createStorage, safePath } = require('./storage.cjs');
const { createAgents } = require('./agents.cjs');
const { createWorkflows } = require('./workflows.cjs');
const { createConsoles } = require('./consoles.cjs');
const { createBuilders } = require('./builders.cjs');
const { createMcp } = require('./mcp.cjs');
const { createActions, createNotifications } = require('./ipc.cjs');
const { startHousekeeping } = require('./housekeeping.cjs');

const startPage = 'doxie://app/index.html';
const appRoot = resolve(__dirname, '../..');
const renderer = join(appRoot, 'dist', 'renderer');
const contentPolicy = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self' data:; img-src 'self' data: blob:; frame-src blob: data:; connect-src 'self'; media-src 'self' blob:; base-uri 'none'; form-action 'none'";

protocol.registerSchemesAsPrivileged([{ scheme: 'doxie', privileges: {
  standard: true, secure: true, supportFetchAPI: true, corsEnabled: true,
} }]);
app.enableSandbox();
app.setAppUserModelId('org.doxieos.desktop');
if (process.env.DOXIE_USER_DATA) app.setPath('userData', resolve(process.env.DOXIE_USER_DATA));

let window;
let storage;
let agents;
let workflows;
let consoles;
let stopHousekeeping;
let pendingRoot;
let quitting = false;
let closed = false;

function emit(data) {
  if (window && !window.isDestroyed() && !window.webContents.isDestroyed()) window.webContents.send('doxie:event', data);
}

function selectedRoot() {
  const preferenceFile = join(app.getPath('userData'), 'workbench-root.json');
  try {
    const saved = JSON.parse(readFileSync(preferenceFile, 'utf8'));
    if (saved.root && existsSync(saved.root)) return saved.root;
  } catch {}
  return null;
}

function repoRoot(start) {
  for (let directory = resolve(start); ; directory = dirname(directory)) {
    if (existsSync(join(directory, '.doxie'))) return directory;
    if (dirname(directory) === directory) return null;
  }
}

function expandPath(value, root) {
  const expanded = value.replace(/%([^%]+)%/g, (token, name) => process.env[name] || token);
  return resolve(root, expanded.startsWith(`~${sep}`) || expanded.startsWith('~/') ? join(homedir(), expanded.slice(2)) : expanded);
}

function releaseNotes() {
  const directory = app.isPackaged ? join(process.resourcesPath, 'release-notes') : join(appRoot, 'docs', 'release-notes');
  const notes = [];
  const files = existsSync(directory) ? readdirSync(directory) : [];
  for (const file of files.filter(name => /^v\d+\.\d+\.\d+\.md$/.test(name)).sort().reverse()) {
    const text = readFileSync(join(directory, file), 'utf8');
    const highlights = text.match(/^highlights:\s*(.+)$/m)?.[1]?.replace(/\s*\|\s*/g, ' · ');
    notes.push({ version: file.slice(1, -3), title: text.match(/^# (.+)$/m)?.[1] || file, body: highlights || '' });
  }
  if (!notes.some(note => note.version === app.getVersion())) notes.unshift({
    version: app.getVersion(), title: 'DoxieOS Desktop', body: 'The workbench, catalog, consoles and workflow engine run together in Electron.',
  });
  return notes;
}

function initialize() {
  const root = resolve(process.env.DOXIE_ROOT || selectedRoot() || (!app.isPackaged && (repoRoot(process.cwd()) || appRoot)) || join(app.getPath('userData'), 'workbench'));
  const legacyLocations = [process.env.LOCALAPPDATA, process.env.XDG_DATA_HOME, join(homedir(), '.local', 'share'), join(homedir(), 'Library', 'Application Support')].filter(Boolean);
  const previous = legacyLocations.map(directory => join(directory, 'Viamus.Doxie.Orchestrator', 'state.db')).find(existsSync);
  const configured = process.env.DOXIE_DATABASE_PATH || process.env.Storage__DatabasePath;
  const databasePath = configured ? expandPath(configured, root)
    : previous || join(app.getPath('userData'), 'state.db');
  const bundledRoot = app.isPackaged ? process.resourcesPath : appRoot;
  storage = createStorage({ root, databasePath, bundledRoot, workspacesRoot: process.env.Storage__WorkspacesDirectory });
  storage.regenerate();
  storage.watchCatalog?.(() => emit({ type: 'catalog:changed' }), error => console.error(error.message));
  const notifications = createNotifications(storage, emit);
  agents = createAgents({ storage, emit, notify: notifications.add });
  workflows = createWorkflows({ storage, agents, emit, notify: notifications.add });
  consoles = createConsoles({ storage, emit });
  const builders = createBuilders({ storage, agents, emit });
  const mcp = createMcp(storage);
  const actions = createActions({ app, dialog, shell, window: () => window, storage, agents, workflows, consoles, builders, notifications, mcp, emit, releaseNotes: releaseNotes() });
  ipcMain.handle('doxie:invoke', (event, action, args) => {
    if (quitting || event.sender !== window?.webContents || event.senderFrame !== event.sender.mainFrame
      || event.senderFrame.url.split('#')[0] !== startPage || !Object.hasOwn(actions, action) || !Array.isArray(args)) {
      throw new Error('This workbench action is unavailable.');
    }
    return actions[action](...args);
  });
  workflows.start();
  stopHousekeeping = startHousekeeping({ storage, builders });
}

function createWindow() {
  window = new BrowserWindow({
    title: 'DoxieOS',
    width: 1440, height: 940, minWidth: 760, minHeight: 550,
    show: false, backgroundColor: '#111210',
    icon: join(__dirname, 'assets', process.platform === 'win32' ? 'icon.ico' : 'icon.png'),
    webPreferences: { preload: join(__dirname, 'preload.cjs'), contextIsolation: true, nodeIntegration: false, sandbox: true },
  });
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', event => event.preventDefault());
  window.webContents.on('will-attach-webview', event => event.preventDefault());
  window.once('ready-to-show', () => window?.show());
  window.on('closed', () => { window = undefined; });
  return window.loadURL(startPage);
}

function createMenu() {
  const menu = [];
  if (process.platform === 'darwin') menu.push({ role: 'appMenu' });
  menu.push({ label: 'File', submenu: [
    { label: 'Open Doxie folder…', accelerator: 'CmdOrCtrl+Shift+O', enabled: !process.env.DOXIE_ROOT, click: async () => {
      if (!window) return;
      const pick = await dialog.showOpenDialog(window, { title: 'Open Doxie folder', defaultPath: storage.root, properties: ['openDirectory', 'createDirectory'] });
      if (pick.canceled || !pick.filePaths[0] || pick.filePaths[0] === storage.root) return;
      const answer = await dialog.showMessageBox(window, {
        type: 'question', message: 'Restart with this Doxie folder?',
        detail: 'The app will stop active runs and terminals before restarting.',
        buttons: ['Cancel', 'Restart'], cancelId: 0, defaultId: 0,
      });
      if (answer.response !== 1) return;
      pendingRoot = pick.filePaths[0];
      app.quit();
    } },
    { type: 'separator' },
    { role: process.platform === 'darwin' ? 'close' : 'quit' },
  ] }, { role: 'editMenu' }, { label: 'View', submenu: [
    { role: 'resetZoom' }, { role: 'zoomIn' }, { role: 'zoomOut' }, { type: 'separator' }, { role: 'togglefullscreen' },
  ] });
  Menu.setApplicationMenu(Menu.buildFromTemplate(menu));
}

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', () => {
    if (!window) return;
    if (window.isMinimized()) window.restore();
    window.focus();
  });
  app.whenReady().then(async () => {
    session.defaultSession.setPermissionCheckHandler((contents, permission, origin, details) => (
      contents === window?.webContents && origin === 'doxie://app' && permission === 'media' && details.mediaType === 'audio'
    ));
    session.defaultSession.setPermissionRequestHandler((contents, permission, callback, details) => {
      callback(contents === window?.webContents && permission === 'media' && details.isMainFrame
        && details.requestingUrl?.split('#')[0] === startPage && details.mediaTypes?.length === 1 && details.mediaTypes[0] === 'audio');
    });
    protocol.handle('doxie', async request => {
      try {
        const url = new URL(request.url);
        if (url.host !== 'app' || !['GET', 'HEAD'].includes(request.method)) return new Response(null, { status: 404 });
        const path = safePath(renderer, decodeURIComponent(url.pathname.slice(1) || 'index.html'));
        const response = await net.fetch(pathToFileURL(path).href, { method: request.method });
        const headers = new Headers(response.headers);
        headers.set('Content-Security-Policy', contentPolicy);
        headers.set('X-Content-Type-Options', 'nosniff');
        return new Response(response.body, { status: response.status, headers });
      } catch {
        return new Response(null, { status: 404 });
      }
    });
    initialize();
    createMenu();
    await createWindow();
    app.on('activate', () => { if (!window && !quitting) void createWindow(); });
  }).catch(error => {
    console.error(error);
    if (!process.env.DOXIE_TEST) dialog.showErrorBox('DoxieOS startup', error.message);
    app.exit(1);
  });
  app.on('window-all-closed', () => {
    if (process.platform !== 'darwin') app.quit();
  });
  app.on('before-quit', event => {
    if (closed || !storage) return;
    event.preventDefault();
    if (quitting) return;
    quitting = true;
    Promise.allSettled([workflows?.close(), consoles?.close(), stopHousekeeping?.()]).then(async () => {
      await agents?.close();
      storage.close();
      if (pendingRoot) {
        const directory = app.getPath('userData');
        mkdirSync(directory, { recursive: true });
        writeFileSync(join(directory, 'workbench-root.json'), JSON.stringify({ root: pendingRoot }), { mode: 0o600 });
        app.relaunch();
      }
    }).catch(error => console.error(error)).finally(() => {
      closed = true;
      app.quit();
    });
  });
}
