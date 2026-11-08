# Approval Gate

The workflow engine records `AwaitingApproval` and posts a desktop notification. An operator opens the run and approves or rejects the specific gate. Approval resumes downstream nodes and forwards upstream artifacts and transient context. Rejection cancels the run and records the operator's comment.

Closing the app ends active executions. An unfinished gate is marked cancelled on the next launch. Re-run the workflow to reach the gate again.
