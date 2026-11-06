const fs = require('node:fs');
const path = require('node:path');
const { randomUUID } = require('node:crypto');
const { homedir } = require('node:os');
const { DatabaseSync } = require('node:sqlite');
const { parse: parseJsonc } = require('jsonc-parser');
const YAML = require('yaml');
const Zip = require('adm-zip');
const { createShims } = require('./shims.cjs');
const { createManagedFiles, connectorIds } = require('./managed-files.cjs');
const { normalizeExecution } = require('./execution.cjs');

const catalogKinds = ['agents', 'skills', 'workflows', 'libraries'];
const manifests = { skills: 'manifest.json', tools: 'manifest.json', workflows: 'workflow.json', libraries: 'library.json', workspaces: 'workspace.json' };
const catalogSetting = 'doxie.catalog.customRoots';
const promptExecutionSetting = 'doxie.connector.prompt.execution';
const generatedContext = '<!-- DoxieOS: auto-generated from workspace.json mountedLibraries -->';
const maxPreview = 256 * 1024;
const maxImage = 4 * 1024 * 1024;
const maxArchive = 64 * 1024 * 1024;
const maxEntries = 4096;
const imageTypes = { '.png': 'image/png', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.gif': 'image/gif',
  '.webp': 'image/webp', '.bmp': 'image/bmp', '.ico': 'image/x-icon', '.avif': 'image/avif' };
const idPattern = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const title = id => id.replace(/[-_]+/g, ' ').replace(/\b\p{L}/gu, char => char.toUpperCase());
const unix = file => file.split(path.sep).join('/');
const json = data => `${JSON.stringify(data, null, 2)}\n`;
const object = value => value && typeof value === 'object' && !Array.isArray(value);
const isAgentKind = kind => kind === 'agents' || kind === 'skills';
const internalAgents = new Set(['agent-builder', 'workflow-builder']);
const retiredAgentId = 'workflow-output-contract-designer';
const retiredDefaultAgent = (catalog, id) => catalog.isDefault && id.toLowerCase() === retiredAgentId;

function stat(file) {
  return fs.lstatSync(file, { throwIfNoEntry: false });
}

function segments(relative) {
  if (typeof relative !== 'string') throw new Error('A relative file path is required');
  if (relative === '') return [];
  const parts = relative.replace(/\\/g, '/').split('/');
  for (const part of parts) {
    if (!part || /[<>:"|?*\x00-\x1f]/u.test(part) || /[. ]$/.test(part) || /^(con|prn|aux|nul|com[1-9¹²³]|lpt[1-9¹²³])(?:\.|$)/i.test(part)) {
      throw new Error('Unsafe relative file path');
    }
  }
  return parts;
}

function safePath(root, relative = '') {
  const parts = segments(relative);
  const base = stat(root);
  if (base && !base.isDirectory()) throw new Error('Invalid storage root');
  let target = root;
  for (const part of parts) {
    target = path.join(target, part);
    if (stat(target)?.isSymbolicLink()) throw new Error('Symbolic links are unavailable in the file browser');
  }
  return target;
}

function oneSegment(id) {
  if (segments(id).length !== 1) throw new Error('Invalid item identifier');
  return id;
}

function validateId(id) {
  oneSegment(id);
  if (!idPattern.test(id)) throw new Error('The item identifier must be kebab-case');
  return id;
}

function children(dir) {
  if (!stat(dir)?.isDirectory()) return [];
  return fs.readdirSync(dir, { withFileTypes: true }).filter(entry => entry.isFile() || entry.isDirectory());
}

function readText(file) {
  if (!stat(file)?.isFile()) return null;
  return fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, '');
}

function readObject(raw) {
  if (!raw) return null;
  const errors = [];
  const data = parseJsonc(raw.replace(/^\uFEFF/, ''), errors, { allowTrailingComma: true });
  return errors.length === 0 && object(data) ? data : null;
}

function atomicWrite(file, data) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const temporary = `${file}.doxie-${randomUUID()}.tmp`;
  try {
    fs.writeFileSync(temporary, data, { flag: 'wx' });
    fs.renameSync(temporary, file);
  } finally {
    fs.rmSync(temporary, { force: true });
  }
}

function writeIfChanged(file, data) {
  const bytes = Buffer.from(data);
  if (stat(file)?.isFile() && fs.readFileSync(file).equals(bytes)) return false;
  atomicWrite(file, bytes);
  return true;
}

function parseMarkdown(text = '') {
  const front = /^\uFEFF?---[ \t]*\r?\n([\s\S]*?)\r?\n---[ \t]*(?:\r?\n|$)/.exec(text);
  if (!front) return { fields: {}, body: text };
  let fields;
  try { fields = YAML.parse(front[1], { maxAliasCount: 10 }); } catch { fields = {}; }
  return { fields: object(fields) ? fields : {}, body: text.slice(front[0].length).replace(/^(?:\r?\n)+/, '') };
}

function formatMarkdown(fields, body) {
  return `---\n${YAML.stringify(fields)}---\n\n${body}`;
}

function field(source, key) {
  const actual = Object.keys(source).find(name => name.toLowerCase() === key.toLowerCase());
  return actual === undefined ? undefined : source[actual];
}

function filesUnder(dir, relative = '') {
  const folder = safePath(dir, relative);
  return children(folder).flatMap(entry => {
    const name = relative ? `${relative}/${entry.name}` : entry.name;
    return entry.isDirectory() ? filesUnder(dir, name) : [name];
  }).sort();
}

function folderEntries(dir) {
  let size = 0;
  const names = filesUnder(dir);
  if (names.length > maxEntries) throw new Error('Too many files to bundle');
  return new Map(names.map(name => {
    const file = safePath(dir, name);
    size += stat(file)?.size ?? 0;
    if (size > maxArchive) throw new Error('Files exceed the 64 MB bundle limit');
    return [name, fs.readFileSync(file)];
  }));
}

function mountIds(value) {
  const unique = new Map();
  for (const id of Array.isArray(value) ? value : []) {
    if (typeof id === 'string' && id.trim() && !unique.has(id.trim().toLowerCase())) unique.set(id.trim().toLowerCase(), id.trim());
  }
  return [...unique.values()];
}

function memoryFiles(dir) {
  return filesUnder(dir).filter(relative => /^(?:(?:memory|memories)\/.*\/?)?[^/]+\.md$/i.test(relative)
    && !/^(memory|readme)\.md$/i.test(path.basename(relative)));
}

function memoryEntry(dir, relativePath) {
  const fileName = path.basename(relativePath);
  const { fields, body } = parseMarkdown(readText(safePath(dir, relativePath)) ?? '');
  return { id: relativePath, fileName, relativePath, name: fields.name ?? fields.title ?? fileName,
    description: fields.description ?? fields.summary ?? '', type: fields.type ?? 'Unknown', content: body,
    priority: fields.priority ?? 'Medium', condition: fields.condition ?? null };
}

function optionsPath(root, value) {
  const localData = process.platform === 'darwin' ? path.join(homedir(), 'Library', 'Application Support')
    : process.env.XDG_DATA_HOME ?? path.join(homedir(), '.local', 'share');
  const expanded = value.replace(/%([^%]+)%/g, (token, name) => {
    if (process.env[name] !== undefined) return process.env[name];
    if (name.toUpperCase() === 'USERPROFILE') return homedir();
    if (name.toUpperCase() === 'LOCALAPPDATA') return localData;
    return token;
  }).replace(/^~[/\\]/, `${homedir()}${path.sep}`);
  return path.resolve(root, expanded);
}

function readZip(input, budget = { bytes: 0, files: 0 }) {
  if (!(input instanceof Uint8Array) || input.byteLength > maxArchive) throw new Error('Invalid ZIP or archive exceeds 64 MB');
  const entries = new Map();
  const names = new Set();
  for (const entry of new Zip(Buffer.from(input)).getEntries()) {
    const name = entry.entryName.replace(/\\/g, '/').replace(/\/$/, '');
    if (!segments(name).length || names.has(name.toLowerCase()) || (entry.attr >>> 16 & 0o170000) === 0o120000) throw new Error('Unsafe ZIP entry');
    names.add(name.toLowerCase());
    budget.files++;
    budget.bytes += entry.header.size;
    if (budget.files > maxEntries || budget.bytes > maxArchive) throw new Error('ZIP exceeds the extraction limits');
    if (!entry.isDirectory) {
      const data = entry.getData();
      if (data.length !== entry.header.size) throw new Error('ZIP entry size does not match its header');
      entries.set(name, data);
    }
  }
  const fileNames = new Set([...entries.keys()].map(name => name.toLowerCase()));
  for (const file of fileNames) {
    const parts = file.split('/');
    for (let index = 1; index < parts.length; index++) {
      if (fileNames.has(parts.slice(0, index).join('/'))) throw new Error('ZIP file conflicts with a folder');
    }
  }
  return entries;
}

function writeZip(entries) {
  const archive = new Zip();
  let size = 0;
  for (const [name, bytes] of entries) {
    size += bytes.length;
    if (size > maxArchive || archive.getEntryCount() >= maxEntries) throw new Error('Files exceed the ZIP bundle limit');
    archive.addFile(name, bytes);
  }
  return archive.toBuffer();
}

function legacySkill(files, id) {
  const sidecar = files.get('orchestrator.json');
  const metadata = readObject(sidecar?.toString());
  if (sidecar && !metadata) throw new Error('Invalid orchestrator.json');
  const { fields, body } = parseMarkdown(files.get('SKILL.md').toString());
  const manifest = { ...metadata, id, description: metadata?.description ?? fields.description ?? id };
  if (!manifest.displayName && fields.name && fields.name !== id) manifest.displayName = fields.name;
  files.set('manifest.json', Buffer.from(json(manifest)));
  files.set('body.md', Buffer.from(body));
  files.delete('SKILL.md');
  files.delete('orchestrator.json');
  files.delete('doxie/generated');
}

function parseAgentZip(entries, withSubagent = true) {
  const folders = new Set([...entries.keys()].filter(name => name.includes('/')).map(name => name.split('/')[0]));
  if (folders.size !== 1) throw new Error('Agent ZIP must contain a single skill folder');
  const [id] = folders;
  validateId(id);
  const files = new Map();
  let agent = null;
  for (const [name, content] of entries) {
    if (name.startsWith(`${id}/`)) files.set(name.slice(id.length + 1), content);
    else if (withSubagent && name === `${id}.md`) agent = content;
    else throw new Error('Unexpected agent ZIP entry');
  }
  if (!files.has('manifest.json') && files.has('SKILL.md') && files.has('orchestrator.json')) legacySkill(files, id);
  const manifest = readObject(files.get('manifest.json')?.toString());
  if (!manifest || field(manifest, 'id') !== id || !files.has('body.md')) throw new Error('Agent ZIP requires a matching manifest.json and body.md');
  if (files.has('agent.md')) {
    const embedded = files.get('agent.md');
    if (agent && !agent.equals(embedded)) throw new Error('Agent ZIP contains conflicting provider definitions');
    if (withSubagent) agent = embedded;
    files.delete('agent.md');
  }
  return { id, files, agent };
}

function entriesEqual(a, b) {
  return a.size === b.size && [...a].every(([name, value]) => b.has(name) && value.equals(b.get(name)));
}

function archiveFolder(entries, prefix) {
  return new Map([...entries].filter(([name]) => name.startsWith(prefix)).map(([name, content]) => [name.slice(prefix.length), content]));
}

function stageChanges(changes) {
  const stages = [];
  try {
    for (const { target, files, bytes } of changes) {
      const stage = { target, temporary: `${target}.doxie-import-${randomUUID()}`, backup: `${target}.doxie-backup-${randomUUID()}`,
        movedOld: false, installed: false, remove: !files && !bytes };
      stages.push(stage);
      if (files) {
        fs.mkdirSync(stage.temporary, { recursive: true });
        for (const [relative, content] of files) atomicWrite(safePath(stage.temporary, relative), content);
      } else if (bytes) atomicWrite(stage.temporary, bytes);
    }
    for (const stage of stages) {
      if (stat(stage.target)) {
        fs.renameSync(stage.target, stage.backup);
        stage.movedOld = true;
      }
      if (!stage.remove) {
        fs.renameSync(stage.temporary, stage.target);
        stage.installed = true;
      }
    }
  } catch (error) {
    for (const stage of stages.reverse()) {
      if (stage.installed) fs.rmSync(stage.target, { force: true, recursive: true });
      if (stage.movedOld) fs.renameSync(stage.backup, stage.target);
    }
    throw error;
  } finally {
    for (const stage of stages) fs.rmSync(stage.temporary, { force: true, recursive: true });
  }
  for (const stage of stages) fs.rmSync(stage.backup, { force: true, recursive: true });
}

function createTransfers({ root, roots, get, directory, selectCatalog, listCatalogs, refreshWorkspaceContexts, updateWorkspaceContext, regenerate }) {
  function exportAgent(id, catalogId, includeSubagent = true) {
    const skill = get('skills', id, catalogId);
    if (!skill) throw new Error(`Agent ${id} was not found`);
    const files = folderEntries(skill.path);
    files.delete('agent.md');
    const entries = new Map([...files].map(([name, content]) => [`${id}/${name}`, content]));
    const agent = includeSubagent && get('agents', id, skill.catalogId);
    if (agent?.agentPath) entries.set(`${id}.md`, fs.readFileSync(agent.agentPath));
    return writeZip(entries);
  }

  function exportItem(kind, id, catalogId, { includeAgents = true } = {}) {
    validateId(id);
    if (kind === 'agents' || kind === 'skills') return exportAgent(id, catalogId, kind === 'agents');
    if (!['workspaces', 'workflows', 'libraries'].includes(kind)) throw new Error('Unsupported export type');
    const item = get(kind, id, catalogId);
    if (!item) throw new Error(`Item ${id} was not found`);
    const singular = kind.slice(0, -1);
    const prefix = kind === 'libraries' ? `${id}/` : `${singular}/${id}/`;
    const entries = new Map([...folderEntries(item.path)].map(([name, content]) => [prefix + name, content]));
    if (kind === 'libraries') {
      if (entries.size === 0) entries.set(`${id}/library.json`, Buffer.from(json({ name: item.name, description: item.description })));
      return writeZip(entries);
    }
    if (kind === 'workspaces' && !entries.has(`${prefix}workspace.json`)) {
      entries.set(`${prefix}workspace.json`, Buffer.from(json({ Name: item.name, Description: item.description, CreatedAt: item.createdAt, MountedLibraries: item.mountedLibraryIds, MountedAgents: item.mountedAgentIds })));
    }
    const agentIds = kind === 'workspaces' ? item.mountedAgentIds : item.nodes.map(node => field(node, 'agentId'));
    const agents = [];
    for (const agentId of includeAgents ? mountIds(agentIds) : []) {
      if (!idPattern.test(agentId) || !get('skills', agentId)) continue;
      const file = `agents/${agentId}.zip`;
      agents.push({ id: agentId, path: file });
      entries.set(file, exportAgent(agentId));
    }
    entries.set(`doxie-${singular}-export.json`, Buffer.from(json({ format: `doxie.${singular}.export.v1`, [`${singular}Id`]: id,
      exportedAt: new Date().toISOString(), agents })));
    return writeZip(entries);
  }

  function decideAgent({ id, files, agent }, catalog, overwrite, withSubagent = true) {
    if (connectorIds.has(id)) throw new Error(`${id} is a built-in connector identifier`);
    const matches = listCatalogs().map(candidate => get('agents', id, candidate.id)).filter(Boolean);
    for (const match of matches) {
      const oldSkill = get('skills', id, match.catalogId);
      const oldAgent = withSubagent && match.agentPath ? fs.readFileSync(match.agentPath) : null;
      const existing = oldSkill && folderEntries(oldSkill.path);
      existing?.delete('agent.md');
      if (existing && entriesEqual(existing, files) && (oldAgent ? agent?.equals(oldAgent) : !agent)) return [];
    }
    if (matches.length && !overwrite) throw new Error(`Agent ${id} already exists with different contents`);
    const destination = matches.find(match => match.catalogId === catalog.id) ?? matches[0];
    const selected = destination ? selectCatalog(destination.catalogId) : catalog;
    if (retiredDefaultAgent(selected, id)) throw new Error('This bundled agent has been retired from the default catalog');
    const nextFiles = new Map(files);
    if (agent) nextFiles.set('agent.md', agent);
    else if (!withSubagent && destination?.agentPath) nextFiles.set('agent.md', fs.readFileSync(destination.agentPath));
    const changes = [{ target: safePath(directory(selected, 'agents'), id), files: nextFiles }];
    const legacyCompanion = safePath(directory(selected, 'agents'), `${id}.md`);
    if (withSubagent && stat(legacyCompanion)?.isFile()) changes.push({ target: legacyCompanion });
    return changes;
  }

  function importItem(kind, archive, { catalogId, overwrite = false, id: flatLibraryId } = {}) {
    const catalog = selectCatalog(catalogId);
    const budget = { bytes: 0, files: 0 };
    const entries = readZip(archive, budget);
    if (kind === 'agents' || kind === 'skills') {
      const imported = parseAgentZip(entries, kind === 'agents');
      stageChanges(decideAgent(imported, catalog, overwrite, kind === 'agents'));
      regenerate();
      return get(kind, imported.id);
    }
    if (!['workspaces', 'workflows', 'libraries'].includes(kind)) throw new Error('Unsupported import type');
    let id;
    let files;
    const changes = [];
    if (kind === 'libraries') {
      const folders = new Set([...entries.keys()].filter(name => name.includes('/')).map(name => name.split('/')[0]));
      if (folders.size > 1 || !entries.size) throw new Error('Library ZIP must contain a single library folder');
      id = validateId([...folders][0] ?? flatLibraryId);
      files = folders.size ? archiveFolder(entries, `${id}/`) : entries;
      if (folders.size && files.size !== entries.size) throw new Error('Unexpected library ZIP entry');
    } else {
      const singular = kind.slice(0, -1);
      const bundleName = `doxie-${singular}-export.json`;
      const bundle = readObject(entries.get(bundleName)?.toString());
      if (bundle?.format !== `doxie.${singular}.export.v1`) throw new Error(`Invalid ${singular} ZIP manifest`);
      id = validateId(bundle[`${singular}Id`]);
      const prefix = `${singular}/${id}/`;
      files = archiveFolder(entries, prefix);
      const manifest = readObject(files.get(manifests[kind])?.toString());
      if (!manifest || (kind === 'workflows' && field(manifest, 'id') !== id)) throw new Error(`Missing or invalid ${manifests[kind]}`);
      const recognized = new Set([bundleName, ...[...files.keys()].map(name => prefix + name)]);
      const included = [];
      for (const dependency of Array.isArray(bundle.agents) ? bundle.agents : []) {
        const agentId = validateId(dependency?.id);
        const file = `agents/${agentId}.zip`;
        if (dependency.path !== file || recognized.has(file) || !entries.has(file)) throw new Error('Invalid ZIP agent dependency');
        const imported = parseAgentZip(readZip(entries.get(file), budget));
        if (agentId !== imported.id) throw new Error('Bundled agent identifier does not match its manifest');
        recognized.add(file);
        if (!connectorIds.has(agentId) && !retiredDefaultAgent(catalog, agentId)) {
          included.push(agentId);
          changes.push(...decideAgent(imported, catalog, overwrite));
        }
      }
      if (recognized.size !== entries.size) throw new Error('Unexpected ZIP entry');
      if (kind === 'workspaces') {
        const oldIds = mountIds(field(manifest, 'mountedAgents') ?? field(manifest, 'mountedAgentIds'));
        if (included.some(agentId => !oldIds.includes(agentId))) {
          for (const key of Object.keys(manifest)) if (/^mountedAgent(?:s|Ids)$/i.test(key)) delete manifest[key];
          manifest.MountedAgents = mountIds([...oldIds, ...included]);
          files.set(manifests[kind], Buffer.from(json(manifest)));
        }
      }
    }
    const existing = get(kind, id);
    const selected = existing ? selectCatalog(existing.catalogId) : catalog;
    const target = safePath(directory(selected, kind), id);
    if (!stat(target) || !entriesEqual(folderEntries(target), files)) {
      if (stat(target) && !overwrite) throw new Error(`Item ${id} already exists with different contents`);
      changes.push({ target, files });
    }
    stageChanges(changes);
    const result = get(kind, id);
    if (kind === 'workspaces') updateWorkspaceContext(result);
    if (kind === 'libraries') refreshWorkspaceContexts(id);
    regenerate();
    return result;
  }

  function legacyEntries() {
    const base = { skills: '.claude/skills', agents: '.claude/agents', libraries: '.claude/libraries', workflows: '.workflows' };
    const result = [];
    for (const [kind, relative] of Object.entries(base)) {
      const source = safePath(root, relative);
      for (const entry of children(source)) {
        const id = kind === 'agents' && entry.isFile() && entry.name.endsWith('.md') ? entry.name.slice(0, -3) : entry.name;
        if (!idPattern.test(id) || (isAgentKind(kind) && (connectorIds.has(id) || id === retiredAgentId))) continue;
        const from = safePath(source, entry.name);
        const target = safePath(roots[kind], isAgentKind(kind) ? id : entry.name);
        if (kind === 'agents') {
          if (entry.isFile() && entry.name.endsWith('.md') && !get('agents', id)?.agentPath) result.push({ kind, id, path: from, target });
        } else if (entry.isDirectory()) {
          if (kind === 'skills' && (get('skills', id) || !stat(safePath(from, 'SKILL.md'))?.isFile() || stat(safePath(from, 'doxie/generated')))) continue;
          if (kind !== 'skills' && stat(target)) continue;
          if (kind === 'workflows' && !stat(safePath(from, 'workflow.json'))?.isFile()) continue;
          result.push({ kind, id, path: from, target });
        }
      }
    }
    return result;
  }

  function detectLegacy() {
    return legacyEntries().map(({ kind, id, path }) => ({ kind, id, path }));
  }

  function migrateLegacy() {
    const legacy = legacyEntries();
    const changes = new Map();
    for (const { kind, id, path, target } of legacy) {
      const existing = changes.get(target) ?? folderEntries(target);
      if (kind === 'agents') existing.set('agent.md', fs.readFileSync(path));
      else {
        const files = folderEntries(path);
        if (kind === 'skills') legacySkill(files, id);
        for (const [file, content] of files) if (!existing.has(file)) existing.set(file, content);
      }
      changes.set(target, existing);
    }
    stageChanges([...changes].map(([target, files]) => ({ target, files })));
    regenerate();
    return legacy.map(({ kind, id }) => ({ kind, id, migrated: true }));
  }

  return { exportItem, importItem, detectLegacy, migrateLegacy };
}

function createStorage({ root, databasePath, bundledRoot, bundledFiles, workspacesRoot, settings = {}, managed = false, legacyRoot }) {
  root = path.resolve(root);
  const defaultCatalog = { id: 'default', name: 'Default catalog', path: safePath(root, '.doxie'), isDefault: true };
  const roots = Object.fromEntries(catalogKinds.map(kind => [kind, path.join(defaultCatalog.path, kind)]));
  roots.skills = roots.agents;
  roots.tools = safePath(root, '.doxie/tools');
  roots.workspaces = managed ? safePath(root, '.workspace') : optionsPath(root, workspacesRoot ?? '.workspace');
  roots.runs = safePath(root, '.runs');
  roots.sandboxes = safePath(root, '.sandbox');
  for (const dir of Object.values(roots)) {
    safePath(dir);
    fs.mkdirSync(dir, { recursive: true });
  }
  const managedFiles = createManagedFiles({ root, safePath, stat });
  if (bundledRoot) {
    const source = path.basename(bundledRoot) === '.doxie' ? path.resolve(bundledRoot) : path.resolve(bundledRoot, '.doxie');
    managedFiles.seed(source, bundledFiles);
  }
  function retireBundledAgent() {
    try { managedFiles.retireBundledAgent(retiredAgentId); }
    catch (error) { console.error('Could not archive the retired DoxieOS agent:', error.message); }
  }
  if (managed) retireBundledAgent();

  databasePath = databasePath ?? path.join(root, 'state.db');
  if (databasePath !== ':memory:') fs.mkdirSync(path.dirname(databasePath), { recursive: true });
  const db = new DatabaseSync(databasePath);
  db.exec(`PRAGMA busy_timeout = 5000;
    CREATE TABLE IF NOT EXISTS key_value_settings (
      key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL, updated_at TEXT NOT NULL
    );`);
  const readSetting = db.prepare('SELECT value FROM key_value_settings WHERE key = ?');
  const writeSetting = db.prepare(`INSERT INTO key_value_settings (key, value, updated_at) VALUES (?, ?, ?)
    ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at`);
  const getSetting = key => readSetting.get(key)?.value ?? null;
  const setSetting = (key, value) => writeSetting.run(key, typeof value === 'string' ? value : JSON.stringify(value), new Date().toISOString());
  for (const [key, value] of Object.entries(settings)) if (getSetting(key) === null) setSetting(key, value);
  const catalogWatches = new Set();

  function normalizeCatalogs(items, base = root) {
    const results = [];
    const ids = new Set(['default']);
    const paths = new Set([process.platform === 'win32' ? defaultCatalog.path.toLowerCase() : defaultCatalog.path]);
    for (const item of Array.isArray(items) ? items : []) {
      if (!object(item)) continue;
      const suppliedPath = field(item, 'path');
      if (typeof suppliedPath !== 'string' || !suppliedPath.trim() || field(item, 'isDefault')) continue;
      const catalogPath = optionsPath(base, suppliedPath.trim());
      const pathKey = process.platform === 'win32' ? catalogPath.toLowerCase() : catalogPath;
      if (paths.has(pathKey)) continue;
      paths.add(pathKey);
      const name = String(field(item, 'name') || path.basename(catalogPath) || 'Custom catalog').trim();
      const slug = String(field(item, 'id') || name).toLowerCase().replace(/[^\p{L}\p{N}]+/gu, '-').replace(/^-|-$/g, '') || 'catalog';
      let id = slug;
      for (let suffix = 2; ids.has(id); suffix++) id = `${slug}-${suffix}`;
      ids.add(id);
      results.push({ id, name, path: catalogPath, isDefault: false });
    }
    return results;
  }

  function configuredCatalogs(base) {
    let items;
    try { items = JSON.parse(getSetting(catalogSetting)); } catch { items = []; }
    return normalizeCatalogs(items, base);
  }

  function listCatalogs() {
    const catalogs = configuredCatalogs();
    return [defaultCatalog, ...catalogs.map(catalog => managed ? { ...catalog, path: managedFiles.catalogRoot(catalog.id) } : catalog)];
  }

  function prepareCatalog(catalog) {
    if (!managed) return catalog;
    const destination = managedFiles.catalogRoot(catalog.id);
    if (!managedFiles.importCatalog(catalog.path, catalog.id)) return null;
    return { ...catalog, path: destination };
  }

  function saveCatalogSetting(catalogs) {
    setSetting(catalogSetting, catalogs.map(({ id, name, path }) => ({ id, name, path })));
  }

  function saveCatalogs(items) {
    const catalogs = normalizeCatalogs(items).map(catalog => {
      const destination = prepareCatalog(catalog);
      if (!destination) throw new Error('Select an existing catalog folder');
      return destination;
    });
    for (const catalog of catalogs) {
      for (const kind of ['agents', 'workflows', 'libraries']) fs.mkdirSync(safePath(catalog.path, kind), { recursive: true });
    }
    saveCatalogSetting(catalogs);
    regenerate();
    for (const watch of catalogWatches) watch.sync();
    return listCatalogs();
  }

  function directory(catalog, kind) {
    if (['workspaces', 'runs', 'sandboxes', 'tools'].includes(kind)) return managed ? safePath(root, unix(path.relative(root, roots[kind]))) : roots[kind];
    if (!catalogKinds.includes(kind) && kind !== 'legacySkills') throw new Error(`Unknown storage type: ${kind}`);
    const folder = kind === 'legacySkills' ? 'skills' : isAgentKind(kind) ? 'agents' : kind;
    if (managed) return safePath(root, catalog.isDefault ? `.doxie/${folder}` : `.doxie/catalogs/${catalog.id}/${folder}`);
    return safePath(catalog.path, folder);
  }

  function selectCatalog(id) {
    const catalog = listCatalogs().find(candidate => candidate.id.toLowerCase() === String(id || 'default').toLowerCase());
    if (!catalog) throw new Error(`Unknown catalog: ${id}`);
    return catalog;
  }

  function load(kind, id, catalog, allowLegacyPrompt = false) {
    oneSegment(id);
    if (isAgentKind(kind)) {
      if (retiredDefaultAgent(catalog, id)) return null;
      if (connectorIds.has(id.toLowerCase()) && !(allowLegacyPrompt && id.toLowerCase() === 'prompt')) return null;
      const canonical = safePath(directory(catalog, 'agents'), id);
      const legacy = safePath(directory(catalog, 'legacySkills'), id);
      const agentCandidates = [canonical, legacy].filter(folder => stat(folder)?.isDirectory());
      const companion = [safePath(canonical, 'agent.md'), safePath(directory(catalog, 'agents'), `${id}.md`)]
        .find(file => stat(file)?.isFile());
      for (const folder of agentCandidates) {
        const metadata = readObject(readText(safePath(folder, 'manifest.json')));
        const body = readText(safePath(folder, 'body.md'));
        if (!metadata || body === null) continue;
        const memories = children(safePath(folder, 'memories')).filter(entry => entry.isFile() && /\.md$/i.test(entry.name))
          .map(entry => memoryEntry(folder, `memories/${entry.name}`));
        const { fields, body: systemPrompt } = parseMarkdown(companion ? readText(companion) : '');
        return { ...metadata, id, path: folder, catalogId: catalog.id, catalogName: catalog.name, catalogRoot: catalog.path,
          name: field(metadata, 'name') || field(metadata, 'displayName') || title(id), body, memories,
          description: field(metadata, 'description') || field(fields, 'description') || '',
          modes: field(metadata, 'modes') ?? {}, requirements: field(metadata, 'requirements') ?? [],
          category: field(metadata, 'category') ?? 'Other', icon: field(metadata, 'icon') ?? null,
          ...(kind === 'agents' ? { skillName: id, agentPath: companion ?? null, systemPrompt,
            tools: field(fields, 'tools') ?? null, model: field(fields, 'model') ?? field(metadata, 'model') ?? null,
            isolation: field(fields, 'isolation') ?? null } : {}) };
      }
      if (kind !== 'agents' || !companion) return null;
      const { fields, body } = parseMarkdown(readText(companion));
      return { id, name: field(fields, 'name') || title(id), description: field(fields, 'description') ?? '',
        path: companion, catalogId: catalog.id, catalogName: catalog.name, catalogRoot: catalog.path,
        skillName: null, agentPath: companion, systemPrompt: body, body, tools: field(fields, 'tools') ?? null,
        model: field(fields, 'model') ?? null, isolation: field(fields, 'isolation') ?? null };
    }
    const itemDir = safePath(directory(catalog, kind), id);
    const info = { id, path: itemDir, catalogId: catalog.id, catalogName: catalog.name, catalogRoot: catalog.path };
    const dirStat = stat(itemDir);
    if (!dirStat?.isDirectory()) return null;
    if (!manifests[kind]) return { ...info, name: id, createdAt: dirStat.birthtime.toISOString() };
    const metadata = readObject(readText(safePath(itemDir, manifests[kind]))) ?? {};
    const name = field(metadata, 'name') || field(metadata, 'displayName') || title(id);
    const description = field(metadata, 'description') ?? '';
    const base = { ...metadata, ...info, name, description };
    if (kind === 'tools') {
      const body = readText(safePath(itemDir, 'body.md'));
      if (!connectorIds.has(id) || !Object.keys(metadata).length) return null;
      const result = { ...base, body: body ?? '', modes: field(metadata, 'modes') ?? {} };
      if (id === 'prompt') {
        return { ...result, modelBacked: true,
          execution: promptExecution(() => field(metadata, 'execution') ?? load('agents', id, catalog, true)?.execution) };
      }
      return result;
    }
    if (kind === 'workflows') {
      if (!Object.keys(metadata).length) return null;
      return { ...base, enabled: field(metadata, 'enabled') !== false, category: field(metadata, 'category') || 'Other',
        trigger: field(metadata, 'trigger') ?? { kind: 'manual' }, nodes: field(metadata, 'nodes') ?? [], edges: field(metadata, 'edges') ?? [],
        workspaceId: field(metadata, 'workspaceId') ?? null, env: field(metadata, 'env') ?? {}, createdAt: field(metadata, 'createdAt'), updatedAt: field(metadata, 'updatedAt') };
    }
    if (kind === 'workspaces') {
      return { ...base, createdAt: field(metadata, 'createdAt') ?? dirStat.birthtime.toISOString(),
        mountedLibraryIds: mountIds(field(metadata, 'mountedLibraryIds') ?? field(metadata, 'mountedLibraries')),
        mountedAgentIds: mountIds(field(metadata, 'mountedAgentIds') ?? field(metadata, 'mountedAgents')) };
    }
    return { ...base, memories: memoryFiles(itemDir).map(relative => memoryEntry(itemDir, relative)),
      files: filesUnder(itemDir).map(relativePath => ({ relativePath, name: path.basename(relativePath) })) };
  }

  function list(kind) {
    const items = new Map();
    const catalogs = catalogKinds.includes(kind) ? listCatalogs() : [defaultCatalog];
    for (const catalog of catalogs) {
      const dir = directory(catalog, kind);
      const ids = new Set();
      if (isAgentKind(kind)) {
        for (const entry of children(dir)) {
          if (entry.isDirectory() && !entry.name.startsWith('.')) ids.add(entry.name);
          if (kind === 'agents' && entry.isFile() && /\.md$/i.test(entry.name)) ids.add(entry.name.slice(0, -3));
        }
        for (const entry of children(directory(catalog, 'legacySkills'))) if (entry.isDirectory() && !entry.name.startsWith('.')) ids.add(entry.name);
      } else {
        for (const entry of children(dir)) if (entry.isDirectory() && !entry.name.startsWith('.')) ids.add(entry.name);
      }
      for (const id of ids) {
        if (isAgentKind(kind) && internalAgents.has(id.toLowerCase())) continue;
        let item;
        try { item = load(kind, id, catalog); } catch { continue; }
        if (item) items.set(id.toLowerCase(), item);
      }
    }
    return [...items.values()].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }));
  }

  function get(kind, id, catalogId) {
    const catalogs = !catalogKinds.includes(kind) ? [defaultCatalog] : catalogId ? [selectCatalog(catalogId)] : listCatalogs().reverse();
    for (const catalog of catalogs) {
      const result = load(kind, id, catalog);
      if (result) return result;
    }
    return null;
  }

  function getDispatchAgent(id, catalogId) {
    if (typeof id !== 'string' || id.toLowerCase() !== 'prompt') return get('agents', id, catalogId);
    const connector = get('tools', 'prompt');
    if (connector) return { ...connector, connectorId: 'prompt' };
    const legacy = load('agents', 'prompt', defaultCatalog, true);
    return legacy && { ...legacy, modelBacked: true, connectorId: 'prompt', execution: promptExecution(() => legacy.execution) };
  }

  function promptExecution(fallback) {
    const stored = getSetting(promptExecutionSetting);
    try { return normalizeExecution(stored === null ? fallback() : JSON.parse(stored)); }
    catch { return null; }
  }

  function setConnectorExecution(id, value) {
    if (id !== 'prompt') throw new Error('This connector does not support provider preferences');
    setSetting(promptExecutionSetting, normalizeExecution(value));
    return get('tools', id);
  }

  function updateWorkspaceContext(workspace) {
    const context = { 'CLAUDE.md': [], 'AGENTS.md': [] };
    for (const id of workspace.mountedLibraryIds) {
      const library = idPattern.test(id) ? get('libraries', id) : null;
      const section = [`## ${id}`, ''];
      for (const lines of Object.values(context)) lines.push(...section);
      if (!library) {
        for (const lines of Object.values(context)) lines.push(`Library \`${id}\` is unavailable.`, '');
        continue;
      }
      for (const relative of memoryFiles(library.path)) {
        const full = safePath(library.path, relative);
        context['CLAUDE.md'].push(`@${unix(path.relative(workspace.path, full))}`);
        context['AGENTS.md'].push(`### ${relative}`, '', readText(full) ?? '', '');
      }
      context['CLAUDE.md'].push('');
    }
    for (const [name, lines] of Object.entries(context)) {
      const target = safePath(workspace.path, name);
      const previous = readText(target);
      if (previous !== null && !previous.split(/\r?\n/, 1)[0].includes('DoxieOS: auto-generated')) continue;
      if (workspace.mountedLibraryIds.length === 0) {
        if (previous !== null) fs.unlinkSync(target);
      } else {
        writeIfChanged(target, [generatedContext, '', `# ${workspace.name} - workspace context`, '', ...lines].join('\n'));
      }
    }
  }

  function save(kind, data) {
    if (!object(data) || (!catalogKinds.includes(kind) && kind !== 'workspaces')) throw new Error('Invalid catalog item');
    const id = validateId(data.id);
    if (isAgentKind(kind) && connectorIds.has(id)) throw new Error(`${id} is a built-in connector identifier`);
    const previous = get(kind, id);
    const catalog = kind === 'workspaces' ? defaultCatalog : selectCatalog(data.catalogId ?? previous?.catalogId);
    if (isAgentKind(kind) && retiredDefaultAgent(catalog, id)) throw new Error('This bundled agent has been retired from the default catalog');
    const current = get(kind, id, catalog.id) ?? previous;
    const moving = Boolean(current && current.catalogId !== catalog.id);
    const sourceCatalog = moving ? selectCatalog(current.catalogId) : catalog;
    const sourceKind = isAgentKind(kind) ? 'skills' : kind;
    const itemDir = safePath(directory(catalog, sourceKind), id);
    const sourceDir = current && stat(current.path)?.isDirectory() ? current.path : itemDir;
    const oldCompanion = isAgentKind(kind) && get('agents', id, sourceCatalog.id)?.agentPath;
    const externalCompanion = oldCompanion && oldCompanion !== safePath(itemDir, 'agent.md') && !oldCompanion.startsWith(`${sourceDir}${path.sep}`);
    const manifest = safePath(sourceDir, manifests[sourceKind]);
    const existingText = readText(manifest);
    const existing = readObject(existingText);
    if (existingText !== null && !existing) throw new Error(`Invalid ${manifests[sourceKind]} for ${id}`);
    const { path: sourcePath, catalogId: ignoredCatalog, catalogName, catalogRoot, skillName, agentPath, body,
      systemPrompt, tools, model, isolation, memories, files, mountedLibraryIds, mountedAgentIds, ...fields } = data;
    const merged = { ...existing, ...fields, id };
    if (isAgentKind(kind) && Object.hasOwn(fields, 'execution')) merged.execution = normalizeExecution(fields.execution);
    if (moving && stat(itemDir)) throw new Error(`The catalog ${catalog.name} already contains the ${id} directory`);
    const staged = moving || sourceDir !== itemDir || externalCompanion ? new Map([...folderEntries(sourceDir), ...folderEntries(itemDir)]) : null;
    function writeItemFile(relative, contents) {
      if (staged) staged.set(relative, Buffer.from(contents));
      else writeIfChanged(safePath(itemDir, relative), contents);
    }
    if (sourceKind === 'skills') {
      merged.displayName = data.name ?? data.displayName ?? current?.name ?? title(id);
      merged.description = data.description ?? current?.description ?? '';
      delete merged.name;
      writeItemFile('body.md', body ?? current?.body ?? '');
      const companionChanged = kind === 'agents' && [systemPrompt, tools, model, isolation].some(value => value !== undefined);
      if (companionChanged || (staged && oldCompanion)) {
        const existingAgentText = oldCompanion ? readText(oldCompanion) : null;
        let contents = existingAgentText;
        if (companionChanged) {
          const existingAgent = parseMarkdown(existingAgentText ?? '');
          const agent = { ...existingAgent.fields, name: id, description: merged.description };
          for (const [key, value] of Object.entries({ tools, model, isolation })) {
            if (value === null || value === '') delete agent[key];
            else if (value !== undefined) agent[key] = value;
          }
          contents = formatMarkdown(agent, systemPrompt ?? existingAgent.body);
        }
        if (contents !== null) writeItemFile('agent.md', contents);
      }
    }
    if (kind === 'workflows') {
      const now = new Date().toISOString();
      merged.name ||= title(id);
      merged.description ??= '';
      merged.createdAt ??= now;
      merged.updatedAt = now;
      merged.trigger ??= { kind: 'manual' };
      merged.nodes ??= [];
      if (Object.hasOwn(fields, 'nodes')) {
        merged.nodes = merged.nodes.map(node => Object.hasOwn(node, 'execution')
          ? { ...node, execution: normalizeExecution(node.execution) } : node);
      }
      merged.edges ??= [];
      merged.category ||= 'Other';
      merged.enabled = merged.enabled !== false && (Boolean(current) || String(merged.trigger.kind).toLowerCase() !== 'cron');
      merged.catalogId = catalog.id;
    }
    if (kind === 'workspaces') {
      for (const key of Object.keys(merged)) {
        if (['name', 'description', 'createdat', 'mountedlibraries', 'mountedagents', 'mountedlibraryids', 'mountedagentids'].includes(key.toLowerCase())) delete merged[key];
      }
      merged.Name = data.name ?? current?.name ?? title(id);
      merged.Description = data.description ?? current?.description ?? '';
      merged.CreatedAt = current?.createdAt ?? new Date().toISOString();
      merged.MountedLibraries = mountIds(mountedLibraryIds ?? field(fields, 'mountedLibraries') ?? current?.mountedLibraryIds);
      merged.MountedAgents = mountIds(mountedAgentIds ?? field(fields, 'mountedAgents') ?? current?.mountedAgentIds);
      delete merged.id;
      if (!current) {
        if (managed && typeof sourcePath === 'string' && sourcePath.trim()) managedFiles.copyWorkspace(sourcePath, id);
        const notes = safePath(itemDir, 'WORKSPACE.md');
        if (!stat(notes)) atomicWrite(notes, `# Workspace ${merged.Name}\n\n${merged.Description}\n`);
        const memoryDirectory = stat(safePath(itemDir, 'memory'));
        if (!memoryDirectory || memoryDirectory.isDirectory()) {
          const memory = safePath(itemDir, 'memory/MEMORY.md');
          if (!stat(memory)) atomicWrite(memory, `# Workspace ${merged.Name} - local memory index\n`);
        }
      }
    }
    writeItemFile(manifests[sourceKind], json(merged));
    if (staged) {
      const changes = [{ target: itemDir, files: staged }];
      if (sourceDir !== itemDir) changes.push({ target: sourceDir });
      if (externalCompanion) changes.push({ target: oldCompanion });
      stageChanges(changes);
    }
    const item = get(kind, id, catalog.id);
    if (kind === 'workspaces') updateWorkspaceContext(item);
    if (kind === 'libraries') refreshWorkspaceContexts(id);
    if (catalogKinds.includes(kind)) regenerate();
    return item;
  }

  function refreshWorkspaceContexts(libraryId) {
    for (const workspace of list('workspaces')) {
      if (workspace.mountedLibraryIds.some(id => id.toLowerCase() === libraryId.toLowerCase())) updateWorkspaceContext(workspace);
    }
  }

  function createLibrary({ id, name, description = '', catalogId, memories = [] }) {
    validateId(id);
    const catalog = selectCatalog(catalogId);
    const target = safePath(directory(catalog, 'libraries'), id);
    if (get('libraries', id) || stat(target)) throw new Error(`Library ${id} already exists`);
    if (!Array.isArray(memories) || memories.length >= maxEntries) throw new Error('Invalid library memories');
    const files = new Map([['library.json', Buffer.from(json({ name: name || title(id), description }))]]);
    const paths = new Set();
    let size = files.get('library.json').length;
    for (const memory of memories) {
      if (!object(memory)) throw new Error('Invalid library memory');
      const parts = memory.relativePath === undefined ? ['memory', `${validateId(memory.id)}.md`] : segments(memory.relativePath);
      if (parts.length < 2 || !['memory', 'memories'].includes(parts[0].toLowerCase()) || !/\.md$/i.test(parts.at(-1))) {
        throw new Error('Library memory must be a Markdown file under memory/ or memories/');
      }
      const file = parts.join('/');
      const key = file.toLowerCase();
      if (paths.has(key) || (typeof memory.content !== 'string' && !Buffer.isBuffer(memory.content))) {
        throw new Error(`Invalid or duplicate library memory ${file}`);
      }
      paths.add(key);
      const content = Buffer.isBuffer(memory.content) ? memory.content : Buffer.from(memory.content);
      size += content.length;
      if (size > maxArchive) throw new Error('Library exceeds the 64 MB size limit');
      files.set(file, content);
    }
    stageChanges([{ target, files }]);
    refreshWorkspaceContexts(id);
    regenerate();
    return get('libraries', id, catalog.id);
  }

  function remove(kind, id, catalogId) {
    if (!catalogKinds.includes(kind) && kind !== 'workspaces') throw new Error('Unsupported removal type');
    const item = get(kind, id, catalogId);
    if (!item) return false;
    if (isAgentKind(kind)) {
      const catalog = selectCatalog(item.catalogId);
      const targets = [safePath(directory(catalog, 'agents'), id), safePath(directory(catalog, 'legacySkills'), id),
        safePath(directory(catalog, 'agents'), `${id}.md`)];
      stageChanges(targets.filter(target => stat(target)).map(target => ({ target })));
    } else fs.rmSync(item.path, { recursive: true, force: true });
    if (kind === 'libraries') refreshWorkspaceContexts(id);
    if (catalogKinds.includes(kind)) regenerate();
    return true;
  }

  function fileTarget(kind, id, relative, catalogId) {
    const item = get(kind, id, catalogId);
    if (!item) throw new Error(`Item ${id} was not found`);
    if (!stat(item.path)?.isDirectory()) throw new Error('This item has no file directory');
    return { item, file: safePath(item.path, relative) };
  }

  function listFiles(kind, id, relative = '', catalogId) {
    const { item, file } = fileTarget(kind, id, relative, catalogId);
    return children(file).map(entry => {
      const child = safePath(file, entry.name);
      const relativePath = unix(path.relative(item.path, child));
      return { name: entry.name, path: relativePath, relativePath, isDirectory: entry.isDirectory(),
        sizeBytes: entry.isFile() ? stat(child)?.size ?? null : null };
    }).sort((a, b) => Number(b.isDirectory) - Number(a.isDirectory) || a.name.localeCompare(b.name));
  }

  function readFile(kind, id, relative, catalogId) {
    const { item, file } = fileTarget(kind, id, relative, catalogId);
    const fileStat = stat(file);
    if (!fileStat?.isFile()) throw new Error('The requested file does not exist');
    const descriptor = fs.openSync(file, 'r');
    const bytes = Buffer.alloc(Math.min(fileStat.size, maxPreview));
    let count;
    try { count = fs.readSync(descriptor, bytes); } finally { fs.closeSync(descriptor); }
    const sample = bytes.subarray(0, count);
    let content = null;
    if (!sample.includes(0)) {
      try { content = new TextDecoder('utf-8', { fatal: true }).decode(sample, { stream: fileStat.size > count }); } catch { /* Binary file. */ }
    }
    const relativePath = unix(path.relative(item.path, file));
    const mime = imageTypes[path.extname(file).toLowerCase()];
    const dataUrl = mime && fileStat.size <= maxImage ? `data:${mime};base64,${fs.readFileSync(file).toString('base64')}` : null;
    return { name: path.basename(file), path: relativePath, relativePath, content, sizeBytes: fileStat.size,
      isBinary: Boolean(mime) || content === null, truncated: fileStat.size > count, dataUrl };
  }

  function writeFile(kind, id, relative, content, catalogId) {
    if (kind === 'tools') throw new Error('Built-in tool definitions are read-only');
    if (typeof content !== 'string' || Buffer.byteLength(content) > maxArchive) throw new Error('Invalid file contents');
    const { file } = fileTarget(kind, id, relative, catalogId);
    if (!relative || stat(file)?.isDirectory()) throw new Error('A file path is required');
    atomicWrite(file, content);
    if (kind === 'libraries') refreshWorkspaceContexts(id);
    if (catalogKinds.includes(kind)) regenerate();
    return readFile(kind, id, relative, catalogId);
  }

  function deleteFile(kind, id, relative, catalogId) {
    if (kind === 'tools') throw new Error('Built-in tool definitions are read-only');
    const { file } = fileTarget(kind, id, relative, catalogId);
    if (!relative || !stat(file)?.isFile()) return false;
    fs.unlinkSync(file);
    if (kind === 'libraries') refreshWorkspaceContexts(id);
    if (catalogKinds.includes(kind)) regenerate();
    return true;
  }

  function resolveFile(kind, id, relative, catalogId) {
    const { file } = fileTarget(kind, id, relative, catalogId);
    if (!stat(file)?.isFile() && !stat(file)?.isDirectory()) throw new Error('The requested path does not exist');
    return file;
  }

  function watchCatalog(onChange, onError = () => {}) {
    const watchers = new Map();
    let timer;
    function queue() {
      clearTimeout(timer);
      timer = setTimeout(() => {
        try {
          const result = regenerate();
          for (const workspace of list('workspaces')) updateWorkspaceContext(workspace);
          onChange(result);
        } catch (error) { onError(error); }
      }, 300);
      timer.unref();
    }
    function sync() {
      const directories = new Set(listCatalogs().flatMap(catalog => [...catalogKinds, 'legacySkills'].map(kind => directory(catalog, kind))));
      for (const [dir, watcher] of watchers) {
        if (directories.has(dir)) continue;
        watcher.close();
        watchers.delete(dir);
      }
      for (const dir of directories) {
        if (watchers.has(dir) || !stat(dir)?.isDirectory()) continue;
        try {
          const watcher = fs.watch(dir, { recursive: true, persistent: false }, queue);
          watcher.on('error', error => {
            watchers.delete(dir);
            onError(error);
          });
          watchers.set(dir, watcher);
        } catch (error) { onError(error); }
      }
    }
    const watch = { sync, stop() {
      clearTimeout(timer);
      for (const watcher of watchers.values()) watcher.close();
      catalogWatches.delete(watch);
    } };
    catalogWatches.add(watch);
    sync();
    return watch.stop;
  }

  const regenerate = createShims({ root, list, safePath, stat, filesUnder, readText, writeIfChanged });

  if (managed && getSetting(catalogSetting) !== null) {
    const configured = configuredCatalogs(legacyRoot ?? root);
    const localized = configured.map(catalog => {
      try { return prepareCatalog(catalog) ?? catalog; }
      catch (error) {
        console.error(`Could not migrate DoxieOS catalog '${catalog.id}': ${error.message}`);
        return catalog;
      }
    });
    if (localized.some((catalog, index) => catalog.path !== configured[index].path)) saveCatalogSetting(localized);
  }

  function importWorkbench(source) {
    if (!managed) throw new Error('Workbench imports require managed storage');
    const counts = managedFiles.importWorkbench(source);
    retireBundledAgent();
    regenerate();
    for (const workspace of list('workspaces')) updateWorkspaceContext(workspace);
    return counts;
  }

  return { root, roots, db, databasePath, managed, runsRoot: roots.runs, workspacesRoot: roots.workspaces, list, get, getDispatchAgent, setConnectorExecution, save, remove, regenerate, watchCatalog, importWorkbench, createLibrary,
    listCatalogs, saveCatalogs, getSetting, setSetting, listFiles, readFile, writeFile, deleteFile, resolveFile,
    ...createTransfers({ root, roots, get, directory, selectCatalog, listCatalogs, refreshWorkspaceContexts, updateWorkspaceContext, regenerate }),
    close() {
      for (const watch of catalogWatches) watch.stop();
      db.close();
    } };
}

module.exports = { createStorage, safePath };
