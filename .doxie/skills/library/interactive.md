# `/library interactive` — author a library through a guided conversation

Conversational authoring for cases where there are no existing source files or current-workspace memories worth packing. Best invoked from a Claude Code terminal where back-and-forth chat is available; DoxieOS hides this mode from its catalog because the UI doesn't yet support follow-up messages from the user.

The skill (in the main thread) drives the interview, accumulates the synthesised memories in memory, and on confirmation dispatches the `library` agent in `from-files` mode with a temporary file (or a tailored variant of the agent input — see Step 5).

## Inputs

- `--id <library-id>` — optional. If absent, the interview asks for it first.
- `--force` — optional. Allow overwriting an existing library.

## Steps

1. **Greet and orient.** Say (one short paragraph):
   > Library author. I'll ask a handful of questions, then dispatch the `library` agent to write `<libraries-root>/<id>/`.

2. **Collect the manifest** — library id (kebab-case), display name, one-sentence description, and an **intro prompt** (a short paragraph: what this library is for, who will use it, what tone they expect). The intro prompt is passed to the agent as `intro_prompt` and used to align naming/classification.

3. **Collect memories, one at a time.** Loop:

   a. Ask: "What's the next thing this library should remember? Skip / done if you're finished."

   b. From the user's answer, classify the memory type (feedback / reference / project / user) per the rules in the agent's `library.md`.

   c. Confirm: "I'll save this as a `<type>` memory titled `<short title>`. OK? (yes / rephrase / change-type)"

   d. On `yes`, accumulate `{name, description, type, body}` into the in-memory plan. Don't write anything to disk yet.

4. **Confirm before writing.** Show a compact summary:
   ```
   Library: <id> ("<Display Name>")
   <description>

   Will write:
     feedback_<x>.md   "<name>"
     reference_<y>.md  "<name>"
     ...
   ```
   Ask: "Write it? (yes / cancel / edit <number>)".

5. **Dispatch via the agent.** On `yes`, call `Agent` with `subagent_type: library` using a `from-files` input with one synthetic source containing the gathered memories — OR materialise the memories as inline source content the agent classifies; either is fine. Either way, the agent owns all disk writes.

6. **Report** the agent's response via the standard one-block format from `SKILL.md`.

## Hard rules

- Never write to disk in the main thread; always dispatch the agent for the actual writes.
- Same memory-file conventions and filename pattern as `from-files`.
- If the user takes more than ~5 turns to add a single memory, summarise what's been gathered so far so they can re-anchor.
- Never `/loop` or `CronCreate`.
