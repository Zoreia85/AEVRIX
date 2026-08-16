param(
  [Parameter(Mandatory = $true)][string]$DesktopPath,
  [Parameter(Mandatory = $true)][string]$OutputPath,
  [int]$AliveSeconds = 5,
  [int]$ExitTimeoutSeconds = 5
)

$ErrorActionPreference = 'Stop'

function Write-Report {
  param([hashtable]$Report)
  $directory = Split-Path -Parent $OutputPath
  if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
  $Report | ConvertTo-Json -Depth 8 | Set-Content -Path $OutputPath -Encoding utf8
  $Report | ConvertTo-Json -Depth 8 | Write-Host
}

if (-not (Test-Path -LiteralPath $DesktopPath -PathType Leaf)) {
  Write-Report @{
    schemaVersion = 1
    status = 'FAIL'
    pass = $false
    reason = 'Desktop executable missing'
    desktopPath = $DesktopPath
  }
  exit 2
}

$resolved = (Resolve-Path -LiteralPath $DesktopPath).Path
$sha256 = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant()
$startedAt = [DateTimeOffset]::UtcNow
$process = $null
$failures = New-Object System.Collections.Generic.List[string]
$aliveAfterWindow = $false
$cleanupVerified = $false
$exitCode = $null

try {
  $process = Start-Process -FilePath $resolved -PassThru
  $pidValue = $process.Id
  Start-Sleep -Seconds $AliveSeconds
  $process.Refresh()

  if ($process.HasExited) {
    $exitCode = $process.ExitCode
    $failures.Add("Desktop exited before the ${AliveSeconds}s startup window; exitCode=$exitCode")
  } else {
    $aliveAfterWindow = $true
  }
}
catch {
  $failures.Add("Startup exception: $($_.Exception.GetType().Name): $($_.Exception.Message)")
}
finally {
  if ($null -ne $process) {
    try {
      $process.Refresh()
      if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
      }
      $deadline = [DateTime]::UtcNow.AddSeconds($ExitTimeoutSeconds)
      do {
        Start-Sleep -Milliseconds 100
        try {
          $check = Get-Process -Id $process.Id -ErrorAction Stop
          $stillAlive = $true
        } catch {
          $stillAlive = $false
        }
      } while ($stillAlive -and [DateTime]::UtcNow -lt $deadline)

      $cleanupVerified = -not $stillAlive
      if (-not $cleanupVerified) {
        $failures.Add("Desktop PID $($process.Id) remained alive after forced cleanup")
      }
    }
    catch {
      $failures.Add("Cleanup exception: $($_.Exception.GetType().Name): $($_.Exception.Message)")
    }
    finally {
      $process.Dispose()
    }
  }
}

$passed = $aliveAfterWindow -and $cleanupVerified -and $failures.Count -eq 0
$report = @{
  schemaVersion = 1
  generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
  startedAtUtc = $startedAt.ToString('o')
  status = $(if ($passed) { 'PASS' } else { 'FAIL' })
  pass = $passed
  desktopPath = $resolved
  desktopSha256 = $sha256
  processId = $(if ($null -ne $process) { $process.Id } else { $null })
  aliveWindowSeconds = $AliveSeconds
  aliveAfterWindow = $aliveAfterWindow
  cleanupVerified = $cleanupVerified
  exitCode = $exitCode
  failures = @($failures)
  scope = 'Startup/process-lifecycle smoke only; this does not prove visual correctness, first-run/terms, accessibility, or end-to-end downstream runtime.'
}

Write-Report $report
exit $(if ($passed) { 0 } else { 1 })
