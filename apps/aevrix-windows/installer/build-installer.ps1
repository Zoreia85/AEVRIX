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
$runtimeHelperPath = Join-Path $installerRoot 'install-windows-app-runtime.ps1'

$windowsAppSdkRuntimeVersion = '2.3.1'
$windowsAppSdkRuntimeNupkgSha256 = 'f15c6c682a81a019e13beaee512de9fb83ffd5a1f3e83b99209b6860a7aebba2'
$windowsAppSdkRuntimeHashes = [ordered]@{
    'Microsoft.WindowsAppRuntime.2.msix' = '75e953dbb33850d3591e590ca15a6aa1e320096b62b00deca846d40b2aacab7b'
    'Microsoft.WindowsAppRuntime.Main.2.msix' = 'fbb8cbda76d62f5d51e6110fbea7c0e598ee12b2b975c458a7b38d16df551926'
    'Microsoft.WindowsAppRuntime.Singleton.2.msix' = '345e9732748903d2f9e62f8772864f893de21e939fad527ac75ddfe1426876bb'
    'Microsoft.WindowsAppRuntime.DDLM.2.msix' = '5ac70030698a36d48b31d14f20d623362a104798b2526fbaea59eeb676278f1b'
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $windowsRoot 'artifacts\installer'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$stagingRoot = Join-Path $windowsRoot 'artifacts\installer-staging'
$desktopPublish = Join-Path $stagingRoot 'desktop'
$enginePublish = Join-Path $stagingRoot 'engine'

foreach ($path in @($desktopProject, $engineProject, $scriptPath, $runtimeHelperPath)) {
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
        [string[]]$AdditionalProperties = @()
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
    ) + $AdditionalProperties

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Project with exit code $LASTEXITCODE"
    }
}

Write-Host "Publishing Desktop with self-contained .NET and framework-dependent Windows App SDK ($RuntimeIdentifier)..."
Invoke-DotnetPublish -Project $desktopProject -Destination $desktopPublish -AdditionalProperties @(
    '-p:Platform=x64',
    '-p:WindowsAppSDKSelfContained=false'
)

Write-Host "Publishing EngineHost self-contained ($RuntimeIdentifier)..."
Invoke-DotnetPublish -Project $engineProject -Destination $enginePublish

# The Desktop remains .NET self-contained but must not privately carry WinUI/Windows App SDK runtime.
# Private Microsoft.UI.Xaml.dll 3.2.3.0 reproducibly crashed at startup with 0xC000027B on Windows Server 2025.
$privateXaml = Join-Path $desktopPublish 'Microsoft.UI.Xaml.dll'
if (Test-Path -LiteralPath $privateXaml -PathType Leaf) {
    throw 'Installer payload unexpectedly contains private Microsoft.UI.Xaml.dll. Windows App SDK must remain framework-dependent.'
}
if (-not (Test-Path -LiteralPath (Join-Path $desktopPublish 'coreclr.dll') -PathType Leaf)) {
    throw 'Installer payload lost the self-contained .NET runtime (coreclr.dll missing).'
}

# The exact Windows App SDK runtime package is already restored by the Desktop project. Reuse that
# immutable, version-bound source rather than a mutable redirect/download endpoint.
$nugetPackagesRoot = if ($env:NUGET_PACKAGES) {
    [IO.Path]::GetFullPath($env:NUGET_PACKAGES)
} else {
    Join-Path $env:USERPROFILE '.nuget\packages'
}
$runtimePackageRoot = Join-Path $nugetPackagesRoot "microsoft.windowsappsdk.runtime\$windowsAppSdkRuntimeVersion"
$runtimeNupkg = Join-Path $runtimePackageRoot "microsoft.windowsappsdk.runtime.$windowsAppSdkRuntimeVersion.nupkg"
$runtimeMsixRoot = Join-Path $runtimePackageRoot 'tools\MSIX\win10-x64'

if (-not (Test-Path -LiteralPath $runtimeNupkg -PathType Leaf)) {
    throw "Exact Windows App SDK runtime nupkg is missing after restore: $runtimeNupkg"
}
$nupkgHash = (Get-FileHash -LiteralPath $runtimeNupkg -Algorithm SHA256).Hash.ToLowerInvariant()
if ($nupkgHash -ne $windowsAppSdkRuntimeNupkgSha256) {
    throw "Windows App SDK runtime nupkg hash mismatch. Expected $windowsAppSdkRuntimeNupkgSha256, got $nupkgHash."
}
if (-not (Test-Path -LiteralPath $runtimeMsixRoot -PathType Container)) {
    throw "Exact Windows App SDK x64 MSIX directory is missing: $runtimeMsixRoot"
}

$runtimePrerequisites = foreach ($entry in $windowsAppSdkRuntimeHashes.GetEnumerator()) {
    $path = Join-Path $runtimeMsixRoot $entry.Key
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Windows App SDK runtime prerequisite is missing: $path"
    }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $entry.Value) {
        throw "Windows App SDK runtime prerequisite hash mismatch for $($entry.Key). Expected $($entry.Value), got $hash."
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    $subject = if ($signature.SignerCertificate) { [string]$signature.SignerCertificate.Subject } else { '' }
    if ($signature.Status -ne 'Valid' -or $subject -notmatch '(^|,\s*)CN=Microsoft Corporation(,|$)') {
        throw "Windows App SDK runtime prerequisite is not valid Microsoft-signed code: $($entry.Key); status=$($signature.Status); signer=$subject"
    }

    [pscustomobject]@{
        file = $entry.Key
        bytes = (Get-Item -LiteralPath $path).Length
        sha256 = $hash
        signatureStatus = [string]$signature.Status
        signerSubject = $subject
    }
}

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
    'AEVRIX.Core.dll',
    'coreclr.dll'
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
    "/DWASDK_RUNTIME_DIR=$runtimeMsixRoot",
    "/DWASDK_RUNTIME_HELPER=$runtimeHelperPath",
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
    schemaVersion = 2
    product = 'AEVRIX'
    productVersion = $ProductVersion
    fileVersion = $FileVersion
    runtimeIdentifier = $RuntimeIdentifier
    dotnetSelfContained = $true
    windowsAppSdkSelfContained = $false
    windowsAppSdkRuntime = [pscustomobject]@{
        version = $windowsAppSdkRuntimeVersion
        sourcePackage = "Microsoft.WindowsAppSDK.Runtime/$windowsAppSdkRuntimeVersion"
        nupkgSha256 = $nupkgHash
        ownership = 'Microsoft shared per-user prerequisite; preserved on AEVRIX uninstall'
        packages = @($runtimePrerequisites)
    }
    generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    installer = [pscustomobject]@{
        file = [IO.Path]::GetFileName($outFile)
        bytes = (Get-Item -LiteralPath $outFile).Length
        sha256 = $hash.Hash.ToLowerInvariant()
        signed = $false
    }
    files = @($payloadManifest)
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

Write-Host "Installer: $outFile"
Write-Host "SHA-256: $($hash.Hash.ToLowerInvariant())"
Write-Host "Payload manifest: $manifestPath"
Write-Host "Windows App SDK runtime: $windowsAppSdkRuntimeVersion / nupkg SHA-256 $nupkgHash"
Write-Warning 'Installer signing is intentionally NOT marked complete. Authenticode remains a separate release gate.'
