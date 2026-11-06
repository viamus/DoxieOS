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
On Windows x64, extract `release/DoxieOS-win32-x64.zip` and run
`DoxieOS.exe` from the extracted `DoxieOS-win32-x64` folder. Keep the
executable beside its bundled resources. The portable app stores user settings
and default workbench data in the Electron user data directory.

## Open a workbench folder

Development launches use the repository folder. The packaged app defaults to
the `workbench` folder under Electron's user data directory and seeds the
bundled `.doxie/` catalog there. Use **File → Open Doxie folder** to select an
existing workbench, or set `DOXIE_ROOT` before launch to select it explicitly.

| Workbench path | Purpose |
| --- | --- |
| `.doxie/` | Canonical skills, subagents, workflows, and libraries. |
| `.workspace/` | Workspace folders, mounts, notes, and durable output. |
| `.runs/` | Workflow node output and agent run artifacts. |
| `.sandbox/` | Builder sessions and scratch folders. |
| `.private/` | User-owned private content. |
| `.claude/`, `.agents/`, `.codex/` | Provider compatibility files. |

SQLite stores run history, streamed output, and application settings. The
**Settings** page shows the active database and workbench paths. Set
`DOXIE_DATABASE_PATH` to select a different database. Set `DOXIE_USER_DATA` to
isolate Electron profile data for testing.

## Upgrade from DoxieOS 1.x

1. Close DoxieOS 1.x before opening the same workbench or database in the desktop app.
2. Open the original workbench folder. The desktop app reads the existing catalog, workspaces, mounts, and artifacts in place.
3. The desktop app looks for `Viamus.Doxie.Orchestrator/state.db` in the previous release's user data location. This includes `%LOCALAPPDATA%` on Windows, `$XDG_DATA_HOME` or `~/.local/share` on Linux, and `~/Library/Application Support` on macOS. Set `DOXIE_DATABASE_PATH` for a different location or an exported database from an earlier containerized install.
4. Open **Settings** and verify that the displayed paths, custom catalogs, provider settings, and MCP definitions point to the expected locations.

The desktop app also reads DoxieOS 1.x ZIP archives. Transfer an agent,
workflow, workspace, or library through its catalog page. Workflow and
workspace bundles can carry related agents. The import dialog requests a
choice when an existing item differs from its archived counterpart.

## Providers and MCP

Install and sign in to Claude Code or OpenAI Codex on the machine running
DoxieOS. **Settings** shows the detected executables and lets you select the
default provider and configure each provider. An agent run or an interactive
console launches its provider on demand.

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
