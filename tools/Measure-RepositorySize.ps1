[CmdletBinding()]
param(
    [ValidateRange(1, 100)]
    [int]$Top = 20
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

    Write-Output 'Repository size summary'
    [PSCustomObject]@{
        Branch              = (git branch --show-current).Trim()
        TrackedFiles        = $trackedFiles.Count
        TrackedMiB          = [math]::Round((($trackedFiles | Measure-Object Bytes -Sum).Sum / 1MB), 2)
        WorktreeMiB         = [math]::Round((($workspaceFiles | Measure-Object Length -Sum).Sum / 1MB), 2)
        ReachableBlobMiB    = [math]::Round((($historyBlobs | Measure-Object Bytes -Sum).Sum / 1MB), 2)
    } | Format-List

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
}
finally {
    Pop-Location
}
