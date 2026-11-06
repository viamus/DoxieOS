# Operations Guide

## Build and launch

Install Node.js 22.17 or newer with npm. From the repository root, install the
locked dependency tree and verify registry signatures and high-severity audit
findings before enabling the two desktop dependencies' setup scripts.

```sh
node tools/check-dependencies.mjs
npm ci --ignore-scripts
npm audit signatures
npm audit --audit-level=high
npm rebuild electron node-pty --ignore-scripts=false
node node_modules/electron/install.js
npm start
```

`npm start` builds the React workbench and opens Electron. Use `npm run build`
to build the renderer by itself. The packaged executable loads that build from
disk and uses Electron's embedded Node.js runtime.

`npm run package` creates an unsigned desktop bundle for the host platform.
On Windows x64, extract `release/DoxieOS-win32-x64.zip` and double-click
`run.bat` or `DoxieOS.exe` inside the extracted folder. Keep the launcher,
executable and bundled resources together. The repository's `run.bat` opens
an available package first and can start the development build with already
installed local dependencies.

## Storage and imports

Both development and packaged launches use Electron's user data directory.
On Windows, this is `%APPDATA%\doxieos`. DoxieOS creates `state.db` and
`workbench/` there, seeds the bundled catalog, and keeps provider profiles in
separate directories within the same user data directory.

| Workbench path | Purpose |
| --- | --- |
| `.doxie/` | Canonical skills, subagents, workflows, and libraries. |
| `.workspace/` | Workspace folders, mounts, notes, and durable output. |
| `.runs/` | Workflow node output and agent run artifacts. |
| `.sandbox/` | Builder sessions and scratch folders. |
| `.private/` | User-owned private content. |
| `.claude/`, `.agents/`, `.codex/` | Generated provider compatibility files. |
| `.providers/` | DoxieOS-specific Codex and Claude Code profiles. |

SQLite stores run history, streamed output, and application settings. The
**Settings** page shows the active database and workbench paths. Use **File →
Import workbench…** to copy missing `.doxie/` and `.workspace/` files from
another workbench into the active one. Adding a custom catalog or choosing a
source folder when creating a workspace also copies files into the managed
storage. Folder copies skip symbolic links, Git metadata, environment files,
and provider profiles.

Use **Settings → Storage paths → Clean temporary files** to remove eligible
finished run output and temporary sandboxes. The confirmation explains which
files are eligible. **Settings** reports the number of removed folders.

## Upgrade from DoxieOS 1.x

1. Close DoxieOS 1.x and launch the desktop app. If the desktop database has yet to be created, DoxieOS copies a snapshot of the previous SQLite database into its user data directory. On Windows, the previous database is normally `%LOCALAPPDATA%\Viamus.Doxie.Orchestrator\state.db`.
2. Select **File → Import workbench…** and choose the original workbench folder to copy its catalog and workspace files.
3. Open **Settings** to verify storage paths and sign in to each provider you use.

The desktop app also reads DoxieOS 1.x ZIP archives. Transfer an agent,
workflow, workspace, or library through its catalog page. Workflow and
workspace bundles can carry related agents. The import dialog requests a
choice when an existing item differs from its archived counterpart.

## Providers and MCP

Install Claude Code or OpenAI Codex on the machine running DoxieOS. Under
**Settings → Agent CLI providers**, select **Authenticate** to sign in with
the provider profile managed by DoxieOS. You can also select the default
provider and configure each executable from that section. An agent run or an
interactive console launches its provider on demand.

MCP configuration lives under **Settings**. DoxieOS reconciles the configured
servers with the provider files. The providers connect to those MCP servers
when invoked. The configured URLs and commands must resolve on the host where
DoxieOS and the providers run.

## Workflow triggers

| Trigger | Desktop behavior |
| --- | --- |
| Manual | Starts from the workbench. |
| Cron | Evaluates enabled schedules in UTC while Electron is running and skips overlapping runs. |
| Event / webhook | Definitions remain in the catalog for compatibility. |

DoxieOS 2 uses Electron IPC for workbench operations. The 1.x HTTP API,
SignalR, and Docker launch entry points were retired with the server runtime.

## Validation

```sh
npm test
npm run test:desktop
npm run package
```

The Node tests use temporary databases and workbench folders. Run the Electron
tests in a graphical session or under Xvfb on headless Linux.
