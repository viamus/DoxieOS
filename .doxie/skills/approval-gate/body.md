

# Approval Gate Skill — human-in-the-loop pause

A built-in workflow primitive. When placed inside a `WorkflowDefinition`, it tells the engine "stop here; wait for a person to make a call before moving forward." Pure connector — never spawns a Claude process. Designed for workflows where automation should hand off the last call to a human (deploy approvals, irreversible writes, escalations).

## When to use

- Auto-generated work that needs human sign-off before downstream effects fire.
- Compliance gates ("only proceed if SRE on-call approves").
- Spot-check checkpoints during development of a new workflow.

## How it behaves

1. The workflow runner reaches a node whose `agentId` is `approval-gate`.
2. The node's status flips to `AwaitingApproval`. The run's overall status also flips to `AwaitingApproval`.
3. A SQLite snapshot is written so the dashboard reflects the parked state.
4. The runner posts to `DOXIE_NOTIFY_URL` so any open DoxieOS tab fires a Snackbar.
5. The run sits parked indefinitely until a human clicks Approve or Reject in the run detail page.
6. **Approve** → the gate node lands as `Succeeded`, downstream nodes start as if the gate had run normally.
7. **Reject** → the gate node lands as `Failed` with the comment captured in its logs; the whole run is `Cancelled`.

## Modes

- `wait` — only mode. Optional `prompt` field shown to the human as the question to answer.

## Caveats

- **MVP restart caveat**: if the orchestrator is restarted *while a run is parked at this gate*, the in-process await is lost. The persisted run will show as `AwaitingApproval` in the dashboard but Approve/Reject won't take effect. Re-run the workflow. A future iteration will rehydrate parked runs from SQLite on startup.
- This skill exists in the catalog so the workflow builder can pick it like any other node. The pause/resume semantics live inside `OrchestratedWorkflowRunner`.

## Why no permissions / no MCP

Pure connector — no I/O outside the workflow runner's own state. Filtered out of `/agents` by default (Connector category) so it doesn't clutter the user-facing catalog.
