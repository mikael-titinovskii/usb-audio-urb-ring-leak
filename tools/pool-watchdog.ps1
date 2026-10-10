param(
  [int]$IntervalSeconds = 60,
  [int]$Threshold = 2,
  [int]$MaxLogLines = 400,
  [int]$MaxCycles = 0,
  # Composite device to restart; when empty it is resolved from the present PnP tree.
  [string]$DeviceId = '',
  # Paths are environment-specific; override on the command line if needed.
  [string]$Root = (Join-Path $env:LOCALAPPDATA 'FocusriteDiagnostics\watchdog'),
  [string]$ProbeScript = (Join-Path $PSScriptRoot 'slot-probe.ps1')
)
# Scarlett pool watchdog.
# The Focusrite 4.150.0.432 driver leaks output USB descriptors from its playback
# ring on some completions (progressive: 10 -> 9 -> ... -> 1). At 1 descriptor the
# schedule runs behind USB time and every transfer returns zero bytes = silence,
# with no Windows event, no PnP event and no service failure.
#
# This watchdog polls the driver's own ETW queue provider and, only when the pool is
# collapsed to $Threshold or fewer descriptors (i.e. audio is silent or about to be),
# restarts the Scarlett device - the only action proven to rebuild the ring.
$ErrorActionPreference = 'Continue'
$root = $Root
New-Item -ItemType Directory -Force -Path $root | Out-Null
$log = Join-Path $root 'watchdog.log'
$probeScript = $ProbeScript
$deviceId = $DeviceId
if (-not $deviceId) {
  $composite = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -like 'USB\VID_1235&PID_8218\*' -and $_.InstanceId -notmatch '&MI_' } |
    Select-Object -First 1
  if (-not $composite) {
    throw 'No present Focusrite Scarlett composite device (USB\VID_1235&PID_8218\*) found; pass -DeviceId explicitly.'
  }
  $deviceId = $composite.InstanceId
}

function Write-Log([string]$m) {
  "$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))  $m" | Out-File -LiteralPath $log -Append -Encoding utf8
  $lines = Get-Content -LiteralPath $log -ErrorAction SilentlyContinue
  if ($lines -and $lines.Count -gt $MaxLogLines) {
    $lines[-$MaxLogLines..-1] | Set-Content -LiteralPath $log -Encoding utf8
  }
}

$history = Join-Path $root 'pool-history.csv'
if (-not (Test-Path -LiteralPath $history)) {
  'timestamp,slots,indices,action' | Out-File -LiteralPath $history -Encoding utf8
}
function Write-History([int]$s, [string]$idx, [string]$action) {
  "$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss')),$s,$idx,$action" | Out-File -LiteralPath $history -Append -Encoding utf8
}

Write-Log "watchdog start (interval=${IntervalSeconds}s threshold=$Threshold maxcycles=$MaxCycles)"

$cycle = 0
while ($true) {
  $cycle++
  $slots = -1
  $detail = ''
  $probed = $false
  try {
    $raw = & powershell -NoProfile -ExecutionPolicy Bypass -File $probeScript -ProbeSeconds 2 2>$null
    $json = ($raw | Where-Object { $_ -match '"Slots"' } | Select-Object -First 1)
    if ($json) {
      $obj = $json | ConvertFrom-Json
      $slots = [int]$obj.Slots
      $detail = $obj.Detail
      $probed = $true
    }
  } catch {
    Write-Log "probe error: $($_.Exception.Message)"
  }

  if (-not $probed) {
    # no parseable probe result (e.g. not elevated, or logman missing) - not the same as an idle pool
    Write-Log 'probe unavailable (check elevation/logman)'
    Write-History -1 '' 'probe-unavailable'
  }
  elseif ($slots -eq -1) {
    # probe ran and reported no playback stream open -> pool not measurable, nothing to do
    Write-Log 'idle/no-stream (pool not measurable)'
    Write-History -1 '' 'idle'
  }
  elseif ($slots -le $Threshold) {
    Write-Log "FAULT detected: pool=$slots $detail -> restarting Scarlett"
    Write-History $slots ($detail -replace ',', ';') 'restart'
    try {
      $r = & pnputil.exe /restart-device $deviceId 2>&1
      Write-Log ("restart: " + (($r | Where-Object { $_ -match 'success|error|Failed' }) -join ' '))
    } catch { Write-Log "restart error: $($_.Exception.Message)" }
    Start-Sleep -Seconds 8
    $raw2 = & powershell -NoProfile -ExecutionPolicy Bypass -File $probeScript -ProbeSeconds 2 2>$null
    $json2 = ($raw2 | Where-Object { $_ -match '"Slots"' } | Select-Object -First 1)
    if ($json2) {
      $o2 = $json2 | ConvertFrom-Json
      Write-Log "after restart: pool=$($o2.Slots) $($o2.Detail)"
      Write-History ([int]$o2.Slots) ($o2.Detail -replace ',', ';') 'after-restart'
    }
  }
  else {
    Write-Log "ok: pool=$slots $detail"
    Write-History $slots ($detail -replace ',', ';') 'ok'
  }
  if ($MaxCycles -gt 0 -and $cycle -ge $MaxCycles) { Write-Log "max cycles reached ($cycle); exiting"; break }
  # poll faster while a stream is open (pool measurable -> silence can actually happen)
  if ($slots -ge 0 -and $IntervalSeconds -gt 30) { Start-Sleep -Seconds 30 } else { Start-Sleep -Seconds $IntervalSeconds }
}
