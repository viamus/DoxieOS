# Central library and memory graph

DoxieOS 2.1 keeps reusable knowledge in one shared library, independently of
workspaces. A workspace is an input/output folder selected for an execution.
The library holds memories, entities, learned parameters, and evidence links
produced by the agent harness. It is a single shared knowledge base with no
manual registration forms and no workspace associations.

## Use the library

Open **Library** to search knowledge or filter by memory type.
Select a result or graph node to inspect its content, typed parameters, source,
and revision history. The graph supports selection, zoom, and relationship
inspection. Agents connect evidence with `supports`, `contradicts`, `related`,
`mentions`, or `supersedes` links.

Every model execution can autonomously read and feed the library, including
agent runs, workflow nodes and builder conversations. The execution contract
instructs agents to record new, reusable, verified learnings and parameters
with evidence before finishing. It adds no separate model invocation for this
purpose, and agents add no filler when nothing new was learned. A revised
learning preserves its predecessor and links the evidence automatically.

The page provides visibility into that process. Manual Markdown belongs in a
workspace and is supplied through the **Workspace Memory** connector as input.
An agent can derive a verified learning from that material; loading the file
does not copy raw Markdown into the shared library. Provider source files are
never edited by library operations. Learning stores knowledge, not model weights.

## Automatic Codex and Claude Code memory feeds

The harness discovers recognized memory locations on startup and refreshes
them in the background every five minutes while the app is open. **Agent
memory sources** shows the current state, source counts, last update and any
excluded files. **Refresh agent memories** requests another harness sync; it
does not register arbitrary Markdown or expose a manual content form.

Discovery, preview and import run in workers. The harness validates a complete
snapshot of file identities and content hashes before writing. If the source
changes during a sync, it takes a fresh snapshot and retries once. Operational
errors appear in feed status and can be retried on the next refresh.

- Codex: agent-produced files under `memories_v2` and `memories`.
- Claude Code: agent-produced files under `memory` and `projects/*/memory`.
- Provider homes are resolved by the harness, not supplied as arbitrary paths
  by the renderer.

Imports accept recognized agent-memory outputs. Global `CLAUDE.md` and other
manually authored profile instructions are not imported as learned knowledge.
Credential/configuration files,
conversation transcripts, sessions, logs, and symbolic links are excluded.
Files with detected credential material are reported as excluded. Feed status
reports limits and unreadable sources. One sync handles up to 4,096 files,
256 KiB per file, and 64 MiB total. Large text is split into bounded memories
with the original source path and chunk number preserved.

Reimporting unchanged material is idempotent. Changed material creates a
revision with provenance. Importing a source does not erase other knowledge;
obsolete versions are retained as history. Source collection files from earlier
DoxieOS versions remain on disk but are not automatically registered as shared
knowledge. Put manual references in a workspace and let an agent derive verified
learnings through the harness.

## Retrieval and token use

Text search works immediately through SQLite FTS5. DoxieOS retrieves bounded
excerpts and evidence IDs rather than copying the entire library into a
provider request. Agent-owned operating instructions and explicit inputs remain
separate from retrieved knowledge.

**Search by meaning** optionally downloads the pinned multilingual
MiniLM embedding model, approximately 135 MB. Its files are verified by
size and SHA-256 before use. Inference runs locally on the CPU in a worker;
model weights and vectors are reused between searches. The shared index stores
vectors for memory revisions. Hybrid retrieval combines text relevance and
vector similarity. Text search remains available while the model is missing.

Provider system prompts, tool definitions, retrieved files and model-side
behavior also consume tokens. Request byte estimates describe the text
DoxieOS supplies, not a guaranteed reduction in billed provider tokens.

## Native agent and workflow access

DoxieOS registers its built-in memory MCP server in its own Codex and Claude
Code profiles. Agents, workflow steps, builder sessions, and consoles can use:

| Tool | Purpose |
| --- | --- |
| `memory_search` | Retrieve relevant memories and learned parameters. |
| `memory_get` | Read a specific memory and its provenance. |
| `memory_remember` | Record a verified learning, entity, or typed parameter. |
| `memory_relate` | Link supporting, conflicting, related, or superseding evidence. |
| `memory_graph` | Inspect a bounded neighborhood of the knowledge graph. |

Read results as source material and retain their IDs when explaining a
decision. Record durable, verified learnings with evidence instead of saving
raw execution logs. Learned parameters accept typed JSON values and reject
credential fields.

The native **Library** workflow connector supports `remember`, `query`
(`search` alias), and `relate`. It publishes receipts and bounded transient
context for downstream steps, without requiring a workspace. The separate
**Workspace Memory** connector lists or loads explicit relative Markdown paths
from a selected workspace. It returns bounded input context and a source
receipt without persisting the raw files as learned knowledge. Legacy source
creation, `from-files`, `mount` and `from-current` Library actions report a
migration error rather than recreating manual library registration or mounts.

```json
{
  "id": "load-manual-reference",
  "kind": "Tool",
  "toolId": "workspace-memory",
  "toolMode": "load",
  "inputs": { "paths": "memory/release-rules.md" }
}
```

## Storage and implementation

- `.memory/knowledge.db`: memories, source provenance, revisions, relations,
  text index, and embedding vectors in a local SQLite database using WAL.
- `.models/`: downloaded, verified local inference models.
- `.doxie/libraries/`: original source collection manifests and reference files.
- `.workspace/`: execution inputs and outputs, without generated memory mounts.

Back up the application profile with DoxieOS closed. SQLite WAL sidecar files
must accompany a live database copy; copying only the main file while it is
being written is not a consistent backup. Source collection ZIP transfer does
not represent a backup of the central learned graph.

The embedded graph/text/vector store keeps the portable desktop distribution
self-contained. Chroma is a possible future remote adapter; its JavaScript
client connects to a running backend. See the [Chroma client documentation](https://github.com/chroma-core/chroma/blob/main/clients/js/packages/chromadb-client/README.md).
The embedding implementation follows the model publisher's
[Transformers.js usage](https://huggingface.co/Xenova/paraphrase-multilingual-MiniLM-L12-v2).
The native tools follow the [MCP tools contract](https://modelcontextprotocol.io/specification/2025-11-25/server/tools)
and [Codex MCP configuration](https://developers.openai.com/codex/mcp/).
