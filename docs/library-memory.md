# Brain: local knowledge and semantic graph

Brain is the shared knowledge base for agents, workflow model steps, builders,
and provider consoles. Workspaces hold execution inputs and outputs. Brain
holds imported memories, verified agent learnings, optimized revisions,
entities, learned parameters, provenance, and weighted relationships.

## Explore knowledge

Open **Brain** to search by words or meaning and filter by memory type and
lifecycle. Select a result or graph node to inspect its content, parameters,
sources, revision history, review date, and connection scores. The map supports
zoom, pan, dragging, semantic and evidence views, and minimum connection
strength. Stronger links draw nodes closer; thickness shows weight. Reset
restores the layout calculated from connection weights.

Explicit `supports`, `contradicts`, `mentions`, `related`, and `supersedes`
relationships carry a strength between 0 and 1. Indexing also connects nearby
embeddings with inferred relationships. Semantic score is cosine similarity;
correlation combines title/tag overlap (70%) and a shared source file (30%).
Weight combines semantic similarity (80%) and correlation (20%). Distance is
`1 - weight`. These scores describe proximity, not causation, factual
confidence, or evidence of support. Inferred links remain distinguishable from
explicit evidence.

The index selects six neighbors per indexed memory with semantic similarity
at least 0.62. Undirected pairs are deduplicated; incoming neighbors can add
more connections. New embeddings update affected neighborhoods; **Reindex
memories** also refreshes existing links.

## Import provider memories manually

Provider memory files are never scanned or imported on startup or on a timer.

1. Select **Find import sources** to discover recognized Codex and Claude Code
   memory directories on this device.
2. Select sources and choose **Preview selected sources**. Inspect the file
   list, excerpts, sizes, and exclusion notes.
3. Select files and choose **Import selected memories**.

Discovery and preview create no knowledge records. Import validates the full
preview snapshot before writing; changed files require another preview.
Unchanged content deduplicates, updates create revisions, and shorter files
retire obsolete chunks while preserving history. Provider files stay intact.
No background resynchronization follows an import.

Recognized sources are Codex `memories_v2` and `memories`, and Claude Code
`memory` and `projects/*/memory`. Root instructions such as `CLAUDE.md`,
credentials, transcripts, sessions, logs, and symbolic links are excluded.
Notes identify excluded paths and reasons. Imports allow 4,096 files, 16 MiB
per file, and 64 MiB total. Long files split into chunks of at most 16,000
UTF-8 bytes, preserving paragraphs where possible, every character, source
path, and chunk number. Limits reject files with an explanation instead of
silently truncating content.

General Markdown remains explicit execution input through **Workspace Memory**.
An agent can derive a verified learning from that input and retain its source.

## Lifecycle and provenance

Origin distinguishes **Imported memory**, **Agent learning**, and **Optimized
knowledge**. Provider, source file, execution ID, and evidence explain where
knowledge came from. Agents can construct and refine knowledge independently
of provider file imports.

New imports are due for review after 90 days; learnings and optimized knowledge
after 180 days, unless another review date is supplied. Passing the date marks
knowledge **Needs review**, preserves the memory, and excludes it from default
agent retrieval. Brain can display these records for inspection. A user or
agent can flag, confirm, or archive knowledge; reviews retain an evidence note.

**Previous version** identifies a superseded revision. **Archived** records also
remain available as history. Neither is returned by default agent search.
Restoring an archived record is explicit; reimporting unchanged content does
not undo an archive. Superseded revisions cannot be confirmed current in place.

## Local retrieval

Word search uses a local term index rebuilt from the graph database. **Enable
semantic search** downloads the pinned multilingual MiniLM model (about 135 MB),
verifies file sizes and SHA-256 hashes, and indexes on the CPU in a worker.
Inference sends no memory content to an embedding service. Indexing retains
all token windows of long chunks.

Hybrid retrieval combines word relevance with exact cosine nearest neighbors
from the native database. No HNSW extension is downloaded or required; this
implementation performs exact vector scans rather than approximate indexing.
Responses contain bounded excerpts and evidence IDs. Full memories are read
on demand. Provider requests still consume tokens for instructions, tools,
retrieved context, and model behavior.

## Mandatory agent connector

The built-in **DoxieOS Brain** MCP server is always registered, including when
optional MCP servers are disabled. Models receive instructions to query
knowledge before substantive work, inspect relevant relationships, check
provenance and lifecycle, and retain verified reusable learnings with evidence.
Registration is enforced by the runtime; following tool-use instructions still
depends on the selected model.

| Tool | Purpose |
| --- | --- |
| `memory_search` | Bounded hybrid retrieval; stale knowledge is excluded unless requested. |
| `memory_get` | Inspect a memory and its provenance. |
| `memory_remember` | Retain a learning, optimized revision, entity, or typed parameter. |
| `memory_relate` | Record explicit weighted evidence relationships. |
| `memory_graph` | Explore a bounded neighborhood and its scores. |
| `memory_review` | Revalidate, flag, or archive knowledge with evidence. |

The **Brain** workflow connector retains internal ID `library` for existing
workflows; `brain` is an execution alias. Its `query` (`search` alias),
`remember`, `relate`, `graph`, and `review` actions return context and receipts. Retired
creation and mount modes report migration errors. **Workspace Memory** supplies
manual Markdown without directly registering it as knowledge.

## Storage and upgrade

- `.memory/brain.lbdb`: LadybugDB graph, provenance, revisions, lifecycle,
  relationships, and typed embedding arrays.
- `.memory/knowledge.db`: preserved legacy SQLite input after migration.
- `.memory/service.json`: temporary private authenticated endpoint for MCP
  clients, removed when the app closes.
- `.models/semantic/`: verified local inference model.

Existing DoxieOS knowledge migrates transactionally, preserving IDs, links,
vectors, and original SQLite files. A marker prevents repeated migration.
This does not scan or import provider homes. Settings and run history remain
in the separate application SQLite database.

The main process owns LadybugDB, commits imports, and serves authenticated
loopback tool calls. Workers discover, validate, and prepare files without
competing for the database lock. The desktop uses LadybugDB's official Node.js
WebAssembly build with the host filesystem, avoiding Windows native addon
compatibility problems. It requires no database service or runtime extension
download. Back up the complete application
profile with DoxieOS closed, including the graph database and sidecars.

See the official [LadybugDB Node API](https://docs.ladybugdb.com/client-apis/nodejs/)
and [concurrency model](https://docs.ladybugdb.com/concurrency/).
