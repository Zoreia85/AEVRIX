[CmdletBinding()]
param(
    [string]$ProductVersion = '0.0.1',
    [string]$FileVersion = '0.0.1.0',
    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64',
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [string]$MakensisPath = $env:MAKENSIS_EXE,
    [string]$SignToolPath = $env:SIGNTOOL_EXE,
    [string]$SigningCertificateThumbprint = $env:AEVRIX_SIGNING_CERT_THUMBPRINT,
    [string]$TimestampUrl = $env:AEVRIX_SIGNING_TIMESTAMP_URL,
    [ValidateSet('CurrentUser', 'LocalMachine')]
    [string]$SigningStoreLocation = 'CurrentUser',
    [switch]$RequireTrustedSignature
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

function Resolve-SignTool {
    if ($script:SignToolPath -and (Test-Path -LiteralPath $script:SignToolPath -PathType Leaf)) {
        return $script:SignToolPath
    }

    $command = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    return $null
}

function Assert-ValidAuthenticodeSignature {
    param([Parameter(Mandatory)] [string]$Path)

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode verification failed for $Path with status $($signature.Status)."
    }
    if (-not $signature.SignerCertificate) {
        throw "Authenticode verification did not return a signer certificate for $Path."
    }
    return $signature
}

function Invoke-AevrixAuthenticodeSign {
    param([Parameter(Mandatory)] [string[]]$Paths)

    if (-not $SigningCertificateThumbprint) {
        if ($RequireTrustedSignature) {
            throw 'Trusted release signing is required but AEVRIX_SIGNING_CERT_THUMBPRINT was not provided.'
        }
        return $false
    }
    if (-not $TimestampUrl) {
        throw 'A timestamp URL is required whenever Authenticode signing is enabled. Set AEVRIX_SIGNING_TIMESTAMP_URL.'
    }

    $tool = Resolve-SignTool
    if (-not $tool) {
        throw 'signtool.exe was not found. Set SIGNTOOL_EXE or install the Windows SDK signing tools.'
    }

    foreach ($path in $Paths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Signing input not found: $path"
        }

        $arguments = @('sign')
        if ($SigningStoreLocation -eq 'LocalMachine') {
            $arguments += '/sm'
        }
        $arguments += @(
            '/sha1', $SigningCertificateThumbprint,
            '/fd', 'SHA256',
            '/tr', $TimestampUrl,
            '/td', 'SHA256',
            '/v',
            $path
        )

        & $tool @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "signtool failed for $path with exit code $LASTEXITCODE"
        }
        $null = Assert-ValidAuthenticodeSignature -Path $path
    }

    return $true
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

# Sign AEVRIX-owned PE payload before packaging. Runtime/vendor binaries retain their upstream signatures.
$ownedPayloadToSign = @(
    (Join-Path $desktopPublish 'AEVRIX.Desktop.exe'),
    (Join-Path $desktopPublish 'AEVRIX.Desktop.dll'),
    (Join-Path $desktopPublish 'AEVRIX.EngineHost.exe'),
    (Join-Path $desktopPublish 'AEVRIX.EngineHost.dll'),
    (Join-Path $desktopPublish 'AEVRIX.Core.dll')
)
$payloadSigned = Invoke-AevrixAuthenticodeSign -Paths $ownedPayloadToSign

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

$installerSigned = Invoke-AevrixAuthenticodeSign -Paths @($outFile)
if ($RequireTrustedSignature -and (-not $payloadSigned -or -not $installerSigned)) {
    throw 'Trusted release signing was required but one or more AEVRIX artifacts remained unsigned.'
}

$installerSignature = if ($installerSigned) {
    Assert-ValidAuthenticodeSignature -Path $outFile
} else {
    Get-AuthenticodeSignature -LiteralPath $outFile
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
    selfContained = $true
    windowsAppSdkSelfContained = $true
    generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    installer = [pscustomobject]@{
        file = [IO.Path]::GetFileName($outFile)
        bytes = (Get-Item -LiteralPath $outFile).Length
        sha256 = $hash.Hash.ToLowerInvariant()
        signed = [bool]$installerSigned
        signatureStatus = $installerSignature.Status.ToString()
        signerSubject = if ($installerSignature.SignerCertificate) { $installerSignature.SignerCertificate.Subject } else { $null }
        signerThumbprint = if ($installerSignature.SignerCertificate) { $installerSignature.SignerCertificate.Thumbprint } else { $null }
        timestamped = [bool]$installerSignature.TimeStamperCertificate
    }
    files = @($payloadManifest)
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

Write-Host "Installer: $outFile"
Write-Host "SHA-256: $($hash.Hash.ToLowerInvariant())"
Write-Host "Payload manifest: $manifestPath"
if ($installerSigned) {
    Write-Host "Authenticode: VALID — $($installerSignature.SignerCertificate.Subject)"
} else {
    Write-Warning 'Installer remains unsigned because no trusted signing identity was configured. This artifact is TEST-ONLY and must not receive public release credit.'
}
