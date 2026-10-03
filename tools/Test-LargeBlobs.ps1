[CmdletBinding()]
param(
    [ValidateRange(1, 1024)]
    [int]$MaxSizeMiB = 5,

    [ValidateRange(0.0, 10240.0)]
    [double]$MaxTreeMiB = 4.00,

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
    $maximumTreeBytes = [math]::Floor([double]$MaxTreeMiB * 1MB)
    $treeMiBDisplay = [math]::Round($treeBytes / 1MB, 2)
    $budgetMiBDisplay = [math]::Round($maximumTreeBytes / 1MB, 2)
    $remainingBytes = $maximumTreeBytes - $treeBytes
    $utilizationPercent = if ($maximumTreeBytes -gt 0) { $treeBytes / $maximumTreeBytes * 100 } else { 0.0 }

    if ($MaxTreeMiB -gt 0 -and $treeBytes -gt $maximumTreeBytes) {
        Write-Output "Current HEAD tree uses $treeBytes bytes ($treeMiBDisplay MiB displayed); budget is $maximumTreeBytes bytes ($MaxTreeMiB MiB)."
        Write-Output ("Over budget by {0} bytes; utilization is {1:N2}%." -f ($treeBytes - $maximumTreeBytes), $utilizationPercent)
        exit 1
    }

    if ($oversized.Count -gt 0) {
        Write-Output "Found $($oversized.Count) file(s) larger than $MaxSizeMiB MiB in $scanLabel."
        $oversized | Format-Table -AutoSize | Out-Host
        exit 1
    }

    # Informational only: warn before the budget is exhausted so the next phase is not forced into an emergency slimming pass.
    if ($MaxTreeMiB -gt 0 -and $utilizationPercent -ge 90) {
        Write-Output ("WARNING: Repository tree usage is {0:N2}% of the configured budget ({1} of {2} bytes); {3} bytes remain." -f $utilizationPercent, $treeBytes, $maximumTreeBytes, $remainingBytes)
    }

    Write-Output "Repository size guard passed:"
    Write-Output ("  HEAD = {0} bytes ({1:N2} MiB)" -f $treeBytes, $treeMiBDisplay)
    Write-Output ("  Budget = {0} bytes ({1:N2} MiB)" -f $maximumTreeBytes, $budgetMiBDisplay)
    Write-Output ("  Remaining = {0} bytes" -f $remainingBytes)
    Write-Output ("  Utilization = {0:N2}%" -f $utilizationPercent)
    Write-Output "  No unapproved file exceeds $MaxSizeMiB MiB in $scanLabel."
}
finally {
    Pop-Location
}
