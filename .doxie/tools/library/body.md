# Library

Libraries are global knowledge shared by agents and workflows. A workspace holds input and output files; knowledge is never mounted into it.

Use `query` (or the compatible `search` alias) to retrieve a bounded set of relevant memories. Supply a focused query and optionally a library ID and maximum result count. No workspace is required.

Use `remember` to save reusable knowledge with a title, exact content and provenance. An optional kind identifies a `memory`, `entity` or `parameter`; structured parameters must be a JSON object. Use `relate` with two memory IDs and a relation such as `supports`, `contradicts`, `supersedes`, `related` or `mentions` to connect knowledge.

The execution harness owns this shared knowledge. There is no manual library registration or raw-file ingestion action. Use Workspace Memory `load` for explicitly selected Markdown inputs; a consuming agent reviews the source material before its harness saves verified learnings.

Each action emits a receipt and structured context for downstream workflow steps. The old `create`, `interactive`, `from-files`, `mount` and `from-current` modes are retired. Use `query` for retrieval and `remember` for verified reusable knowledge produced by an execution.
