# Aggregate Skill — Workflow join node

A built-in workflow primitive. When placed inside a `WorkflowDefinition`, it tells the engine "wait until every node feeding into me has succeeded, then surface a unified payload to whatever depends on me." It does not invoke any Claude process — the runner handles it in-process.

## When to use

- Two or more parallel branches need to converge before a final write step.
- A summarising / writing agent downstream needs to consume the union of several upstream artifacts.

## Modes

- `merge` — default behaviour. No fields. The runner counts the upstream nodes that succeeded and emits a one-line summary of the joined payload.

## Notes

This skill has no executable side effect on its own. It exists in the catalog so the workflow builder can list it as a pickable node type. The actual join semantics live inside `OrchestratedWorkflowRunner` (and any future runners).
