[CmdletBinding()]
param(
    [ValidateRange(1, 100)]
    [int]$Top = 20,

    # Optional diagnostic limits. 0 disables the check. The hard tree budget is owned by
    # tools/Test-LargeBlobs.ps1, so these default to disabled rather than duplicating it.
    [ValidateRange(0, 10240)]
    [int]$MaxTrackedMiB = 0,

    [ValidateRange(0, 10240)]
    [int]$MaxGitMiB = 0
)

$ErrorActionPreference = 'Stop'

$repositoryRootText = (git rev-parse --show-toplevel 2>$null).Trim()
if (-not $repositoryRootText) {
    throw 'Run this script from inside a Git repository.'
}
$repositoryRoot = (Resolve-Path -LiteralPath $repositoryRootText).Path
$gitDirectory = (Resolve-Path -LiteralPath (git rev-parse --git-dir).Trim()).Path
$separator = [System.IO.Path]::DirectorySeparatorChar

Push-Location -LiteralPath $repositoryRoot
try {
    # Tracked HEAD size comes from Git tree metadata only. This is the authoritative committed
    # view: identical on every platform, independent of worktree line-ending conversion, and it
    # never requires every tracked path to exist on disk (which is not portable across runners).
    $trackedBlobs = @(git ls-tree -r -l HEAD | ForEach-Object {
        if ($_ -match '^\d+\s+blob\s+([0-9a-f]+)\s+(\d+)\t(.*)$') {
            [PSCustomObject]@{
                Oid   = $Matches[1]
                Bytes = [double]$Matches[2]
                MiB   = [math]::Round([double]$Matches[2] / 1MB, 2)
                Path  = $Matches[3]
            }
        }
    })
    $trackedBytes = [double](($trackedBlobs | Measure-Object Bytes -Sum).Sum)
    $trackedMiBDisplay = [math]::Round($trackedBytes / 1MB, 2)

    # Worktree payload excludes Git internals using platform-safe path comparison.
    $workspaceFiles = @(Get-ChildItem -LiteralPath $repositoryRoot -File -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object {
            $fullName = $_.FullName
            -not ($fullName -eq $gitDirectory -or $fullName.StartsWith($gitDirectory + $separator, [System.StringComparison]::Ordinal))
        })
    $worktreeBytes = [double](($workspaceFiles | Measure-Object Length -Sum).Sum)

    # Reachable blobs across all local refs; a history metric, never the tree budget.
    $historyBlobs = @(git rev-list --objects --all |
        git cat-file --batch-check='%(objecttype) %(objectname) %(objectsize) %(rest)' |
        ForEach-Object {
            if ($_ -match '^blob ([0-9a-f]+) ([0-9]+)(?: (.*))?$') {
                [PSCustomObject]@{
                    Bytes = [double]$Matches[2]
                    MiB   = [math]::Round([double]$Matches[2] / 1MB, 2)
                    Oid   = $Matches[1]
                    Path  = $Matches[3]
                }
            }
        })
    $reachableBlobBytes = [double](($historyBlobs | Measure-Object Bytes -Sum).Sum)

    # .git object database size, reported in KiB by git count-objects -v.
    $gitObjectStats = @{}
    git count-objects -v | ForEach-Object {
        if ($_ -match '^([^:]+):\s+(\d+)$') {
            $gitObjectStats[$Matches[1]] = [double]$Matches[2]
        }
    }
    $gitObjectKiB = 0.0
    foreach ($key in 'size', 'size-pack', 'size-garbage') {
        if ($gitObjectStats.ContainsKey($key)) { $gitObjectKiB += $gitObjectStats[$key] }
    }
    $gitBytes = $gitObjectKiB * 1KB

    $summary = [PSCustomObject]@{
        Branch            = (git branch --show-current).Trim()
        TrackedFiles      = $trackedBlobs.Count
        TrackedBytes      = $trackedBytes
        TrackedMiBDisplay = $trackedMiBDisplay
        WorktreeMiB       = [math]::Round($worktreeBytes / 1MB, 2)
        GitMiB            = [math]::Round($gitBytes / 1MB, 2)
        ReachableBlobMiB  = [math]::Round($reachableBlobBytes / 1MB, 2)
    }

    Write-Output 'Repository size summary'
    Write-Output 'Tracked HEAD blobs are the committed tree budget metric; worktree and .git values are diagnostics only.'
    $summary | Format-List

    Write-Output "Largest $Top tracked HEAD blobs"
    $trackedBlobs |
        Sort-Object Bytes -Descending |
        Select-Object -First $Top MiB, Path |
        Format-Table -AutoSize

    Write-Output "Largest $Top blobs reachable from local refs"
    $historyBlobs |
        Sort-Object Bytes -Descending |
        Select-Object -First $Top MiB, Oid, Path |
        Format-Table -AutoSize

    Write-Output 'Git object database'
    git count-objects -vH

    # Optional limits compare exact bytes; rounded MiB values are never used for the decision.
    $maxTrackedBytes = [math]::Floor([double]$MaxTrackedMiB * 1MB)
    $maxGitBytes = [math]::Floor([double]$MaxGitMiB * 1MB)
    $violations = @()
    if ($MaxTrackedMiB -gt 0 -and $trackedBytes -gt $maxTrackedBytes) {
        $violations += "Tracked HEAD blobs use $trackedBytes bytes ($trackedMiBDisplay MiB displayed); budget is $maxTrackedBytes bytes ($MaxTrackedMiB MiB)."
    }
    if ($MaxGitMiB -gt 0 -and $gitBytes -gt $maxGitBytes) {
        $violations += "Git metadata uses $gitBytes bytes; budget is $maxGitBytes bytes ($MaxGitMiB MiB)."
    }
    if ($violations.Count -gt 0) {
        throw ($violations -join [Environment]::NewLine)
    }
}
finally {
    Pop-Location
}
