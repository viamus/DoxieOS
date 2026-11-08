# Brain

Brain is local knowledge shared by agents and workflows, including user-imported memories, verified learnings, and optimized revisions. A workspace holds input and output files; knowledge is never mounted into it.

Use `query` (or the compatible `search` alias) to retrieve a bounded set of relevant memories. Supply a focused query and optionally a library ID and maximum result count. No workspace is required.

Use `remember` to save reusable knowledge with a title, exact content and provenance. An optional kind identifies a `memory`, `entity` or `parameter`; structured parameters must be a JSON object. Use `relate` with two memory IDs and a relation such as `supports`, `contradicts`, `supersedes`, `related` or `mentions` to connect knowledge.

The mandatory Brain connector lets agents retrieve focused knowledge and inspect weighted graph neighborhoods and provenance. Semantic similarity and correlation indicate proximity, not evidence of support. Revalidate memories marked stale before relying on them; use native `memory_review` with evidence to record the review. Provider memory files are imported only through the user's explicit preview and selection in Brain; there is no automatic provider import. Use Workspace Memory `load` for other explicit Markdown inputs.

Explicit `relate` accepts an optional `weight` between 0 and 1. The internal connector ID `library` remains compatible with existing workflows.

Use `graph` to inspect a neighborhood by memory ID or query, optionally filtering by minimum strength. Use `review` with a memory ID, status (`current`, `stale` or `archived`) and evidence to record a lifecycle decision. `remember` can mark an optimized learning and set its next review date.

Each action emits a receipt and structured context for downstream workflow steps. The old `create`, `interactive`, `from-files`, `mount` and `from-current` modes are retired. Use `query` for retrieval and `remember` for verified reusable knowledge produced by an execution.
