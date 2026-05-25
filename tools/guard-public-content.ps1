param(
    [switch]$Staged
)

$ErrorActionPreference = "Stop"

$repoRootRaw = (git rev-parse --show-toplevel).Trim()
if ([string]::IsNullOrWhiteSpace($repoRootRaw)) {
    Write-Error "Could not resolve the git repository root."
    exit 2
}
$repoRoot = (Resolve-Path -LiteralPath $repoRootRaw).Path
$repoRootWithSeparator = $repoRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

function Join-Pattern([string[]]$Parts) {
    return ($Parts -join '')
}

$blockedPatterns = @(
    @{ Pattern = Join-Pattern @('(?i)\b', 'am', 'bev', '\b'); Label = 'company-specific reference' },
    @{ Pattern = Join-Pattern @('(?i)', 'ab', '\s*[-_ ]?\s*', 'in', '\s*', 'bev'); Label = 'company-specific reference' },
    @{ Pattern = Join-Pattern @('(?i)\b', 'abin', 'bev', '\b'); Label = 'company-specific reference' },
    @{ Pattern = Join-Pattern @('(?i)\b', 'am', 'bev', '\s*', 'tech', '\b|\b', 'am', 'bev', 'tech', '\b'); Label = 'company-specific reference' },
    @{ Pattern = Join-Pattern @('(?i)\b', 'am', 'bev', 'ize', '\b'); Label = 'private toolkit reference' },
    @{ Pattern = Join-Pattern @('(?i)\b', 'AM', 'BEV', '-SA\b|\b', 'am', 'bev', 'devs\b|@', 'am', 'bev'); Label = 'private organization reference' },
    @{ Pattern = Join-Pattern @('(?i)\b', 'brew', 'dat\b|\b', 'brew', 'zone\b|\b', 'touch', 'less\b|\b', 'lud', 'eritz\b'); Label = 'private internal artifact reference' },
    @{ Pattern = Join-Pattern @('(?i)\b', 'azure', '\s+', 'dev', 'ops\b|\bmcp-', 'azure', 'dev', 'ops\b|@', 'azure', '/mcp'); Label = 'removed external integration reference' },
    @{ Pattern = Join-Pattern @('(?i)\b', 'A', 'DO', '\b'); Label = 'removed external integration acronym' },
    @{ Pattern = Join-Pattern @('(?i)\b', 'data', 'dog', '\b'); Label = 'removed external integration reference' },
    @{ Pattern = Join-Pattern @('(?i)\b', 'sonar', 'qube', '\b|\b', 'SONAR', '_TOKEN\b'); Label = 'removed external integration reference' },
    @{ Pattern = Join-Pattern @('(?i)\b', 'L', 'GPD\b|\bG', 'DPR\b'); Label = 'removed privacy catalog reference' },
    @{ Pattern = Join-Pattern @('(?i)\b', 'pull', '[_ -]?', 'request', '\b|\bP', 'Rs?\b'); Label = 'removed review catalog reference' },
    @{ Pattern = Join-Pattern @('(?i)C:[\\/](Work', 'space|Users[\\/]via', 'mu|DoxieOS)'); Label = 'local absolute Windows path' },
    @{ Pattern = Join-Pattern @('(?i)DoxieOS', '-Public'); Label = 'local public-clone name reference' }
)

$includeExtensions = @(
    '.cs', '.csproj', '.razor', '.css', '.js', '.json', '.md', '.yml', '.yaml',
    '.xml', '.props', '.targets', '.slnx', '.toml', '.ps1', '.sh', '.dockerignore',
    '.gitignore', '.webmanifest', '.svg'
)

$ignoredPathParts = @(
    '/.git/',
    '/bin/',
    '/obj/',
    '/.claude/',
    '/.codex/',
    '/.agents/',
    '/.doxie/index/'
)

function Test-TextFile([string]$Path) {
    $name = [System.IO.Path]::GetFileName($Path)
    $ext = [System.IO.Path]::GetExtension($Path)
    if ($includeExtensions -contains $name) { return $true }
    if ($includeExtensions -contains $ext) { return $true }
    return $false
}

function Should-Skip([string]$Path) {
    $normalized = $Path.Replace('\', '/')
    foreach ($part in $ignoredPathParts) {
        if ($normalized.IndexOf($part, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }
    return $false
}

if ($Staged) {
    $candidatePaths = git diff --cached --name-only --diff-filter=ACMR
} else {
    $candidatePaths = git ls-files
}

$findings = New-Object System.Collections.Generic.List[string]

foreach ($relativePath in $candidatePaths) {
    if ([string]::IsNullOrWhiteSpace($relativePath)) { continue }

    $fullPath = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($repoRoot, $relativePath))
    if ($fullPath -ne $repoRoot -and -not $fullPath.StartsWith($repoRootWithSeparator, [StringComparison]::OrdinalIgnoreCase)) {
        $findings.Add("${relativePath}: path resolves outside repository")
        continue
    }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }
    if (Should-Skip $fullPath) { continue }
    if (-not (Test-TextFile $fullPath)) { continue }

    $content = Get-Content -LiteralPath $fullPath -Raw -ErrorAction Stop
    foreach ($blocked in $blockedPatterns) {
        $match = [regex]::Match($content, $blocked.Pattern)
        if ($match.Success) {
            $lineNumber = ($content.Substring(0, $match.Index) -split "`n").Count
            $findings.Add("${relativePath}:${lineNumber}: $($blocked.Label) -> '$($match.Value)'")
        }
    }
}

if ($findings.Count -gt 0) {
    Write-Host "Public content guard failed. Remove or rewrite these references before committing:" -ForegroundColor Red
    foreach ($finding in $findings) {
        Write-Host "  - $finding" -ForegroundColor Red
    }
    exit 1
}

Write-Host "Public content guard passed." -ForegroundColor Green
