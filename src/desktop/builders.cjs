const { randomUUID } = require('node:crypto');
const { existsSync, mkdirSync, readFileSync, readdirSync, renameSync, rmSync, statSync, writeFileSync } = require('node:fs');
const { join, relative, resolve } = require('node:path');
const { parse } = require('jsonc-parser');
const { settingsFor } = require('./providers.cjs');
const { normalizeExecution, resolveExecution } = require('./execution.cjs');
const { safePath } = require('./storage.cjs');

const safeId = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const knownKinds = new Set(['agent', 'workflow']);
const providerNames = { codex: 'Codex', 'claude-code': 'Claude Code', ollama: 'Ollama', lmstudio: 'LM Studio' };

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

function builderRequest(messages, text, stage = 'message') {
  const recent = [];
  let size = 0;
  for (const turn of messages.slice(-20).reverse()) {
    size += Buffer.byteLength(JSON.stringify(turn));
    if (size > 64 * 1024) break;
    recent.unshift(turn);
  }
  const request = { schema: 'doxie.builder-request.v1', stage, previousTurns: recent, omittedTurns: messages.length - recent.length, message: text };
  return [
    'Read AGENTS.md for this builder’s instructions. Inspect the existing draft before changing it.',
    'Continue from the explicitly provided conversation below. If omittedTurns is greater than zero, the full transcript is available in .draft/desktop-session.json.',
    JSON.stringify(request, null, 2),
  ].join('\n\n');
}

function describeFailure(providerId, diagnostics, { id: runId, status } = {}) {
  const provider = providerNames[providerId] || 'The provider';
  const source = { providerId, ...(runId ? { runId } : {}) };
  if (/\b(?:unauthori[sz]ed|401\s+(?:client error|unauthorized))\b|missing (?:bearer|api key)|authentication (?:failed|required)|not logged in|(?:run|please)\s+\S*\s*(?:login|sign in)|(?:login|sign.in) (?:is )?required/i.test(diagnostics)) {
    return { ...source, kind: 'authentication', message: providerId === 'codex'
      ? 'Sign in to the installed Codex CLI with ChatGPT, then retry this message.'
      : `Sign in to ${provider}, then retry this message.` };
  }
  if (/\b(?:429|rate.limit|quota exceeded|too many requests)\b/i.test(diagnostics)) {
    return { ...source, kind: 'rate-limit', message: `${provider} reached a usage limit. Retry when it is available or change the builder model.` };
  }
  if (['Cancelled', 'Interrupted'].includes(status)) {
    return { ...source, kind: 'cancelled', message: 'The builder response was interrupted. Retry the last message to continue.' };
  }
  if (/\bCLI was not found\b/i.test(diagnostics)) {
    return { ...source, kind: 'provider', message: `Install ${provider} or configure its executable in Settings, then retry.` };
  }
  return { ...source, kind: 'provider', message: `${provider} could not finish this response. Check the provider and model, then retry.` };
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
    const session = {
      id, kind, messages: [], status: 'Ready', editingId: legacy.editAgentId || legacy.editWorkflowId,
      ...saved, ...(current.has(id) ? { status: 'Running' } : {}), draft: latest ? read(latest.path) : null,
    };
    if (session.status === 'Running' && !current.has(id)) {
      session.status = 'Failed';
      session.failure = describeFailure(session.providerId, '', { status: 'Interrupted' });
    }
    if (session.status === 'Failed' && !session.failure) {
      const last = session.messages.at(-1);
      session.failure = describeFailure(session.providerId, last?.role === 'assistant' ? last.text || last.content || '' : '');
      // Older releases appended the failed provider's diagnostics as an assistant turn.
      if (last?.role === 'assistant') session.messages = session.messages.slice(0, -1);
    }
    return session;
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
    const session = persist({
      id, kind, workspaceId: workspace?.id, editingId, catalogId: params.catalogId || original?.catalogId,
      ...execution, status: 'Ready', messages: [],
    });
    if (params.initialize === true) {
      const language = settingsFor(storage).language;
      const request = `Begin the ${kind} builder conversation in ${language}. Briefly introduce your role and ask one clear question about ${original ? 'the changes the user wants to make to the existing draft' : 'what the new agent should do'}. Wait for the user's requirements before creating or changing a manifest.`;
      session.initialPrompt = request;
      void respond(session, request, false, true).catch(error => console.error('Could not persist builder initialization:', error.message));
      return get(id);
    }
    return session;
  }

  function idleSession(id) {
    const session = get(id);
    if (!session) throw new Error('Builder session not found.');
    if (current.has(id)) throw new Error('The builder is already responding.');
    return session;
  }

  function remove(id) {
    const session = idleSession(id);
    const directory = resolve(paths(id).directory);
    if (relative(resolve(base), directory) !== id) throw new Error('Invalid builder session path.');
    rmSync(directory, { recursive: true });
    emit({ type: 'builder:removed', id, kind: session.kind });
    return true;
  }

  function updateExecution(id, params = {}) {
    const session = idleSession(id);
    const builder = storage.get('agents', `${session.kind}-builder`, 'default');
    const execution = resolveExecution(settingsFor(storage), builder?.execution, params);
    return persist({ ...session, effort: null, ...execution });
  }

  function updateDraftExecution(id, execution = null) {
    const session = idleSession(id);
    if (session.kind !== 'agent') throw new Error('Select execution on the workflow nodes.');
    if (!session.draft || typeof session.draft !== 'object' || Array.isArray(session.draft)) {
      throw new Error('There is no agent draft to configure.');
    }
    const { draft } = paths(id);
    write(safePath(draft, 'manifest.json'), { ...session.draft, execution: normalizeExecution(execution) });
    return persist(session);
  }

  function message(id, text) {
    if (typeof text !== 'string' || !text.trim()) throw new Error('Enter a message.');
    return respond(idleSession(id), text, false);
  }

  function retry(id) {
    const session = idleSession(id);
    if (session.failure && session.initializing && session.initialPrompt) return respond(session, session.initialPrompt, true, true);
    const turn = session.messages.at(-1);
    const text = turn?.text ?? turn?.content;
    if (!session.failure || turn?.role !== 'user' || typeof text !== 'string' || !text.trim()) throw new Error('There is no failed builder message to retry.');
    return respond(session, text, true);
  }

  async function respond(session, text, retrying, initializing = false) {
    const { id } = session;
    const { directory, draft } = paths(id);
    const providerId = session.providerId || settingsFor(storage).defaultProvider;
    const prompt = builderRequest(retrying && !initializing ? session.messages.slice(0, -1) : session.messages, text, initializing ? 'initialize' : 'message');
    if (!retrying && !initializing) session.messages.push({ role: 'user', text });
    session.initializing = initializing;
    if (!initializing) delete session.initialPrompt;
    session.status = 'Running';
    session.failure = null;
    current.add(id);
    try {
      persist(session);
      const job = agents.runBuilder(session.kind, { cwd: directory, providerId, model: session.model, effort: session.effort, prompt });
      const result = await agents.wait(job.id);
      const output = result?.output || [];
      if (result?.status === 'Completed') {
        const reply = output.filter(item => item.source.toLowerCase() === 'stdout' && !item.text.startsWith('▶')).map(item => item.text).join('\n');
        if (reply || read(safePath(draft, 'manifest.json'))) {
          session.status = 'Ready';
          session.initializing = false;
          delete session.initialPrompt;
          session.messages.push({ role: 'assistant', text: reply || 'The draft is ready to review.' });
        } else {
          session.failure = { kind: 'response', providerId, runId: job.id, message: 'The provider returned no response or draft. Retry the last message.' };
        }
      } else {
        session.failure = describeFailure(providerId, output.map(item => item.text).join('\n'), { id: job.id, status: result?.status });
      }
    } catch (error) {
      session.failure = describeFailure(providerId, error.message);
    } finally {
      current.delete(id);
    }
    if (session.failure) session.status = 'Failed';
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
    const body = manifest.body || manifest.systemPrompt || manifest.content;
    if (typeof body !== 'string' || !body.trim()) throw new Error('The draft needs agent instructions. Ask the builder to add them before saving.');
    return { ...manifest, modes, execution: normalizeExecution(manifest.execution), body };
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

  return { start, get, list, remove, message, retry, updateExecution, updateDraftExecution, promote };
}

module.exports = { createBuilders };
