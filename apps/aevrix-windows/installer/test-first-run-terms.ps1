[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Installer,
    [Parameter(Mandatory)] [string]$ProductVersion,
    [Parameter(Mandatory)] [string]$EvidencePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows) {
    throw 'First-run AVA requires Windows.'
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\AEVRIX'
$desktopPath = Join-Path $installDir 'AEVRIX.Desktop.exe'
$uninstallPath = Join-Path $installDir 'uninstall.exe'
$firstRunRoot = Join-Path $env:LOCALAPPDATA 'AEVRIX\UserData'
$acceptancePath = Join-Path $firstRunRoot 'first-run-acceptance.json'
$presentationPath = Join-Path $firstRunRoot 'first-run-presentation.json'
$expectedRevision = 'preview-authorized-use-v1'

function Invoke-Setup {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [string[]]$Arguments = @('/S')
    )

    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Setup '$([IO.Path]::GetFileName($Path))' exited with $($process.ExitCode)."
    }
    return [int]$process.ExitCode
}

function Find-UiElement {
    param(
        [Parameter(Mandatory)] [int]$ProcessId,
        [string]$AutomationId,
        [string]$Name
    )

    $conditions = @(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $ProcessId)
    )

    if ($AutomationId) {
        $conditions += [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            $AutomationId)
    }
    if ($Name) {
        $conditions += [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Name)
    }

    $condition = if ($conditions.Count -eq 1) {
        $conditions[0]
    }
    else {
        [System.Windows.Automation.AndCondition]::new([System.Windows.Automation.Condition[]]$conditions)
    }

    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
}

function Wait-UiElement {
    param(
        [Parameter(Mandatory)] [int]$ProcessId,
        [string]$AutomationId,
        [string]$Name,
        [int]$TimeoutSeconds = 20
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $element = Find-UiElement -ProcessId $ProcessId -AutomationId $AutomationId -Name $Name
        if ($null -ne $element) {
            return $element
        }
        Start-Sleep -Milliseconds 200
    }

    throw "UI Automation element not found within ${TimeoutSeconds}s. AutomationId='$AutomationId' Name='$Name'."
}

function Wait-File {
    param([Parameter(Mandatory)] [string]$Path, [int]$TimeoutSeconds = 20)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            return
        }
        Start-Sleep -Milliseconds 200
    }
    throw "Expected file was not created within ${TimeoutSeconds}s: $Path"
}

function Stop-TestProcess {
    param([System.Diagnostics.Process]$Process)
    if ($null -eq $Process) { return }
    $Process.Refresh()
    if (-not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        $Process.WaitForExit()
    }
}

Remove-Item -LiteralPath $acceptancePath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $presentationPath -Force -ErrorAction SilentlyContinue

if (Test-Path -LiteralPath $uninstallPath -PathType Leaf) {
    Invoke-Setup -Path $uninstallPath | Out-Null
}
if (Test-Path -LiteralPath $installDir -PathType Container) {
    Remove-Item -LiteralPath $installDir -Recurse -Force
}

$installExit = Invoke-Setup -Path $Installer
if (-not (Test-Path -LiteralPath $desktopPath -PathType Leaf)) {
    throw 'Installed Desktop executable is missing before first-run AVA.'
}

$firstProcess = $null
$secondProcess = $null
$initialAcceptDisabled = $false
$acceptanceTransitionObserved = $false
$secondLaunchSkippedFirstRun = $false

try {
    Write-Host '[first-run] Launch installed Desktop with no acceptance state'
    $firstProcess = Start-Process -FilePath $desktopPath -PassThru
    Wait-File -Path $presentationPath

    $confirm = Wait-UiElement -ProcessId $firstProcess.Id -AutomationId 'AevrixFirstRunConfirm'
    $accept = Wait-UiElement -ProcessId $firstProcess.Id -AutomationId 'AevrixFirstRunAccept'
    $initialAcceptDisabled = -not $accept.Current.IsEnabled
    if (-not $initialAcceptDisabled) {
        throw 'First-run accept button was enabled before explicit confirmation.'
    }
    if (Test-Path -LiteralPath $acceptancePath) {
        throw 'Acceptance state existed before the user accepted the first-run conditions.'
    }

    $toggle = [System.Windows.Automation.TogglePattern]$confirm.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    $toggle.Toggle()

    $accept = Wait-UiElement -ProcessId $firstProcess.Id -AutomationId 'AevrixFirstRunAccept'
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    while (-not $accept.Current.IsEnabled -and [DateTimeOffset]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
        $accept = Wait-UiElement -ProcessId $firstProcess.Id -AutomationId 'AevrixFirstRunAccept' -TimeoutSeconds 2
    }
    if (-not $accept.Current.IsEnabled) {
        throw 'First-run accept button did not enable after explicit confirmation.'
    }

    $invoke = [System.Windows.Automation.InvokePattern]$accept.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()

    Wait-File -Path $acceptancePath
    $accepted = Get-Content -LiteralPath $acceptancePath -Raw | ConvertFrom-Json
    if ([int]$accepted.SchemaVersion -ne 1 -or [string]$accepted.TermsRevision -ne $expectedRevision -or -not $accepted.AcceptedAtUtc) {
        throw 'Persisted first-run acceptance did not match the current schema/revision.'
    }

    $null = Wait-UiElement -ProcessId $firstProcess.Id -Name 'Navegação principal do AEVRIX'
    $acceptanceTransitionObserved = $true
    Stop-TestProcess -Process $firstProcess

    Write-Host '[first-run] Relaunch installed Desktop with persisted acceptance'
    $secondProcess = Start-Process -FilePath $desktopPath -PassThru
    $null = Wait-UiElement -ProcessId $secondProcess.Id -Name 'Navegação principal do AEVRIX'
    Start-Sleep -Milliseconds 500
    $firstRunAgain = Find-UiElement -ProcessId $secondProcess.Id -AutomationId 'AevrixFirstRunAccept'
    if ($null -ne $firstRunAgain) {
        throw 'First-run terms surface reappeared after current acceptance was persisted.'
    }
    $secondLaunchSkippedFirstRun = $true
}
finally {
    Stop-TestProcess -Process $firstProcess
    Stop-TestProcess -Process $secondProcess
}

$uninstallExit = Invoke-Setup -Path $uninstallPath
if (Test-Path -LiteralPath $installDir -PathType Container) {
    throw 'Product install directory survived first-run AVA uninstall.'
}
if (-not (Test-Path -LiteralPath $acceptancePath -PathType Leaf)) {
    throw 'First-run acceptance user data did not survive uninstall.'
}

$presentation = Get-Content -LiteralPath $presentationPath -Raw | ConvertFrom-Json
$acceptance = Get-Content -LiteralPath $acceptancePath -Raw | ConvertFrom-Json
$evidenceDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($EvidencePath))
New-Item -ItemType Directory -Force -Path $evidenceDirectory | Out-Null

[pscustomobject]@{
    schemaVersion = 1
    productVersion = $ProductVersion
    termsRevision = $expectedRevision
    installExitCode = $installExit
    uninstallExitCode = $uninstallExit
    presentationObserved = $true
    presentedAtUtc = [string]$presentation.PresentedAtUtc
    initialAcceptDisabled = $initialAcceptDisabled
    explicitConfirmationRequired = $true
    acceptancePersisted = $true
    acceptedAtUtc = [string]$acceptance.AcceptedAtUtc
    commandCenterTransitionObserved = $acceptanceTransitionObserved
    secondLaunchSkippedFirstRun = $secondLaunchSkippedFirstRun
    acceptanceSurvivedUninstall = $true
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $EvidencePath -Encoding utf8NoBOM

Write-Host 'PASS: installed first-run terms were presented, explicit confirmation was required, acceptance transitioned to Command Center, persisted across relaunch, and survived uninstall.'
