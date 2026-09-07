[CmdletBinding()]
param(
    [ValidateRange(1, 100)]
    [int]$Top = 20,

    [ValidateRange(0, 10240)]
    [int]$MaxTrackedMiB = 3,

    [ValidateRange(0, 10240)]
    [int]$MaxGitMiB = 15
)

$ErrorActionPreference = 'Stop'

$repositoryRootText = (git rev-parse --show-toplevel 2>$null).Trim()
if (-not $repositoryRootText) {
    throw 'Run this script from inside a Git repository.'
}
$repositoryRoot = (Resolve-Path -LiteralPath $repositoryRootText).Path
$gitDirectoryText = (git rev-parse --git-dir).Trim()
$gitDirectory = (Resolve-Path -LiteralPath $gitDirectoryText).Path.TrimEnd('\')

Push-Location -LiteralPath $repositoryRoot
try {
    $trackedFiles = @(git ls-files | ForEach-Object {
        if (Test-Path -LiteralPath $_ -PathType Leaf) {
            $file = Get-Item -LiteralPath $_
            [PSCustomObject]@{
                Bytes = $file.Length
                MiB   = [math]::Round($file.Length / 1MB, 2)
                Path  = $_
            }
        }
    })

    $workspaceFiles = @(Get-ChildItem -LiteralPath $repositoryRoot -File -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object {
            -not $_.FullName.StartsWith(
                $gitDirectory + '\',
                [System.StringComparison]::OrdinalIgnoreCase
            )
        })
    $gitFiles = @(Get-ChildItem -LiteralPath $gitDirectory -File -Recurse -Force -ErrorAction SilentlyContinue)

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

    $summary = [PSCustomObject]@{
        Branch              = (git branch --show-current).Trim()
        TrackedFiles        = $trackedFiles.Count
        TrackedMiB          = [math]::Round((($trackedFiles | Measure-Object Bytes -Sum).Sum / 1MB), 2)
        WorktreeMiB         = [math]::Round((($workspaceFiles | Measure-Object Length -Sum).Sum / 1MB), 2)
        GitMiB              = [math]::Round((($gitFiles | Measure-Object Length -Sum).Sum / 1MB), 2)
        ReachableBlobMiB    = [math]::Round((($historyBlobs | Measure-Object Bytes -Sum).Sum / 1MB), 2)
    }

    Write-Output 'Repository size summary'
    $summary | Format-List

    Write-Output "Largest $Top tracked files"
    $trackedFiles |
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

    $violations = @()
    if ($MaxTrackedMiB -gt 0 -and $summary.TrackedMiB -gt $MaxTrackedMiB) {
        $violations += "Tracked files use $($summary.TrackedMiB) MiB; budget is $MaxTrackedMiB MiB."
    }
    if ($MaxGitMiB -gt 0 -and $summary.GitMiB -gt $MaxGitMiB) {
        $violations += "Git metadata uses $($summary.GitMiB) MiB; budget is $MaxGitMiB MiB."
    }
    if ($violations.Count -gt 0) {
        throw ($violations -join [Environment]::NewLine)
    }
}
finally {
    Pop-Location
}
