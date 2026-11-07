# Doxie catalog

Author provider-backed agents and workflows in the Doxie catalog.

```text
.doxie/
├── agents/<id>/
│   ├── manifest.json
│   ├── body.md
│   ├── agent.md         # optional Claude subagent adapter
│   └── memories/       # optional reusable agent memories
├── tools/<id>/         # shipped connector descriptors
├── workflows/<id>/workflow.json
└── libraries/<id>/library.json
```

DoxieOS reads an agent from its manifest and body. Its manifest defines the stable ID, display metadata, input modes and prerequisites. Add `execution: { "providerId": "codex", "model": "<model>" }` to explicitly select a provider and model. A workflow can override that choice for an individual agent node.

DoxieOS generates provider compatibility files from the agent catalog. Claude Code receives `.claude/skills/` and optional `.claude/agents/` files. Codex receives `.agents/skills/` and the catalog index. Edit the canonical agent before regenerating those files.

Workflow connectors live under `.doxie/tools/`. The runtime handles native operations in-process and dispatches Prompt to its configured model. Reserved connector IDs are `aggregate`, `approval-gate`, `if-else`, `library`, `loop`, `prompt`, `sandbox` and `write-to-workspace`. Agent Builder and Workflow Builder run through the authoring screens.

Earlier workbenches stored agents under `.doxie/skills/<id>/` and optional Claude companions in `.doxie/agents/<id>.md`. DoxieOS can still read those definitions and import existing archives. Imported and newly saved agent definitions use the current layout. Use the catalog pages to share assets by ZIP.

See the [authoring guide](../docs/authoring-agents-and-workflows.md) for workflow examples and the structured handoff.
