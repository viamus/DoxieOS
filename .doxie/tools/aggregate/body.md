# Aggregate

The workflow engine runs this tool after the direct upstream nodes resolve. It combines artifact text and file metadata into `merged.md`, with each section labeled by source node and filename. It also forwards any transient context from upstream producers without changing the JSON text. When upstream supplies context alone, the tool forwards the context without creating a report.

Use an explicit Write to Workspace node when the combined document should become a workspace deliverable.
