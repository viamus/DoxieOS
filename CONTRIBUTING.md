# Contributing to DoxieOS

Thanks for sending changes. This is a small repo with a handful of collaborators, so we keep things simple.

## Setup

1. Clone the repository and install Node.js 22.17 or newer with npm.
2. Install and verify locked packages before running dependency setup scripts.

   ```sh
   node tools/check-dependencies.mjs
   npm ci --ignore-scripts
   npm audit signatures
   npm audit --audit-level=high
   npm rebuild electron node-pty --ignore-scripts=false
   node node_modules/electron/install.js
   ```

3. Build and test before pushing.

   ```sh
   npm run build
   npm test
   npm run test:desktop
   ```

4. Install Python 3 if your shell does not have PowerShell available. The Git hooks use it as the cross-platform fallback.

Enable the public content guard before your first commit:

```sh
git config core.hooksPath .githooks
```

The hook blocks company-specific, private-catalog, local-machine, and credential references. It runs with PowerShell when available and falls back to Python on Linux/macOS.

## Branch naming

- `feat/<short-description>` — new feature
- `fix/<short-description>` — bug fix
- `chore/<short-description>` — refactor, build, CI, docs
- `test/<short-description>` — test-only

Use kebab-case, keep it short.

## Changes

1. Branch from `main` (always — there is no `develop` integration branch).
2. Push your branch and open a review against `main`. The review template will guide you.
3. Review the dependency checks and the build and test results in CI before merging.
4. Prefer a **merge commit** for branches with a meaningful series of commits; **squash** for ones with churn-only commits.

## Code conventions

- Keep renderer access to native capabilities behind the preload bridge. Validate request paths and data in the main process.
- Test behavior using isolated catalog folders and temporary databases.
- New skills (`.doxie/skills/<name>/`) and agents (`.doxie/agents/<name>.md`) are canonical. Provider shims under `.claude/` and `.codex/` are generated output and should not be edited by hand.
- Memory packs in `.doxie/libraries/` are versioned only when they are first-party/shared project assets. Personal provider-side copies under `.claude/libraries/` stay local.

## Reporting issues

Open a GitHub issue. Include:

- What you tried (commands/flow).
- What you expected.
- What actually happened (with error output / logs).
- Your operating system, DoxieOS version, and `node --version` if you built from source.

## Local sandboxes & workspaces

DoxieOS allocates `.sandbox/<tag>/` for transient agent scratch work and `.workspace/<name>/` for user-owned project folders. Both are gitignored — never push their contents.
