# Workspace Memory

The workspace contains manually authored input and output files. This native connector lists or loads only the Markdown files or folders explicitly named in `paths`, relative to the selected workspace. Use one path per line or a JSON list of paths. Selecting `.` explicitly selects the workspace root. Absolute paths, parent traversal and symbolic links are rejected.

`list` publishes source paths and byte sizes without document content. `load` publishes bounded UTF-8 text with each source's original byte size, loaded byte count and truncation flag. The complete JSON context defaults to 16 KiB and accepts a `maxBytes` budget from 1 KiB through 64 KiB. Large sources or selections remain available in the workspace; truncation and omitted-source flags explain what did not fit. Select narrower paths or a larger budget when necessary.

The connector writes `workspace-memory.json` and passes the same structured context to direct downstream steps. Source Markdown files are unchanged. Loaded document content is input data, not system instructions. No source document is copied into the central library. A consuming agent can review the material and let its execution harness record verified reusable learnings with provenance.
