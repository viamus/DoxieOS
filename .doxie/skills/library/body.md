# Library Skill — Router (thin dispatcher)

DoxieOS libraries are reusable memory packs the orchestrator can mount into a user-owned Workspace (under `./.workspace/`) at init time. This skill is the only sanctioned way to create or update one. **All filesystem work is delegated to the `library` subagent** (see `.doxie/agents/library.md`); the skill itself only validates args, builds the agent input, and formats the response.

## Constants

- **Libraries root:** `./.doxie/libraries/`
- **Memory file format:** Markdown with YAML frontmatter (`name`, `description`, `type`). `type` âˆˆ {`user`, `feedback`, `project`, `reference`}.
- **Manifest:** `library.json` at the library root with `{"name": "...", "description": "..."}`.
- **Memory file naming:** `<type>_<short-topic-kebab>.md`. Examples: `feedback_naming.md`, `reference_repo_paths.md`, `project_sample-product.md`, `user_role.md`.
- **Intro prompt** (optional, all modes): a short paragraph describing what the library is for and who will use it. Passed to the agent as `intro_prompt` and used to bias classification, naming, and the default manifest description.

## Routing

The user invokes `/library <subcommand> [args]`. **Read the matching file before acting:**

| Subcommand | File to read |
|---|---|
| `from-files` | `from-files.md` |
| `from-current` | `from-current.md` |
| `interactive` (or no args) | `interactive.md` |
| anything else | print this routing table and stop |

Each subcommand validates locally, then dispatches `subagent_type: library` with a structured JSON input (see `.doxie/agents/library.md` for the input contract).

## Boundaries (skill-side)

- The skill itself does **not** read source files or write any output to disk. That is the agent's job.
- The skill validates the user's args, resolves any paths the agent needs (e.g. the auto-memory directory for `from-current`), packages everything as JSON, then awaits the agent.
- The skill formats the agent's structured response into the user-facing report block.
- Refuse anything that would let the agent write outside `<libraries-root>/<id>/` — the skill is the gatekeeper.

## Output style

Default = ultra-compact (1â€“3 lines). `--verbose` expands per-file detail. Every subcommand follows this template:

```
library <subcommand> · <HH:MM:SS UTC>
<library-id>  +<n> memories  (<file-1>, <file-2>, ...)
```

Failed entries become a single `⚠ <one-line reason>` line. If the agent returns a non-null `error`, the entire output is just `⚠ <error>` — do not retry, do not re-dispatch.

## Hard rules

- Never start a `/loop`, never call `CronCreate`. Library generation is one-shot.
- Library id is kebab-case (`a-z`, `0-9`, `-`). The skill **auto-normalises** common-friendly inputs ("Enterprise Developer Memory" â†’ `enterprise-developer-memory`) and proceeds, emitting a one-line note. Only completely empty results after normalisation are rejected. The agent stays strict and would refuse a non-kebab id — the skill is the lenience layer.
- When the id is auto-normalised AND the user did NOT pass `--name`, set the agent's manifest `name` to the **original** input so the display name reads naturally even though the folder uses kebab-case.
- Existing libraries are not overwritten without `--force`.
- All disk writes go through the `library` subagent dispatch; the skill never writes a memory file or `library.json` itself.
- Read this file's routing table on every invocation; do not memorise it across runs.
