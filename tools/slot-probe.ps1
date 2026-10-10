param(
  [int]$ProbeSeconds = 2,
  [switch]$TestRecovery,
  # Composite device to restart; when empty it is resolved from the present PnP tree.
  [string]$DeviceId = '',
  # Paths are environment-specific; override on the command line if needed.
  [string]$Root = (Join-Path $env:LOCALAPPDATA 'FocusriteDiagnostics\probes'),
  [string]$Analyzer = (Join-Path $PSScriptRoot 'analyze-queue.ps1')
)
# Fast slot probe: bounded 2s capture of the Focusrite driver's own USB queue
# provider, then count how many OUTPUT descriptors are circulating.
# With -TestRecovery, also measure which recovery restores the pool to 10:
# (1) audio service restart, (2) device restart - testing cheapest first.
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator rights required.' }

$root = $Root
New-Item -ItemType Directory -Force -Path $root | Out-Null
$analyzer = $Analyzer
$provider = '{deadbeef-dead-beef-0005-feedfacebeef}'

function Invoke-Probe {
  param([string]$Tag)
  $id = 'SlotProbe-' + [guid]::NewGuid().ToString('N').Substring(0,8)
  $dir = Join-Path $root $id
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $etl = Join-Path $dir 'q.etl'
  $null = & logman create trace $id -ets -p $provider 0x100401 5 -o $etl -f bin -max 64 -bs 32 -nb 8 32 -ct perf 2>&1
  Start-Sleep -Seconds $ProbeSeconds
  $null = & logman stop $id -ets 2>&1
  if (-not (Test-Path -LiteralPath $etl)) { return [pscustomobject]@{ Tag=$Tag; Slots=-1; Note='no etl' } }
  $report = & powershell -NoProfile -ExecutionPolicy Bypass -File $analyzer -TracePath $etl -ReportLabel P 2>&1
  $line = ($report | Where-Object { $_ -match '^OUT submissions' -and $_ -match 'index_counts=' } | Select-Object -First 1)
  $slots = -1; $desc = ''
  if ($line) {
    $after = ($line -replace '.*index_counts=', '')
    $slots = @($after -split ',' | Where-Object { $_ -match ':' }).Count
    $desc = $line.Trim()
    $indices = (($after -split ',' | ForEach-Object { ($_ -split ':')[0].Trim() }) -join ',')
    $desc = "indices=$indices"
  }
  # keep the diagnostic folder small (probes are frequent)
  Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
  return [pscustomobject]@{ Tag=$Tag; Time=(Get-Date -Format 'HH:mm:ss'); Slots=$slots; Detail=$desc }
}

$sb = [System.Text.StringBuilder]::new()
$s0 = Invoke-Probe -Tag 'initial'
[void]$sb.AppendLine(($s0 | ConvertTo-Json -Compress))
Write-Output ($s0 | ConvertTo-Json -Compress)

if ($TestRecovery -and $s0.Slots -lt 10 -and $s0.Slots -ge 0) {
  # (1) cheapest: restart the audio service (kills audiodg, tears down all streams)
  Restart-Service Audiosrv -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 5
  $s1 = Invoke-Probe -Tag 'after-Audiosrv-restart'
  [void]$sb.AppendLine(($s1 | ConvertTo-Json -Compress))
  Write-Output ($s1 | ConvertTo-Json -Compress)

  if ($s1.Slots -lt 10) {
    # (2) device restart
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
    $null = & pnputil.exe /restart-device $deviceId 2>&1
    Start-Sleep -Seconds 8
    $s2 = Invoke-Probe -Tag 'after-device-restart'
    [void]$sb.AppendLine(($s2 | ConvertTo-Json -Compress))
    Write-Output ($s2 | ConvertTo-Json -Compress)
  }
}
$sb.ToString() | Out-File -LiteralPath (Join-Path $root 'probe-results.txt') -Append -Encoding utf8
