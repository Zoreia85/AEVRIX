[CmdletBinding()]
param(
    [string]$ProductVersion = '0.0.1',
    [string]$FileVersion = '0.0.1.0',
    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64',
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [string]$MakensisPath = $env:MAKENSIS_EXE
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows) {
    throw 'AEVRIX Windows installer builds require Windows.'
}

$installerRoot = Split-Path -Parent $PSCommandPath
$windowsRoot = Split-Path -Parent $installerRoot
$desktopProject = Join-Path $windowsRoot 'src\AEVRIX.Desktop\AEVRIX.Desktop.csproj'
$engineProject = Join-Path $windowsRoot 'src\AEVRIX.EngineHost\AEVRIX.EngineHost.csproj'
$scriptPath = Join-Path $installerRoot 'aevrix.nsi'

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $windowsRoot 'artifacts\installer'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$stagingRoot = Join-Path $windowsRoot 'artifacts\installer-staging'
$desktopPublish = Join-Path $stagingRoot 'desktop'
$enginePublish = Join-Path $stagingRoot 'engine'

foreach ($path in @($desktopProject, $engineProject, $scriptPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required build input not found: $path"
    }
}

if (Test-Path -LiteralPath $stagingRoot) {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $desktopPublish, $enginePublish, $OutputDirectory | Out-Null

function Invoke-DotnetPublish {
    param(
        [Parameter(Mandatory)] [string]$Project,
        [Parameter(Mandatory)] [string]$Destination,
        [switch]$WindowsAppSdkSelfContained
    )

    $arguments = @(
        'publish', $Project,
        '--configuration', $Configuration,
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '--output', $Destination,
        '-p:ContinuousIntegrationBuild=true',
        '-p:DebugType=none',
        '-p:DebugSymbols=false'
    )
    if ($WindowsAppSdkSelfContained) {
        $arguments += '-p:WindowsAppSDKSelfContained=true'
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Project with exit code $LASTEXITCODE"
    }
}

Write-Host "Publishing Desktop self-contained ($RuntimeIdentifier)..."
Invoke-DotnetPublish -Project $desktopProject -Destination $desktopPublish -WindowsAppSdkSelfContained

Write-Host "Publishing EngineHost self-contained ($RuntimeIdentifier)..."
Invoke-DotnetPublish -Project $engineProject -Destination $enginePublish

# DesktopEngineSession requires EngineHost beside AEVRIX.Desktop.exe. Copy only EngineHost-owned
# launcher/manifest files; common Core/runtime assemblies remain those from the Desktop publish.
$engineArtifacts = @(
    'AEVRIX.EngineHost.exe',
    'AEVRIX.EngineHost.dll',
    'AEVRIX.EngineHost.deps.json',
    'AEVRIX.EngineHost.runtimeconfig.json'
)
foreach ($name in $engineArtifacts) {
    $source = Join-Path $enginePublish $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "EngineHost publish is incomplete: missing $name"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $desktopPublish $name) -Force
}

$requiredPayload = @(
    'AEVRIX.Desktop.exe',
    'AEVRIX.Desktop.dll',
    'AEVRIX.Desktop.deps.json',
    'AEVRIX.Desktop.runtimeconfig.json',
    'AEVRIX.EngineHost.exe',
    'AEVRIX.EngineHost.dll',
    'AEVRIX.EngineHost.deps.json',
    'AEVRIX.EngineHost.runtimeconfig.json',
    'AEVRIX.Core.dll'
)
foreach ($name in $requiredPayload) {
    if (-not (Test-Path -LiteralPath (Join-Path $desktopPublish $name) -PathType Leaf)) {
        throw "Installer payload is incomplete: missing $name"
    }
}

# Reject debug artifacts and secrets before packaging.
$forbiddenPayload = Get-ChildItem -LiteralPath $desktopPublish -Recurse -File | Where-Object {
    $_.Extension -ieq '.pdb' -or
    $_.Name -match '(?i)(\.env($|\.)|id_rsa|id_ed25519|\.pem$|\.pfx$|\.key$|credentials|secrets?)'
}
if ($forbiddenPayload) {
    $names = ($forbiddenPayload.FullName -join [Environment]::NewLine)
    throw "Forbidden debug/secret-like installer payload detected:`n$names"
}

if (-not $MakensisPath) {
    $command = Get-Command 'makensis.exe' -ErrorAction SilentlyContinue
    if ($command) {
        $MakensisPath = $command.Source
    }
}
if (-not $MakensisPath -or -not (Test-Path -LiteralPath $MakensisPath -PathType Leaf)) {
    throw 'makensis.exe was not found. Set MAKENSIS_EXE or pass -MakensisPath.'
}

$outFile = Join-Path $OutputDirectory "AEVRIX-$ProductVersion-$RuntimeIdentifier-Setup.exe"
if (Test-Path -LiteralPath $outFile) {
    Remove-Item -LiteralPath $outFile -Force
}

$nsisArgs = @(
    '/V4',
    "/DPRODUCT_VERSION=$ProductVersion",
    "/DFILE_VERSION=$FileVersion",
    "/DPUBLISH_DIR=$desktopPublish",
    "/DOUTFILE=$outFile",
    $scriptPath
)

Write-Host "Compiling installer with $MakensisPath..."
& $MakensisPath @nsisArgs
if ($LASTEXITCODE -ne 0) {
    throw "makensis failed with exit code $LASTEXITCODE"
}
if (-not (Test-Path -LiteralPath $outFile -PathType Leaf)) {
    throw "Installer output was not produced: $outFile"
}

$hash = Get-FileHash -LiteralPath $outFile -Algorithm SHA256
$hashPath = "$outFile.sha256"
"$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($outFile))" | Set-Content -LiteralPath $hashPath -Encoding ascii -NoNewline

$payloadManifest = Get-ChildItem -LiteralPath $desktopPublish -Recurse -File |
    Sort-Object FullName |
    ForEach-Object {
        [pscustomobject]@{
            path = [IO.Path]::GetRelativePath($desktopPublish, $_.FullName).Replace('\\', '/')
            bytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
$manifestPath = Join-Path $OutputDirectory "AEVRIX-$ProductVersion-$RuntimeIdentifier-payload.json"
[pscustomobject]@{
    schemaVersion = 1
    product = 'AEVRIX'
    productVersion = $ProductVersion
    fileVersion = $FileVersion
    runtimeIdentifier = $RuntimeIdentifier
    selfContained = $true
    windowsAppSdkSelfContained = $true
    generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    installer = [pscustomobject]@{
        file = [IO.Path]::GetFileName($outFile)
        bytes = (Get-Item -LiteralPath $outFile).Length
        sha256 = $hash.Hash.ToLowerInvariant()
        signed = $false
    }
    files = @($payloadManifest)
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

Write-Host "Installer: $outFile"
Write-Host "SHA-256: $($hash.Hash.ToLowerInvariant())"
Write-Host "Payload manifest: $manifestPath"
Write-Warning 'Installer signing is intentionally NOT marked complete. Authenticode remains a separate release gate.'
