# tools/sync.ps1
#   One-shot: stage everything, commit, push to origin/<current branch>.
#
# Usage:
#   .\tools\sync.ps1 "fix: tighten Tgt EOM tooltip"
#
# Notes:
#   - Run from the repo root (or anywhere — the script `cd`s back to root).
#   - First time you push, Git Credential Manager opens a browser window for
#     GitHub OAuth. After that, subsequent runs are silent.
#   - Refuses to commit if the working tree is clean.
#   - Refuses to push to `main` directly when the branch is `main` (forces
#     you to create a feature branch first, matching the team workflow).
#
# Quick-add a feature branch instead (recommended):
#   git checkout -b feature/<short-name>
#   .\tools\sync.ps1 "first cut of <short-name>"
#   # Then open a PR on github.com.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Message
)

$ErrorActionPreference = 'Stop'

# Ensure we're at the repo root regardless of where the user invoked from.
$repoRoot = git rev-parse --show-toplevel 2>$null
if (-not $repoRoot) {
    Write-Error "Not inside a git repository."
    exit 1
}
Set-Location $repoRoot

# Refuse to commit on main — feature-branch workflow only.
$branch = git rev-parse --abbrev-ref HEAD
if ($branch -eq 'main') {
    Write-Error @"
Refusing to commit on 'main'.
The team workflow requires a feature branch + PR. Run:

    git checkout -b feature/<short-description>
    .\tools\sync.ps1 "$Message"

…then open a Pull Request on GitHub.
"@
    exit 2
}

# Bail if nothing changed.
$status = git status --porcelain
if (-not $status) {
    Write-Host "Working tree is clean — nothing to commit."
    exit 0
}

git add -A
git commit -m "$Message"
git push -u origin $branch
Write-Host ""
Write-Host "Pushed '$branch' to origin. Open a PR at:" -ForegroundColor Green
Write-Host "  https://github.com/sheeja72/LPMSIM/pull/new/$branch" -ForegroundColor Cyan
