const fs = require('node:fs');
const path = require('node:path');
const { randomUUID } = require('node:crypto');

const catalogKinds = ['agents', 'skills', 'workflows', 'libraries'];
const connectorIds = new Set(['aggregate', 'approval-gate', 'if-else', 'library', 'loop', 'prompt', 'sandbox', 'workspace-memory', 'write-to-workspace']);
const connectorFiles = ['manifest.json', 'body.md', 'run.md'];
const excludedNames = new Set(['.git', '.private', '.mcp.json', '.claude', '.claude.json', '.codex', '.agents', '.providers', 'node_modules']);
const emptyCounts = () => ({ agents: 0, skills: 0, workflows: 0, libraries: 0, workspaces: 0, files: 0, skipped: 0 });

function contains(parent, child) {
  const relative = path.relative(parent, child);
  return !path.isAbsolute(relative) && relative !== '..' && !relative.startsWith(`..${path.sep}`);
}

function createManagedFiles({ root, safePath, stat }) {
  const target = relative => safePath(root, relative);
  const catalogRoot = id => target(`.doxie/catalogs/${id}`);

  function entries(directory) {
    return stat(directory)?.isDirectory() ? fs.readdirSync(directory, { withFileTypes: true }) : [];
  }

  function sourceDirectory(directory, relative) {
    try {
      const candidate = safePath(directory, relative);
      return stat(candidate)?.isDirectory() ? candidate : null;
    } catch (error) {
      if (error.code && error.code !== 'ENOTDIR') throw error;
      return null;
    }
  }

  function excluded(name) {
    const lower = name.toLowerCase();
    return excludedNames.has(lower) || lower === '.env' || lower.startsWith('.env.');
  }

  function safeEntry(directory, entry, counts) {
    if ((!entry.isFile() && !entry.isDirectory()) || excluded(entry.name)) {
      counts.skipped++;
      return null;
    }
    try { return safePath(directory, entry.name); }
    catch (error) {
      if (error.code) throw error;
      counts.skipped++;
      return null;
    }
  }

  function copyFile(source, destination, replace = false) {
    const previous = stat(destination);
    if (previous && (!replace || !previous.isFile() || fs.readFileSync(source).equals(fs.readFileSync(destination)))) return false;
    const temporary = `${destination}.doxie-copy-${randomUUID()}.tmp`;
    try {
      fs.copyFileSync(source, temporary, fs.constants.COPYFILE_EXCL);
      const current = stat(destination);
      if (current && (!replace || !current.isFile())) return false;
      fs.renameSync(temporary, destination);
      return true;
    } finally {
      fs.rmSync(temporary, { force: true });
    }
  }

  function copyTree(source, destination, counts, skipRootFile) {
    fs.mkdirSync(destination, { recursive: true });
    for (const entry of entries(source)) {
      if (skipRootFile && entry.name.toLowerCase() === skipRootFile) {
        counts.skipped++;
        continue;
      }
      const from = safeEntry(source, entry, counts);
      if (!from) continue;
      const existing = stat(path.join(destination, entry.name));
      if (existing && (entry.isFile() || !existing.isDirectory())) {
        counts.skipped++;
        continue;
      }
      const to = safePath(destination, entry.name);
      if (entry.isDirectory()) copyTree(from, to, counts);
      else if (copyFile(from, to)) counts.files++;
      else counts.skipped++;
    }
  }

  function copyItems(kind, source, destination, counts, mergeFiles = false) {
    if (!source) return;
    fs.mkdirSync(destination, { recursive: true });
    for (const entry of entries(source)) {
      if (entry.name.startsWith('.') || !entry.isDirectory()
        || (['agents', 'skills'].includes(kind) && connectorIds.has(entry.name.toLowerCase()))) {
        counts.skipped++;
        continue;
      }
      const from = safeEntry(source, entry, counts);
      if (!from) continue;
      const existing = stat(path.join(destination, entry.name));
      if (existing) {
        if (mergeFiles && existing.isDirectory() && entry.isDirectory()) {
          const before = counts.files;
          copyTree(from, safePath(destination, entry.name), counts);
          if (counts.files > before) counts[kind]++;
        } else counts.skipped++;
        continue;
      }
      const to = safePath(destination, entry.name);
      const temporary = path.join(destination, `.doxie-copy-${randomUUID()}`);
      try {
        copyTree(from, temporary, counts);
        fs.renameSync(temporary, to);
      } finally {
        fs.rmSync(temporary, { force: true, recursive: true });
      }
      counts[kind]++;
    }
  }

  function assertDisjoint(source, destination) {
    if (contains(source, destination) || contains(destination, source)) throw new Error('Select a source folder outside the destination workbench');
  }

  function copyCatalog(source, destination, counts) {
    const agentDirectory = target(`${destination}/agents`);
    for (const kind of catalogKinds) {
      const folder = sourceDirectory(source, kind);
      copyItems(kind, folder, kind === 'skills' ? agentDirectory : target(`${destination}/${kind}`), counts, true);
      if (kind !== 'agents' || !folder) continue;
      for (const entry of entries(folder)) {
        if (!entry.isFile() || !/^[a-z0-9]+(?:-[a-z0-9]+)*\.md$/i.test(entry.name)) continue;
        const id = entry.name.slice(0, -3);
        if (connectorIds.has(id.toLowerCase())) continue;
        const targetFile = target(`${destination}/agents/${id}/agent.md`);
        if (stat(targetFile)) continue;
        fs.mkdirSync(path.dirname(targetFile), { recursive: true });
        if (copyFile(safePath(folder, entry.name), targetFile)) {
          counts.agents++;
          counts.files++;
        }
      }
    }
  }

  function importWorkbench(source) {
    const folder = path.resolve(source);
    if (!stat(folder)?.isDirectory()) throw new Error('Select an existing Doxie folder');
    assertDisjoint(fs.realpathSync(folder), fs.realpathSync(root));
    const counts = emptyCounts();
    const canonical = sourceDirectory(folder, '.doxie');
    if (canonical) copyCatalog(canonical, '.doxie', counts);
    copyItems('workspaces', sourceDirectory(folder, '.workspace'), target('.workspace'), counts, true);
    return counts;
  }

  function copyWorkspace(source, id) {
    const folder = path.resolve(source);
    if (!stat(folder)?.isDirectory()) throw new Error('Select an existing workspace source folder');
    assertDisjoint(fs.realpathSync(folder), fs.realpathSync(root));
    const destination = target(`.workspace/${id}`);
    if (stat(destination)) throw new Error('A workspace with this identifier already exists');
    const temporary = target(`.workspace/.doxie-copy-${randomUUID()}`);
    try {
      copyTree(folder, temporary, emptyCounts(), 'workspace.json');
      fs.renameSync(temporary, destination);
    } finally {
      fs.rmSync(temporary, { force: true, recursive: true });
    }
  }

  function importCatalog(source, id) {
    const destination = catalogRoot(id);
    if (path.relative(source, destination) === '') return true;
    if (!stat(source)?.isDirectory()) return false;
    const canonical = sourceDirectory(source, '.doxie');
    const folder = canonical && !catalogKinds.some(kind => sourceDirectory(source, kind)) ? canonical : source;
    assertDisjoint(fs.realpathSync(folder), path.resolve(fs.realpathSync(root), path.relative(root, destination)));
    const counts = emptyCounts();
    copyCatalog(folder, `.doxie/catalogs/${id}`, counts);
    return true;
  }

  function seed(bundle, bundledFiles) {
    if (!stat(bundle)?.isDirectory() || path.relative(bundle, target('.doxie')) === '') return;
    if (!bundledFiles) copyCatalog(bundle, '.doxie', emptyCounts());
    const files = bundledFiles ?? [...connectorIds].flatMap(id => connectorFiles.map(file => `tools/${id}/${file}`));
    for (const file of files) {
      const relative = file.replace(/\\/g, '/').replace(/^\.doxie\//, '');
      const [kind, id, ...parts] = relative.split('/');
      if ((!catalogKinds.includes(kind) && kind !== 'tools') || !id || [id, ...parts].some(excluded)) continue;
      const agentId = kind === 'agents' && !parts.length ? id.replace(/\.md$/i, '') : id;
      if (['agents', 'skills'].includes(kind) && connectorIds.has(agentId.toLowerCase())) continue;
      if (kind === 'tools' && !connectorIds.has(id)) continue;
      const destinationRelative = kind === 'skills' ? ['agents', id, ...parts].join('/')
        : kind === 'agents' && !parts.length && /\.md$/i.test(id) ? `agents/${agentId}/agent.md` : relative;
      const from = safePath(bundle, relative);
      if (!stat(from)?.isFile()) continue;
      const destination = target(`.doxie/${destinationRelative}`);
      fs.mkdirSync(path.dirname(destination), { recursive: true });
      copyFile(from, destination, kind === 'tools');
    }
  }

  function retireBundledAgent(id) {
    for (const kind of ['agents', 'skills']) {
      const source = target(`.doxie/${kind}/${id}`);
      if (!stat(source)?.isDirectory()) continue;
      const archive = target(`.doxie/.retired/${id}`);
      fs.mkdirSync(archive, { recursive: true });
      const destination = safePath(archive, kind);
      fs.renameSync(source, stat(destination) ? safePath(archive, `${kind}-${randomUUID()}`) : destination);
    }
  }

  return { importWorkbench, importCatalog, seed, retireBundledAgent, catalogRoot, copyWorkspace };
}

module.exports = { createManagedFiles, connectorIds };
