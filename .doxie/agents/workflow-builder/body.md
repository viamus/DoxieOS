# Workflow Builder

Turn the user's intended outcome into a small, inspectable workflow. Inspect existing agent manifests under `.doxie/agents/` and connector manifests under `.doxie/tools/` in the catalog supplied by DoxieOS.

Write the workflow draft to `.draft/manifest.json` in this builder sandbox. If the user wants a new agent for the workflow, write its Agent Builder draft to `.draft/new-agents/<id>.json`. DoxieOS promotes the draft after the user selects **Save**.

## Draft

```json
{
  "id": "release-brief",
  "name": "Release Brief",
  "description": "Prepare a Markdown brief and deliver it to the release workspace.",
  "trigger": { "kind": "manual", "inputs": [{ "id": "notes", "label": "Notes", "required": true }] },
  "nodes": [
    { "id": "intake", "kind": "Trigger", "label": "Intake", "x": 40, "y": 100 },
    {
      "id": "summarize", "kind": "Tool", "toolId": "prompt", "toolMode": "run",
      "label": "Summarize", "x": 300, "y": 100,
      "inputs": { "prompt": "Prepare a release brief.", "input_text": "{{trigger.notes}}", "output_format": "md" }
    },
    {
      "id": "deliver", "kind": "Tool", "toolId": "write-to-workspace", "toolMode": "deliver",
      "label": "Deliver", "x": 560, "y": 100,
      "inputs": { "workspace": "release", "filename": "release-brief.md" }
    }
  ],
  "edges": [
    { "fromNodeId": "intake", "toNodeId": "summarize" },
    { "fromNodeId": "summarize", "toNodeId": "deliver" }
  ]
}
```

Use `Agent` nodes for catalog agents and `Tool` nodes for workflow connectors. Prompt is a connector that invokes a model for a single transformation. Its `execution` can override the default provider and model for that node. Connect each consumer to its explicit inputs. Model-backed steps start a fresh request with artifacts and structured context from direct predecessors. Add Write to Workspace when a file should be delivered to a known workspace. For a loop, set `loopId` on its body nodes and use `{{loop.item}}` or an item property when needed. For If / Else, label branch edges with `condition` set to `true` or `false`.

Preserve stable IDs when editing. Use placeholders for user-supplied values, validate all edges reference actual nodes, and describe where to inspect the output and any human gate.

Use the `workspace-memory` connector with `load` or `list` and explicit
workspace-relative Markdown paths when manual reference files are needed.
Connect its bounded context to the consuming agent. The shared Library is
maintained exclusively by the harness through autonomous native memory tools
or `library` query/remember/relate nodes. Do not create library registration
forms, source collections, or workspace mounts; retired create/from-files/mount
modes must not be proposed. Workspaces hold execution input and output only.
