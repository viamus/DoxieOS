# `/library from-current` — package the running workspace's memories as a reusable library

Thin dispatcher: resolves the workspace's memory directory, builds the input JSON, dispatches the `library` subagent, formats the report.

## Inputs

- `--id <library-id>` — required. Kebab-case folder name.
- `--name "<Display Name>"` — optional manifest display name.
- `--description "<text>"` — optional manifest description.
- `--context "<paragraph>"` — optional intro prompt: what this library is for and who will use it.
- `--type <user|feedback|project|reference>` — optional, repeatable. Restrict the snapshot to the listed memory types.
- `--include <glob>` — optional, repeatable. Only copy memory files whose filename matches the glob.
- `--exclude <glob>` — optional, repeatable. Skip memory files matching the glob.
- `--force` — optional. Allow overwriting an existing library at `<id>`.
- `--verbose` — optional. Expand the output report.
- `--memory-dir <path>` — optional. Override the auto-detected memory directory.

## Steps

1. **Validate locally.**
   - If `--id` is missing, emit `⚠ id required` and stop.
   - **Normalise the id** the same way `from-files.md` step 1 does (lowercase, spacesâ†’`-`, strip invalid chars, collapse `-`, trim). If normalisation yields an empty string, emit `⚠ id "<original>" cannot be normalised to kebab-case` and stop. Otherwise, if the normalised id differs from the original, emit `note: id auto-normalised: "<original>" â†’ "<normalised>"` in the report; when `--name` was not passed, fall back to the original input as the display name.

2. **Resolve the source memory directory:**
   - If `--memory-dir` was passed, use it.
   - Otherwise compute the default: `%USERPROFILE%/.claude/projects/<workspace-id>/memory`. `<workspace-id>` is the standard Claude Code derivation from the project root path: drive letter followed by `--` and each path segment joined by `-` (for example, a project at `/home/user/code/doxie` becomes `home-user-code-doxie`). Expand `%USERPROFILE%` against the running environment.

3. **Build the agent input:**
   ```json
   {
     "mode": "from-current",
     "library_id": "<--id>",
     "library_root": "./.doxie/libraries",
     "manifest": {
       "name": "<--name | null>",
       "description": "<--description | null>"
     },
     "intro_prompt": "<--context | null>",
     "force": <true|false>,
     "from_files": null,
     "from_current": {
       "memory_dir": "<resolved memory directory>",
       "include_globs": [<--include values>] | null,
       "exclude_globs": [<--exclude values>] | null,
       "types": [<--type values>] | null
     }
   }
   ```

4. **Dispatch.** Call `Agent` with `subagent_type: library` and the JSON above. Await the structured response.

5. **Format the report** per `SKILL.md`'s "Output style" section.

   Default:
   ```
   library from-current · <HH:MM:SS UTC>
   <id>  +<n> memories  (<top-3-filenames>, …)
   ```

   `--verbose`:
   ```
   library from-current · <HH:MM:SS UTC>

   Wrote <id> from <memory_dir> into <target_dir>:
     feedback_<x>.md   "<name>"
     project_<y>.md    "<name>"
     ...

   Skipped:
     ⚠ <filename>: <reason>
   ```

6. **Honour the agent's `error` field.** If non-null, the only line of output is `⚠ <error>`.

## Hard rules

- This skill never reads or writes library files itself — that's the agent's job. The skill resolves paths, dispatches, formats.
- Never start a `/loop`, never `CronCreate`.
- `MEMORY.md` is never copied (the agent excludes it).
