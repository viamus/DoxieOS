const { randomUUID } = require('node:crypto');
const { existsSync } = require('node:fs');
const { join } = require('node:path');
const pty = require('node-pty');
const { executablePath, invocation } = require('./processes.cjs');
const { definitions, providerFor, providerEnvironment, usesCodex, localProviderArgs } = require('./providers.cjs');

function size(value, fallback) {
  return Number.isInteger(value) ? Math.max(2, Math.min(500, value)) : fallback;
}

function createConsoles({ storage, emit }) {
  const sessions = new Map();
  const windows = process.platform === 'win32';
  const summarize = session => session.info;
  const changed = session => emit({ type: 'console:changed', console: summarize(session) });

  function start(options = {}) {
    const workspace = options.workspaceId ? storage.get('workspaces', options.workspaceId) : null;
    if (options.workspaceId && !workspace) throw new Error('Workspace not found.');
    const cwd = workspace?.path || storage.root;
    const requestedProvider = options.providerId || (Object.hasOwn(definitions, options.kind) ? options.kind : null);
    const provider = requestedProvider ? providerFor(storage, requestedProvider, { model: options.model }) : null;
    const env = { ...(provider?.environment || providerEnvironment(storage)), TERM: 'xterm-256color', DOXIE_PROJECT_ROOT: storage.root, DOXIE_ROOT: storage.root };
    delete env.ELECTRON_RUN_AS_NODE;
    const executable = provider?.executable || (windows
      ? executablePath('pwsh', env) || executablePath('powershell', env) || env.ComSpec
      : env.SHELL || '/bin/sh');
    const login = options.auth || options.kind === 'auth';
    if (login && provider?.requiresAuth === false) throw new Error(`${provider.name} uses a local endpoint.`);
    let args = windows && executable && !/cmd\.exe$/i.test(executable) ? ['-NoLogo'] : [];
    if (provider && usesCodex(provider)) args = login ? ['login'] : [
      '--dangerously-bypass-approvals-and-sandbox', ...localProviderArgs(provider),
      ...(provider.localBaseUrl ? ['--model', provider.model] : []),
    ];
    if (provider?.id === 'claude-code') args = login ? ['auth', 'login'] : ['--dangerously-skip-permissions'];
    const mcpConfig = join(storage.root, '.mcp.json');
    if (provider?.id === 'claude-code' && !login && existsSync(mcpConfig)) args.push('--mcp-config', mcpConfig);
    if (!executable) throw new Error('No shell executable was found.');
    const command = invocation(executable, args);
    const argv = command.verbatim ? command.args.join(' ') : command.args;
    const processHandle = pty.spawn(command.executable, argv, {
      name: 'xterm-256color', cols: size(options.cols, 100), rows: size(options.rows, 28), cwd, env,
    });
    const id = randomUUID();
    const title = options.title || (login && provider ? `${provider.name} · Sign in` : provider?.name || workspace?.name || 'Local shell');
    const info = {
      id, kind: login ? 'auth' : provider?.id || 'shell', providerId: provider?.id || null,
      title, label: title,
      workspaceId: workspace?.id || null, cwd, status: 'Running', startedAt: new Date().toISOString(),
    };
    const session = { info, processHandle, output: '', sequence: 0, stopped: false };
    session.completion = new Promise(resolve => {
      processHandle.onExit(({ exitCode }) => {
        info.status = session.stopped ? 'Cancelled' : exitCode === 0 ? 'Completed' : 'Failed';
        info.exitCode = exitCode;
        info.finishedAt = new Date().toISOString();
        changed(session);
        resolve();
      });
    });
    sessions.set(id, session);
    processHandle.onData(data => {
      session.output = (session.output + data).slice(-250_000);
      emit({ type: 'console:output', id, data, sequence: ++session.sequence });
    });
    changed(session);
    return { ...info };
  }

  function active(id) {
    const session = sessions.get(id);
    return session?.info.status === 'Running' && !session.stopped ? session : null;
  }

  function stop(id) {
    const session = active(id);
    if (!session) return false;
    session.stopped = true;
    session.processHandle.kill();
    return true;
  }

  return {
    start,
    stop,
    list: () => [...sessions.values()].map(summarize).reverse(),
    snapshot(id) {
      const session = sessions.get(id);
      return session ? { ...session.info, output: session.output, sequence: session.sequence } : null;
    },
    write(id, data) {
      if (typeof data !== 'string' || data.length > 500_000) throw new Error('Invalid terminal input.');
      active(id)?.processHandle.write(data);
    },
    resize(id, cols, rows) {
      const session = active(id);
      if (session) session.processHandle.resize(size(cols, 100), size(rows, 28));
    },
    remove(id) {
      if (sessions.get(id)?.info.status === 'Running') throw new Error('Stop the console before removing it.');
      return sessions.delete(id);
    },
    async close() {
      for (const id of sessions.keys()) stop(id);
      let timeout;
      await Promise.race([
        Promise.all([...sessions.values()].map(session => session.completion)),
        new Promise(resolve => { timeout = setTimeout(resolve, 3000); }),
      ]).finally(() => clearTimeout(timeout));
    },
  };
}

module.exports = { createConsoles };
