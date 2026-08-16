[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$OldInstaller,
    [Parameter(Mandatory)] [string]$NewInstaller,
    [Parameter(Mandatory)] [string]$OldVersion,
    [Parameter(Mandatory)] [string]$NewVersion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows) {
    throw 'Installer lifecycle tests require Windows.'
}

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\AEVRIX'
$userDataRoot = Join-Path $env:LOCALAPPDATA 'AEVRIX'
$uninstallReg = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AEVRIX'
$appReg = 'HKCU:\Software\AEVRIX'

function Invoke-Setup {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [string[]]$Arguments = @('/S'),
        [int[]]$AllowedExitCodes = @(0)
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Setup binary not found: $Path"
    }

    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -Wait -PassThru
    if ($AllowedExitCodes -notcontains $process.ExitCode) {
        throw "Setup '$Path' exited with $($process.ExitCode); allowed: $($AllowedExitCodes -join ', ')"
    }
    return $process.ExitCode
}

function Assert-InstalledVersion {
    param([Parameter(Mandatory)] [string]$Expected)

    if (-not (Test-Path -LiteralPath $uninstallReg)) {
        throw 'AEVRIX uninstall registry entry is missing.'
    }
    $actual = (Get-ItemProperty -LiteralPath $uninstallReg -Name DisplayVersion).DisplayVersion
    if ($actual -ne $Expected) {
        throw "Installed version mismatch. Expected '$Expected', got '$actual'."
    }

    foreach ($name in @(
        'AEVRIX.Desktop.exe',
        'AEVRIX.Desktop.dll',
        'AEVRIX.Desktop.deps.json',
        'AEVRIX.Desktop.runtimeconfig.json',
        'AEVRIX.EngineHost.exe',
        'AEVRIX.EngineHost.dll',
        'AEVRIX.EngineHost.deps.json',
        'AEVRIX.EngineHost.runtimeconfig.json',
        'AEVRIX.Core.dll',
        'AEVRIX-Setup.exe',
        'uninstall.exe'
    )) {
        $path = Join-Path $installDir $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Installed payload missing: $path"
        }
    }
}

# Clean any residue from a failed previous test without touching AEVRIX user data.
if (Test-Path -LiteralPath (Join-Path $installDir 'uninstall.exe') -PathType Leaf) {
    Invoke-Setup -Path (Join-Path $installDir 'uninstall.exe') -Arguments @('/S') -AllowedExitCodes @(0)
}
if (Test-Path -LiteralPath $installDir) {
    Remove-Item -LiteralPath $installDir -Recurse -Force
}
Remove-Item -LiteralPath $uninstallReg -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $appReg -Recurse -Force -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Force -Path $userDataRoot | Out-Null
$preservationMarker = Join-Path $userDataRoot 'installer-lifecycle-preserve.txt'
$markerContent = "preserve-$([Guid]::NewGuid().ToString('N'))"
Set-Content -LiteralPath $preservationMarker -Value $markerContent -Encoding utf8NoBOM -NoNewline

Write-Host "[1/5] Clean install $OldVersion"
Invoke-Setup -Path $OldInstaller -Arguments @('/S') -AllowedExitCodes @(0)
Assert-InstalledVersion -Expected $OldVersion

Write-Host '[2/5] Repair exact installed version after controlled file loss'
$repairTarget = Join-Path $installDir 'AEVRIX.Desktop.runtimeconfig.json'
Remove-Item -LiteralPath $repairTarget -Force
if (Test-Path -LiteralPath $repairTarget) {
    throw 'Repair precondition failed: target file was not removed.'
}
$repairSetup = Join-Path $installDir 'AEVRIX-Setup.exe'
Invoke-Setup -Path $repairSetup -Arguments @('/S', '/REPAIR') -AllowedExitCodes @(0)
if (-not (Test-Path -LiteralPath $repairTarget -PathType Leaf)) {
    throw 'Repair failed to restore AEVRIX.Desktop.runtimeconfig.json.'
}
Assert-InstalledVersion -Expected $OldVersion

Write-Host "[3/5] Upgrade $OldVersion -> $NewVersion"
Invoke-Setup -Path $NewInstaller -Arguments @('/S') -AllowedExitCodes @(0)
Assert-InstalledVersion -Expected $NewVersion

Write-Host "[4/5] Downgrade resistance $NewVersion -> $OldVersion"
$downgradeExit = Invoke-Setup -Path $OldInstaller -Arguments @('/S') -AllowedExitCodes @(1638)
if ($downgradeExit -ne 1638) {
    throw "Downgrade was not rejected with Windows Installer-style code 1638; got $downgradeExit."
}
Assert-InstalledVersion -Expected $NewVersion

Write-Host '[5/5] Uninstall and preserve user data'
Invoke-Setup -Path (Join-Path $installDir 'uninstall.exe') -Arguments @('/S') -AllowedExitCodes @(0)
Start-Sleep -Milliseconds 500
if (Test-Path -LiteralPath $uninstallReg) {
    throw 'Uninstall registry entry survived uninstall.'
}
if (Test-Path -LiteralPath $appReg) {
    throw 'AEVRIX application registry key survived uninstall.'
}
if (Test-Path -LiteralPath (Join-Path $installDir 'AEVRIX.Desktop.exe')) {
    throw 'AEVRIX Desktop binary survived uninstall.'
}
if (-not (Test-Path -LiteralPath $preservationMarker -PathType Leaf)) {
    throw 'AEVRIX user-data preservation marker was removed by uninstall.'
}
$actualMarker = Get-Content -LiteralPath $preservationMarker -Raw
if ($actualMarker -ne $markerContent) {
    throw 'AEVRIX user-data preservation marker changed during installer lifecycle.'
}

Remove-Item -LiteralPath $preservationMarker -Force
Write-Host 'PASS: install -> repair -> upgrade -> downgrade-block -> uninstall lifecycle completed; user data preserved.'
