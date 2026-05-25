# Sandbox Skill

Manage agent-owned transient folders under `./.sandbox/`. A sandbox is scratch
space for temporary files, experiments, or cloned repositories that should not
pollute the main workspace.

## Modes

- `new`: allocate a fresh sandbox folder.
- `list`: show existing sandbox folders and basic state.
- `clean`: remove eligible sandbox folders.

## Rules

- The skill only manages sandbox folders.
- It does not clone repositories or run project commands.
- Never delete outside `./.sandbox/`.
- Treat all sandbox contents as temporary.
