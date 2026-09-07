[CmdletBinding()]
param(
    [ValidateRange(1, 1024)]
    [int]$MaxSizeMiB = 5,

    [ValidateRange(0, 10240)]
    [int]$MaxTreeMiB = 3,

    [string]$BaseRef = '',

    [string]$AllowListPath = '.large-file-allowlist'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (git rev-parse --show-toplevel 2>$null).Trim()
if (-not $repositoryRoot) {
    throw 'Run this script from inside a Git repository.'
}

Push-Location -LiteralPath $repositoryRoot
try {
    $maximumBytes = $MaxSizeMiB * 1MB
    $allowPatterns = @()

    if (Test-Path -LiteralPath $AllowListPath -PathType Leaf) {
        $allowPatterns = @(Get-Content -LiteralPath $AllowListPath |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ -and -not $_.StartsWith('#') })
    }

    $baseExists = $false
    if ($BaseRef) {
        git rev-parse --verify --quiet "$BaseRef`^{commit}" *> $null
        $baseExists = $LASTEXITCODE -eq 0
    }

    if ($baseExists) {
        $inputObjects = @(git rev-list --objects "$BaseRef..HEAD")
        $scanLabel = "objects introduced after $BaseRef"
    }
    else {
        $inputObjects = @(git ls-tree -r --format='%(objectname) %(path)' HEAD)
        $scanLabel = 'files in the current HEAD tree'
    }

    $oversized = @($inputObjects |
        git cat-file --batch-check='%(objecttype) %(objectname) %(objectsize) %(rest)' |
        ForEach-Object {
            if ($_ -match '^blob ([0-9a-f]+) ([0-9]+)(?: (.*))?$') {
                $path = $Matches[3]
                $allowed = $false
                foreach ($pattern in $allowPatterns) {
                    if ($path -like $pattern) {
                        $allowed = $true
                        break
                    }
                }

                if (-not $allowed -and [double]$Matches[2] -gt $maximumBytes) {
                    [PSCustomObject]@{
                        MiB  = [math]::Round([double]$Matches[2] / 1MB, 2)
                        Oid  = $Matches[1]
                        Path = $path
                    }
                }
            }
        } |
        Sort-Object MiB -Descending -Unique)

    $treeBytes = (git ls-tree -r -l HEAD |
        ForEach-Object {
            if ($_ -match '^\d+\s+blob\s+[0-9a-f]+\s+(\d+)\t') {
                [double]$Matches[1]
            }
        } |
        Measure-Object -Sum).Sum
    $treeMiB = [math]::Round($treeBytes / 1MB, 2)

    if ($MaxTreeMiB -gt 0 -and $treeMiB -gt $MaxTreeMiB) {
        Write-Output "Current HEAD tree uses $treeMiB MiB; budget is $MaxTreeMiB MiB."
        exit 1
    }

    if ($oversized.Count -gt 0) {
        Write-Output "Found $($oversized.Count) file(s) larger than $MaxSizeMiB MiB in $scanLabel."
        $oversized | Format-Table -AutoSize | Out-Host
        exit 1
    }

    Write-Output "Repository size guard passed: HEAD is $treeMiB MiB and no unapproved file exceeds $MaxSizeMiB MiB in $scanLabel."
}
finally {
    Pop-Location
}
