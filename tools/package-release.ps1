[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$PublishDirectory,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [Parameter(Mandatory)]
    [string]$GitCommit,

    [string]$GitTag = 'manual',

    [string]$Rid = 'win-x64',

    [string]$TargetFramework = 'net10.0-windows',

    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-StableVersion {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
        throw "Release version must use the stable X.Y.Z format; received '$Value'."
    }
}

function Get-ArtifactName {
    param([string]$ReleaseVersion, [string]$ReleaseRid)
    return "AIHelper-$ReleaseVersion-$ReleaseRid.zip"
}

function Test-ForbiddenReleaseFile {
    param([string]$RelativePath)
    $normalized = $RelativePath.Replace('\', '/').ToLowerInvariant()
    $name = [System.IO.Path]::GetFileName($normalized)
    if ($name -eq 'config.json' -or $name -eq '.env') { return $true }
    if ($name.EndsWith('.log') -or $name.EndsWith('.user') -or $name.EndsWith('.tmp')) { return $true }
    if ($normalized.StartsWith('testresults/') -or $name.StartsWith('testhost')) { return $true }
    if ($name.Contains('credential') -or $name.Contains('password') -or $name.Contains('historical') -or $name.Contains('dataset')) { return $true }
    return $false
}

Assert-StableVersion -Value $Version
if ([string]::IsNullOrWhiteSpace($Rid)) { throw 'Release RID is required.' }
if ([string]::IsNullOrWhiteSpace($GitCommit)) { throw 'GitCommit is required.' }
if ($GitTag -ne 'manual' -and $GitTag -ne "v$Version") { throw "Git tag '$GitTag' does not match release version '$Version'." }

if ($ValidateOnly) {
    [pscustomobject]@{ Version = $Version; Artifact = Get-ArtifactName -ReleaseVersion $Version -ReleaseRid $Rid; GitTag = $GitTag }
    return
}

if (-not (Test-Path -LiteralPath $PublishDirectory -PathType Container)) { throw "Publish directory does not exist: $PublishDirectory" }
if (Test-Path -LiteralPath $OutputDirectory) { throw "Output directory must be fresh and must not already exist: $OutputDirectory" }

$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$approvedRoot = Join-Path $OutputDirectory 'approved'
[System.IO.Directory]::CreateDirectory($approvedRoot) | Out-Null

$executablePath = Join-Path $publishRoot 'AIHelper.exe'
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) { throw 'Publish output must contain AIHelper.exe for embedded-version verification.' }

$fileVersionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($executablePath)
$expectedFileVersion = "$Version.0"
if ($fileVersionInfo.FileVersion -ne $expectedFileVersion) { throw "Embedded file version '$($fileVersionInfo.FileVersion)' does not equal '$expectedFileVersion'." }
if ($fileVersionInfo.ProductVersion -ne $Version) { throw "Embedded product/informational version '$($fileVersionInfo.ProductVersion)' does not equal '$Version'." }
$informationalVersion = $fileVersionInfo.ProductVersion

# Single-file publish intentionally bundles AIHelper.dll into AIHelper.exe. When a DLL is present (for example, a framework-dependent diagnostic publish), verify its attribute too.
$assemblyPath = Join-Path $publishRoot 'AIHelper.dll'
if (Test-Path -LiteralPath $assemblyPath -PathType Leaf) {
    $assembly = [System.Reflection.Assembly]::LoadFrom($assemblyPath)
    $informationalAttributes = $assembly.GetCustomAttributes([System.Reflection.AssemblyInformationalVersionAttribute], $false)
    if ($informationalAttributes.Count -ne 1 -or $informationalAttributes[0].InformationalVersion -ne $Version) {
        throw 'Published AIHelper.dll informational version does not equal the release version.'
    }
}

Get-ChildItem -LiteralPath $publishRoot -File -Recurse -Force | ForEach-Object {
    $relativePath = [System.IO.Path]::GetRelativePath($publishRoot, $_.FullName)
    if (Test-ForbiddenReleaseFile -RelativePath $relativePath) { throw "Forbidden release content detected: $relativePath" }
    if ($_.Extension.Equals('.pdb', [System.StringComparison]::OrdinalIgnoreCase)) { return }
    $destination = Join-Path $approvedRoot $relativePath
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($destination)) | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
}

$artifactName = Get-ArtifactName -ReleaseVersion $Version -ReleaseRid $Rid
$artifactPath = Join-Path $OutputDirectory $artifactName
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($approvedRoot, $artifactPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$zip = [System.IO.Compression.ZipFile]::OpenRead($artifactPath)
try {
    foreach ($entry in $zip.Entries) {
        if ([string]::IsNullOrEmpty($entry.Name)) { continue }
        if (Test-ForbiddenReleaseFile -RelativePath $entry.FullName -or $entry.Name.EndsWith('.pdb', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Forbidden ZIP entry detected: $($entry.FullName)"
        }
    }
}
finally {
    $zip.Dispose()
}

$sha256 = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
$sha256SumsPath = Join-Path $OutputDirectory 'SHA256SUMS.txt'
[System.IO.File]::WriteAllText($sha256SumsPath, "$sha256  $artifactName`n", [System.Text.UTF8Encoding]::new($false))
$sumLine = [System.IO.File]::ReadAllText($sha256SumsPath).Trim()
if ($sumLine -notmatch '^(?<hash>[0-9a-f]{64})  (?<artifact>[^\r\n]+)$' -or $Matches.hash -ne $sha256 -or $Matches.artifact -ne $artifactName) {
    throw 'SHA256SUMS.txt does not match the generated artifact.'
}
if ((Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sha256) { throw 'Artifact hash changed during release packaging.' }

$sdkVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sdkVersion)) { throw 'Unable to determine the actual .NET SDK version.' }
$provenance = [ordered]@{
    version = $Version
    gitCommit = $GitCommit
    gitTag = $GitTag
    buildDateUtc = [DateTime]::UtcNow.ToString('O')
    rid = $Rid
    targetFramework = $TargetFramework
    dotnetSdk = $sdkVersion
    artifact = $artifactName
    sha256 = $sha256
}
$provenancePath = Join-Path $OutputDirectory 'release-manifest.json'
[System.IO.File]::WriteAllText($provenancePath, ($provenance | ConvertTo-Json -Depth 3), [System.Text.UTF8Encoding]::new($false))
$readProvenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
if ($readProvenance.version -ne $Version -or $readProvenance.artifact -ne $artifactName -or $readProvenance.sha256 -ne $sha256 -or $readProvenance.rid -ne $Rid -or $readProvenance.gitCommit -ne $GitCommit -or $readProvenance.gitTag -ne $GitTag -or $readProvenance.dotnetSdk -ne $sdkVersion) {
    throw 'release-manifest.json consistency validation failed.'
}

[pscustomobject]@{
    Version = $Version
    Artifact = $artifactPath
    Sha256Sums = $sha256SumsPath
    Provenance = $provenancePath
    Sha256 = $sha256
    EmbeddedVersion = $informationalVersion
    DotnetSdk = $sdkVersion
}
