

# Approval Gate Skill — human-in-the-loop pause

This built-in workflow connector pauses a run until a person makes a decision. The workflow engine handles it directly.

## When to use

- Auto-generated work that needs human sign-off before downstream effects fire.
- Compliance gates ("only proceed if SRE on-call approves").
- Spot-check checkpoints during development of a new workflow.

## How it behaves

1. The workflow runner reaches a node whose `agentId` is `approval-gate`.
2. The node's status flips to `AwaitingApproval`. The run's overall status also flips to `AwaitingApproval`.
3. A SQLite snapshot is written so the dashboard reflects the parked state.
4. DoxieOS publishes an approval notification in the desktop workbench.
5. The run sits parked indefinitely until a human clicks Approve or Reject in the run detail page.
6. **Approve** → the gate node lands as `Succeeded`, downstream nodes start as if the gate had run normally.
7. **Reject** → the gate node lands as `Failed` with the comment captured in its logs; the whole run is `Cancelled`.

## Modes

- `wait` — only mode. Optional `prompt` field shown to the human as the question to answer.

## Closing DoxieOS while a gate is waiting

On startup, DoxieOS marks unfinished workflow runs from the previous session
as cancelled. Re-run the workflow to reach the gate again.
