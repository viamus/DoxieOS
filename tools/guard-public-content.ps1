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

$configPath = Join-Path -Path $PSScriptRoot -ChildPath 'public-content-blocklist.json'
$config = Get-Content -LiteralPath $configPath -Raw -ErrorAction Stop | ConvertFrom-Json

$blockedPatterns = @(
    foreach ($blocked in $config.blockedPatterns) {
        @{ Pattern = Join-Pattern ([string[]]$blocked.patternParts); Label = $blocked.label }
    }
)

$includeExtensions = @($config.includeExtensions)
$ignoredPathParts = @($config.ignoredPathParts)

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
