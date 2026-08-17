[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$OldInstaller,
    [Parameter(Mandatory)] [string]$NewInstaller,
    [Parameter(Mandatory)] [string]$OldVersion,
    [Parameter(Mandatory)] [string]$NewVersion,
    [Parameter(Mandatory)] [string]$EvidencePath
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
$startMenuDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\AEVRIX'
$phaseExitCodes = [ordered]@{}

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
        throw "Setup '$([IO.Path]::GetFileName($Path))' exited with $($process.ExitCode); allowed: $($AllowedExitCodes -join ', ')"
    }
    return [int]$process.ExitCode
}

function Get-RegistrySnapshot {
    param([Parameter(Mandatory)] [string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $item = Get-ItemProperty -LiteralPath $Path
    $values = [ordered]@{}
    foreach ($property in $item.PSObject.Properties | Where-Object { $_.Name -notmatch '^PS' }) {
        $values[$property.Name] = [string]$property.Value
    }
    return [pscustomobject]$values
}

function Get-ProductInventory {
    $files = @()
    if (Test-Path -LiteralPath $installDir -PathType Container) {
        $files = @(Get-ChildItem -LiteralPath $installDir -Recurse -File | Sort-Object FullName | ForEach-Object {
            [pscustomobject]@{
                path = [IO.Path]::GetRelativePath($installDir, $_.FullName).Replace('\\', '/')
                bytes = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        })
    }

    $shortcuts = @()
    if (Test-Path -LiteralPath $startMenuDir -PathType Container) {
        $shortcuts = @(Get-ChildItem -LiteralPath $startMenuDir -File | Sort-Object Name | ForEach-Object { $_.Name })
    }

    $services = @(Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'AEVRIX*' } | Sort-Object Name | ForEach-Object { $_.Name })
    $scheduledTasks = @()
    if (Get-Command Get-ScheduledTask -ErrorAction SilentlyContinue) {
        $scheduledTasks = @(Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like 'AEVRIX*' -or $_.TaskPath -like '*AEVRIX*' } | Sort-Object TaskPath, TaskName | ForEach-Object { "$($_.TaskPath)$($_.TaskName)" })
    }

    return [pscustomobject]@{
        installDirExists = (Test-Path -LiteralPath $installDir -PathType Container)
        files = $files
        uninstallRegistry = Get-RegistrySnapshot -Path $uninstallReg
        applicationRegistry = Get-RegistrySnapshot -Path $appReg
        startMenuShortcuts = $shortcuts
        serviceNames = $services
        scheduledTaskNames = $scheduledTasks
    }
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
            throw "Installed payload missing: $name"
        }
    }
}

# Clean product-owned residue from a previous test. User data is intentionally untouched.
if (Test-Path -LiteralPath (Join-Path $installDir 'uninstall.exe') -PathType Leaf) {
    Invoke-Setup -Path (Join-Path $installDir 'uninstall.exe') -Arguments @('/S') -AllowedExitCodes @(0) | Out-Null
}
if (Test-Path -LiteralPath $installDir) {
    Remove-Item -LiteralPath $installDir -Recurse -Force
}
Remove-Item -LiteralPath $uninstallReg -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $appReg -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $startMenuDir) {
    Remove-Item -LiteralPath $startMenuDir -Recurse -Force -ErrorAction SilentlyContinue
}

$before = Get-ProductInventory

New-Item -ItemType Directory -Force -Path $userDataRoot | Out-Null
$preservationMarker = Join-Path $userDataRoot 'installer-lifecycle-preserve.txt'
$markerContent = "preserve-$([Guid]::NewGuid().ToString('N'))"
Set-Content -LiteralPath $preservationMarker -Value $markerContent -Encoding utf8NoBOM -NoNewline

Write-Host "[1/6] Interrupt clean install $OldVersion at deterministic post-prerequisite/pre-payload AVA hold"
$interrupted = Start-Process -FilePath $OldInstaller -ArgumentList @('/S', '/AVAINTERRUPTHOLD=15000') -PassThru
$interruptionObserved = $false
try {
    # Runtime prerequisite deployment can legitimately precede AEVRIX-owned surface creation.
    # Wait up to 90 seconds for the exact test-only hold to create $installDir, then terminate.
    for ($i = 0; $i -lt 9000; $i++) {
        $interrupted.Refresh()
        if ($interrupted.HasExited) { break }
        if (Test-Path -LiteralPath $installDir -PathType Container) {
            $interruptionObserved = $true
            Stop-Process -Id $interrupted.Id -Force
            break
        }
        Start-Sleep -Milliseconds 10
    }
    if (-not $interruptionObserved) {
        $interrupted.Refresh()
        $exitDetail = if ($interrupted.HasExited) { "exitCode=$($interrupted.ExitCode)" } else { 'processStillRunning=true' }
        $runtimePackages = @(Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'WindowsAppRuntime' } | ForEach-Object { "$($_.Name)@$($_.Version)/$($_.Architecture)" })
        throw "Installer did not expose the deterministic partial-install surface within the AVA window; interruption recovery is not proven. $exitDetail; runtimePackages=$($runtimePackages -join ',')"
    }
    $interrupted.WaitForExit()
    $phaseExitCodes.interruptedInstall = [int]$interrupted.ExitCode
}
finally {
    if (-not $interrupted.HasExited) {
        Stop-Process -Id $interrupted.Id -Force -ErrorAction SilentlyContinue
        $interrupted.WaitForExit()
    }
}
$afterInterruption = Get-ProductInventory
if (-not $afterInterruption.installDirExists) {
    throw 'Controlled interruption did not leave any product-owned partial install surface to recover.'
}

Write-Host "[2/6] Recover interrupted install with exact same installer $OldVersion"
$phaseExitCodes.recoveryInstall = Invoke-Setup -Path $OldInstaller -Arguments @('/S') -AllowedExitCodes @(0)
Assert-InstalledVersion -Expected $OldVersion
$afterRecovery = Get-ProductInventory

Write-Host '[3/6] Repair exact installed version after controlled file loss'
$repairTarget = Join-Path $installDir 'AEVRIX.Desktop.runtimeconfig.json'
Remove-Item -LiteralPath $repairTarget -Force
if (Test-Path -LiteralPath $repairTarget) {
    throw 'Repair precondition failed: target file was not removed.'
}
$repairSetup = Join-Path $installDir 'AEVRIX-Setup.exe'
$phaseExitCodes.repair = Invoke-Setup -Path $repairSetup -Arguments @('/S', '/REPAIR') -AllowedExitCodes @(0)
if (-not (Test-Path -LiteralPath $repairTarget -PathType Leaf)) {
    throw 'Repair failed to restore AEVRIX.Desktop.runtimeconfig.json.'
}
Assert-InstalledVersion -Expected $OldVersion
$afterRepair = Get-ProductInventory

Write-Host "[4/6] Upgrade $OldVersion -> $NewVersion"
$phaseExitCodes.upgrade = Invoke-Setup -Path $NewInstaller -Arguments @('/S') -AllowedExitCodes @(0)
Assert-InstalledVersion -Expected $NewVersion
$afterUpgrade = Get-ProductInventory

Write-Host "[5/6] Downgrade resistance $NewVersion -> $OldVersion"
$downgradeExit = Invoke-Setup -Path $OldInstaller -Arguments @('/S') -AllowedExitCodes @(1638)
$phaseExitCodes.downgradeAttempt = $downgradeExit
if ($downgradeExit -ne 1638) {
    throw "Downgrade was not rejected with Windows Installer-style code 1638; got $downgradeExit."
}
Assert-InstalledVersion -Expected $NewVersion

Write-Host '[6/6] Uninstall and preserve user data'
$phaseExitCodes.uninstall = Invoke-Setup -Path (Join-Path $installDir 'uninstall.exe') -Arguments @('/S') -AllowedExitCodes @(0)
Start-Sleep -Milliseconds 500
$afterUninstall = Get-ProductInventory
if ($afterUninstall.installDirExists -or $afterUninstall.uninstallRegistry -or $afterUninstall.applicationRegistry -or $afterUninstall.startMenuShortcuts.Count -ne 0 -or $afterUninstall.serviceNames.Count -ne 0 -or $afterUninstall.scheduledTaskNames.Count -ne 0) {
    throw 'Product-owned residue survived uninstall.'
}
if (-not (Test-Path -LiteralPath $preservationMarker -PathType Leaf)) {
    throw 'AEVRIX user-data preservation marker was removed by uninstall.'
}
$actualMarker = Get-Content -LiteralPath $preservationMarker -Raw
if ($actualMarker -ne $markerContent) {
    throw 'AEVRIX user-data preservation marker changed during installer lifecycle.'
}

$installedDesktop = $afterUpgrade.files | Where-Object { $_.path -eq 'AEVRIX.Desktop.exe' } | Select-Object -First 1
$installedEngine = $afterUpgrade.files | Where-Object { $_.path -eq 'AEVRIX.EngineHost.exe' } | Select-Object -First 1
if (-not $installedDesktop -or -not $installedEngine) {
    throw 'Final installed executable hashes could not be captured.'
}

$evidenceDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($EvidencePath))
New-Item -ItemType Directory -Force -Path $evidenceDirectory | Out-Null
[pscustomobject]@{
    schemaVersion = 1
    oldVersion = $OldVersion
    newVersion = $NewVersion
    phaseExitCodes = [pscustomobject]$phaseExitCodes
    interruption = [pscustomobject]@{
        observed = $interruptionObserved
        partialSurfacePresent = $afterInterruption.installDirExists
        recoverySucceeded = $true
        mechanism = 'AVA_POST_PREREQUISITE_PRE_PAYLOAD_HOLD_15000MS'
    }
    installedExecutableHashes = [pscustomobject]@{
        desktopSha256 = $installedDesktop.sha256
        engineHostSha256 = $installedEngine.sha256
    }
    inventories = [pscustomobject]@{
        beforeInstall = $before
        afterInterruption = $afterInterruption
        afterRecovery = $afterRecovery
        afterRepair = $afterRepair
        afterUpgrade = $afterUpgrade
        afterUninstall = $afterUninstall
    }
    residueVerdict = 'PASS_NO_PRODUCT_OWNED_RESIDUE'
    userDataPreservation = 'PASS'
} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $EvidencePath -Encoding utf8NoBOM

Remove-Item -LiteralPath $preservationMarker -Force
Write-Host 'PASS: interruption -> recovery -> repair -> upgrade -> downgrade-block -> uninstall lifecycle completed; product residue absent; user data preserved.'