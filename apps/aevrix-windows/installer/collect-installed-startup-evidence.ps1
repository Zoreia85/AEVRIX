[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallDirectory,

    [Parameter(Mandatory = $true)]
    [string]$CandidateSha,

    [Parameter(Mandatory = $true)]
    [string]$EvidencePath,

    [int]$ObservationSeconds = 20
)

$ErrorActionPreference = 'Stop'

if ($CandidateSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'CandidateSha must be an exact 40-character commit SHA.'
}

$installRoot = [IO.Path]::GetFullPath($InstallDirectory)
$desktop = Join-Path $installRoot 'AEVRIX.Desktop.exe'
$diagnostic = Join-Path $env:LOCALAPPDATA 'AEVRIX\Diagnostics\startup-failure.json'

$requiredPayload = @(
    'AEVRIX.Desktop.exe',
    'AEVRIX.Desktop.dll',
    'AEVRIX.Desktop.deps.json',
    'AEVRIX.Desktop.runtimeconfig.json',
    'Microsoft.WindowsAppRuntime.Bootstrap.dll'
)

$missing = @($requiredPayload | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $installRoot $_) -PathType Leaf)
})

$desktopSha256 = if (Test-Path -LiteralPath $desktop -PathType Leaf) {
    (Get-FileHash -LiteralPath $desktop -Algorithm SHA256).Hash.ToLowerInvariant()
} else {
    $null
}

$process = $null
$startedAtUtc = $null
$observedAtUtc = [DateTimeOffset]::UtcNow
$processAliveAtObservation = $false
$exitCode = $null
$runtimeMilliseconds = $null
$startupFailure = $null

try {
    if ($missing.Count -gt 0) {
        throw "Installed self-contained payload is incomplete: $($missing -join ', ')"
    }

    Remove-Item -LiteralPath $diagnostic -Force -ErrorAction SilentlyContinue
    $startedAtUtc = [DateTimeOffset]::UtcNow
    $process = Start-Process -FilePath $desktop -PassThru
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds([Math]::Max(1, $ObservationSeconds))

    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { break }
        Start-Sleep -Milliseconds 200
    }

    $observedAtUtc = [DateTimeOffset]::UtcNow
    $process.Refresh()
    $processAliveAtObservation = -not $process.HasExited

    if ($process.HasExited) {
        $exitCode = $process.ExitCode
        $runtimeMilliseconds = [Math]::Max(0, [long]($observedAtUtc - $startedAtUtc).TotalMilliseconds)
    }

    if (Test-Path -LiteralPath $diagnostic -PathType Leaf) {
        try {
            $raw = Get-Content -LiteralPath $diagnostic -Raw | ConvertFrom-Json
            $startupFailure = [pscustomobject]@{
                schemaVersion = [int]$raw.schemaVersion
                recordedAtUtc = [string]$raw.recordedAtUtc
                stage = [string]$raw.stage
                exceptionType = [string]$raw.exceptionType
                hresult = [string]$raw.hresult
                sha256 = (Get-FileHash -LiteralPath $diagnostic -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
        catch {
            $startupFailure = [pscustomobject]@{
                parseStatus = 'INVALID_SANITIZED_DIAGNOSTIC'
                sha256 = (Get-FileHash -LiteralPath $diagnostic -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
    }
}
finally {
    if ($null -ne $process) {
        try {
            $process.Refresh()
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                $process.WaitForExit()
            }
        }
        catch { }
    }

    $record = [pscustomobject]@{
        schemaVersion = 1
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        candidateSha = $CandidateSha.ToLowerInvariant()
        installDirectoryClass = 'LOCALAPPDATA_PROGRAMS_AEVRIX'
        installedDesktopSha256 = $desktopSha256
        requiredPayload = @($requiredPayload)
        missingPayload = @($missing)
        startedAtUtc = if ($startedAtUtc) { $startedAtUtc.ToString('O') } else { $null }
        observedAtUtc = $observedAtUtc.ToString('O')
        processAliveAtObservation = $processAliveAtObservation
        exitCode = $exitCode
        runtimeMilliseconds = $runtimeMilliseconds
        startupFailure = $startupFailure
    }

    $parent = Split-Path -Parent $EvidencePath
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $EvidencePath -Encoding utf8NoBOM
    $hash = (Get-FileHash -LiteralPath $EvidencePath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($EvidencePath))" | Set-Content -LiteralPath "$EvidencePath.sha256" -Encoding utf8NoBOM
}

if ($missing.Count -gt 0) { exit 20 }
if (-not $processAliveAtObservation -and $null -eq $startupFailure) { exit 21 }
exit 0
