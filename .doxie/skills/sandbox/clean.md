# `/sandbox clean` - remove finished sandboxes

Delete sandboxes that are safe to discard.

## Inputs

Optional arg: `--tag <slug>` - restrict cleanup to sandboxes named `<slug>` or `<slug>_<digits>` (the collision-suffix variants of that tag). Without this filter, all sandboxes are eligible. Useful when an agent wants to clean only its own sandboxes, e.g. `/sandbox clean --tag experiment-1234`.

## Eligibility for safe deletion

A sandbox is **safe to delete** iff:

- Every git repo inside it is **clean** (`git status --porcelain` empty), AND
- Every git repo inside it is **fully pushed**: an upstream is set AND `git log @{u}.. --oneline` is empty.

A sandbox with no `.git` anywhere is **also safe** (it's just notes the user/agent didn't keep).

A sandbox is **NOT safe to delete** if any repo is dirty, has unpushed commits, or has no upstream set (we can't verify pushed-ness).

## Steps

1. **Run the same scan as `list.md`** to gather state for every sandbox.
   - **If `--tag <slug>` was given**, filter the scanned set to only `<slug>` and `<slug>_<digits>`. Sandboxes outside the filter are excluded entirely from this invocation (not even shown).

2. **Partition** the in-scope sandboxes into:
   - `safe-to-delete` - meets all eligibility rules above.
   - `keep` - does not. For each "keep", record a one-line reason: `ace-frontend dirty`, `ace-backend has 2 unpushed commits`, `ace-backend no upstream set`, etc.

3. **Show both lists to the user.** Print:
   - The exact paths that would be removed.
   - The "keep" sandboxes with their blocking reasons.

4. **Ask for explicit confirmation** before deleting. Require an affirmative reply (e.g. "sim", "yes", "go"). If the user types anything ambiguous, do not delete - ask again or abort. **Never delete without confirmation, even if the user's invocation seems to say "just do it".**

5. **After confirmation**, remove each safe directory recursively:
   - `Bash`: `rm -rf "./.sandbox/<id>"`
   - Do this per directory (don't glob into `rm -rf`).

6. **Re-run the listing** so the user sees the new state.

## Hard rules

- Never force-delete a `keep` sandbox, even if the user says "yes to all". If they want it gone, they fix the blocker (push, commit, set upstream) and re-run `/sandbox clean`. Surface the blocker; don't bypass it.
- Never run `rm -rf` outside `./.sandbox/`. Validate the path starts with `./.sandbox/` AND has at least one non-empty segment after that AND has no `..` components before deleting.
- If `Bash` `rm -rf` fails on Windows due to a locked file (editor, IDE, watcher), report the error and skip - don't retry destructively.
