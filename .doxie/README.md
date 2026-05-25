# `.doxie/` — canonical authoring layer

This is where you author. Everything else is derived.

```
.doxie/
â”œâ”€â”€ skills/<id>/manifest.json   â† provider-neutral skill metadata
â”œâ”€â”€ skills/<id>/body.md         â† skill instructions (what Claude reads from SKILL.md today)
â”œâ”€â”€ skills/<id>/<companions>    â† subcommand .md files, scripts/, refs/, anything else
â”œâ”€â”€ agents/<id>.md              â† Claude subagent definition (frontmatter + body)
â””â”€â”€ libraries/<id>/             â† memory packs (DoxieOS-internal concept)
    â”œâ”€â”€ library.json
    â””â”€â”€ memory/*.md
```

## What this folder is for

Provider-specific layouts (`.claude/`, `.codex/`) are auto-generated. DoxieOS regenerates them on every save under `.doxie/` via an in-process `FileSystemWatcher` (300 ms debounce).

`.doxie/`, `.claude/`, and `.codex/` are **all gitignored**. The contents of `.doxie/` are per-developer config — your personal skills, agents, libraries, and workflows live on your machine, not in changes.

This means:

- **One authoring surface** instead of N. The same skill ships to every CLI you support.
- **No provider lock-in.** Adding a new provider is one new emitter; no per-skill migration.
- **No accidental config sharing.** Your `.doxie/` content stays on your machine. Teams that need to share skills/libraries/workflows do it through DoxieOS's first-party kit (shipped as embedded resources) or by exporting/importing zips via the Settings UI.

## What this folder is NOT for

- **Not** a place to store generated artifacts. The pipeline rewrites them on every save; manual edits are overwritten.
- **Not** a runtime cache. State (run history, settings) lives in SQLite under `%LOCALAPPDATA%`.
- **Not** for libraries unrelated to DoxieOS — those go elsewhere in the workspace.

## Skill manifest schema

Every `manifest.json` carries the same shape:

```jsonc
{
  "id": "<kebab-case-id>",            // must match the folder name
  "description": "<one-line summary>", // surfaces in Claude's frontmatter and Codex's catalog
  "displayName": "<UI label>",         // optional; falls back to title-cased id
  "category": "Inspector",             // bucket for the orchestrator UI
  "requirements": [                    // external prerequisites surfaced in the agent detail page
    { "kind": "Mcp", "name": "example-mcp", "required": true, "purpose": "..." }
  ],
  "modes": {                           // subcommands ("/skill <mode>")
    "check": {
      "description": "...",
      "argumentsTemplate": "check --foo {foo}",
      "fields": [{ "id": "foo", "label": "Foo", "placeholder": "..." }]
    }
  }
}
```

`body.md` is plain markdown — what Claude reads from `SKILL.md` today, minus the YAML frontmatter (the frontmatter is reconstructed from `manifest.json` on regen).

## Agent definition

Agents are single markdown files at `.doxie/agents/<id>.md` with YAML frontmatter:

```markdown
---
name: explorer
description: Read-only search agent for locating code.
tools: Grep,Glob,Read
model: claude-sonnet-4-5
---

You are a search-only agent...
```

The Claude shim copies this 1:1 to `.claude/agents/<id>.md`. Codex gets an entry in `AGENTS.md` describing the agent.

## Editing canon while DoxieOS is offline

The watcher only fires while the orchestrator is running. If you edit `.doxie/` while it's offline (or after a fresh checkout), open **Settings â†’ Doxie codegen â†’ Regenerate shims** to force a pass. Reports counts in a toast.

## Migrating an existing skill from `.claude/` to `.doxie/`

If you upgraded from v0.1 with skills authored under `.claude/skills/`, open **Settings â†’ Doxie codegen â†’ Migrate legacy skills**. DoxieOS scans `.claude/` for unmanaged folders (those without the `doxie/generated` marker), folds each into the new `.doxie/` shape, and runs a regen pass to verify the output matches what Claude Code was reading before. Hand-authored folders that don't carry the marker are left alone until you click migrate.

## Idempotency contract

Running the regen pipeline N times produces a byte-identical output tree. Every emitter:

- Compares bytes before writing. Unchanged files are not touched (no mtime bumps, no false watcher pings).
- Stamps a `doxie/generated` marker inside each managed folder. Orphan cleanup only deletes folders carrying that marker — hand-authored skills under `.claude/skills/` are safe.
- Emits in stable order so byte-equivalence holds across runs and machines.

If you see a regen pass produce a non-empty diff for unchanged input, that's a bug — please open an issue with the input and the diff.
