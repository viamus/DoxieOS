const { copyFile, readFile, readdir, stat } = require('node:fs/promises');
const { basename, extname, isAbsolute, join, relative, sep } = require('node:path');
const { randomUUID } = require('node:crypto');
const { nextCron } = require('./workflows.cjs');
const { safePath } = require('./storage.cjs');
const { sweepScratch } = require('./housekeeping.cjs');
const { definitions, settingsFor, providerEnvironment, jsonSetting } = require('./providers.cjs');
const { normalizeModel } = require('./execution.cjs');
const { discoverModels } = require('./model-catalog.cjs');
const { executablePath, launch } = require('./processes.cjs');

const privateRunFolder = name => name.toLowerCase() === '.doxie-transient';
const fileTypes = {
  '.md': 'text/markdown', '.csv': 'text/csv', '.txt': 'text/plain', '.json': 'application/json',
  '.png': 'image/png', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.webp': 'image/webp',
  '.gif': 'image/gif', '.avif': 'image/avif', '.bmp': 'image/bmp', '.pdf': 'application/pdf',
};
function validatedEnvironment(environment = {}, perRun = false) {
  if (!environment || typeof environment !== 'object' || Array.isArray(environment)
    || Object.entries(environment).some(([key, value]) => !/^[A-Za-z_][\w]*$/.test(key) || typeof value !== 'string'
      || value.includes('\0') || (perRun && /^DOXIE_/i.test(key)))) {
    throw new Error('Invalid provider environment variables.');
  }
  return environment;
}

function createNotifications(storage, emit) {
  const notifications = jsonSetting(storage, 'desktop.notifications', []);
  const persist = () => storage.setSetting('desktop.notifications', notifications);
  return {
    list: () => notifications,
    add(data) {
      const notification = { ...data, id: randomUUID(), timestamp: new Date().toISOString(), body: data.body || data.message || '' };
      notifications.unshift(notification);
      notifications.length = Math.min(notifications.length, 250);
      persist();
      emit({ type: 'notification', notification });
    },
    read(id) {
      for (const item of notifications) {
        if (!id || item.id === id) item.readAt ??= new Date().toISOString();
      }
      persist();
      return true;
    },
  };
}

async function preview(path, metadata = {}) {
  const mimeType = fileTypes[extname(path).toLowerCase()] || 'application/octet-stream';
  const result = { ...metadata, name: metadata.name || basename(path), mimeType };
  if (mimeType.startsWith('image/') || mimeType === 'application/pdf') {
    if (result.sizeBytes <= 12 * 1024 * 1024) result.dataUrl = `data:${mimeType};base64,${(await readFile(path)).toString('base64')}`;
  }
  return result;
}

async function openPath(shell, path) {
  const error = await shell.openPath(path);
  if (error) throw new Error(error);
  return true;
}

function createActions({ app, dialog, shell, window, storage, agents, workflows, consoles, builders, notifications, mcp, emit, releaseNotes }) {
  const inventories = new Map();

  function modelsFor(id, options) {
    if (!Object.hasOwn(definitions, id)) throw new Error('Unknown provider.');
    const cached = inventories.get(id);
    if (cached && (!cached.expires || (!options?.refresh && cached.expires > Date.now()))) return cached.promise;
    const provider = settingsFor(storage).providers[id];
    const environment = providerEnvironment(storage, { ...process.env, ...provider.environment });
    const entry = { expires: 0 };
    entry.promise = discoverModels({
      providerId: id, environment, hostEnvironment: process.env, workspaceRoot: storage.root,
      executable: executablePath(provider.executable, environment), useHostProfiles: process.env.DOXIE_TEST !== '1',
    }).then(inventory => {
      const models = new Map(inventory.models.map(model => [model.id, model]));
      for (const id of [...(Array.isArray(provider.models) ? provider.models : []), provider.model]) {
        if (id && !models.has(id)) models.set(id, { id, label: id, source: 'configured' });
      }
      return { ...inventory, models: [...models.values()] };
    }).finally(() => { entry.expires = Date.now() + 60_000; });
    inventories.set(id, entry);
    return entry.promise;
  }

  function settings() {
    const current = settingsFor(storage);
    return {
      ...current, mcpServers: mcp.publicList(),
      providers: Object.fromEntries(Object.entries(current.providers).map(([id, provider]) => {
        const { environment, ...publicProvider } = provider;
        return [id, { ...publicProvider, hasEnvironment: Boolean(Object.keys(environment || {}).length), available: provider.installed }];
      })),
    };
  }

  function saveSettings(patch) {
    if (patch.language && !['en', 'pt-BR', 'es'].includes(patch.language)) throw new Error('Unsupported language.');
    if (patch.defaultProvider && !Object.hasOwn(definitions, patch.defaultProvider)) throw new Error('Unknown provider.');
    if (patch.providers) {
      const old = jsonSetting(storage, 'desktop.providers', {});
      const configured = {};
      for (const [id, defaults] of Object.entries(definitions)) {
        const value = { ...defaults, ...old[id], ...patch.providers[id] };
        const models = Array.isArray(value.models) ? value.models.map(normalizeModel).filter(Boolean) : defaults.models;
        const environment = validatedEnvironment(value.environment ?? old[id]?.environment);
        const executable = String(value.executable || defaults.executable).trim();
        configured[id] = { ...(defaults.localBaseUrl && executable === defaults.executable ? {} : { executable }), model: normalizeModel(value.model), models, environment };
      }
      storage.setSetting('desktop.providers', configured);
      inventories.clear();
    }
    if (patch.language) storage.setSetting('ui.language', patch.language);
    if (patch.defaultProvider) storage.setSetting('default_agent_provider', patch.defaultProvider);
    if (patch.catalogs && JSON.stringify(patch.catalogs) !== JSON.stringify(storage.listCatalogs())) storage.saveCatalogs(patch.catalogs);
    if (patch.mcpServers && JSON.stringify(patch.mcpServers) !== JSON.stringify(mcp.publicList())) mcp.save(patch.mcpServers);
    return settings();
  }

  function runPath(id, child = '') {
    if (typeof child !== 'string' || child.split(/[\\/]/).some(privateRunFolder)) throw new Error('Temporary workflow context is private to the runner.');
    const agentRun = agents.getRun(id);
    const workflow = agentRun ? null : workflows.getRun(id);
    if (!agentRun && !workflow) throw new Error('Run not found.');
    const path = agentRun?.outputDir || (workflow ? join(storage.roots.runs, workflow.id) : null);
    if (!path) throw new Error('The run has no output folder.');
    for (const candidate of [storage.roots.runs, storage.roots.sandboxes]) {
      const fromRoot = relative(candidate, path);
      if (!fromRoot || fromRoot === '..' || fromRoot.startsWith(`..${sep}`) || isAbsolute(fromRoot)) continue;
      return safePath(safePath(candidate, fromRoot.split(sep).join('/')), child);
    }
    throw new Error('The run output folder is outside the workbench.');
  }

  async function copyToDialog(source) {
    const result = await dialog.showSaveDialog(window(), { defaultPath: basename(source) });
    if (result.canceled || !result.filePath) return null;
    await copyFile(source, result.filePath);
    return true;
  }

  function files(prefix, kind) {
    const resolve = (id, path, catalogId) => storage.resolveFile(kind, id, path, catalogId);
    return {
      [`${prefix}:list`]: (id, path = '', catalogId) => storage.listFiles(kind, id, path, catalogId),
      [`${prefix}:read`]: (id, path, catalogId) => preview(resolve(id, path, catalogId), storage.readFile(kind, id, path, catalogId)),
      [`${prefix}:open`]: (id, path = '', catalogId) => openPath(shell, resolve(id, path, catalogId)),
      [`${prefix}:download`]: (id, path, catalogId) => copyToDialog(resolve(id, path, catalogId)),
    };
  }

  function startRun(id, params) {
    return agents.start(id, {
      arguments: params.arguments, inputs: params.inputs || params.fields, mode: params.mode, modeId: params.modeId,
      workspaceId: params.workspaceId, catalogId: params.catalogId, providerId: params.providerId, model: params.model,
      env: validatedEnvironment(params.env, true),
    });
  }

  return {
    bootstrap() {
      const catalog = Object.fromEntries(['agents', 'workflows', 'workspaces', 'libraries', 'tools'].map(kind => [kind, storage.list(kind)]));
      const { skills, legacySkills, ...publicRoots } = storage.roots;
      catalog.workflows = catalog.workflows.map(workflow => {
        const scheduled = workflow.enabled && ['cron', '1'].includes(String(workflow.trigger?.kind).toLowerCase());
        const nextRunAt = scheduled ? nextCron(workflow.trigger.cronExpression || workflow.trigger.expression)?.toISOString() : null;
        return { ...workflow, nextRunAt };
      });
      const configuration = settings();
      return {
        ...catalog, connectors: catalog.tools, root: storage.root, storage: {
          'App data': app.getPath('userData'), 'Data directory': storage.root, 'State database': storage.databasePath,
          'Provider profiles': safePath(storage.root, '.providers'), ...publicRoots,
        },
        settings: configuration, providers: Object.values(configuration.providers), agentRuns: agents.listRuns(),
        workflowRuns: workflows.listRuns(), notifications: notifications.list(), consoles: consoles.list(),
        version: app.getVersion(),
        releaseNotes,
      };
    },
    'catalog:list': (kind) => storage.list(kind),
    'catalog:get': (kind, id, catalogId) => storage.get(kind, id, catalogId),
    'catalog:save': (kind, item) => {
      const saved = storage.save(kind, item);
      emit({ type: 'catalog:changed', kind });
      return saved;
    },
    'catalog:remove': (kind, id, catalogId) => {
      const removed = storage.remove(kind, id, catalogId);
      emit({ type: 'catalog:changed', kind });
      return removed;
    },
    'catalog:regenerate': () => {
      mcp.refresh();
      return storage.regenerate();
    },
    'catalog:detectLegacy': () => storage.detectLegacy(),
    'catalog:migrateLegacy': () => {
      const result = storage.migrateLegacy();
      emit({ type: 'catalog:changed' });
      return result;
    },
    'catalog:import': async (kind, catalogId) => {
      const selection = await dialog.showOpenDialog(window(), { properties: ['openFile'], filters: [{ name: 'DoxieOS archive', extensions: ['zip'] }] });
      if (selection.canceled || !selection.filePaths[0]) return null;
      const file = selection.filePaths[0];
      const filename = basename(file).replace(/(?:\.doxie)?\.zip$/i, '');
      const importOptions = { catalogId, id: filename };
      if ((await stat(file)).size > 64 * 1024 * 1024) throw new Error('Catalog archive exceeds 64 MiB.');
      const data = await readFile(file);
      try {
        const item = storage.importItem(kind, data, importOptions);
        emit({ type: 'catalog:changed', kind });
        return item;
      } catch (error) {
        if (!/already exists/i.test(error.message)) throw error;
        const choice = await dialog.showMessageBox(window(), {
          type: 'warning', buttons: ['Cancel', 'Replace'], defaultId: 0, cancelId: 0,
          message: 'An item in this archive already exists. Replace it?',
        });
        if (choice.response !== 1) return null;
        const item = storage.importItem(kind, data, { ...importOptions, overwrite: true });
        emit({ type: 'catalog:changed', kind });
        return item;
      }
    },
    'catalog:export': async (kind, id, catalogId) => {
      const selection = await dialog.showSaveDialog(window(), { defaultPath: `${id}.doxie.zip`, filters: [{ name: 'DoxieOS archive', extensions: ['zip'] }] });
      if (selection.canceled || !selection.filePath) return null;
      const { writeFile } = require('node:fs/promises');
      await writeFile(selection.filePath, storage.exportItem(kind, id, catalogId));
      return true;
    },
    ...files('files', 'workspaces'),
    ...files('library-files', 'libraries'),
    'libraries:create': ({ id, name, description, catalogId }) => {
      const item = storage.createLibrary({ id, name, description, catalogId });
      emit({ type: 'catalog:changed', kind: 'libraries' });
      return item;
    },
    'library-files:write': (id, path, content, catalogId, options) => {
      if (typeof path !== 'string' || !/\.md$/i.test(path)) throw new Error('Library memories must be Markdown files.');
      if (options?.create) {
        let exists = true;
        try { storage.resolveFile('libraries', id, path, catalogId); }
        catch (error) {
          if (error.message !== 'The requested path does not exist') throw error;
          exists = false;
        }
        if (exists) throw new Error('This memory already exists.');
      }
      const result = storage.writeFile('libraries', id, path, content, catalogId);
      emit({ type: 'catalog:changed', kind: 'libraries' });
      return result;
    },
    'library-files:delete': (id, path, catalogId) => {
      if (typeof path !== 'string' || !/\.md$/i.test(path)) throw new Error('Library memories must be Markdown files.');
      const removed = storage.deleteFile('libraries', id, path, catalogId);
      if (removed) emit({ type: 'catalog:changed', kind: 'libraries' });
      return removed;
    },
    'run-files:list': async (id, path = '') => {
      const directory = runPath(id, path);
      let entries;
      try { entries = await readdir(directory, { withFileTypes: true }); }
      catch (error) { if (error.code === 'ENOENT') return []; throw error; }
      return Promise.all(entries.filter(entry => !privateRunFolder(entry.name) && (entry.isDirectory() || entry.isFile())).map(async entry => {
        const relativePath = path ? `${path}/${entry.name}` : entry.name;
        return { name: entry.name, path: relativePath, relativePath, isDirectory: entry.isDirectory(), sizeBytes: entry.isFile() ? (await stat(join(directory, entry.name))).size : null };
      }));
    },
    'run-files:read': async (id, relativePath) => {
      const file = runPath(id, relativePath);
      const information = await stat(file);
      if (!information.isFile()) throw new Error('Select a file to preview.');
      let content = null;
      const binaryType = /^(image\/|application\/pdf)/.test(fileTypes[extname(file).toLowerCase()] || '');
      if (!binaryType) {
        const { open } = require('node:fs/promises');
        const handle = await open(file, 'r');
        const bytes = Buffer.alloc(Math.min(information.size, 256 * 1024));
        try {
          const { bytesRead } = await handle.read(bytes);
          if (!bytes.includes(0)) content = new TextDecoder('utf-8', { fatal: true }).decode(bytes.subarray(0, bytesRead), { stream: bytesRead < information.size });
        } catch (error) {
          if (!(error instanceof TypeError)) throw error;
        } finally { await handle.close(); }
      }
      return preview(file, { path: relativePath, content, isBinary: content == null, sizeBytes: information.size, truncated: information.size > 256 * 1024 });
    },
    'run-files:open': (id, path = '') => openPath(shell, runPath(id, path)),
    'run-files:download': (id, path) => copyToDialog(runPath(id, path)),
    'workspace:pickDirectory': async () => {
      const selection = await dialog.showOpenDialog(window(), { properties: ['openDirectory'] });
      return selection.canceled ? null : selection.filePaths[0];
    },
    'workspace:open': (id, target) => {
      const directory = storage.resolveFile('workspaces', id, '');
      if (target !== 'editor') return openPath(shell, directory);
      const executable = executablePath('code');
      if (!executable) throw new Error('The VS Code CLI was not found.');
      const child = launch(executable, [directory], { cwd: directory, stdio: 'ignore' });
      return new Promise((resolve, reject) => {
        child.once('error', reject);
        child.once('spawn', () => { child.unref(); resolve(true); });
      });
    },
    'agent:start': (id, params = {}) => {
      if (!storage.get('agents', id, params.catalogId)) throw new Error('Agent not found.');
      return startRun(id, params);
    },
    'connector:start': (id, params = {}) => {
      if (!storage.get('tools', id)?.modelBacked) throw new Error('This connector runs inside a workflow.');
      return startRun(id, params);
    },
    'connector:saveExecution': (id, execution) => {
      const connector = storage.setConnectorExecution(id, execution);
      emit({ type: 'catalog:changed', kind: 'tools' });
      return connector;
    },
    'agent:run': (id) => agents.getRun(id),
    'agent:stop': (id) => agents.stop(id),
    'agent:guide': (id, text) => agents.addHint(id, text),
    'workflow:start': (id, input) => workflows.run(id, input),
    'workflow:run': (id) => workflows.getRun(id),
    'workflow:approve': (id, node, decision) => workflows.approve(id, node, decision),
    'workflow:stop': (id) => workflows.stop(id),
    'workflow:rerun': (id, node, text) => workflows.rerun(id, node, text),
    'workflow:guide': (id, text) => workflows.addHint(id, text),
    'console:start': (options) => consoles.start({ ...options, title: options?.title || options?.label }),
    'console:snapshot': (id) => consoles.snapshot(id),
    'console:write': (id, data) => consoles.write(id, data),
    'console:resize': (id, cols, rows) => consoles.resize(id, cols, rows),
    'console:stop': (id) => consoles.stop(id),
    'console:remove': (id) => consoles.remove(id),
    'builder:start': (kind, params) => builders.start(kind, params),
    'builder:get': (id) => builders.get(id),
    'builder:list': (kind) => builders.list(kind),
    'builder:message': (id, text) => builders.message(id, text),
    'builder:promote': (id, options) => builders.promote(id, options),
    'settings:save': saveSettings,
    'models:list': modelsFor,
    'settings:auth': (providerId) => consoles.start({ providerId, auth: true }),
    'settings:importMcp': () => mcp.importGlobals(),
    'storage:cleanup': async () => ({ removed: await sweepScratch({ storage, builders, retentionMs: 0 }) }),
    'notifications:read': (id) => notifications.read(id),
    'notifications:openSource': (id) => {
      const item = notifications.list().find(notification => notification.id === id);
      if (!item?.sourcePath) throw new Error('The notification has no source folder.');
      return openPath(shell, item.sourcePath);
    },
    'history:clear': () => {
      agents.clearHistory();
      workflows.clearHistory();
      return true;
    },
    'shell:openExternal': (value) => {
      const url = new URL(value);
      if (!['https:', 'http:', 'mailto:'].includes(url.protocol)) throw new Error('Unsupported link.');
      return shell.openExternal(url.href);
    },
  };
}

module.exports = { createActions, createNotifications };
