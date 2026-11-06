const { randomUUID } = require('node:crypto');
const { appendFileSync, existsSync, mkdirSync, readFileSync, statSync, writeFileSync } = require('node:fs');
const { isAbsolute, join, relative, resolve, sep } = require('node:path');
const { createInterface } = require('node:readline');
const { launch, killTree } = require('./processes.cjs');
const { providerFor, headlessArgs, inputMessage, providerOutput } = require('./providers.cjs');

const now = () => new Date().toISOString();
const usageColumns = {
  output_dir: 'TEXT', input_tokens: 'INTEGER', output_tokens: 'INTEGER',
  cache_read_tokens: 'INTEGER', cache_creation_tokens: 'INTEGER', total_cost_usd: 'REAL',
  provider_id: 'TEXT',
};

function matches(condition, context) {
  if (!condition?.trim()) return true;
  return condition.split(/\s+AND\s+/i).every(atom => {
    if (/^always$/i.test(atom.trim())) return true;
    const comparison = atom.match(/^\s*(mode|provider|workspace_id)\s*(!=|=)\s*(["'])(.*?)\3\s*$/i);
    if (!comparison) return false;
    const equal = context[comparison[1].toLowerCase()] === comparison[4];
    return comparison[2] === '=' ? equal : !equal;
  });
}

function agentModes(agent) {
  if (Array.isArray(agent.modes)) return agent.modes;
  return Object.entries(agent.modes || {}).map(([id, mode]) => ({ ...mode, id }));
}

function argumentsFor(params, mode) {
  const inputs = params.inputs || {};
  if (params.arguments?.trim()) return params.arguments.trim();
  for (const field of mode?.fields || []) {
    if (field.required && !String(inputs[field.id] ?? '').trim()) throw new Error(`${field.label || field.id} is required.`);
  }
  return mode?.argumentsTemplate?.replace(/\{([\w-]+)\}/g, (_, key) => String(inputs[key] ?? '')).trim()
    || Object.values(inputs).filter(value => value !== '').join(' ');
}

function skillContents(storage, agent, skillId, catalogId, args, inlined) {
  const skill = storage.get('skills', skillId, catalogId) || storage.get('skills', skillId);
  const skillRoot = skill?.path || skill?.directory || join(storage.roots.skills, skillId);
  const command = args.split(/\s+/, 1)[0];
  let body = skill?.body || skill?.content || agent.body || agent.content || '';
  if (inlined && /^[a-z][\w-]*$/i.test(command) && command !== 'body') {
    try { body = storage.readFile('skills', skillId, `${command}.md`, skill?.catalogId).content || body; } catch {}
  }
  if (!body && existsSync(join(skillRoot, 'body.md'))) body = readFileSync(join(skillRoot, 'body.md'), 'utf8');
  return { skill, skillRoot, body };
}

function reportedOutput(storage, lines, cwd) {
  for (const { text, source } of lines) {
    if (source !== 'Stdout') continue;
    try {
      const { output_dir: directory } = JSON.parse(text.trim().replace(/^```(?:json)?\s*|\s*```$/gi, ''));
      if (typeof directory !== 'string' || !directory.trim()) continue;
      const path = resolve(cwd, directory);
      const confined = [storage.roots.runs, storage.roots.sandboxes].some(root => {
        const child = relative(root, path);
        return child && child !== '..' && !child.startsWith(`..${sep}`) && !isAbsolute(child);
      });
      if (confined && statSync(path).isDirectory()) return path;
    } catch {}
  }
  return null;
}

function collectMemory(storage, skill, workspace, context) {
  const priority = { high: 3, medium: 2, low: 1 };
  const memory = (Array.isArray(skill?.memories) ? skill.memories : [])
    .filter(item => matches(item.condition, context))
    .sort((a, b) => (priority[String(b.priority).toLowerCase()] || 0) - (priority[String(a.priority).toLowerCase()] || 0))
    .map(item => `### ${item.name || item.fileName}\n${item.body || item.content || ''}`);
  if (workspace) {
    const addWorkspaceFile = relative => {
      try {
        const result = storage.readFile('workspaces', workspace.id, relative);
        const text = Buffer.isBuffer(result) ? result.toString('utf8') : result?.text || result?.content;
        if (text) memory.push(`### Workspace: ${relative}\n${text}`);
      } catch {}
    };
    addWorkspaceFile('WORKSPACE.md');
    try {
      const queue = ['memory'];
      for (const folder of queue) {
        for (const entry of storage.listFiles('workspaces', workspace.id, folder)) {
          const relative = entry.path || entry.relativePath;
          if (!relative || queue.length > 100 || memory.length > 100) break;
          if (entry.isDirectory || entry.kind === 'directory') queue.push(relative);
          else if (relative.endsWith('.md')) addWorkspaceFile(relative);
        }
      }
    } catch {}
    for (const id of workspace.mountedLibraryIds || workspace.libraryIds || []) {
      const library = storage.get('libraries', id);
      for (const item of library?.memories || []) memory.push(`### ${library.name}: ${item.name}\n${item.content}`);
    }
  }
  return memory.join('\n\n').slice(0, 60_000);
}

function createAgents({ storage, emit = () => {}, notify = () => {} }) {
  const { db } = storage;
  db.exec(`
    CREATE TABLE IF NOT EXISTS agent_runs (
      id TEXT PRIMARY KEY NOT NULL, agent_id TEXT NOT NULL, arguments TEXT NOT NULL,
      started_at TEXT NOT NULL, finished_at TEXT, status TEXT NOT NULL, exit_code INTEGER
    );
    CREATE TABLE IF NOT EXISTS agent_run_outputs (
      id INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL REFERENCES agent_runs(id) ON DELETE CASCADE,
      timestamp TEXT NOT NULL, source TEXT NOT NULL, text TEXT NOT NULL
    );
    CREATE INDEX IF NOT EXISTS idx_runs_agent ON agent_runs(agent_id);
    CREATE INDEX IF NOT EXISTS idx_outputs_run ON agent_run_outputs(run_id, id);
  `);
  const columns = new Set(db.prepare('PRAGMA table_info(agent_runs)').all().map(column => column.name));
  for (const [name, type] of Object.entries(usageColumns)) {
    if (!columns.has(name)) db.exec(`ALTER TABLE agent_runs ADD COLUMN ${name} ${type}`);
  }
  db.prepare("UPDATE agent_runs SET status='Interrupted', finished_at=? WHERE status IN ('Queued','Running')").run(now());
  const record = db.prepare('SELECT * FROM agent_runs WHERE id=?');
  const output = db.prepare('SELECT id, timestamp, source, text FROM agent_run_outputs WHERE run_id=? ORDER BY id DESC LIMIT 5000');
  const latestOutput = db.prepare('SELECT source, text FROM agent_run_outputs WHERE run_id=? ORDER BY id DESC LIMIT 10');
  const storeLine = db.prepare('INSERT INTO agent_run_outputs(run_id,timestamp,source,text) VALUES(?,?,?,?)');
  const active = new Map();

  function fromRow(row) {
    if (!row) return null;
    const usage = row.input_tokens == null && row.total_cost_usd == null ? null : {
      inputTokens: row.input_tokens || 0, outputTokens: row.output_tokens || 0,
      cacheReadTokens: row.cache_read_tokens || 0, cacheCreationTokens: row.cache_creation_tokens || 0,
      totalCostUsd: row.total_cost_usd,
    };
    return {
      id: row.id, agentId: row.agent_id, arguments: row.arguments, startedAt: row.started_at,
      finishedAt: row.finished_at, status: row.status, exitCode: row.exit_code,
      outputDir: row.output_dir, providerId: row.provider_id, usage,
    };
  }

  function getRun(id) {
    const run = fromRow(record.get(id));
    return run ? { ...run, output: output.all(id).reverse().map(item => ({ ...item })) } : null;
  }

  function listRuns(agentId) {
    const rows = agentId
      ? db.prepare('SELECT * FROM agent_runs WHERE agent_id=? ORDER BY started_at DESC LIMIT 500').all(agentId)
      : db.prepare('SELECT * FROM agent_runs ORDER BY started_at DESC LIMIT 500').all();
    return rows.map(fromRow);
  }

  function execute(agent, params, cwd, provider, prompt, outputDir) {
    const id = randomUUID();
    const keepAlive = Boolean(params.keepSessionAlive);
    if (keepAlive && provider.id === 'codex') throw new Error('Persistent agent sessions require Claude Code.');
    if (!statSync(cwd).isDirectory()) throw new Error('The selected run folder is unavailable.');
    mkdirSync(outputDir, { recursive: true });
    const hintsFile = join(outputDir, '.operator-hints.md');
    writeFileSync(hintsFile, '', { flag: 'a', mode: 0o600 });
    const augmentedPrompt = `${prompt}\n\nIf new operator guidance is available, read it from the file identified by DOXIE_OPERATOR_HINTS_FILE before the next work cycle.`;
    const options = headlessArgs(provider, params.sessionId);
    const mcpConfig = join(storage.root, '.mcp.json');
    if (provider.id === 'claude-code' && cwd !== storage.root && existsSync(mcpConfig)) options.push('--mcp-config', mcpConfig);
    const child = launch(provider.executable, options, {
      cwd, stdio: ['pipe', 'pipe', 'pipe'],
      env: { ...provider.environment, DOXIE_AGENT_OUTPUT_DIR: outputDir, DOXIE_OPERATOR_HINTS_FILE: hintsFile },
    });
    try {
      db.prepare(`INSERT INTO agent_runs(id,agent_id,arguments,started_at,status,output_dir,provider_id)
        VALUES(?,?,?,?,'Running',?,?)`).run(id, agent.id, params.displayArguments || '', now(), outputDir, provider.id);
    } catch (error) {
      child.on('error', () => {});
      child.stdin.on('error', () => {});
      killTree(child);
      throw error;
    }
    let finish;
    const completed = new Promise(resolve => { finish = resolve; });
    const job = { child, completed, hintsFile, keepAlive, cancelled: false };
    active.set(id, job);
    const changed = () => emit({ type: 'agent:changed', run: fromRow(record.get(id)) });
    changed();
    let usage;
    let sessionId = params.sessionId;
    let errored = false;
    let hasResponse = false;

    function append(source, text) {
      if (typeof text !== 'string' || !text.trim()) return;
      const timestamp = now();
      const line = { source, text, timestamp };
      storeLine.run(id, timestamp, source, text);
      params.onOutput?.(line);
      emit({ type: 'agent:output', id, line });
    }

    createInterface({ input: child.stdout, crlfDelay: Infinity }).on('line', line => {
      const parsed = providerOutput(provider, line);
      if (parsed.sessionId) sessionId = parsed.sessionId;
      if (parsed.usage) usage = parsed.usage;
      if (parsed.error) errored = true;
      for (const text of parsed.lines || []) {
        if (text && !text.startsWith('▶') && !parsed.error) hasResponse = true;
        append(parsed.error ? 'Stderr' : 'Stdout', text);
      }
      if (!hasResponse && !parsed.error && parsed.fallback) append('Stdout', parsed.fallback);
    });
    createInterface({ input: child.stderr, crlfDelay: Infinity }).on('line', line => append('Stderr', line));
    child.on('error', error => {
      errored = true;
      append('Stderr', error.message);
    });
    child.stdin.on('error', error => {
      if (!job.cancelled && error.code !== 'EPIPE') append('Stderr', error.message);
    });
    child.once('close', (code) => {
      const status = job.cancelled ? 'Cancelled' : code === 0 && !errored ? 'Completed' : 'Failed';
      const reported = !params.workflowRunId && !params.builder ? reportedOutput(storage, latestOutput.all(id), cwd) : null;
      db.prepare(`UPDATE agent_runs SET status=?, finished_at=?, exit_code=?, input_tokens=?, output_tokens=?,
        cache_read_tokens=?, cache_creation_tokens=?, total_cost_usd=?, output_dir=? WHERE id=?`).run(
        status, now(), code, usage?.inputTokens ?? null, usage?.outputTokens ?? null,
        usage?.cacheReadTokens ?? null, usage?.cacheCreationTokens ?? null, usage?.totalCostUsd ?? null, reported || outputDir, id,
      );
      active.delete(id);
      const run = { ...getRun(id), sessionId };
      finish(run);
      changed();
      if (!params.workflowRunId && !params.builder) notify({
        title: agent.displayName || agent.name || agent.id,
        message: `Agent run ${status.toLowerCase()}.`, severity: status === 'Failed' ? 'error' : 'info',
        href: `/agents/${agent.id}?run=${id}`, runId: id,
      });
    });
    child.stdin.write(inputMessage(provider, augmentedPrompt));
    if (!keepAlive) child.stdin.end();
    return fromRow(record.get(id));
  }

  function start(agentId, params = {}) {
    const agent = storage.get('agents', agentId, params.catalogId);
    if (!agent) throw new Error('Agent not found.');
    const modes = agentModes(agent);
    const modeId = params.modeId || (typeof params.mode === 'string' ? params.mode : params.mode?.id);
    const mode = modes.find(item => item.id === modeId) || (!modeId && modes.find(item => !item.hidden));
    if (modeId && !mode && modes.length) throw new Error(`Unknown agent mode: ${modeId}`);
    const workspace = params.workspaceId ? storage.get('workspaces', params.workspaceId) : null;
    if (params.workspaceId && !workspace) throw new Error('Workspace not found.');
    if (mode?.requiresWorkspace && !workspace && !params.cwd) throw new Error('This mode requires a workspace.');
    const cwd = params.cwd || workspace?.path || storage.root;
    const args = argumentsFor(params, mode);
    const skillId = agent.skillName || agent.skillId || agent.id;
    const provider = providerFor(storage, params.providerId, params);
    const slash = args.startsWith('/') ? args : `/${skillId}${args ? ` ${args}` : ''}`;
    const keepSessionAlive = params.keepSessionAlive ?? mode?.keepSessionAlive;
    const canUseShim = cwd === storage.root && existsSync(join(cwd, '.claude', 'skills', skillId, 'SKILL.md'));
    const nativeSlash = provider.id === 'claude-code' && (keepSessionAlive || slash.startsWith('/loop ') || canUseShim);
    const { skill, body, skillRoot } = skillContents(storage, agent, skillId, params.catalogId || agent.catalogId, args, !nativeSlash);
    Object.assign(provider.environment, {
      DOXIE_AGENT_ID: agent.id, DOXIE_AGENT_SKILL_ID: skillId, DOXIE_AGENT_SKILL_ROOT: skillRoot,
      DOXIE_ROOT: storage.root, DOXIE_PROJECT_ROOT: storage.root,
    });
    const memories = collectMemory(storage, skill, workspace, { mode: mode?.id || '', provider: provider.id, workspace_id: workspace?.id || '' });
    const useSlash = nativeSlash || !body;
    const prompt = useSlash ? slash : [
      `Execute the Doxie skill "${skillId}" using these arguments:\n${args}`,
      `Resolve skill-relative files from DOXIE_AGENT_SKILL_ROOT (${skillRoot}).`,
      body.replace(/(?<![/\w])\/([a-z][a-z0-9-]+)(?![/\w])/g, provider.id === 'codex' ? '$1' : '/$1'),
    ].join('\n\n---\n\n');
    const outputDir = params.env?.DOXIE_WORKFLOW_OUTPUT_DIR || join(storage.roots.runs, 'agents', randomUUID());
    return execute(agent, {
      ...params, keepSessionAlive,
      displayArguments: args.length > 10_000 ? `stdin-payload:${args.length} chars` : args,
    }, cwd, provider, memories ? `${prompt}\n\n## Doxie context\n${memories}` : prompt, outputDir);
  }

  function runBuilder(kind, params) {
    const provider = providerFor(storage, params.providerId, { env: { DOXIE_ROOT: storage.root, DOXIE_PROJECT_ROOT: storage.root } });
    return execute({ id: `${kind}-builder` }, { ...params, builder: true }, params.cwd, provider, params.prompt, params.cwd);
  }

  function wait(id) {
    return active.get(id)?.completed || Promise.resolve(getRun(id));
  }

  function stop(id) {
    const job = active.get(id);
    if (!job) return false;
    job.cancelled = true;
    killTree(job.child);
    return true;
  }

  function addHint(id, text) {
    const job = active.get(id);
    if (!job) throw new Error('The agent run has ended.');
    if (typeof text !== 'string' || !text.trim()) throw new Error('Enter an instruction.');
    appendFileSync(job.hintsFile, `\n## ${now()}\n\n${text.trim()}\n`);
    return true;
  }

  async function close() {
    const ids = [...active.keys()];
    const jobs = ids.map(wait);
    for (const id of ids) stop(id);
    await Promise.all(jobs);
  }

  function clearHistory() {
    return db.prepare("DELETE FROM agent_runs WHERE status NOT IN ('Running','Queued')").run().changes;
  }

  return { start, runBuilder, wait, stop, addHint, getRun, listRuns, close, clearHistory };
}

module.exports = { createAgents, matches, argumentsFor };
