# `/prompt run` — run the prompt against the input, emit output

Thin dispatcher: validates args, builds the input JSON, dispatches the `prompt` subagent, formats the response per `SKILL.md`'s output style.

## Inputs

- `<prompt-text>` — required positional. The instruction to run. Quoted because it usually contains spaces.
- `--prompt <text>` — optional alias for the positional form. Useful when the prompt is generated from a textarea (DoxieOS) and there's no clean way to escape it as a positional.
- `--prompt-file <path>` — optional. Read the prompt from a file instead of inlining it. Useful for long prompts that don't fit cleanly in a form field.
- `--input-text <text>` — optional. Static input text to feed into the prompt. Mutually exclusive with `--input-file`. Both are ignored if `$DOXIE_WORKFLOW_INPUT_DIRS` is set.
- `--input-file <path>` — optional. Read input from this file. Mutually exclusive with `--input-text`. Both are ignored if `$DOXIE_WORKFLOW_INPUT_DIRS` is set.
- `--output-format <text|md|json>` — optional, default `text`. Anything outside the set is rejected.
- `--output-schema <text>` — optional. Used only when `--output-format json`. Free-form: can be a JSON Schema, an example object, or English describing the shape. The agent uses it to validate its own output before writing.
- `--system-prompt <text>` — optional. A system-level instruction prepended to the run. Useful for setting tone / persona / hard rules across the run.
- `--verbose` — optional. Expand the output report.

## Steps

1. **Validate the prompt.**
   - Exactly one of: positional, `--prompt`, or `--prompt-file` must be supplied. If zero or more than one, emit `⚠ supply exactly one of: positional prompt, --prompt, --prompt-file` and stop.
   - If `--prompt-file` is set, validate the path exists and is readable. If not, emit `⚠ --prompt-file path not found: <path>` and stop. Pass the absolute path through; the agent reads it.
   - If the resolved prompt text is empty after trim, emit `⚠ prompt is empty` and stop.

2. **Validate the input source.**
   - If `$DOXIE_WORKFLOW_INPUT_DIRS` is set in the environment, the agent will read from there — `--input-text` and `--input-file` are ignored (skill emits a soft note in `--verbose` mode but does not fail).
   - Otherwise: at most one of `--input-text` and `--input-file` may be set. If both, emit `⚠ --input-text and --input-file are mutually exclusive` and stop.
   - If `--input-file` is set, validate the path exists. If not, emit `⚠ --input-file path not found: <path>` and stop.

3. **Validate output format and schema.**
   - `--output-format` defaults to `text`. If supplied, it must be one of `text`, `md`, `json`. Otherwise emit `⚠ --output-format must be one of: text, md, json` and stop.
   - `--output-schema` may only be set when `--output-format` is `json`. If set under any other format, emit `⚠ --output-schema only valid with --output-format json` and stop.

4. **Build the agent input:**
   ```json
   {
     "prompt_text": "<resolved prompt>",
     "prompt_file": "<path | null>",
     "system_prompt": "<text | null>",
     "input_source": "workflow_dirs" | "text" | "file" | "empty",
     "input_text": "<text | null>",
     "input_file": "<path | null>",
     "workflow_input_dirs": [<one entry per upstream node dir, parsed from $DOXIE_WORKFLOW_INPUT_DIRS by splitting on ';'>] | null,
     "workflow_output_dir": "<env value | null>",
     "output_format": "text | md | json",
     "output_schema": "<text | null>",
     "cwd": "<absolute path to current working directory>"
   }
   ```

   `input_source` is set by the skill based on which path won the resolution above (so the agent doesn't have to re-derive it).

5. **Dispatch.** Call `Agent` with `subagent_type: prompt` and pass the JSON above. Await the agent's structured response.

6. **Format the output.** Per `SKILL.md`'s "Output style" section.

   Default:
   ```
   prompt run · <HH:MM:SS UTC>
   <input-bytes>B in â†’ <output-bytes>B <format> at <output-path>
   ```

   `--verbose`: see the template in `SKILL.md`.

7. **Honour the agent's `error` field.** If non-null, that's the only line of output: `⚠ <error>`. Don't try to recover or re-dispatch.

## Hard rules

- The skill never reads input, never runs the prompt, never writes output. All of that is the agent's job. The skill validates args, dispatches, formats.
- Never start a `/loop`, never `CronCreate`.
- Pass through the user's overrides verbatim; the agent supplies sane defaults when null.
- Reject contradictory flag combinations (positional + `--prompt`, both input sources, schema without json) early — clearer error from the skill than a confused agent.
