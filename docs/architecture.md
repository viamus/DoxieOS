# Architecture

DoxieOS is an Electron application. React renders the workbench and the
Electron main process runs the Node.js catalog, workflow engine, SQLite state,
PTY consoles, and provider adapters.

## System Overview

```mermaid
flowchart TD
    Renderer["React workbench"] <-->|Preload IPC bridge| Main["Electron main process"]
    Main --> Catalog["Doxie Catalog Store"]
    Main --> SQLite["SQLite State"]
    Main --> Runner["Agent / Workflow Runner"]
    Main --> Notifications["Notifications Bus"]
    Main --> Consoles["PTY Console Store"]
    Catalog --> Doxie[".doxie/"]
    Catalog --> CustomCatalogs["Managed custom catalog copies"]
    Runner --> ProviderResolver["Provider Resolver"]
    ProviderResolver --> ClaudeCLI["Claude Code CLI"]
    ProviderResolver --> CodexCLI["OpenAI Codex CLI"]
    Runner --> RunOutputs[".runs/"]
    Runner --> Workspace[".workspace/"]
    Notifications --> Main
```

## Runtime Boundaries

| Boundary | Responsibility |
| --- | --- |
| Renderer | Pages, settings, builders, history, notifications, and graph editing. |
| Preload | Named IPC operations and runtime events exposed to the renderer. |
| Main | Validates operations and owns filesystem access, SQLite, processes, and dialogs. |
| Agents | Catalog parsing, provider prompt assembly, run persistence, process lifecycle. |
| Workflows | Graph execution, selected-edge handoffs, transient context, native tools and durable artifact contracts. |
| Consoles | PTY sessions and streaming output. |
| Storage | SQLite state, catalog roots, workspaces, sandboxes, run outputs. |

## Execution Flow

```mermaid
sequenceDiagram
    participant U as Developer
    participant UI as DoxieOS UI
    participant R as Runner
    participant C as Catalog
    participant W as Workspace
    participant P as Provider CLI
    participant H as History

    U->>UI: Start agent or workflow
    UI->>R: Dispatch through Electron IPC
    R->>C: Load agent/workflow definition
    R->>W: Load workspace context and mounted memories
    R->>P: Start a fresh provider session with the typed request
    P-->>R: Stream output and result metadata
    P-->>R: Publish transient context and durable artifacts
    R->>H: Persist run state, selected model, usage, logs, durable outputs
    H-->>UI: Render history/detail/notifications
```

The handoff lives for the active workflow. It carries context JSON as the
exact text published by the producer and indexes isolated artifact copies for
the consumer. The engine drops the transport at workflow end. Native tools run
inside the engine, with no model selection or provider process.

## Workflow Run States

```mermaid
stateDiagram-v2
    [*] --> Queued
    Queued --> Running
    Running --> AwaitingApproval
    AwaitingApproval --> Running: Approved
    AwaitingApproval --> Cancelled: Rejected or previous session ended
    Running --> Succeeded
    Running --> Failed
    Running --> Cancelled
    Succeeded --> [*]
    Failed --> [*]
    Cancelled --> [*]
```

## Storage Model

```mermaid
flowchart LR
    App["DoxieOS"] --> Profile["Electron user data"]
    Profile --> DB["state.db"]
    Profile --> Providers["Provider profiles"]
    Profile --> Workbench["workbench/"]
    Workbench --> DoxieRoot[".doxie/ catalog"]
    Workbench --> WorkspaceRoot[".workspace/"]
    Workbench --> RunsRoot[".runs/"]
    Workbench --> SandboxRoot[".sandbox/"]
    Workbench --> PrivateRoot[".private/"]
```

Development and packaged launches use the same storage layout. The main
process copies imported catalogs and workspace files into the workbench.
Version the source catalog when assets are part of a product or team workflow.
The runtime profile also contains local output and provider state.

## Provider Compatibility

DoxieOS emits provider compatibility files from the canonical `.doxie/` catalog.
Claude Code and Codex consume the files and the context assembled at dispatch.
Each provider runs with a profile managed inside Electron user data. Sign-in
from Settings uses the same profile as agent runs and consoles.

```mermaid
flowchart TD
    Agent[".doxie/agents/<id>"] --> Normalize["Doxie agent definition"]
    Normalize --> ClaudeSkill[".claude/skills/<id>/SKILL.md"]
    Normalize --> CodexIndex[".codex/AGENTS.md"]
    Normalize --> CodexSkills[".agents/skills/<id>/SKILL.md"]
    Normalize --> UI["DoxieOS pages"]
    Normalize --> Runner["Provider dispatch"]
```

## Release Notes

Previous release notes and illustrations are available in the [release history](release-notes/README.md).
