const fs = require('node:fs');
const path = require('node:path');
const { randomUUID } = require('node:crypto');

const catalogKinds = ['agents', 'skills', 'workflows', 'libraries'];
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

  function copyFile(source, destination) {
    const temporary = `${destination}.doxie-copy-${randomUUID()}.tmp`;
    try {
      fs.copyFileSync(source, temporary, fs.constants.COPYFILE_EXCL);
      if (stat(destination)) return false;
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
      if (entry.name.startsWith('.') || (kind === 'agents' ? !entry.isFile() || !/\.md$/i.test(entry.name) : !entry.isDirectory())) {
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
      if (entry.isFile()) {
        if (!copyFile(from, to)) continue;
        counts.files++;
      } else {
        const temporary = path.join(destination, `.doxie-copy-${randomUUID()}`);
        try {
          copyTree(from, temporary, counts);
          fs.renameSync(temporary, to);
        } finally {
          fs.rmSync(temporary, { force: true, recursive: true });
        }
      }
      counts[kind]++;
    }
  }

  function assertDisjoint(source, destination) {
    if (contains(source, destination) || contains(destination, source)) throw new Error('Select a source folder outside the destination workbench');
  }

  function importWorkbench(source) {
    const folder = path.resolve(source);
    if (!stat(folder)?.isDirectory()) throw new Error('Select an existing Doxie folder');
    assertDisjoint(fs.realpathSync(folder), fs.realpathSync(root));
    const counts = emptyCounts();
    for (const kind of catalogKinds) {
      copyItems(kind, sourceDirectory(folder, `.doxie/${kind}`), target(`.doxie/${kind}`), counts, true);
    }
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
    for (const kind of catalogKinds) copyItems(kind, sourceDirectory(folder, kind), target(`.doxie/catalogs/${id}/${kind}`), counts, true);
    return true;
  }

  function seed(bundle, bundledFiles) {
    if (!stat(bundle)?.isDirectory() || path.relative(bundle, target('.doxie')) === '') return;
    if (bundledFiles) {
      for (const file of bundledFiles) {
        const relative = file.replace(/\\/g, '/').replace(/^\.doxie\//, '');
        const [kind, ...parts] = relative.split('/');
        if (!catalogKinds.includes(kind) || !parts.length || parts.some(excluded)) continue;
        const from = safePath(bundle, relative);
        if (!stat(from)?.isFile()) continue;
        const destination = target(`.doxie/${relative}`);
        if (stat(destination)) continue;
        fs.mkdirSync(path.dirname(destination), { recursive: true });
        copyFile(from, destination);
      }
      return;
    }
    const counts = emptyCounts();
    for (const kind of catalogKinds) copyItems(kind, sourceDirectory(bundle, kind), target(`.doxie/${kind}`), counts, true);
  }

  return { importWorkbench, importCatalog, seed, catalogRoot, copyWorkspace };
}

module.exports = { createManagedFiles };
