# `/sandbox list` - show all sandboxes and their state

Render a compact overview of every sandbox folder under `./.sandbox/`.

## Steps

1. **Enumerate sandboxes.** Glob `./.sandbox/*/`. If none exist (or the parent itself is missing), say so and stop.

2. **For each sandbox, gather:**
   - **ID** - the directory name. May be an integer (`42`) or a tag (`experiment-1234`, possibly with a `_2` collision suffix).
   - **Tag** - the value after `**Tag:**` in `SANDBOX.md` (or `(none)` if untagged). Useful for grouping `--tag` filters in `clean`.
   - **Purpose** - the value after `**Purpose:**` in `SANDBOX.md`. If the file is missing or the line is missing, use `(not set)`.
   - **Created** - the date after `**Created:**` in `SANDBOX.md`.
   - **Repos** - every `.git` directory inside the sandbox at depth 1 (use `Glob` with pattern `.sandbox/*/*/.git`). For each found repo:
     - repo dir name (the parent of `.git`)
     - current branch: `git -C <path> rev-parse --abbrev-ref HEAD`
     - dirty? `git -C <path> status --porcelain` non-empty -> `DIRTY`
     - unpushed? if no upstream is set -> `no-upstream`; else if `git -C <path> log @{u}.. --oneline` non-empty -> `unpushed:<count>`; else `pushed`
   - Run the git probes in parallel where reasonable.

3. **Render** as one block per sandbox. Show the dir name as the heading, the tag in brackets if present, the purpose quoted, then the date:

   ```text
   .sandbox/1 - "Review experiment for risk-level feature" (2026-04-30)
     ace-backend   feature/risk-level-very-high    clean, pushed
     ace-frontend  feature/risk-level-very-high    DIRTY
   .sandbox/experiment-1234 [tag: experiment-1234] - "Investigating build issue" (2026-04-30)
     ace-backend   feature/risk-level-very-high    clean, unpushed:1
   .sandbox/3 - "(not set)" (2026-04-28)
     (no repos cloned)
   ```

4. If a sandbox has no `.git` anywhere, show `(no repos cloned)` instead of an empty repo list.

5. End with a one-line summary: total sandboxes, count of tagged vs untagged, and the next untagged ordinal that `/sandbox new` (without `--tag`) would assign (`max + 1`).

## Notes

- Read-only - never modify anything in this subcommand.
- If `SANDBOX.md` is malformed, surface that fact in the output (e.g. `(SANDBOX.md missing)`).
