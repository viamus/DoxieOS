# `/sandbox new` - create the next sandbox

Create the next available sandbox folder under `./.sandbox/`.

## Inputs

Args (all optional):

- `--tag <slug>` - assign this tag. Folder will be `.sandbox/<slug>/` (or `.sandbox/<slug>_2/`, `_3/`, ... on collision). Sanitize the slug: allowed chars `[a-zA-Z0-9._-]`, anything else replaced with `-`.
- Remaining args (after `--tag <slug>`, if present) - one-line purpose/title used in `SANDBOX.md`.

If neither was given, optionally ask the user briefly ("Qual e o proposito deste sandbox?") before creating. Purpose is what makes `list` useful later. (When invoked from another agent, skip the question if a purpose is implicit.)

## Steps

1. **Ensure the parent exists.** If `./.sandbox/` doesn't exist, create it.

2. **Decide the sandbox folder name.**

   **If `--tag <slug>` was given:**
   - Sanitize `<slug>` (replace any char outside `[a-zA-Z0-9._-]` with `-`).
   - Try `.sandbox/<slug>/`. If `Glob ./.sandbox/<slug>` returns it, try `<slug>_2`, then `_3`, etc., until a free name is found.

   **Otherwise (no tag):**
   - Glob `./.sandbox/*/` to enumerate existing dirs.
   - From each dir name, **only** parse the integer if the name matches exactly `<digits>` (no extra characters - tagged dirs are skipped, including collision-suffixed ones).
   - Choose `max(parsed integers) + 1`. If no untagged sandbox exists yet, start at `1`.
   - **Never reuse** lower numbers, even if they were deleted. Ordinals are monotonically increasing for the lifetime of the .sandbox root.
   - There is **no upper bound**.

3. **Create the folder structure** (let `<id>` be the chosen name component, e.g. `42` or `experiment-1234` or `experiment-1234_2`):
   - `./.sandbox/<id>/`
   - `./.sandbox/<id>/memory/`

4. **Write `SANDBOX.md`** at `./.sandbox/<id>/SANDBOX.md` (substitute `<id>`, today's date in `YYYY-MM-DD`, the tag if present, and the purpose; if no purpose was given, use `(not set)`):

   ```markdown
   # Sandbox <id>

   **Created:** <YYYY-MM-DD>
   **Tag:** <tag-or-"(none)">
   **Purpose:** <purpose>

   ## Repos cloned here
   _(filled by user/agent as they clone)_

   ## Session notes
   _(scratch - anything temporary that shouldn't go to global memory)_
   ```

5. **Write the local memory index** at `./.sandbox/<id>/memory/MEMORY.md`:

   ```markdown
   # Sandbox <id> - local memory index

   _(sandbox-scoped temporary memories - separate from global memory)_
   ```

6. **Report back:**
   - The absolute path that was created.
   - A reminder that repos are cloned manually inside that folder.
   - A note that anything temporary (notes, local memory) belongs in this sandbox, not in the global memory dir.
   - When invoked by another agent, also return the sandbox path as a structured value the caller can parse.

## Failure modes

- If the parent `./.sandbox/` doesn't exist for some reason, create it first.
- If the chosen folder already exists (race or stale state):
  - For untagged: bump to the next integer and try again.
  - For tagged: append/increment the `_<n>` collision suffix.
- Never overwrite existing content.
