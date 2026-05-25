# Contributing to DoxieOS

Thanks for sending changes. This is a small repo with a handful of collaborators, so we keep things simple.

## Setup

1. Clone anywhere on disk — DoxieOS resolves paths relative to whatever folder it's launched from.
2. Install the .NET SDK pinned in `global.json` (currently .NET 10). [Download](https://dotnet.microsoft.com/).
3. Install Python 3 if your shell does not have PowerShell available. The versioned Git hooks use it as the cross-platform fallback.
4. Build and test before pushing:

   ```sh
   dotnet build Solution.slnx -c Release
   dotnet test  Solution.slnx -c Release
   ```

If both succeed locally, the CI is very likely to be green too.

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
3. CI must be green before merge. The matrix runs on `windows-latest` and `ubuntu-latest` against the SDK in `global.json`.
4. Prefer a **merge commit** for branches with a meaningful series of commits; **squash** for ones with churn-only commits.

## Code conventions

- C# nullable is enabled — fix the warnings, don't suppress.
- Tests cover **business rules** at ~80% — not raw lines of code.
- New skills (`.doxie/skills/<name>/`) and agents (`.doxie/agents/<name>.md`) are canonical. Provider shims under `.claude/` and `.codex/` are generated output and should not be edited by hand.
- Memory packs in `.doxie/libraries/` are versioned only when they are first-party/shared project assets. Personal provider-side copies under `.claude/libraries/` stay local.

## Reporting issues

Open a GitHub issue. Include:

- What you tried (commands/flow).
- What you expected.
- What actually happened (with error output / logs).
- Your OS + `dotnet --version`.

## Local sandboxes & workspaces

DoxieOS allocates `.sandbox/<tag>/` for transient agent scratch work and `.workspace/<name>/` for user-owned project folders. Both are gitignored — never push their contents.
