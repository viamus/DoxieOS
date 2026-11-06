# Library

Create a library with a new kebab-case ID and optional display name and description. `create` writes the library manifest. `from-files` takes explicit paths relative to the selected workspace, one per line, or Markdown and text artifacts from direct upstream nodes. `from-current` reads the selected workspace's `memory/` directory.

DoxieOS copies the selected content into the library using an atomic catalog write. Existing library IDs produce a conflict. Memory text is copied as supplied. Library mounts make the resulting content available to future runs.
