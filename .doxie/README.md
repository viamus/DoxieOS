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

Edit canonical content here. Electron regenerates provider compatibility files
under `.claude/`, `.agents/`, and `.codex/` on save and watches configured
catalog folders for external edits. The public repository tracks the bundled
catalog. Keep personal content in a separate workbench or custom catalog and
use the relevant catalog page to share a ZIP bundle.

Run history and settings live in SQLite. Workspace output and scratch files
live under `.workspace/`, `.runs/`, and `.sandbox/`. The Settings page shows
the active paths.

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

DoxieOS regenerates provider files on launch. While the desktop app is open,
use **Settings → Regenerate provider files** to trigger a refresh manually.

## Migrating an existing skill from `.claude/` to `.doxie/`

The legacy catalog migration copies unmanaged `.claude/` assets and old
`.workflows/` definitions into the canonical `.doxie/` structure. The desktop
workbench can also import DoxieOS 1.x ZIP archives.

## Idempotency contract

Running the regen pipeline N times produces a byte-identical output tree. Every emitter:

- Compares bytes before writing. Unchanged files are not touched (no mtime bumps, no false watcher pings).
- Stamps a `doxie/generated` marker inside each managed folder. Orphan cleanup only deletes folders carrying that marker — hand-authored skills under `.claude/skills/` are safe.
- Emits in stable order so byte-equivalence holds across runs and machines.

If you see a regen pass produce a non-empty diff for unchanged input, that's a bug — please open an issue with the input and the diff.
