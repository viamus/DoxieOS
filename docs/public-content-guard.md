# Public Content Guard

DoxieOS public commits must stay free of company-specific, private-catalog, and local-machine references.

The repository ships a versioned pre-commit hook plus a standalone scanner:

- `.githooks/pre-commit`
- `tools/guard-public-content.ps1`
- `tools/guard-public-content.py`
- `tools/public-content-blocklist.json`

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

On Linux/macOS without PowerShell:

```bash
python3 tools/guard-public-content.py
```

Scan only staged files, the same way the hook does:

```bash
pwsh -NoProfile -ExecutionPolicy Bypass -File tools/guard-public-content.ps1 -Staged
```

```bash
python3 tools/guard-public-content.py --staged
```

## What It Blocks

The guard blocks:

- enterprise names, large-company references, private organization handles, and private toolkit names;
- private migration/reference-pack file names and private repository names;
- removed vendor-specific catalog references;
- removed private workflow/catalog terms;
- local absolute paths from Windows, macOS, Linux, and WSL developer machines;
- common credential material such as API keys, tokens, private keys, and JWTs.

When it fails, rewrite the reference into a domain-neutral example or keep the material in a private catalog.
