# Public Content Guard

DoxieOS public commits must stay free of company-specific, private-catalog, and local-machine references.

The repository ships a versioned pre-commit hook plus a standalone scanner:

- `.githooks/pre-commit`
- `tools/guard-public-content.ps1`

## Enable The Hook

Run once after cloning:

```bash
git config core.hooksPath .githooks
```

After that, every commit runs the public content guard against staged text files.

## Run Manually

Scan tracked files:

```bash
pwsh -NoProfile -ExecutionPolicy Bypass -File tools/guard-public-content.ps1
```

On Windows PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/guard-public-content.ps1
```

Scan only staged files, the same way the hook does:

```bash
pwsh -NoProfile -ExecutionPolicy Bypass -File tools/guard-public-content.ps1 -Staged
```

## What It Blocks

The guard blocks:

- company names, private organization handles, and private toolkit names;
- removed vendor-specific catalog references;
- removed private workflow/catalog terms;
- local absolute paths from developer machines.

When it fails, rewrite the reference into a domain-neutral example or keep the material in a private catalog.
