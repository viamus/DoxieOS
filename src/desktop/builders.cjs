const { randomUUID } = require('node:crypto');
const { existsSync, mkdirSync, readFileSync, readdirSync, renameSync, statSync, writeFileSync } = require('node:fs');
const { join } = require('node:path');
const { parse } = require('jsonc-parser');
const { settingsFor } = require('./providers.cjs');
const { normalizeExecution, resolveExecution } = require('./execution.cjs');
const { safePath } = require('./storage.cjs');

const safeId = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const knownKinds = new Set(['agent', 'workflow']);

function read(path) {
  try {
    const errors = [];
    const data = parse(readFileSync(path, 'utf8').replace(/^\uFEFF/, ''), errors, { allowTrailingComma: true });
    return errors.length ? null : data;
  } catch {
    return null;
  }
}

function write(path, data) {
  const temporary = `${path}.${randomUUID()}.tmp`;
  writeFileSync(temporary, JSON.stringify(data, null, 2) + '\n', { mode: 0o600 });
  renameSync(temporary, path);
}

function builderRequest(messages, text) {
  const recent = [];
  let size = 0;
  for (const turn of messages.slice(-20).reverse()) {
    size += Buffer.byteLength(JSON.stringify(turn));
    if (size > 64 * 1024) break;
    recent.unshift(turn);
  }
  const request = { schema: 'doxie.builder-request.v1', previousTurns: recent, omittedTurns: messages.length - recent.length, message: text };
  return [
    'Read AGENTS.md for this builder’s instructions. Inspect the existing draft before changing it.',
    'Continue from the explicitly provided conversation below. If omittedTurns is greater than zero, the full transcript is available in .draft/desktop-session.json.',
    JSON.stringify(request, null, 2),
  ].join('\n\n');
}

function createBuilders({ storage, agents, emit }) {
  const current = new Set();
  const base = storage.roots.sandboxes;
  mkdirSync(base, { recursive: true });

  function paths(id) {
    if (!/^builder-(agent|workflow)-[a-z0-9-]+$/.test(id)) throw new Error('Invalid builder session.');
    const directory = safePath(base, id);
    return { directory, draft: safePath(directory, '.draft') };
  }

  function get(id) {
    const { draft } = paths(id);
    const saved = read(safePath(draft, 'desktop-session.json'));
    if (!saved && !existsSync(draft)) return null;
    const legacy = read(safePath(draft, 'builder-meta.json')) || {};
    const kind = id.startsWith('builder-agent-') ? 'agent' : 'workflow';
    const drafts = [safePath(draft, 'manifest.json')];
    if (kind === 'workflow') drafts.push(safePath(draft, 'workflow.json'));
    const latest = drafts.map(path => ({ path, time: statSync(path, { throwIfNoEntry: false })?.mtimeMs }))
      .filter(candidate => candidate.time !== undefined).sort((a, b) => b.time - a.time)[0];
    return {
      id, kind, messages: [], status: 'Ready', editingId: legacy.editAgentId || legacy.editWorkflowId,
      ...saved, ...(current.has(id) ? { status: 'Running' } : {}), draft: latest ? read(latest.path) : null,
    };
  }

  function list(kind) {
    const sessions = readdirSync(base, { withFileTypes: true })
      .filter(item => item.isDirectory() && /^builder-(agent|workflow)-[a-z0-9-]+$/.test(item.name))
      .map(item => get(item.name)).filter(item => item && (!kind || item.kind === kind));
    return sessions.sort((a, b) => String(b.updatedAt || '').localeCompare(String(a.updatedAt || '')));
  }

  function persist(session) {
    const { draft } = paths(session.id);
    const { draft: manifest, providerSessionId, ...data } = session;
    data.updatedAt = new Date().toISOString();
    write(safePath(draft, 'desktop-session.json'), data);
    const updated = get(session.id);
    emit({ type: 'builder:changed', session: updated });
    return updated;
  }

  function start(kind, params = {}) {
    if (!knownKinds.has(kind)) throw new Error('Unknown builder.');
    const id = `builder-${kind}-${randomUUID()}`;
    const { directory, draft } = paths(id);
    const editingId = params.editingId || params.editId || params.id;
    const original = editingId ? storage.get(`${kind}s`, editingId, params.catalogId) : null;
    if (editingId && !original) throw new Error('Catalog item not found.');
    const workspace = params.workspaceId ? storage.get('workspaces', params.workspaceId) : null;
    if (params.workspaceId && !workspace) throw new Error('Workspace not found.');
    const builder = storage.get('agents', `${kind}-builder`, 'default');
    if (!builder?.body) throw new Error('The bundled builder agent is missing.');
    const execution = resolveExecution(settingsFor(storage), builder.execution, params);
    mkdirSync(join(draft, 'new-agents'), { recursive: true });
    const context = [
      `# DoxieOS ${kind} builder`,
      'Use the canonical agent instructions below to work with the user and update .draft/manifest.json in this sandbox. The user will review the draft before promotion. Keep edits inside this sandbox.',
      workspace ? `Attached workspace for reference: ${workspace.path}` : '',
      `Canonical catalog: ${storage.root}`,
      builder.body,
    ].filter(Boolean).join('\n\n');
    for (const name of ['CLAUDE.md', 'AGENTS.md']) writeFileSync(join(directory, name), context);
    if (original) write(join(draft, 'manifest.json'), original);
    return persist({
      id, kind, workspaceId: workspace?.id, editingId, catalogId: params.catalogId || original?.catalogId,
      ...execution, status: 'Ready', messages: [],
    });
  }

  async function message(id, text) {
    const session = get(id);
    if (!session) throw new Error('Builder session not found.');
    if (current.has(id)) throw new Error('The builder is already responding.');
    if (typeof text !== 'string' || !text.trim()) throw new Error('Enter a message.');
    const { directory } = paths(id);
    const providerId = session.providerId || settingsFor(storage).defaultProvider;
    const prompt = builderRequest(session.messages, text);
    session.messages.push({ role: 'user', text });
    current.add(id);
    try {
      persist(session);
      const job = agents.runBuilder(session.kind, { cwd: directory, providerId, model: session.model, prompt });
      const result = await agents.wait(job.id);
      session.status = result.status === 'Completed' ? 'Ready' : 'Failed';
      const source = result.status === 'Completed' ? 'stdout' : 'stderr';
      const reply = result.output.filter(item => item.source.toLowerCase() === source && !item.text.startsWith('▶')).map(item => item.text).join('\n');
      session.messages.push({ role: 'assistant', text: reply || `Provider run ${result.status.toLowerCase()}.` });
    } catch (error) {
      session.status = 'Failed';
      session.messages.push({ role: 'assistant', text: error.message });
    } finally {
      current.delete(id);
    }
    return persist(session);
  }

  function validateAgent(manifest) {
    if (!safeId.test(manifest?.id || '') || !String(manifest.name || manifest.displayName || '').trim()) {
      throw new Error('An agent draft needs a kebab-case ID and a name.');
    }
    const modes = Array.isArray(manifest.modes)
      ? Object.fromEntries(manifest.modes.map(({ id, ...mode }) => [id, mode])) : manifest.modes || {};
    for (const [id, mode] of Object.entries(modes)) {
      if (!safeId.test(id)) throw new Error(`Invalid mode ID: ${id}`);
      const fields = new Set((mode.fields || []).map(field => field.id));
      for (const match of (mode.argumentsTemplate || '').matchAll(/\{([\w-]+)\}/g)) {
        if (!fields.has(match[1])) throw new Error(`Mode ${id} references an undefined field: ${match[1]}`);
      }
    }
    return { ...manifest, modes, execution: normalizeExecution(manifest.execution), body: manifest.body || manifest.systemPrompt || manifest.content };
  }

  function promote(id, options = {}) {
    const session = get(id);
    if (!session?.draft || current.has(id)) throw new Error('There is no ready draft to save.');
    const { draft } = paths(id);
    const catalogId = options.catalogId || session.catalogId;
    const manifest = { ...session.draft, catalogId, ...(options.icon ? { icon: options.icon } : {}) };
    const kind = `${session.kind}s`;
    if (!safeId.test(manifest.id || '') || !String(manifest.name || manifest.displayName || '').trim()) throw new Error('The draft needs a kebab-case ID and a name.');
    if (session.editingId && session.editingId !== manifest.id) throw new Error('Keep the original ID when editing an existing item.');
    if (!session.editingId && storage.get(kind, manifest.id, catalogId)) throw new Error('A catalog item with this ID already exists.');
    if (session.kind === 'workflow') {
      const nodeIds = new Set((manifest.nodes || []).map(node => node.id));
      if (!nodeIds.size || nodeIds.size !== manifest.nodes.length) throw new Error('The workflow needs nodes with distinct IDs.');
      for (const edge of manifest.edges || []) {
        if (!nodeIds.has(edge.fromNodeId) || !nodeIds.has(edge.toNodeId)) throw new Error('The draft contains an edge with an unknown node.');
      }
      for (const node of manifest.nodes) {
        if (Object.hasOwn(node, 'execution')) node.execution = normalizeExecution(node.execution);
      }
      const newAgentsDirectory = join(draft, 'new-agents');
      const newAgents = existsSync(newAgentsDirectory) ? readdirSync(newAgentsDirectory, { withFileTypes: true }) : [];
      const items = newAgents.filter(item => item.isFile() && item.name.endsWith('.json'))
        .map(item => validateAgent(read(join(newAgentsDirectory, item.name))));
      for (const agent of items) {
        if (!storage.get('agents', agent.id, catalogId)) storage.save('agents', { ...agent, catalogId });
      }
    }
    const result = storage.save(kind, session.kind === 'agent' ? validateAgent(manifest) : manifest);
    session.status = 'Saved';
    persist(session);
    emit({ type: 'catalog:changed', kind });
    return result;
  }

  return { start, get, list, message, promote };
}

module.exports = { createBuilders };
