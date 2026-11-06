const { randomUUID } = require('node:crypto');
const { existsSync, mkdirSync, readFileSync, readdirSync, renameSync, writeFileSync } = require('node:fs');
const { join } = require('node:path');
const { parse } = require('jsonc-parser');
const { settingsFor } = require('./providers.cjs');

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

function createBuilders({ storage, agents, emit }) {
  const current = new Set();
  const base = storage.roots.sandboxes;
  mkdirSync(base, { recursive: true });

  function paths(id) {
    if (!/^builder-(agent|workflow)-[a-z0-9-]+$/.test(id)) throw new Error('Invalid builder session.');
    const directory = join(base, id);
    return { directory, draft: join(directory, '.draft') };
  }

  function get(id) {
    const { draft } = paths(id);
    const saved = read(join(draft, 'desktop-session.json'));
    const manifest = read(join(draft, 'manifest.json'));
    if (!saved && !existsSync(draft)) return null;
    const legacy = read(join(draft, 'builder-meta.json')) || {};
    const kind = id.startsWith('builder-agent-') ? 'agent' : 'workflow';
    return {
      id, kind, messages: [], status: 'Ready', editingId: legacy.editAgentId || legacy.editWorkflowId,
      ...saved, ...(current.has(id) ? { status: 'Running' } : {}), draft: manifest,
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
    const { draft: manifest, ...data } = session;
    data.updatedAt = new Date().toISOString();
    write(join(draft, 'desktop-session.json'), data);
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
    const skill = storage.get('skills', `${kind}-builder`);
    const skillBody = skill?.body || skill?.content || readFileSync(join(storage.roots.skills, `${kind}-builder`, 'body.md'), 'utf8');
    mkdirSync(join(draft, 'new-agents'), { recursive: true });
    const context = [
      `# DoxieOS ${kind} builder`,
      'Use the canonical skill below to work with the user and update .draft/manifest.json in this sandbox. The user will review the draft before promotion. Keep edits inside this sandbox.',
      workspace ? `Attached workspace for reference: ${workspace.path}` : '',
      `Canonical catalog: ${storage.root}`,
      skillBody,
    ].filter(Boolean).join('\n\n');
    for (const name of ['CLAUDE.md', 'AGENTS.md']) writeFileSync(join(directory, name), context);
    if (original) write(join(draft, 'manifest.json'), original);
    return persist({
      id, kind, workspaceId: workspace?.id, editingId, catalogId: params.catalogId || original?.catalogId,
      providerId: params.providerId || settingsFor(storage).defaultProvider, status: 'Ready', messages: [],
    });
  }

  async function message(id, text) {
    const session = get(id);
    if (!session) throw new Error('Builder session not found.');
    if (current.has(id)) throw new Error('The builder is already responding.');
    if (typeof text !== 'string' || !text.trim()) throw new Error('Enter a message.');
    const { directory } = paths(id);
    const providerId = session.providerId || settingsFor(storage).defaultProvider;
    const prompt = session.providerSessionId ? text : `Read AGENTS.md in this sandbox for the Doxie builder instructions.\n\nUser request:\n${text}`;
    session.messages.push({ role: 'user', text });
    current.add(id);
    try {
      persist(session);
      const job = agents.runBuilder(session.kind, { cwd: directory, providerId, sessionId: session.providerSessionId, prompt });
      const result = await agents.wait(job.id);
      session.providerSessionId = result.sessionId || session.providerSessionId;
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
    return { ...manifest, modes, body: manifest.systemPrompt || manifest.body || manifest.content };
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
