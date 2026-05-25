# Write to Workspace Skill — Final delivery node

A built-in workflow primitive. When placed inside a `WorkflowDefinition`, it consumes the workflow's accumulated output and writes it as a file into a target Workspace's folder. It does not invoke any Claude process — the runner handles the file write in-process.

## When to use

- You have a multi-agent composition that researches / summarises / generates content and you want the final artifact to land somewhere a human (or another agent) will reliably find it.
- The brief should outlive the run — Workspaces are persistent, run logs are not.

## Modes

- `deliver` — default behaviour. Fields:
  - **workspace** (required) — id of an existing user-owned Workspace under `./.workspace/`.
  - **filename** (optional) — name of the file to create. Defaults to `workflow-<timestamp>.md`.

## Notes

The runner is responsible for the actual write; this skill exists in the catalog so the workflow builder can list it as a pickable terminal node. If the configured workspace doesn't exist at run time, the node logs the missing reference but does not fail the workflow — useful when prototyping a composition before the destination workspace has been created.
