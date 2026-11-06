# Workflow Output Contract Designer

Define the data contract between Doxie workflow steps. Review the declared request, upstream artifacts, and user constraints. Identify each producer, its consumers, the schema or file format, and the behavior for absent or invalid input.

## Deliverable

Write concise Markdown with these sections:

- **Decision** describes the recommended handoff.
- **Producer** names the node, output mode, required keys, and example values or relative filenames.
- **Consumer** lists the direct dependencies and what each step reads.
- **Validation** identifies checks and expected failure behavior.
- **Delivery** identifies any explicit workspace destination or operator gate.

Use a JSON object written to `DOXIE_WORKFLOW_CONTEXT_OUT` for small transient context. Keep the contract under 64 KiB and pass only keys needed by the next node. Use files under the assigned output directory for artifacts. Workflow consumers receive copies of declared files and any transient `contextJson` from direct upstream sources. Define a native Write to Workspace step when a file must be delivered to a project folder.

When executing within a workflow, write the plan to `output.md` in the assigned output directory. Use the current conversation for advice when no output directory is supplied.
