const { randomBytes } = require('node:crypto');
const { constants } = require('node:fs');
const { readdir, mkdir, mkdtemp, readFile, writeFile, copyFile, open, stat, lstat, rm } = require('node:fs/promises');
const path = require('node:path');
const { parseTree } = require('jsonc-parser');
const { safePath } = require('./storage.cjs');
const { runNativeUtility } = require('./native-tools.cjs');

const logSeparator = '\x1e';
const runKinds = ['Queued', 'Running', 'Succeeded', 'Failed', 'Cancelled', 'AwaitingApproval'];
const nodeStatuses = ['Pending', 'Running', 'Succeeded', 'Failed', 'Skipped', 'AwaitingApproval'];
const nodeKinds = ['trigger', 'agent', 'aggregate', 'output', 'loop', 'decision', 'tool'];
const triggerKinds = ['manual', 'cron', 'event', 'webhook'];
const terminal = new Set(['Succeeded', 'Failed', 'Cancelled']);
const connectorKinds = new Map([
  ['aggregate', 'aggregate'], ['write-to-workspace', 'output'], ['loop', 'loop'],
  ['if-else', 'decision'], ['approval-gate', 'approval'], ['library', 'library'], ['sandbox', 'sandbox'], ['prompt', 'prompt'],
]);
const transientFolder = '.doxie-transient';
const maxContextBytes = 64 * 1024;
const maxRunContextBytes = 512 * 1024;
const trueBranches = new Set(['true', 'then', 'yes', 'y', 'pass', 'passed', 'match', 'matched']);
const falseBranches = new Set(['false', 'else', 'no', 'n', 'fail', 'failed', 'unmatched']);
const now = () => new Date().toISOString();
const lower = value => String(value ?? '').toLowerCase();
const normalized = value => lower(value).trim();
const caseInsensitive = (object, key) => {
  if (!object || typeof object !== 'object') return undefined;
  if (Object.hasOwn(object, key)) return object[key];
  const property = Object.keys(object).find(name => lower(name) === lower(key));
  return property === undefined ? undefined : object[property];
};

function initializeRunStore(db) {
  db.exec(`
    CREATE TABLE IF NOT EXISTS workflow_runs (
      id TEXT PRIMARY KEY NOT NULL, workflow_id TEXT NOT NULL, triggered_by TEXT NOT NULL,
      started_at TEXT NOT NULL, finished_at TEXT NULL, status TEXT NOT NULL,
      trigger_inputs TEXT NULL, catalog_id TEXT NULL
    );
    CREATE TABLE IF NOT EXISTS workflow_node_runs (
      run_id TEXT NOT NULL REFERENCES workflow_runs(id) ON DELETE CASCADE, node_id TEXT NOT NULL,
      status TEXT NOT NULL, started_at TEXT NULL, finished_at TEXT NULL, output_summary TEXT NULL,
      logs TEXT NOT NULL DEFAULT '', PRIMARY KEY (run_id, node_id)
    );
    CREATE INDEX IF NOT EXISTS idx_wf_runs_workflow ON workflow_runs(workflow_id);
    CREATE INDEX IF NOT EXISTS idx_wf_runs_started ON workflow_runs(started_at DESC);
  `);
  const columns = new Set(db.prepare('PRAGMA table_info(workflow_runs)').all().map(row => row.name));
  if (!columns.has('trigger_inputs')) db.exec('ALTER TABLE workflow_runs ADD COLUMN trigger_inputs TEXT NULL');
  if (!columns.has('catalog_id')) db.exec('ALTER TABLE workflow_runs ADD COLUMN catalog_id TEXT NULL');
  const nodeColumns = new Set(db.prepare('PRAGMA table_info(workflow_node_runs)').all().map(row => row.name));
  if (!nodeColumns.has('provider_id')) db.exec('ALTER TABLE workflow_node_runs ADD COLUMN provider_id TEXT NULL');
  if (!nodeColumns.has('model')) db.exec('ALTER TABLE workflow_node_runs ADD COLUMN model TEXT NULL');
  if (!nodeColumns.has('has_transient_context')) db.exec('ALTER TABLE workflow_node_runs ADD COLUMN has_transient_context INTEGER NOT NULL DEFAULT 0');

  const updateRun = db.prepare(`
    INSERT INTO workflow_runs (id, workflow_id, triggered_by, started_at, finished_at, status, trigger_inputs, catalog_id)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?)
    ON CONFLICT (id) DO UPDATE SET finished_at=excluded.finished_at, status=excluded.status,
      trigger_inputs=excluded.trigger_inputs, catalog_id=excluded.catalog_id
  `);
  const updateNode = db.prepare(`
    INSERT INTO workflow_node_runs (run_id, node_id, status, started_at, finished_at, output_summary, logs, provider_id, model, has_transient_context)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    ON CONFLICT (run_id, node_id) DO UPDATE SET status=excluded.status, started_at=excluded.started_at,
      finished_at=excluded.finished_at, output_summary=excluded.output_summary, logs=excluded.logs,
      provider_id=excluded.provider_id, model=excluded.model, has_transient_context=excluded.has_transient_context
  `);

  const runs = new Map(db.prepare('SELECT * FROM workflow_runs').all().map(row => [row.id, {
    id: row.id,
    workflowId: row.workflow_id,
    catalogId: row.catalog_id,
    triggeredBy: row.triggered_by,
    startedAt: row.started_at,
    finishedAt: row.finished_at,
    status: readEnum(runKinds, row.status, 'Failed'),
    triggerInputs: readInputs(row.trigger_inputs),
    nodeRuns: Object.create(null),
  }]));
  for (const row of db.prepare('SELECT * FROM workflow_node_runs').all()) {
    const run = runs.get(row.run_id);
    if (!run) continue;
    run.nodeRuns[row.node_id] = {
      nodeId: row.node_id,
      status: readEnum(nodeStatuses, row.status, 'Skipped'),
      startedAt: row.started_at,
      finishedAt: row.finished_at,
      outputSummary: row.output_summary,
      providerId: row.provider_id,
      model: row.model,
      hasTransientContext: Boolean(row.has_transient_context),
      logs: row.logs ? row.logs.split(logSeparator) : [],
    };
  }

  function save(run, nodes = Object.values(run.nodeRuns)) {
    db.exec('BEGIN');
    try {
      updateRun.run(run.id, run.workflowId, run.triggeredBy, run.startedAt, run.finishedAt,
        run.status, JSON.stringify(run.triggerInputs), run.catalogId);
      for (const node of nodes) {
        updateNode.run(run.id, node.nodeId, node.status, node.startedAt, node.finishedAt,
          node.outputSummary, node.logs.join(logSeparator), node.providerId, node.model, Number(node.hasTransientContext));
      }
      db.exec('COMMIT');
    } catch (error) {
      db.exec('ROLLBACK');
      throw error;
    }
  }

  function clear() {
    const ids = [...runs.values()].filter(run => terminal.has(run.status)).map(run => run.id);
    const deleteNodes = db.prepare('DELETE FROM workflow_node_runs WHERE run_id=?');
    const deleteRun = db.prepare('DELETE FROM workflow_runs WHERE id=?');
    db.exec('BEGIN');
    try {
      for (const id of ids) {
        deleteNodes.run(id);
        deleteRun.run(id);
      }
      db.exec('COMMIT');
    } catch (error) {
      db.exec('ROLLBACK');
      throw error;
    }
    for (const id of ids) runs.delete(id);
    return ids.length;
  }

  return { runs, save, clear };
}

function readEnum(choices, value, fallback) {
  if (typeof value === 'number' || /^\d+$/.test(String(value))) return choices[Number(value)] ?? fallback;
  return choices.find(choice => lower(choice) === lower(value)) ?? fallback;
}

function readInputs(value) {
  try {
    const inputs = JSON.parse(value);
    if (inputs && typeof inputs === 'object' && !Array.isArray(inputs)) return inputs;
  } catch (error) {
    if (!(error instanceof SyntaxError)) throw error;
  }
  return {};
}

function createNodeRun(nodeId) {
  return { nodeId, status: 'Pending', startedAt: null, finishedAt: null, outputSummary: null, providerId: null, model: null, hasTransientContext: false, logs: [] };
}

function safeId(value, label) {
  if (typeof value !== 'string' || !/^[a-z0-9_][a-z0-9._-]*$/i.test(value) || value.endsWith('.')) {
    throw new Error(`Invalid ${label} '${value}'`);
  }
  return value;
}

function nodeKind(node) {
  const kind = readEnum(nodeKinds, node.kind, 'agent');
  if (kind === 'tool') {
    const tool = connectorKinds.get(lower(node.toolId));
    if (!tool) throw new Error(`Unknown native workflow tool '${node.toolId}' in node '${node.id}'`);
    return tool;
  }
  return kind === 'agent' ? connectorKinds.get(lower(node.agentId)) ?? kind : kind;
}

function normalizeDefinition(definition) {
  safeId(definition.id, 'workflow id');
  const nodes = definition.nodes ?? [];
  const byId = new Map();
  for (const node of nodes) {
    safeId(node.id, 'workflow node id');
    nodeKind(node);
    const key = lower(node.id);
    if (byId.has(key)) throw new Error(`Duplicate workflow node '${node.id}'`);
    byId.set(key, { ...node, loopId: null });
  }
  const resolve = id => {
    const node = byId.get(lower(id));
    if (!node) throw new Error(`Unknown workflow node '${id}'`);
    return node;
  };
  for (const original of nodes) {
    if (!original.loopId) continue;
    const node = resolve(original.id);
    const owner = resolve(original.loopId);
    if (nodeKind(owner) !== 'loop' || owner.id === node.id || nodeKind(node) === 'loop') {
      throw new Error(`Invalid loop owner '${owner.id}' for node '${node.id}'`);
    }
    node.loopId = owner.id;
  }
  const edges = (definition.edges ?? []).map(edge => ({
    fromNodeId: resolve(edge.fromNodeId).id,
    toNodeId: resolve(edge.toNodeId).id,
    condition: normalizeCondition(edge.condition),
  }));
  const mainNodes = [...byId.values()].filter(node => !node.loopId);
  const mainEdges = edges.filter(edge => !resolve(edge.toNodeId).loopId).map(edge => {
    const source = resolve(edge.fromNodeId);
    return source.loopId ? { ...edge, fromNodeId: source.loopId, condition: null } : edge;
  });
  validateDag(mainNodes, mainEdges);
  for (const loop of mainNodes.filter(node => nodeKind(node) === 'loop')) {
    const bodyNodes = [...byId.values()].filter(node => node.loopId === loop.id);
    const bodyIds = new Set(bodyNodes.map(node => node.id));
    validateDag(bodyNodes, edges.filter(edge => bodyIds.has(edge.fromNodeId) && bodyIds.has(edge.toNodeId)));
  }
  return { ...definition, nodes: [...byId.values()], edges, mainNodes, mainEdges };
}

function validateDag(nodes, edges) {
  const incoming = new Map(nodes.map(node => [node.id, 0]));
  const outgoing = new Map(nodes.map(node => [node.id, []]));
  for (const { fromNodeId, toNodeId } of edges) {
    incoming.set(toNodeId, incoming.get(toNodeId) + 1);
    outgoing.get(fromNodeId).push(toNodeId);
  }
  const ready = nodes.filter(node => incoming.get(node.id) === 0).map(node => node.id);
  for (let index = 0; index < ready.length; index++) {
    for (const next of outgoing.get(ready[index])) {
      const remaining = incoming.get(next) - 1;
      incoming.set(next, remaining);
      if (!remaining) ready.push(next);
    }
  }
  if (ready.length !== nodes.length) throw new Error('The workflow contains a dependency cycle');
}

function normalizeCondition(value) {
  const branch = normalized(value);
  if (!branch || branch === 'always' || branch === 'any') return null;
  if (trueBranches.has(branch)) return 'true';
  if (falseBranches.has(branch)) return 'false';
  return branch;
}

function validateInputs(definition, supplied) {
  const values = { ...supplied };
  for (const field of definition.trigger?.inputs ?? []) {
    let value = caseInsensitive(values, field.id);
    if (!String(value ?? '').trim() && field.defaultValue != null) {
      const existing = Object.keys(values).find(key => lower(key) === lower(field.id));
      values[existing ?? field.id] = field.defaultValue;
      value = field.defaultValue;
    }
    if (field.required && !String(value ?? '').trim()) throw new Error(`Missing required trigger input '${field.id}'`);
    if (String(value ?? '').trim() && field.options?.length && !field.options.some(option => lower(option) === lower(value))) {
      throw new Error(`Invalid trigger input option for '${field.id}'`);
    }
  }
  return values;
}

function jsonPathParts(selector) {
  return String(selector ?? '').split('.').map(part => part.trim()).filter(Boolean);
}

function getJsonPath(value, selector) {
  for (const key of jsonPathParts(selector)) {
    value = Array.isArray(value) ? (/^\d+$/.test(key) ? value[Number(key)] : undefined) : caseInsensitive(value, key);
    if (value === undefined) return undefined;
  }
  return value;
}

function getJsonNode(node, selector) {
  for (const key of jsonPathParts(selector)) {
    if (node?.type === 'array' && /^\d+$/.test(key)) {
      node = node.children?.[Number(key)];
    } else if (node?.type === 'object') {
      const property = node.children?.findLast(item => item.children[0].value === key)
        ?? node.children?.find(item => lower(item.children[0].value) === lower(key));
      node = property?.children[1];
    } else return undefined;
  }
  return node;
}

function scalar(value) {
  if (value === undefined || value === null) return '';
  return typeof value === 'string' ? value : JSON.stringify(value);
}

function substitute(value, triggerInputs, loop) {
  return String(value ?? '').replace(/\{\{\s*(trigger|loop)\.([a-z][\w.-]*)\s*\}\}/gi, (_, scope, selector) => {
    if (lower(scope) === 'trigger') return scalar(caseInsensitive(triggerInputs, selector));
    const item = /^item(?:\.(.+))?$/i.exec(selector);
    if (item && loop?.itemNode) {
      const raw = getJsonNode(loop.itemNode, item[1]);
      if (!raw || raw.type === 'null') return '';
      if (raw.type === 'string') return raw.value;
      return loop.sourceJson.slice(raw.offset, raw.offset + raw.length);
    }
    return scalar(getJsonPath(loop, selector));
  });
}

async function optionalStat(file, follow = true) {
  try { return await (follow ? stat(file) : lstat(file)); }
  catch (error) {
    if (error.code !== 'ENOENT' && error.code !== 'ENOTDIR') throw error;
    return null;
  }
}

async function filesUnder(directory, recursive = false) {
  if (!(await optionalStat(directory, false))?.isDirectory()) return [];
  const entries = await readdir(directory, { withFileTypes: true });
  const files = entries.filter(entry => entry.isFile()).map(entry => path.join(directory, entry.name));
  if (recursive) {
    for (const entry of entries.filter(entry => entry.isDirectory())) {
      files.push(...await filesUnder(path.join(directory, entry.name), true));
    }
  }
  return files.sort();
}

async function copyArtifacts(from, to, exclude = () => false, recursive = true) {
  let copied = 0;
  for (const file of await filesUnder(from, recursive)) {
    if (exclude(lower(path.basename(file)))) continue;
    const destination = safePath(to, path.relative(from, file));
    await mkdir(path.dirname(destination), { recursive: true });
    await copyFile(file, destination);
    copied++;
  }
  return copied;
}

const mediaTypes = new Map([
  ['.json', 'application/json'], ['.md', 'text/markdown'], ['.markdown', 'text/markdown'],
  ['.txt', 'text/plain'], ['.log', 'text/plain'], ['.csv', 'text/csv'], ['.html', 'text/html'], ['.htm', 'text/html'],
  ['.xml', 'application/xml'], ['.yaml', 'application/yaml'], ['.yml', 'application/yaml'],
  ['.pdf', 'application/pdf'], ['.png', 'image/png'], ['.jpg', 'image/jpeg'], ['.jpeg', 'image/jpeg'], ['.webp', 'image/webp'],
]);

function publicArtifact(relative) {
  return relative.split(/[\\/]/).every(part => part && !part.startsWith('.'));
}

function sortByPath(left, right) {
  return left.relativePath < right.relativePath ? -1 : left.relativePath > right.relativePath ? 1 : 0;
}

async function artifactEntries(directory) {
  if (!directory || !(await optionalStat(directory, false))?.isDirectory()) return [];
  const manifestPath = safePath(directory, '.doxie-artifact.json');
  const manifestInfo = await optionalStat(manifestPath, false);
  let files;
  if (manifestInfo) {
    if (!manifestInfo.isFile() || manifestInfo.size > 128_000) throw new Error('Invalid workflow artifact manifest');
    const metadata = JSON.parse(await readFile(manifestPath, 'utf8'));
    if (!Array.isArray(metadata?.files) || metadata.files.some(file => typeof file !== 'string' || !publicArtifact(file))) {
      throw new Error('Workflow artifact manifest must declare visible files as relative paths');
    }
    files = metadata.files.map(file => safePath(directory, file));
  } else {
    files = (await filesUnder(directory, true)).filter(file => publicArtifact(path.relative(directory, file)));
  }
  const artifacts = [];
  for (const file of new Set(files)) {
    const info = await optionalStat(file, false);
    if (!info?.isFile()) throw new Error(`Declared workflow artifact '${path.basename(file)}' is unavailable`);
    const relativePath = path.relative(directory, file).split(path.sep).join('/');
    artifacts.push({ file, relativePath, mediaType: mediaTypes.get(lower(path.extname(file))) ?? 'application/octet-stream', sizeBytes: info.size });
  }
  return artifacts.sort(sortByPath);
}

function findUpstreamArtifact(upstream, filename) {
  const relative = String(filename).replaceAll('\\', '/');
  return upstream.flatMap(source => source.artifacts).find(artifact => artifact.relativePath === relative) ?? null;
}

function readArtifact(artifact) {
  return artifact.file ? readFile(artifact.file, 'utf8') : Promise.resolve(artifact.content);
}

function describeArtifact(artifact) {
  if (artifact.mediaType.startsWith('text/') || ['application/json', 'application/xml', 'application/yaml'].includes(artifact.mediaType)) {
    return readArtifact(artifact);
  }
  return Promise.resolve(`Binary artifact (${artifact.mediaType}, ${artifact.sizeBytes} bytes).`);
}

async function upstreamJson(upstream, filename, allowContext) {
  const artifact = findUpstreamArtifact(upstream, filename);
  if (artifact && filename !== 'context') {
    return { text: await readArtifact(artifact), sourcePath: artifact.file ?? null, transient: artifact.transient === true };
  }
  if (!allowContext && filename !== 'context') return null;
  const contexts = upstreamContexts(upstream);
  if (contexts.length > 1) throw new Error('Multiple direct upstream contexts are available. Select an artifact filename to disambiguate.');
  return contexts.length ? { text: contexts[0].contextJson, sourcePath: null, transient: true } : null;
}

async function snapshotSources(transientRoot, upstream, signal) {
  const sources = [];
  const emitted = new Set(upstream.flatMap(source => (source.contexts ?? [])
    .filter(item => item.nodeId === source.nodeId).map(contextKey)));
  for (const [index, source] of upstream.entries()) {
    signal.throwIfAborted();
    const artifactDirectory = source.artifacts.length ? safePath(transientRoot, `${index}-${source.nodeId}`) : null;
    if (artifactDirectory) await mkdir(artifactDirectory);
    for (const artifact of source.artifacts) {
      signal.throwIfAborted();
      const destination = safePath(artifactDirectory, artifact.relativePath);
      await mkdir(path.dirname(destination), { recursive: true });
      if (artifact.file) await copyFile(artifact.file, destination, constants.COPYFILE_FICLONE);
      else await writeFile(destination, artifact.content, { encoding: 'utf8', signal });
    }
    const contextJson = source.contexts?.find(context => context.nodeId === source.nodeId)?.contextJson;
    const forwardedContexts = (source.contexts ?? []).filter(item => {
      const key = contextKey(item);
      if (emitted.has(key)) return false;
      emitted.add(key);
      return true;
    });
    sources.push({
      kind: source.kind, nodeId: source.nodeId, artifactDirectory,
      artifacts: source.artifacts.map(({ relativePath, mediaType, sizeBytes }) => ({ relativePath, mediaType, sizeBytes })),
      ...(contextJson === undefined ? {} : { contextJson }),
      ...(forwardedContexts.length ? { forwardedContexts } : {}),
    });
  }
  return sources;
}

function contextKey(item) {
  return `${item.nodeId}:${item.iteration?.index ?? ''}`;
}

function upstreamContexts(upstream) {
  const distinct = new Map();
  for (const source of upstream) {
    for (const item of source.contexts ?? []) distinct.set(contextKey(item), item);
  }
  return [...distinct.values()];
}

function writeNodeContexts(scope, record, node, contexts, needsFresh = false) {
  if (!contexts.length && !needsFresh) return;
  record.hasTransientContext = true;
  scope.contexts.set(node.id, contexts);
}

async function readContextOutput(transport) {
  const output = safePath(transport, 'context.json');
  const info = await optionalStat(output, false);
  if (!info) return null;
  if (!info.isFile() || info.size > maxContextBytes) throw new Error('Workflow context must be a JSON file of at most 64 KiB');
  const file = await open(output, 'r');
  try {
    const buffer = Buffer.alloc(maxContextBytes + 1);
    let size = 0;
    while (size < buffer.length) {
      const { bytesRead } = await file.read(buffer, size, buffer.length - size, size);
      if (!bytesRead) break;
      size += bytesRead;
    }
    if (size > maxContextBytes) throw new Error('Workflow context must be a JSON file of at most 64 KiB');
    try {
      const contextJson = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(buffer.subarray(0, size));
      const value = JSON.parse(contextJson);
      if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Expected a JSON object');
      return { contextJson, size };
    } catch {
      throw new Error('Workflow context must contain a valid UTF-8 JSON object');
    }
  } finally {
    await file.close();
    await rm(output, { force: true });
  }
}

async function forwardArtifacts(upstream, directory, excluded, signal) {
  const files = upstream.flatMap(source => source.artifacts.filter(artifact => !artifact.transient && !excluded.has(lower(artifact.relativePath)))
    .map(artifact => ({ ...artifact, nodeId: source.nodeId })));
  const paths = new Map();
  for (const artifact of files) {
    const key = lower(artifact.relativePath);
    paths.set(key, (paths.get(key) ?? 0) + 1);
  }
  const forwarded = [];
  for (const artifact of files) {
    signal.throwIfAborted();
    const relative = paths.get(lower(artifact.relativePath)) > 1
      ? `_sources/${artifact.nodeId}/${artifact.relativePath}` : artifact.relativePath;
    const destination = safePath(directory, relative);
    await mkdir(path.dirname(destination), { recursive: true });
    if (artifact.file) await copyFile(artifact.file, destination, constants.COPYFILE_FICLONE | constants.COPYFILE_EXCL);
    else await writeFile(destination, artifact.content, { encoding: 'utf8', signal, flag: 'wx' });
    forwarded.push(relative);
  }
  return forwarded;
}

const previewExtensions = new Map([
  ['.md', 'markdown'], ['.markdown', 'markdown'], ['.html', 'html'], ['.htm', 'html'], ['.json', 'json'],
  ['.yaml', 'yaml'], ['.yml', 'yaml'], ['.txt', 'text'], ['.log', 'text'],
]);
const previewNames = ['output.md', 'report.md', 'summary.md', 'result.md', 'index.md', 'output.html', 'report.html', 'result.json', 'output.json'];

function scorePreview(file) {
  const preferred = previewNames.indexOf(lower(path.basename(file)));
  if (preferred >= 0) return preferred;
  const format = previewExtensions.get(lower(path.extname(file)));
  return { markdown: 20, html: 30, json: 40 }[format] ?? 80;
}

async function previewArtifact(directory) {
  if (!(await optionalStat(directory, false))?.isDirectory()) return null;
  const manifestPath = safePath(directory, '.doxie-artifact.json');
  let candidates;
  try {
    const info = await optionalStat(manifestPath);
    if (info?.isFile() && info.size < 128_000) {
      const declared = JSON.parse(await readFile(manifestPath, 'utf8'))?.files;
      if (Array.isArray(declared) && declared.length) {
        candidates = declared.filter(file => typeof file === 'string').flatMap(file => {
          try { return [safePath(directory, file)]; }
          catch { return []; }
        });
      }
    }
  } catch (error) {
    if (!(error instanceof SyntaxError)) throw error;
  }
  candidates ??= await filesUnder(directory, true);
  const previewable = candidates.filter(file => !path.basename(file).startsWith('.') && previewExtensions.has(lower(path.extname(file))));
  previewable.sort((left, right) => scorePreview(left) - scorePreview(right) || left.localeCompare(right));
  for (const file of previewable) {
    const information = await optionalStat(file, false);
    if (!information?.isFile()) continue;
    const handle = await open(file, 'r');
    try {
      const buffer = Buffer.alloc(Math.min(information.size, 300_000));
      const { bytesRead } = await handle.read(buffer, 0, buffer.length, 0);
      const content = buffer.toString('utf8', 0, bytesRead);
      if (!content.trim()) continue;
      return {
        content, contentFormat: previewExtensions.get(lower(path.extname(file))), sourcePath: file,
        contentTruncated: information.size > bytesRead, contentLength: information.size,
      };
    } finally {
      await handle.close();
    }
  }
  return null;
}

async function writeJson(file, value) {
  await writeFile(file, `${JSON.stringify(value, null, 2)}\n`, 'utf8');
}

async function manifest(directory, { producerId, producerName, kind, title, tags, files }, replace = false) {
  const target = safePath(directory, '.doxie-artifact.json');
  if (!replace && (await optionalStat(target))?.isFile()) return;
  const artifacts = files ?? (await artifactEntries(directory)).map(file => file.relativePath);
  if (artifacts.length) {
    await writeJson(target, { schema: 'doxie.output-artifact.v1', producerId, producerName, kind, title, tags, files: artifacts });
  }
}

const decisionOperators = new Map([
  ['==', 'equals'], ['=', 'equals'], ['eq', 'equals'], ['!=', 'not-equals'], ['<>', 'not-equals'], ['ne', 'not-equals'],
  ['>', 'greater-than'], ['gt', 'greater-than'], ['<', 'less-than'], ['lt', 'less-than'],
  ['>=', 'greater-or-equal'], ['gte', 'greater-or-equal'], ['<=', 'less-or-equal'], ['lte', 'less-or-equal'],
  ['non-empty', 'not-empty'],
]);

function isEmpty(value) {
  return value == null || (typeof value === 'string' && !value.trim())
    || (typeof value === 'object' && Object.keys(value).length === 0);
}

function isTruthy(value) {
  const falseStrings = ['false', '0', 'no', 'nao', 'não', 'null', 'undefined'];
  return !isEmpty(value) && Boolean(value) && !(typeof value === 'string' && falseStrings.includes(normalized(value)));
}

function decisionEquals(value, expected) {
  return typeof value === 'number' && String(expected).trim() && Number.isFinite(Number(expected))
    ? value === Number(expected) : lower(scalar(value)) === lower(expected);
}

function decisionContains(value, expected) {
  if (Array.isArray(value)) return value.some(item => decisionEquals(item, expected));
  if (value && typeof value === 'object') {
    return Object.entries(value).some(([key, item]) => lower(key).includes(lower(expected)) || decisionContains(item, expected));
  }
  return lower(scalar(value)).includes(lower(expected));
}

function evaluateDecision(value, operator, expected, sourceFound) {
  const exists = value !== undefined;
  const numeric = typeof value === 'number' && String(expected).trim() && Number.isFinite(Number(expected));
  switch (operator) {
    case 'exists': return exists;
    case 'not-exists': return !exists;
    case 'truthy': return isTruthy(value);
    case 'falsy': return !isTruthy(value);
    case 'empty': return isEmpty(value);
    case 'not-empty': return !isEmpty(value);
    case 'equals': return exists && decisionEquals(value, expected);
    case 'not-equals': return sourceFound ? !exists || !decisionEquals(value, expected) : !!expected;
    case 'contains': return exists && decisionContains(value, expected);
    case 'greater-than': return !!numeric && value > Number(expected);
    case 'less-than': return !!numeric && value < Number(expected);
    case 'greater-or-equal': return !!numeric && value >= Number(expected);
    case 'less-or-equal': return !!numeric && value <= Number(expected);
    default: throw new Error(`Unsupported if-else operator '${operator}'`);
  }
}

const weekdays = ['sun', 'mon', 'tue', 'wed', 'thu', 'fri', 'sat'];
const months = ['jan', 'feb', 'mar', 'apr', 'may', 'jun', 'jul', 'aug', 'sep', 'oct', 'nov', 'dec'];

function parseCronField(field, min, max, names) {
  const values = new Set();
  const readValue = part => {
    const name = names?.indexOf(lower(part).slice(0, 3)) ?? -1;
    if (name >= 0) return name + min;
    return /^\d+$/.test(part) ? Number(part) : NaN;
  };
  for (const part of field.split(',')) {
    const [range, stepText, ...rest] = part.split('/');
    const [first, last, ...restRange] = range.split('-');
    const start = range === '*' ? min : readValue(first);
    const end = range === '*' ? max : last === undefined ? (stepText ? max : start) : readValue(last);
    const step = stepText === undefined ? 1 : readValue(stepText);
    if (rest.length || restRange.length || !Number.isInteger(start) || !Number.isInteger(end)
      || !Number.isInteger(step) || step < 1 || start < min || end > max || end < start) return null;
    for (let value = start; value <= end; value += step) values.add(value);
  }
  return values;
}

function cronFields(expression) {
  const fields = String(expression ?? '').trim().split(/\s+/);
  if (fields.length !== 5) return null;
  const values = [
    parseCronField(fields[0], 0, 59), parseCronField(fields[1], 0, 23),
    parseCronField(fields[2], 1, 31), parseCronField(fields[3], 1, 12, months),
    parseCronField(fields[4], 0, 6, weekdays),
  ];
  return values.some(field => !field) ? null : values;
}

function parseCron(expression) {
  const values = cronFields(expression);
  if (!values) return null;
  return time => [time.getUTCMinutes(), time.getUTCHours(), time.getUTCDate(), time.getUTCMonth() + 1, time.getUTCDay()]
    .every((value, index) => values[index].has(value));
}

function nextCron(expression, after = new Date()) {
  const fields = cronFields(expression);
  const cutoff = new Date(after).getTime();
  if (!fields || !Number.isFinite(cutoff)) return null;
  const [minutes, hours, days, months, weekdays] = fields;
  const times = [...hours].flatMap(hour => [...minutes].map(minute => (hour * 60 + minute) * 60_000)).sort((left, right) => left - right);
  const day = new Date(cutoff);
  day.setUTCHours(0, 0, 0, 0);
  const throughYear = day.getUTCFullYear() + 400;
  // The calendar (including weekday assignments) repeats every 400 years.
  while (day.getUTCFullYear() <= throughYear) {
    if (months.has(day.getUTCMonth() + 1) && days.has(day.getUTCDate()) && weekdays.has(day.getUTCDay())) {
      const start = day.getTime();
      const next = times.find(offset => start + offset > cutoff);
      if (next !== undefined) return new Date(start + next);
    }
    day.setUTCDate(day.getUTCDate() + 1);
  }
  return null;
}

function createWorkflows({ storage, agents, emit = () => {}, notify = () => {}, clock = () => new Date() }) {
  const store = initializeRunStore(storage.db);
  const active = new Map();
  const cronCursors = new Map();
  const runsRoot = path.resolve(storage.roots?.runs ?? path.join(storage.root, '.runs'));
  const transientRoot = id => safePath(runsRoot, path.join(safeId(id, 'run id'), transientFolder));
  async function clearTransient(id) {
    try {
      await rm(transientRoot(id), { recursive: true, force: true, maxRetries: 2, retryDelay: 100 });
    } catch (error) {
      console.error(`Could not clear transient data for workflow ${id}: ${error.message}`);
    }
  }
  let timer;
  let closed = false;

  const interrupted = [];
  for (const run of store.runs.values()) {
    if (terminal.has(run.status)) continue;
    interrupted.push(clearTransient(run.id));
    run.status = 'Cancelled';
    run.finishedAt = now();
    for (const node of Object.values(run.nodeRuns)) {
      if (!['Pending', 'Running', 'AwaitingApproval'].includes(node.status)) continue;
      node.status = 'Skipped';
      node.finishedAt = run.finishedAt;
      node.logs.push('[skipped - previous DoxieOS session ended]');
    }
    store.save(run);
  }

  const runDir = id => path.join(runsRoot, safeId(id, 'run id'));
  const nodeDir = (id, nodeId) => path.join(runDir(id), safeId(nodeId, 'workflow node id'));
  const publish = run => emit({ type: 'workflow:changed', run: structuredClone(run) });
  const approvalKey = nodeRun => lower(nodeRun.nodeId);

  function update(run, nodes) {
    store.save(run, nodes);
    publish(run);
  }

  function getRun(id) {
    return structuredClone(store.runs.get(id) ?? null);
  }

  function listRuns(workflowId) {
    const runs = [...store.runs.values()].filter(run => !workflowId || lower(run.workflowId) === lower(workflowId));
    return structuredClone(runs.sort((left, right) => Date.parse(right.startedAt) - Date.parse(left.startedAt)));
  }

  function stop(id) {
    const context = active.get(id);
    if (!context || context.controller.signal.aborted) return false;
    context.controller.abort();
    return true;
  }

  function addHint(id, hint) {
    const context = active.get(id);
    if (!context || typeof hint !== 'string' || !hint.trim()) return false;
    const size = Buffer.byteLength(hint);
    if (size > 4096 || context.hints.reduce((total, item) => total + Buffer.byteLength(item), size) > maxContextBytes) {
      throw new Error('Workflow guidance is limited to 4 KiB per message and 64 KiB per run.');
    }
    context.hints.push(hint);
    const targets = Object.values(context.run.nodeRuns).filter(node => ['Running', 'AwaitingApproval'].includes(node.status));
    for (const node of targets) node.logs.push('[operator hint] transient guidance received');
    const sendAgentHint = agents.addHint ?? agents.addOperatorHint;
    for (const [nodeId, agentId] of context.agentRuns) {
      try { sendAgentHint?.call(agents, agentId, hint); }
      catch (error) { context.run.nodeRuns[nodeId].logs.push(`[warning] could not forward hint: ${error.message}`); }
    }
    update(context.run, targets);
    return true;
  }

  function approve(id, nodeId, decision) {
    const context = active.get(id);
    const key = lower(nodeId);
    const resolve = context?.approvals.get(key);
    const accepted = typeof decision === 'boolean' ? decision : decision?.approve;
    if (!resolve || typeof accepted !== 'boolean' || context.controller.signal.aborted) return false;
    context.approvals.delete(key);
    resolve({ approved: accepted, comment: String(decision?.comment ?? '').trim() });
    return true;
  }

  function findDefinition(id, catalogId) {
    const definition = storage.get('workflows', id, catalogId);
    if (!definition) throw new Error(`Workflow '${id}' was not found`);
    return definition;
  }

  function run(id, input = {}) {
    const supplied = input ?? {};
    const options = ['triggerInputs', 'catalogId', 'triggeredBy'].some(key => Object.hasOwn(supplied, key));
    const definition = findDefinition(id, options ? supplied.catalogId : undefined);
    return launch(definition, options ? supplied.triggeredBy ?? 'manual' : 'manual',
      validateInputs(definition, options ? supplied.triggerInputs ?? {} : supplied));
  }

  function rerun(id, nodeId, guidance) {
    const previous = store.runs.get(id);
    if (!previous) throw new Error(`Workflow run '${id}' was not found`);
    const definition = normalizeDefinition(findDefinition(previous.workflowId, previous.catalogId ?? undefined));
    const requested = definition.nodes.find(node => lower(node.id) === lower(nodeId));
    if (!requested) throw new Error(`Workflow node '${nodeId}' was not found`);
    const start = requested.loopId ?? requested.id;
    const reachable = new Set([start]);
    for (const current of reachable) {
      for (const edge of definition.edges.filter(edge => edge.fromNodeId === current)) reachable.add(edge.toNodeId);
    }
    const hint = typeof guidance === 'string' && guidance.trim() ? guidance : '';
    if (Buffer.byteLength(hint) > 4096) throw new Error('Workflow guidance is limited to 4 KiB per message.');
    return launch(definition, `rerun:${id}:${requested.id}`, previous.triggerInputs, { previous, reachable, hint, start });
  }

  function launch(rawDefinition, triggeredBy, triggerInputs, replay = null) {
    if (closed) throw new Error('The workflow engine is closed');
    if (rawDefinition.enabled === false) throw new Error(`Workflow '${rawDefinition.id}' is disabled`);
    const definition = normalizeDefinition(rawDefinition);
    const id = randomBytes(6).toString('hex');
    const run = {
      id, workflowId: definition.id, catalogId: definition.catalogId ?? null,
      triggeredBy, startedAt: now(), finishedAt: null, status: 'Queued', triggerInputs,
      nodeRuns: Object.fromEntries(definition.nodes.map(node => [node.id, createNodeRun(node.id)])),
    };
    const context = {
      definition, run, controller: new AbortController(), approvals: new Map(), agentRuns: new Map(),
      hints: replay?.hint ? [replay.hint] : [], task: null, contextBytes: 0,
    };
    store.save(run);
    store.runs.set(id, run);
    active.set(id, context);
    context.task = Promise.resolve().then(() => execute(context, replay));
    context.task.catch(error => console.error(`Could not complete workflow ${id}`, error));
    publish(run);
    return structuredClone(run);
  }

  async function wait(id) {
    await active.get(id)?.task;
    return getRun(id);
  }

  async function execute(context, replay) {
    const { definition, run, controller } = context;
    try {
      run.status = 'Running';
      update(run, []);
      await mkdir(runsRoot, { recursive: true });
      await mkdir(safePath(runsRoot, run.id), { recursive: true });
      const scope = {
        signal: controller.signal, base: runDir(run.id), outputDir: id => nodeDir(run.id, id), contexts: new Map(),
        nodeRun: node => run.nodeRuns[node.id],
        inputSources: (node, edges) => getInputSources(scope, edges),
      };
      if (replay) {
        run.nodeRuns[replay.start].logs.push(`[rerun-from] replaying from '${replay.start}' using run ${replay.previous.id}`);
        if (replay.hint) run.nodeRuns[replay.start].logs.push('[rerun-from guidance] transient guidance received');
        for (const node of definition.nodes.filter(node => node.loopId && !replay.reachable.has(node.id))) {
          await reuseNode(context, scope, node, replay);
        }
      }
      await executeGraph(context, scope, definition.mainNodes, definition.mainEdges, replay);
    } catch (error) {
      const pending = Object.values(run.nodeRuns).find(node => node.status === 'Pending' || node.status === 'Running');
      if (pending) {
        pending.status = 'Failed';
        pending.finishedAt = now();
        pending.logs.push(`[engine error] ${error.message}`);
      }
      if (!controller.signal.aborted) run.status = 'Failed';
    } finally {
      await clearTransient(run.id);
      const unfinished = Object.values(run.nodeRuns).filter(node => ['Pending', 'Running'].includes(node.status));
      for (const node of unfinished) {
        node.status = 'Skipped';
        node.finishedAt = now();
        node.logs.push(controller.signal.aborted ? '[skipped - run cancelled]' : '[skipped - owner did not run]');
      }
      const failed = Object.values(run.nodeRuns).some(node => !node.nodeId.includes('#iter-') && node.status === 'Failed');
      run.status = controller.signal.aborted ? 'Cancelled' : failed || run.status === 'Failed' ? 'Failed' : 'Succeeded';
      run.finishedAt = now();
      try {
        clearTimeout(context.outputUpdate);
        update(run);
        await notifyCompletion(context);
      } finally {
        active.delete(run.id);
      }
    }
  }

  async function getInputSources(scope, edges) {
    const upstream = [];
    for (const sourceId of new Set(edges.map(edge => edge.fromNodeId))) {
      const directory = safePath(scope.base, path.relative(scope.base, scope.outputDir(sourceId)));
      const contexts = scope.contexts.get(sourceId);
      if (!contexts && scope.nodeRun({ id: sourceId }).hasTransientContext) {
        throw new Error(`The prior context for node '${sourceId}' expired. Rerun from its original producer or restart the workflow.`);
      }
      upstream.push({ kind: 'node', nodeId: sourceId, artifacts: await artifactEntries(directory), contexts });
    }
    return upstream;
  }

  async function withHandoff(context, scope, node, upstream, action) {
    const { run, definition } = context;
    const root = transientRoot(run.id);
    await mkdir(root, { recursive: true, mode: 0o700 });
    const transport = await mkdtemp(safePath(root, 'input-'));
    try {
      const sources = await snapshotSources(transport, upstream, scope.signal);
      return await action({
        schema: 'doxie.workflow-handoff.v1', workflowId: definition.id, runId: run.id, nodeId: node.id,
        ...(scope.loop ? { iteration: { index: scope.loop.index, total: scope.loop.total } } : {}), sources,
      }, transport);
    } finally {
      await rm(safePath(root, path.basename(transport)), { recursive: true, force: true, maxRetries: 2, retryDelay: 100 });
    }
  }

  async function executeGraph(context, scope, nodes, edges, replay) {
    const byId = new Map(nodes.map(node => [node.id, node]));
    const incoming = new Map(nodes.map(node => [node.id, []]));
    for (const edge of edges) incoming.get(edge.toNodeId).push(edge);
    const tasks = new Map();
    const branch = new Map();
    function task(id) {
      if (!tasks.has(id)) tasks.set(id, Promise.resolve().then(() => executeOne(byId.get(id))));
      return tasks.get(id);
    }
    async function executeOne(node) {
      const record = scope.nodeRun(node);
      if (replay && !replay.reachable.has(node.id)) {
        const selected = await reuseNode(context, scope, node, replay);
        if (selected) branch.set(node.id, selected);
        return record.status;
      }
      try {
        const dependencies = incoming.get(node.id);
        const states = await Promise.all(dependencies.map(edge => task(edge.fromNodeId)));
        scope.signal.throwIfAborted();
        const unmet = dependencies.find((edge, index) => states[index] !== 'Succeeded'
          || (edge.condition && branch.get(edge.fromNodeId) !== edge.condition));
        if (unmet) {
          record.status = 'Skipped';
          record.logs.push(unmet.condition
            ? `[skipped - condition '${unmet.condition}' not selected by '${unmet.fromNodeId}']`
            : `[skipped - upstream '${unmet.fromNodeId}' did not succeed]`);
          return record.status;
        }
        record.status = 'Running';
        record.startedAt = now();
        if (scope.loop) record.logs.push(`[loop] iter-${scope.loop.index} of ${scope.loop.total}`);
        update(context.run, [record]);
        const selected = await executeNode(context, scope, node, record, await scope.inputSources(node, dependencies));
        if (selected) branch.set(node.id, selected);
        scope.signal.throwIfAborted();
        record.status = 'Succeeded';
      } catch (error) {
        if (error.name === 'AbortError') {
          record.status = 'Skipped';
          record.logs.push('[node cancelled]');
        } else {
          record.status = 'Failed';
          record.logs.push(`[error] ${error.message}`);
        }
      } finally {
        record.finishedAt = now();
        update(context.run, [record]);
      }
      return record.status;
    }
    const results = await Promise.allSettled(nodes.map(node => task(node.id)));
    const unexpected = results.find(result => result.status === 'rejected');
    if (unexpected) throw unexpected.reason;
  }

  async function reuseNode(context, scope, node, replay) {
    const record = scope.nodeRun(node);
    const original = replay.previous.nodeRuns[node.id];
    if (original?.status !== 'Succeeded') {
      record.status = 'Skipped';
      record.finishedAt = now();
      record.logs.push(`[rerun-from] skipped: source node was ${original?.status ?? 'missing'}`);
      update(context.run, [record]);
      return null;
    }
    const source = safePath(runsRoot, path.join(replay.previous.id, node.id));
    const destination = safePath(scope.base, node.id);
    if ((await optionalStat(source, false))?.isDirectory()) {
      await mkdir(destination, { recursive: true });
      await copyArtifacts(source, destination);
    }
    record.status = 'Succeeded';
    record.startedAt = original.startedAt;
    record.finishedAt = original.finishedAt ?? now();
    record.outputSummary = original.outputSummary ?? `reused from ${replay.previous.id}/${node.id}`;
    record.providerId = original.providerId;
    record.model = original.model;
    record.hasTransientContext = original.hasTransientContext;
    record.logs.push(`[rerun-from] reused artifacts from run ${replay.previous.id}, node ${node.id}`);
    update(context.run, [record]);
    if (nodeKind(node) !== 'decision') return null;
    const decisionFile = path.join(destination, 'decision.json');
    if (!(await optionalStat(decisionFile))?.isFile()) return null;
    return normalizeCondition(JSON.parse(await readFile(decisionFile, 'utf8')).selectedBranch);
  }

  async function executeNode(context, scope, node, record, upstream) {
    const { run } = context;
    const kind = nodeKind(node);
    const modelBacked = kind === 'agent' || kind === 'prompt';
    const inputs = Object.fromEntries(Object.entries(node.inputs ?? {}).map(([key, value]) => [
      modelBacked ? key : lower(key),
      modelBacked && typeof value !== 'string' ? value : substitute(value, run.triggerInputs, scope.loop),
    ]));
    if (scope.loop && !['agent', 'decision', 'library', 'sandbox', 'prompt'].includes(kind)) {
      throw new Error(`Loop body node '${node.id}' must be an agent or supported connector`);
    }
    if (kind === 'trigger') {
      record.logs.push(`trigger fired (${run.triggeredBy})`);
      record.outputSummary = `trigger:${run.triggeredBy} at ${run.startedAt.slice(11, 19)}`;
      return null;
    }
    const directory = safePath(scope.base, node.id);
    await mkdir(directory, { recursive: true });
    switch (kind) {
      case 'prompt':
      case 'agent': await withHandoff(context, scope, node, upstream, (handoff, transport) => dispatchAgent(context, scope, node, record, inputs, handoff, directory, transport)); break;
      case 'aggregate': await aggregate(context, node, record, upstream, directory); break;
      case 'output': await outputToWorkspace(context, scope, node, record, inputs, upstream); break;
      case 'decision': {
        const selected = await decide(context, scope, node, record, inputs, upstream, directory);
        writeNodeContexts(scope, record, node, upstreamContexts(upstream), upstream.some(source => source.contexts));
        return selected;
      }
      case 'loop': await executeLoop(context, scope, node, record, inputs, upstream, directory); break;
      case 'approval': await approval(context, scope, node, record, inputs, upstream, directory); break;
      case 'library':
      case 'sandbox': await withHandoff(context, scope, node, upstream, handoff => utility(context, scope, node, kind, record, inputs, handoff, directory)); break;
    }
    if (kind === 'approval' || kind === 'aggregate') {
      writeNodeContexts(scope, record, node, upstreamContexts(upstream), upstream.some(source => source.contexts));
    }
    return null;
  }

  function workspaceFor(context, scope, node) {
    const { definition, run } = context;
    const workspaceId = substitute(node.workspaceId, run.triggerInputs, scope.loop).trim()
      || substitute(definition.workspaceId, run.triggerInputs, scope.loop).trim();
    if (!workspaceId) return null;
    const workspace = storage.get('workspaces', workspaceId);
    if (!workspace) throw new Error(`Workspace '${workspaceId}' for node '${node.id}' was not found`);
    return workspace;
  }

  async function utility(context, scope, node, toolId, record, inputs, handoff, directory) {
    const { run, definition } = context;
    const toolMode = node.toolMode ?? node.agentMode;
    const explicitMountTarget = toolId === 'library' && toolMode === 'mount' && inputs.workspace?.trim();
    const result = await runNativeUtility(toolId, {
      storage, toolMode, inputs, workspace: explicitMountTarget ? null : workspaceFor(context, scope, node),
      sourceDirectories: handoff.sources.map(source => source.artifactDirectory).filter(Boolean),
      outputDir: directory, scratchRoot: safePath(transientRoot(run.id), 'sandboxes'), signal: scope.signal,
      catalogId: definition.catalogId,
    });
    record.outputSummary = result.summary;
    record.logs.push(`[${toolId}] ${result.summary}`);
    if (toolId === 'sandbox') writeNodeContexts(scope, record, node, [], true);
  }

  async function dispatchAgent(context, scope, node, record, inputs, handoff, directory, transport) {
    const { definition, run } = context;
    const isPrompt = nodeKind(node) === 'prompt';
    const agentId = isPrompt ? 'prompt' : node.agentId;
    const role = isPrompt ? 'connector' : 'agent';
    if (!agentId) throw new Error(`Agent node '${node.id}' has no agent bound`);
    const agent = (definition.catalogId && storage.getDispatchAgent(agentId, definition.catalogId)) || storage.getDispatchAgent(agentId);
    if (!agent) throw new Error(`Unknown ${role} '${agentId}' in node '${node.id}'`);
    const modes = Array.isArray(agent.modes) ? agent.modes : Object.entries(agent.modes ?? {}).map(([id, mode]) => ({ ...mode, id }));
    const requestedMode = isPrompt ? node.toolMode ?? node.agentMode : node.agentMode;
    const mode = requestedMode ? modes.find(candidate => lower(candidate.id) === lower(requestedMode)) : modes[0];
    const modeId = mode?.id ?? requestedMode ?? null;
    const workspace = workspaceFor(context, scope, node);
    if (mode?.requiresWorkspace && !workspace) throw new Error(`${role} mode '${modeId}' requires a workspace`);
    const { providerId, model } = node.execution ?? {};
    if (model && !providerId) throw new Error(`Node '${node.id}' must specify the provider for its model override`);
    const upstream = handoff.sources.filter(source => source.artifactDirectory
      && (source.kind !== 'loop-item' || scope.loopEntry === node.id));

    const env = {
      ...definition.env,
      ...(scope.loop ? {
        LOOP_ITEM: scope.loop.itemJson, LOOP_INDEX: String(scope.loop.index), LOOP_TOTAL: String(scope.loop.total),
      } : {}),
      DOXIE_WORKFLOW_ID: definition.id,
      DOXIE_WORKFLOW_ROOT: definition.path ?? path.join(storage.roots?.workflows ?? path.join(storage.root, '.doxie', 'workflows'), definition.id),
      DOXIE_WORKFLOW_OUTPUT_DIR: directory,
      ...(workspace ? { DOXIE_WORKFLOW_WORKSPACE_ID: workspace.id, DOXIE_WORKFLOW_WORKSPACE_PATH: workspace.path } : {}),
      DOXIE_WORKFLOW_INPUT_DIRS: upstream.map(source => source.artifactDirectory).join(';'),
    };
    for (const [key, value] of Object.entries(env)) {
      if (['LOOP_ITEM', 'DOXIE_WORKFLOW_INPUT_DIRS'].includes(key)) continue;
      record.logs.push(`env: ${key}=${/token|secret|password|key|_pat$/i.test(key) ? '***' : value}`);
    }
    if (upstream.length) record.logs.push(`inputs: ${upstream.map(source => `${source.nodeId} (${source.artifacts.length})`).join(', ')}`);
    if (workspace) record.logs.push(`cwd: workspace '${workspace.id}' (${workspace.path})`);
    const templated = mode?.argumentsTemplate;
    const args = typeof templated === 'string'
      ? templated.replace(/\{([\w-]+)\}/g, (placeholder, key) => String(caseInsensitive(inputs, key) ?? '').trim()).replace(/\s+/g, ' ').trim()
      : Object.entries(inputs).map(([key, value]) => `--${key} ${value}`).join(' ');
    record.logs.push(`dispatching ${role} '${agent.name ?? agent.id}' (mode: ${modeId ?? 'default'})`);
    publish(run);

    let mirrored = 0;
    function onOutput(line) {
      if (!line) return;
      const text = typeof line === 'string' ? line : String(line.text ?? '');
      const stderr = lower(line.source) === 'stderr' || line.source === 1;
      record.logs.push(stderr ? `[stderr] ${text}` : text);
      mirrored++;
      if (!context.outputUpdate) {
        context.outputUpdate = setTimeout(() => {
          context.outputUpdate = null;
          publish(run);
        }, 50);
      }
    }

    scope.signal.throwIfAborted();
    const started = await agents.start(agentId, {
      arguments: args, inputs, mode: modeId, modeId, keepSessionAlive: mode?.keepSessionAlive ?? false,
      workspaceId: workspace?.id, workspacePath: workspace?.path, cwd: workspace?.path, env, catalogId: agent.catalogId,
      providerId, model, handoff, guidance: [...context.hints], contextOutputPath: safePath(transport, 'context.json'), workflowRunId: run.id, onOutput,
    });
    const agentRunId = typeof started === 'string' ? started : started?.id;
    if (!agentRunId) throw new Error(`${role} '${agentId}' did not return a run id`);
    context.agentRuns.set(record.nodeId, agentRunId);
    const cancelAgent = () => {
      Promise.resolve().then(() => agents.stop(agentRunId)).catch(error => record.logs.push(`[warning] ${error.message}`));
    };
    scope.signal.addEventListener('abort', cancelAgent, { once: true });
    try {
      if (scope.signal.aborted) cancelAgent();
      record.providerId = started?.providerId ?? null;
      record.model = started?.model ?? null;
      update(run, [record]);
      const result = await agents.wait(agentRunId);
      record.providerId = result?.providerId ?? record.providerId;
      record.model = result?.model ?? record.model;
      for (const line of (result?.output ?? []).slice(mirrored)) onOutput(line);
      scope.signal.throwIfAborted();
      if (!['completed', 'succeeded'].includes(lower(result?.status))) {
        throw new Error(`Agent run ended with status ${result?.status ?? 'unknown'}`);
      }
      await enforceGate(node, directory, inputs);
      const published = await readContextOutput(transport);
      const artifacts = await artifactEntries(directory);
      await manifest(directory, {
        producerId: agent.id, producerName: agent.name ?? agent.id, kind: 'workflow-agent-node', title: `${agent.name ?? agent.id} output`,
        tags: ['workflow', 'agent', definition.id, node.id, agent.id, modeId].filter(Boolean),
        files: artifacts.map(artifact => artifact.relativePath),
      });
      if (published) {
        if (context.contextBytes + published.size > maxRunContextBytes) throw new Error('Workflow context exceeded its 512 KiB run budget');
        context.contextBytes += published.size;
        writeNodeContexts(scope, record, node, [{
          nodeId: node.id, ...(scope.loop ? { iteration: { index: scope.loop.index, total: scope.loop.total } } : {}),
          contextJson: published.contextJson,
        }]);
      }
      record.logs.push('agent run completed (exit ok)');
      if (published) record.logs.push(`Published ${published.size} bytes of transient workflow context`);
      if (!artifacts.length && !published) record.logs.push('No explicit workflow artifacts or context were published. Console output is available in the run logs.');
      record.outputSummary = `${agent.name ?? agent.id} published ${artifacts.length} artifact(s)${published ? ' and transient context' : ''}`;
    } finally {
      scope.signal.removeEventListener('abort', cancelAgent);
      context.agentRuns.delete(record.nodeId);
    }
  }

  async function enforceGate(node, directory, inputs) {
    if (lower(node.agentId) !== 'breaking-stories') return;
    const gateFile = safePath(directory, '.doxie-gate.json');
    const report = safePath(directory, 'breaking-stories-report.md');
    const gate = (await optionalStat(gateFile))?.isFile() ? JSON.parse(await readFile(gateFile, 'utf8')) : null;
    const reportStatus = !gate?.status && (await optionalStat(report))?.isFile()
      ? /Status[^:\r\n]*:\s*([^\r\n]+)/i.exec(await readFile(report, 'utf8'))?.[1].replace(/^[*`\s]+|[*`\s]+$/g, '') : null;
    const status = normalized(gate?.status || reportStatus);
    if (!status) throw new Error('breaking-stories did not produce a gate result or report Status line');
    const pass = ['pass', 'delivery-ready', 'override', 'override-ready'].includes(status);
    const stopOnBlock = !['', '0', 'false', 'no', 'nao', 'não', 'off'].includes(normalized(inputs['fail-on-breaking-block'] ?? inputs['fail-on-block'] ?? 'true'));
    if (!pass && stopOnBlock) throw new Error(`breaking-stories blocked delivery (${status})${gate?.reason ? `: ${gate.reason}` : ''}`);
  }

  async function aggregate(context, node, record, upstream, directory) {
    const { definition } = context;
    const files = upstream.flatMap(source => source.artifacts);
    if (!files.length) {
      record.outputSummary = 'No explicit artifacts to aggregate';
      return;
    }
    const sections = [];
    for (const source of upstream) {
      for (const file of source.artifacts) sections.push(`<!-- from: ${file.relativePath} -->\nSource: ${source.nodeId}\n\n${await describeArtifact(file)}`);
    }
    await writeFile(safePath(directory, 'merged.md'), sections.join('\n\n---\n\n'), 'utf8');
    await manifest(directory, {
      producerId: definition.id, producerName: definition.name, kind: 'workflow-aggregate', title: `${definition.name} aggregate`,
      tags: ['workflow', 'aggregate', definition.id, node.id], files: ['merged.md'],
    }, true);
    record.logs.push(`merged ${files.length} file(s) → merged.md`);
    record.outputSummary = `aggregated ${files.length} file(s) from ${upstream.length} upstream(s)`;
  }

  async function outputToWorkspace(context, scope, node, record, inputs, upstream) {
    const { definition, run } = context;
    const workspaceId = substitute(node.outputWorkspaceId || inputs.workspace, run.triggerInputs).trim();
    if (!workspaceId) {
      record.logs.push('(no target workspace bound - payload discarded)');
      record.outputSummary = 'discarded - no workspace bound';
      return;
    }
    const workspace = storage.get('workspaces', workspaceId);
    if (!workspace) {
      record.logs.push(`Workspace '${workspaceId}' was not found`);
      record.outputSummary = `missing workspace: ${workspaceId}`;
      return;
    }
    const stamp = now().slice(0, 19).replaceAll('-', '').replaceAll(':', '').replace('T', '-');
    const filename = substitute(node.outputFileName || inputs.filename, run.triggerInputs).trim() || `workflow-${stamp}.md`;
    const file = safePath(workspace.path, filename);
    const files = upstream.flatMap(source => source.artifacts);
    if (!files.length) {
      record.outputSummary = 'No explicit artifacts to write to the workspace';
      return;
    }
    const sections = [`# Workflow output (${definition.id})`, `Run \`${run.id}\` delivered at ${now()}.`, `Composed from ${files.length} upstream file(s).`];
    for (const source of upstream) {
      for (const file of source.artifacts) sections.push(`---\n\n## from \`${file.relativePath}\`\n\nSource: ${source.nodeId}\n\n${await describeArtifact(file)}`);
    }
    try {
      await writeFile(file, `${sections.join('\n\n')}\n`, { encoding: 'utf8', signal: scope.signal });
      await manifest(workspace.path, {
        producerId: definition.id, producerName: definition.name, kind: 'workflow-output', title: path.parse(filename).name,
        tags: ['workflow', 'workspace-output', definition.id, node.id, workspace.id], files: [filename],
      }, true);
      record.logs.push(`wrote ${filename} into workspace '${workspace.id}' (${files.length} upstream file(s))`);
      record.outputSummary = `${workspace.id}/${filename}`;
    } catch (error) {
      if (error.name === 'AbortError') throw error;
      record.logs.push(`[warning] could not write '${filename}': ${error.message}`);
      record.outputSummary = '(write failed - see logs)';
    }
  }

  async function decide(context, scope, node, record, inputs, upstream, directory) {
    const { definition } = context;
    const source = inputs.source || inputs.source_file || inputs['source-file'] || 'result.json';
    const selector = inputs.path || inputs.json_path || inputs['json-path'] || null;
    const requested = normalized(inputs.operator || inputs.op || 'truthy').replaceAll('_', '-');
    const operator = decisionOperators.get(requested) ?? requested;
    const expected = inputs.value ?? inputs.expected ?? '';
    const interpolatesLoopItem = Object.values(node.inputs ?? {}).some(value => /\{\{\s*loop\./i.test(String(value)));
    let payload;
    let actual;
    let matched;
    try {
      payload = await upstreamJson(upstream, source, !inputs.source && !inputs.source_file && !inputs['source-file']);
      actual = payload ? getJsonPath(JSON.parse(payload.text), selector) : undefined;
      matched = evaluateDecision(actual, operator, expected, Boolean(payload));
    } catch (error) {
      if (payload?.transient || interpolatesLoopItem) throw new Error('Could not evaluate the decision from transient inputs.');
      throw error;
    }
    const redact = payload?.transient || interpolatesLoopItem;
    const selectedBranch = String(matched);
    const reason = redact ? (matched ? 'condition matched' : 'condition did not match')
      : !payload ? `source '${source}' was not found`
        : actual === undefined ? `path '${selector}' was not found` : matched ? 'condition matched' : 'condition did not match';
    const forwarded = await forwardArtifacts(upstream, directory, new Set(['decision.json']), scope.signal);
    const actualKind = actual === undefined ? null : actual === null ? 'Null' : Array.isArray(actual) ? 'Array'
      : typeof actual === 'boolean' ? actual ? 'True' : 'False' : `${typeof actual}`.replace(/^./, letter => letter.toUpperCase());
    await writeJson(safePath(directory, 'decision.json'), {
      selectedBranch, matched,
      ...(redact ? {} : { source, sourcePath: payload?.sourcePath ?? null, path: selector, operator, expected,
        actual: actual === undefined ? null : scalar(actual), actualKind }),
      reason, evaluatedAt: now(),
    });
    await manifest(directory, {
      producerId: 'if-else', producerName: 'If/Else', kind: 'workflow-decision-node', title: `${definition.name} decision`,
      tags: ['workflow', 'decision', definition.id, node.id, selectedBranch], files: [...forwarded, 'decision.json'],
    });
    record.logs.push(`[if-else] selected branch '${selectedBranch}' (${reason}); forwarded ${forwarded.length} upstream file(s)`);
    record.outputSummary = `if/else: ${selectedBranch}`;
    return selectedBranch;
  }

  async function approval(context, scope, node, record, inputs, upstream, directory) {
    const { definition, run } = context;
    const key = approvalKey(record);
    scope.signal.throwIfAborted();
    let onAbort;
    const decision = new Promise((resolve, reject) => {
      onAbort = () => reject(scope.signal.reason);
      scope.signal.addEventListener('abort', onAbort, { once: true });
      context.approvals.set(key, resolve);
    });
    decision.catch(() => {});
    record.status = 'AwaitingApproval';
    record.logs.push('[approval-gate] paused - waiting for human decision');
    if (inputs.prompt?.trim()) record.logs.push(`[approval-gate] prompt: ${inputs.prompt}`);
    run.status = 'AwaitingApproval';
    let result;
    try {
      update(run, [record]);
      sendNotification({
        title: `Approval requested · ${definition.name ?? definition.id}`,
        body: inputs.prompt || `Review ${node.label ?? node.id} to continue the workflow.`,
        severity: 'warning', href: workflowHref(run), agentId: `workflow:${run.workflowId}`,
      });
      result = await decision;
    } finally {
      scope.signal.removeEventListener('abort', onAbort);
      context.approvals.delete(key);
    }
    run.status = context.approvals.size ? 'AwaitingApproval' : 'Running';
    if (!result.approved) {
      const reason = result.comment || '(no reason given)';
      record.outputSummary = `rejected: ${reason}`;
      record.logs.push(`[approval-gate] rejected - ${reason}`);
      stop(run.id);
      throw new Error(`Approval gate rejected: ${reason}`);
    }
    record.logs.push(`[approval-gate] approved${result.comment ? ` - ${result.comment}` : ''}`);
    record.outputSummary = 'approved';
    const forwarded = await forwardArtifacts(upstream, directory, new Set(['_approval-comment.md']), scope.signal);
    if (result.comment) {
      await writeFile(safePath(directory, '_approval-comment.md'), result.comment, 'utf8');
    }
    record.logs.push(`[approval-gate] forwarded ${forwarded.length} upstream file(s)`);
  }

  async function executeLoop(context, parentScope, node, record, inputs, upstream, directory) {
    const { definition, run } = context;
    const bodyNodes = definition.nodes.filter(candidate => candidate.loopId === node.id);
    const resultFile = safePath(directory, 'results.json');
    if (!bodyNodes.length) {
      await writeJson(resultFile, []);
      record.outputSummary = 'loop: empty body';
      return;
    }
    const bodyIds = new Set(bodyNodes.map(candidate => candidate.id));
    const bodyEdges = definition.edges.filter(edge => bodyIds.has(edge.fromNodeId) && bodyIds.has(edge.toNodeId));
    const entries = [...new Set(definition.edges.filter(edge => edge.fromNodeId === node.id && bodyIds.has(edge.toNodeId)).map(edge => edge.toNodeId))];
    const exits = [...new Set(definition.edges.filter(edge => bodyIds.has(edge.fromNodeId) && !bodyIds.has(edge.toNodeId)).map(edge => edge.fromNodeId))];
    if (entries.length !== 1) throw new Error(`Loop '${node.id}' needs one body entry. Found ${entries.length}`);
    if (exits.length > 1) throw new Error(`Loop '${node.id}' allows one body exit. Found ${exits.length}`);
    const [entry] = entries;
    const exit = exits[0] ?? bodyNodes.find(candidate => !bodyEdges.some(edge => edge.fromNodeId === candidate.id))?.id ?? entry;
    const source = inputs.array_source || 'result.json';
    const payload = await upstreamJson(upstream, source, !inputs.array_source);
    const array = payload ? getJsonPath(JSON.parse(payload.text), inputs.array_path) : [];
    if (!Array.isArray(array)) throw new Error(`Loop array source '${source}' does not contain a JSON array at '${inputs.array_path || '(root)'}'`);
    const arrayNode = payload ? getJsonNode(parseTree(payload.text), inputs.array_path) : undefined;
    const parsedConcurrency = Number(inputs.concurrency);
    const concurrency = inputs.concurrency && Number.isSafeInteger(parsedConcurrency) ? Math.max(1, parsedConcurrency) : 4;
    const continueOnFailure = normalized(inputs.on_failure) === 'continue';
    record.logs.push(`[loop] ${array.length} item(s), concurrency=${concurrency}, on_failure=${continueOnFailure ? 'continue' : 'fail-fast'}`);

    for (const bodyNode of bodyNodes) {
      const bodyRun = run.nodeRuns[bodyNode.id];
      bodyRun.status = 'Running';
      bodyRun.startedAt = now();
      bodyRun.logs.push(`[loop] running via '${node.id}'. See ${bodyNode.id}#iter-* for iteration detail.`);
    }
    update(run, bodyNodes.map(bodyNode => run.nodeRuns[bodyNode.id]));
    const failFast = new AbortController();
    const signal = AbortSignal.any([parentScope.signal, failFast.signal]);
    const outcomes = new Array(array.length);
    const iterationContexts = new Array(array.length);
    let next = 0;
    async function worker() {
      while (!signal.aborted && next < array.length) {
        const index = next++;
        const itemNode = arrayNode?.children?.[index];
        const itemJson = itemNode ? payload.text.slice(itemNode.offset, itemNode.offset + itemNode.length) : JSON.stringify(array[index]);
        const iteration = { index, item: array[index], itemJson, itemNode, sourceJson: payload?.text, total: array.length };
        const iterationDir = path.join(directory, `iter-${index}`);
        try {
          await mkdir(safePath(directory, `iter-${index}`), { recursive: true });
          const loopInput = {
            kind: 'loop-item', nodeId: node.id,
            artifacts: [{ relativePath: 'item.json', mediaType: 'application/json', sizeBytes: Buffer.byteLength(itemJson), content: itemJson, transient: true }],
          };
          const scope = {
            loop: iteration, loopEntry: entry, signal, base: iterationDir, outputDir: id => path.join(iterationDir, id), contexts: new Map(),
            nodeRun: node => run.nodeRuns[`${node.id}#iter-${index}`],
            inputSources: async (node, edges) => {
              const sources = await getInputSources(scope, edges);
              return node.id === entry ? [loopInput, ...sources] : [...sources, loopInput];
            },
          };
          for (const bodyNode of bodyNodes) {
            const key = `${bodyNode.id}#iter-${index}`;
            run.nodeRuns[key] = createNodeRun(key);
          }
          await executeGraph(context, scope, bodyNodes, bodyEdges);
          const exitRun = scope.nodeRun({ id: exit });
          const outputDir = scope.outputDir(exit);
          const success = exitRun.status === 'Succeeded';
          const error = success ? null : bodyNodes.flatMap(bodyNode => scope.nodeRun(bodyNode).logs)
            .find(line => line.startsWith('[error]')) ?? 'iteration failed';
          outcomes[index] = { index, success, outputDir, error, files: (await artifactEntries(outputDir)).map(file => file.relativePath) };
          if (success) iterationContexts[index] = scope.contexts.get(exit);
        } catch (error) {
          outcomes[index] = { index, success: false, outputDir: iterationDir, error: error.message, files: [] };
        }
        if (!outcomes[index].success && !continueOnFailure) failFast.abort();
      }
    }
    await Promise.all(Array.from({ length: Math.min(concurrency, array.length) }, () => worker()));
    const complete = Array.from(outcomes, (outcome, index) => outcome ?? {
      index, success: false, outputDir: path.join(directory, `iter-${index}`), error: 'cancelled before start', files: [],
    });
    const successes = complete.filter(outcome => outcome.success);
    await writeJson(resultFile, continueOnFailure ? successes : complete);
    const retainedContexts = iterationContexts.filter(Boolean);
    writeNodeContexts(parentScope, record, node, retainedContexts.flat(), retainedContexts.length > 0);
    record.outputSummary = `loop: ${successes.length}/${array.length} succeeded`;
    record.logs.push(`[loop] ${record.outputSummary} → results.json`);
    for (const bodyNode of bodyNodes) {
      const bodyRun = run.nodeRuns[bodyNode.id];
      const states = complete.map(({ index }) => run.nodeRuns[`${bodyNode.id}#iter-${index}`]?.status);
      const succeeded = states.filter(status => status === 'Succeeded').length;
      const failed = states.includes('Failed');
      bodyRun.status = failed && (!continueOnFailure || !succeeded) ? 'Failed' : succeeded ? 'Succeeded' : 'Skipped';
      bodyRun.finishedAt = now();
      bodyRun.outputSummary = `loop body: ${succeeded}/${array.length} iter(s) succeeded`;
    }
    parentScope.signal.throwIfAborted();
    if (!continueOnFailure && successes.length < array.length) {
      throw new Error(`Loop failed: ${array.length - successes.length}/${array.length} iteration(s) failed (on_failure=fail-fast)`);
    }
  }

  function workflowHref(run) {
    return `/workflows/${encodeURIComponent(run.workflowId)}?run=${encodeURIComponent(run.id)}`;
  }

  function sendNotification(notification) {
    Promise.resolve().then(() => notify({ ...notification, message: notification.body })).catch(error => {
      console.error('Could not publish workflow notification', error);
    });
  }

  async function notifyCompletion({ definition, run }) {
    const nodes = Object.values(run.nodeRuns);
    const succeeded = nodes.filter(node => node.status === 'Succeeded').length;
    const failed = nodes.filter(node => node.status === 'Failed').length;
    const skipped = nodes.filter(node => node.status === 'Skipped').length;
    const severity = run.status === 'Succeeded' ? 'success' : run.status === 'Cancelled' ? 'warning' : 'error';
    let body = `Workflow ${lower(run.status)} · ${succeeded}/${nodes.length} succeeded · ${failed} failed · ${skipped} skipped (${run.triggeredBy})`;
    let preview;
    try {
      const order = { Failed: 0, Succeeded: 1 };
      const sorted = nodes.filter(node => !node.nodeId.includes('#iter-')).sort((left, right) =>
        (order[left.status] ?? 2) - (order[right.status] ?? 2) || Date.parse(right.finishedAt) - Date.parse(left.finishedAt));
      for (const node of sorted) {
        preview = await previewArtifact(safePath(runsRoot, path.join(run.id, node.nodeId)));
        if (preview) break;
      }
      preview ??= await previewArtifact(safePath(runsRoot, run.id));
      if (preview) {
        const raw = preview.contentFormat === 'html' ? preview.content.replace(/<[^>]+>/g, ' ') : preview.content;
        const excerpt = raw.replace(/<!--[^]*?-->/g, ' ').replace(/\s+/g, ' ').trim().slice(0, 180);
        body += ` · ${path.basename(preview.sourcePath)}${excerpt ? `: ${excerpt}` : ''}`;
      }
    } catch (error) {
      console.error(`Could not preview workflow ${run.id}`, error);
    }
    sendNotification({ ...preview, title: definition.name ?? definition.id, body, severity, href: workflowHref(run), agentId: `workflow:${run.workflowId}` });
  }

  function tick(date = clock()) {
    if (closed) return;
    const minute = 60_000;
    const current = Math.floor(date.getTime() / minute) * minute;
    const seen = new Set();
    for (const workflow of storage.list('workflows')) {
      if (readEnum(triggerKinds, workflow.trigger?.kind) !== 'cron' || workflow.enabled === false) continue;
      const expression = workflow.trigger.cronExpression;
      if (!cronFields(expression)) continue;
      const key = `${workflow.catalogId ?? 'default'}/${workflow.id}/${expression}`;
      seen.add(key);
      const previous = cronCursors.get(key);
      if (!previous || current < previous.at) {
        cronCursors.set(key, { at: current, pending: false });
        continue;
      }
      const next = previous.pending ? null : nextCron(expression, previous.at);
      const due = previous.pending || (next !== null && next.getTime() <= current);
      const busy = [...active.values()].some(({ run }) => !terminal.has(run.status)
        && run.workflowId === workflow.id && run.catalogId === (workflow.catalogId ?? null));
      cronCursors.set(key, { at: current, pending: due && busy });
      if (!due || busy) continue;
      try {
        launch(workflow, `cron:${expression}`, {});
      } catch (error) {
        console.error(`Could not start scheduled workflow ${workflow.id}`, error);
      }
    }
    for (const key of cronCursors.keys()) {
      if (!seen.has(key)) cronCursors.delete(key);
    }
  }

  function start() {
    if (timer || closed) return;
    const schedule = () => {
      try { tick(); }
      catch (error) { console.error('Could not check workflow schedules', error); }
      const minute = 60_000;
      timer = setTimeout(schedule, minute - (clock().getTime() % minute));
      timer.unref?.();
    };
    schedule();
  }

  async function close() {
    closed = true;
    clearTimeout(timer);
    const jobs = [...active.entries()];
    for (const [id] of jobs) stop(id);
    await Promise.allSettled([...interrupted, ...jobs.map(([, context]) => context.task)]);
  }

  return { run, listRuns, getRun, wait, approve, stop, rerun, addHint, clearHistory: store.clear, start, tick, close };
}

module.exports = { createWorkflows, parseCron, nextCron };
