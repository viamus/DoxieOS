# Sandbox

Use `new` to allocate temporary work space within a workflow run. An optional `tag` identifies the sandbox. Use `list` to inspect the sandboxes owned by that run. Use `clean` to schedule cleanup for a run-owned sandbox.

DoxieOS removes run-owned scratch at the end of the workflow. Publish files through a node's output directory if another node should receive them. Use Write to Workspace for a deliverable that should outlive the run.
