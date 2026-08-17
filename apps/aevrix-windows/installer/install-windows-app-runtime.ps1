[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RuntimeRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -ne 'Windows_NT') {
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

# Do not depend on Microsoft.PowerShell.Security module autoload. NSIS launches the helper from
# a minimal native PowerShell 5.1 host where that module can be discoverable yet fail to load.
# WinVerifyTrust verifies the embedded signature cryptographically using Windows trust policy;
# the signer certificate is then constrained to Microsoft Corporation.
if (-not ('AevrixWinTrust.NativeMethods' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace AevrixWinTrust
{
    public static class NativeMethods
    {
        private const uint WTD_UI_NONE = 2;
        private const uint WTD_REVOKE_NONE = 0;
        private const uint WTD_CHOICE_FILE = 1;
        private const uint WTD_STATEACTION_IGNORE = 0;
        private const uint WTD_REVOCATION_CHECK_NONE = 0x00000010;
        private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint WinVerifyTrust(
            IntPtr hwnd,
            [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID,
            IntPtr pWVTData);

        public static uint VerifyFile(string filePath)
        {
            IntPtr pathPtr = IntPtr.Zero;
            IntPtr fileInfoPtr = IntPtr.Zero;
            IntPtr trustDataPtr = IntPtr.Zero;
            try
            {
                pathPtr = Marshal.StringToCoTaskMemUni(filePath);
                WINTRUST_FILE_INFO fileInfo = new WINTRUST_FILE_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)),
                    pcwszFilePath = pathPtr,
                    hFile = IntPtr.Zero,
                    pgKnownSubject = IntPtr.Zero
                };
                fileInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
                Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

                WINTRUST_DATA trustData = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                    pPolicyCallbackData = IntPtr.Zero,
                    pSIPClientData = IntPtr.Zero,
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_NONE,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pFile = fileInfoPtr,
                    dwStateAction = WTD_STATEACTION_IGNORE,
                    hWVTStateData = IntPtr.Zero,
                    pwszURLReference = IntPtr.Zero,
                    dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL,
                    dwUIContext = 0
                };
                trustDataPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WINTRUST_DATA)));
                Marshal.StructureToPtr(trustData, trustDataPtr, false);

                Guid action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
                return WinVerifyTrust(IntPtr.Zero, action, trustDataPtr);
            }
            finally
            {
                if (trustDataPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(trustDataPtr);
                if (fileInfoPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(fileInfoPtr);
                if (pathPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(pathPtr);
            }
        }
    }
}
'@
}

function Get-Sha256Hex {
    param([Parameter(Mandatory)] [string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $bytes = $sha256.ComputeHash($stream)
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    return ([System.BitConverter]::ToString($bytes)).Replace('-', '').ToLowerInvariant()
}

function Assert-TrustedMicrosoftSignature {
    param([Parameter(Mandatory)] [string]$Path)

    $trustResult = [AevrixWinTrust.NativeMethods]::VerifyFile($Path)
    if ($trustResult -ne 0) {
        throw ('Windows trust verification failed for {0}. WinVerifyTrust=0x{1:X8}.' -f $Path, $trustResult)
    }

    try {
        $baseCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate]::CreateFromSignedFile($Path)
        $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($baseCertificate)
    }
    catch {
        throw "Unable to read embedded signer certificate for $Path. $($_.Exception.Message)"
    }

    try {
        $subject = [string]$certificate.Subject
        if ($subject -notmatch '(^|,\s*)CN=Microsoft Corporation(,|$)') {
            throw "Unexpected signer for $Path. Subject=$subject"
        }
    }
    finally {
        $certificate.Dispose()
    }
}

function Get-MsixIdentity {
    param([Parameter(Mandatory)] [string]$Path)

    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $manifestEntry = $archive.GetEntry('AppxManifest.xml')
        if ($null -eq $manifestEntry) {
            throw "MSIX does not contain AppxManifest.xml: $Path"
        }
        $stream = $manifestEntry.Open()
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

foreach ($packageEntry in $expected.GetEnumerator()) {
    $path = Join-Path $RuntimeRoot $packageEntry.Key
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Windows App Runtime package is missing: $path"
    }

    $hash = Get-Sha256Hex -Path $path
    if ($hash -ne $packageEntry.Value) {
        throw "Windows App Runtime hash mismatch for $($packageEntry.Key). Expected $($packageEntry.Value), got $hash."
    }

    Assert-TrustedMicrosoftSignature -Path $path

    $identity = Get-MsixIdentity -Path $path
    if ($identity.Architecture -and $identity.Architecture -notin @('x64', 'neutral')) {
        throw "Unexpected Windows App Runtime architecture for $($packageEntry.Key): $($identity.Architecture)"
    }

    $installed = @(Get-AppxPackage -Name $identity.Name -ErrorAction SilentlyContinue | Where-Object {
        [string]$_.Architecture -in @('X64', 'Neutral')
    } | Sort-Object { [version]$_.Version } -Descending)

    if ($installed.Count -gt 0 -and [version]$installed[0].Version -ge $identity.Version) {
        Write-Host "Windows App Runtime prerequisite already satisfied: $($identity.Name) $($installed[0].Version) >= $($identity.Version)."
        continue
    }

    Write-Host "Installing Windows App Runtime prerequisite: $($identity.Name) $($identity.Version) from $($packageEntry.Key)."
    Add-AppxPackage -Path $path -ForceApplicationShutdown -ErrorAction Stop

    $verified = @(Get-AppxPackage -Name $identity.Name -ErrorAction SilentlyContinue | Where-Object {
        [string]$_.Architecture -in @('X64', 'Neutral') -and [version]$_.Version -ge $identity.Version
    })
    if ($verified.Count -eq 0) {
        throw "Windows App Runtime prerequisite was not observable after deployment: $($identity.Name) >= $($identity.Version)."
    }
}

Write-Host 'PASS: exact SHA-256-pinned, WinVerifyTrust-validated Microsoft Windows App Runtime 2.3.1 prerequisites are satisfied for the current user.'
