# `/library from-files` — synthesise memories from arbitrary source files

Thin dispatcher: parses CLI args, builds the input JSON, dispatches the `library` subagent, formats the response per the SKILL.md output style.

## Inputs

- `--id <library-id>` — required. Kebab-case folder name.
- `--name "<Display Name>"` — optional manifest display name.
- `--description "<text>"` — optional manifest description.
- `--context "<paragraph>"` — optional intro prompt: what this library is for and who will use it. Strongly recommended — biases classification and naming, and seeds the manifest description when `--description` is omitted.
- `--force` — optional. Allow overwriting an existing library at `<id>`.
- `--verbose` — optional. Expand the output report.
- `<paths...>` — required, positional. Each path is a file OR a directory. Directories are walked recursively by the agent for `.md` and `.txt` files (skipping `.git/`, `node_modules/`, `bin/`, `obj/`, and dot-folders).

## Steps

1. **Validate locally.**
   - If `--id` is missing, emit `⚠ id required` and stop.
   - **Normalise the id.** If `--id` is not already kebab-case, derive a kebab id from it:
     1. Lowercase the input.
     2. Replace whitespace with `-`.
     3. Strip any character outside `[a-z0-9-]`.
     4. Collapse consecutive `-` into one.
     5. Trim leading/trailing `-`.
     If the normalised id is empty, emit `⚠ id "<original>" cannot be normalised to kebab-case` and stop.
     If the normalised id differs from the original, emit a one-line note in the report (`note: id auto-normalised: "<original>" â†’ "<normalised>"`) and continue with the normalised id. If the user did not pass `--name`, use the original input as the display name so the manifest still reads naturally (e.g. `name: "Enterprise Developer Memory"`, id `enterprise-developer-memory`).
   - If no positional source paths were given, emit `⚠ at least one source file or folder is required` and stop.

2. **Build the agent input:**
   ```json
   {
     "mode": "from-files",
     "library_id": "<--id>",
     "library_root": "./.doxie/libraries",
     "manifest": {
       "name": "<--name | null>",
       "description": "<--description | null>"
     },
     "intro_prompt": "<--context | null>",
     "force": <true|false>,
     "from_files": {
       "source_paths": [<positional paths — files and/or directories>]
     },
     "from_current": null
   }
   ```

3. **Dispatch.** Call `Agent` with `subagent_type: library` and pass the JSON above. Await the agent's structured response.

4. **Format the report** per `SKILL.md`'s "Output style" section. Compact by default, `--verbose` expands per-file detail.

   Default:
   ```
   library from-files · <HH:MM:SS UTC>
   <id>  +<n> memories  (<top-3-filenames>, …)
   ```

   `--verbose`:
   ```
   library from-files · <HH:MM:SS UTC>

   Wrote <id> from <m> source files into <target_dir>:
     feedback_<x>.md   "<name>"
     reference_<y>.md  "<name>"
     ...

   Skipped:
     ⚠ <path>: <reason>

   Warnings:
     · <warning>
   ```

5. **Honour the agent's `error` field.** If non-null, that's the only line of output: `⚠ <error>`. Don't try to recover or re-dispatch.

## Hard rules

- This skill never reads or writes library files itself — all of that is the agent's job. The skill validates args, dispatches, formats.
- Never start a `/loop`, never `CronCreate`.
- Pass through the user's `--name` / `--description` verbatim; the agent supplies sane defaults when null.
