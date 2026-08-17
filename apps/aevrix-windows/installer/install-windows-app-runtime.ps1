[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RuntimeRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows) {
    throw 'Windows App Runtime deployment requires Windows.'
}

$RuntimeRoot = [IO.Path]::GetFullPath($RuntimeRoot)
if (-not (Test-Path -LiteralPath $RuntimeRoot -PathType Container)) {
    throw "Windows App Runtime payload directory does not exist: $RuntimeRoot"
}

$expected = [ordered]@{
    'Microsoft.WindowsAppRuntime.2.msix' = '75e953dbb33850d3591e590ca15a6aa1e320096b62b00deca846d40b2aacab7b'
    'Microsoft.WindowsAppRuntime.Main.2.msix' = 'fbb8cbda76d62f5d51e6110fbea7c0e598ee12b2b975c458a7b38d16df551926'
    'Microsoft.WindowsAppRuntime.Singleton.2.msix' = '345e9732748903d2f9e62f8772864f893de21e939fad527ac75ddfe1426876bb'
    'Microsoft.WindowsAppRuntime.DDLM.2.msix' = '5ac70030698a36d48b31d14f20d623362a104798b2526fbaea59eeb676278f1b'
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-MsixIdentity {
    param([Parameter(Mandatory)] [string]$Path)

    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $archive.GetEntry('AppxManifest.xml')
        if ($null -eq $entry) {
            throw "MSIX does not contain AppxManifest.xml: $Path"
        }
        $stream = $entry.Open()
        try {
            $reader = New-Object System.IO.StreamReader($stream, [Text.Encoding]::UTF8, $true)
            try {
                [xml]$manifest = $reader.ReadToEnd()
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }

    $identity = $manifest.Package.Identity
    if ($null -eq $identity -or [string]::IsNullOrWhiteSpace([string]$identity.Name) -or [string]::IsNullOrWhiteSpace([string]$identity.Version)) {
        throw "MSIX identity is incomplete: $Path"
    }

    [pscustomobject]@{
        Name = [string]$identity.Name
        Version = [version]([string]$identity.Version)
        Architecture = [string]$identity.ProcessorArchitecture
    }
}

foreach ($entry in $expected.GetEnumerator()) {
    $path = Join-Path $RuntimeRoot $entry.Key
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Windows App Runtime package is missing: $path"
    }

    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $entry.Value) {
        throw "Windows App Runtime hash mismatch for $($entry.Key). Expected $($entry.Value), got $hash."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $path
    $subject = if ($signature.SignerCertificate) { [string]$signature.SignerCertificate.Subject } else { '' }
    if ($signature.Status -ne 'Valid' -or $subject -notmatch '(^|,\s*)CN=Microsoft Corporation(,|$)') {
        throw "Windows App Runtime signature is not valid Microsoft code for $($entry.Key). Status=$($signature.Status); signer=$subject"
    }

    $identity = Get-MsixIdentity -Path $path
    if ($identity.Architecture -and $identity.Architecture -notin @('x64', 'neutral')) {
        throw "Unexpected Windows App Runtime architecture for $($entry.Key): $($identity.Architecture)"
    }

    $installed = @(Get-AppxPackage -Name $identity.Name -ErrorAction SilentlyContinue | Where-Object {
        $_.Architecture -in @('X64', 'Neutral')
    } | Sort-Object { [version]$_.Version } -Descending)

    if ($installed.Count -gt 0 -and [version]$installed[0].Version -ge $identity.Version) {
        Write-Host "Windows App Runtime prerequisite already satisfied: $($identity.Name) $($installed[0].Version) >= $($identity.Version)."
        continue
    }

    Write-Host "Installing Windows App Runtime prerequisite: $($identity.Name) $($identity.Version) from $($entry.Key)."
    Add-AppxPackage -Path $path -ForceApplicationShutdown -ErrorAction Stop

    $verified = @(Get-AppxPackage -Name $identity.Name -ErrorAction SilentlyContinue | Where-Object {
        $_.Architecture -in @('X64', 'Neutral') -and [version]$_.Version -ge $identity.Version
    })
    if ($verified.Count -eq 0) {
        throw "Windows App Runtime prerequisite was not observable after deployment: $($identity.Name) >= $($identity.Version)."
    }
}

Write-Host 'PASS: exact Microsoft-signed Windows App Runtime 2.3.1 prerequisites are satisfied for the current user.'
