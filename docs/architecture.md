# Architecture

DoxieOS is a .NET / Blazor Server application with a canonical Doxie catalog,
provider-specific compatibility generation, local SQLite state, and CLI-backed
agent execution.

## System Overview

```mermaid
flowchart TD
    Browser["Browser UI"] --> Blazor["Blazor Server App"]
    Blazor --> Catalog["Doxie Catalog Store"]
    Blazor --> SQLite["SQLite State"]
    Blazor --> Runner["Agent / Workflow Runner"]
    Blazor --> Notifications["Notifications Bus"]
    Blazor --> Consoles["PTY Console Store"]
    Catalog --> Doxie[".doxie/"]
    Catalog --> CustomCatalogs["Custom Catalog Roots"]
    Runner --> ProviderResolver["Provider Resolver"]
    ProviderResolver --> ClaudeCLI["Claude Code CLI"]
    ProviderResolver --> CodexCLI["OpenAI Codex CLI"]
    Runner --> RunOutputs[".runs/"]
    Runner --> Workspace[".workspace/"]
    Notifications --> Browser
```

## Runtime Boundaries

| Boundary | Responsibility |
| --- | --- |
| UI | Pages, settings, builders, history, notifications, graph editing, release notes. |
| Agents | Catalog parsing, provider prompt assembly, run persistence, process lifecycle. |
| Workflows | Graph execution, node dispatch, trigger handling, output contracts. |
| Context | PTY/process abstractions and platform-specific child process control. |
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
    UI->>R: Dispatch request
    R->>C: Load agent/workflow definition
    R->>W: Load workspace context and mounted memories
    R->>P: Start provider process with assembled prompt
    P-->>R: Stream output and result metadata
    R->>H: Persist run state, usage, logs, outputs
    H-->>UI: Render history/detail/notifications
```

## Workflow Run States

```mermaid
stateDiagram-v2
    [*] --> Queued
    Queued --> Running
    Running --> WaitingForApproval
    WaitingForApproval --> Running: Approved
    WaitingForApproval --> Cancelled: Rejected
    Running --> Completed
    Running --> Failed
    Running --> Cancelled
    Completed --> [*]
    Failed --> [*]
    Cancelled --> [*]
```

## Storage Model

```mermaid
flowchart LR
    App["DoxieOS"] --> DB["SQLite state"]
    App --> DoxieRoot[".doxie/ catalog"]
    App --> WorkspaceRoot[".workspace/"]
    App --> RunsRoot[".runs/"]
    App --> SandboxRoot[".sandbox/"]
    App --> PrivateRoot[".private/"]
```

The canonical catalog should be versioned when assets are part of the product or
team workflow. Runtime output folders and private provider state should stay
local unless there is a deliberate reason to share them.

## Provider Compatibility

DoxieOS does not make Claude Code or Codex the source of truth. Instead, it emits
compatibility files from `.doxie/` so providers can run against the same catalog.

```mermaid
flowchart TD
    Skill[".doxie/skills/<id>"] --> Normalize["Doxie model"]
    Normalize --> ClaudeSkill[".claude/skills/<id>/SKILL.md"]
    Normalize --> CodexIndex[".codex/AGENTS.md"]
    Normalize --> UI["DoxieOS pages"]
    Normalize --> Runner["Provider dispatch"]
```

## Release Notes

App release notes are stored under
`src/Viamus.Doxie.Orchestrator/docs/release-notes/` because they are embedded in
the application assembly and shown in the release notes modal.
