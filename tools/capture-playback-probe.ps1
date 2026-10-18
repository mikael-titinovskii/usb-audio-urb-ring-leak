[CmdletBinding()]
param(
    [ValidateRange(1,600)][int]$DurationSeconds = 90,
    [switch]$NonInteractive
)
$ErrorActionPreference = 'Stop'
# Captures only the Focusrite Windows playback endpoint, never a microphone.
# No administrative privileges, driver resets, or setting changes are used.
$renderRoot = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render'
$devices = @(Get-ChildItem -LiteralPath $renderRoot | ForEach-Object {
    $state = Get-ItemProperty -LiteralPath $_.PSPath
    $p = Get-ItemProperty -LiteralPath ($_.PSPath + '\Properties')
    if ($state.DeviceState -eq 1 -and $p.'{b3f8fa53-0004-438e-9003-51a46e139bfc},6' -match 'Focusrite') {
        [pscustomobject]@{ EndpointId = '{0.0.0.00000000}.' + $_.PSChildName; Name = $p.'{a45c254e-df1c-4efd-8020-67d146a850e0},2' }
    }
})
if ($devices.Count -ne 1) { throw "Expected one active Focusrite playback endpoint; found $($devices.Count)." }
if (-not ('ScarlettPlaybackProbe.Recorder' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'PlaybackProbe.cs') }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$outDir = Join-Path $PSScriptRoot "playback-probe-$stamp"
[void](New-Item -ItemType Directory -Path $outDir)
$statsPath = 'C:\ProgramData\Focusrite\Notifier\Logs\Scarlett_Solo_4th_Gen_stats.log'
if (Test-Path -LiteralPath $statsPath) { Copy-Item -LiteralPath $statsPath -Destination (Join-Path $outDir 'focusrite-stats-before.txt') }
@(& (Join-Path $PSScriptRoot 'get-audio-sessions.ps1')) | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outDir 'sessions-before.json') -Encoding UTF8
$recorder = New-Object ScarlettPlaybackProbe.Recorder
$markers = New-Object 'System.Collections.Generic.List[object]'
$interactive = -not $NonInteractive -and -not [Console]::IsInputRedirected
Write-Host 'Recording Focusrite playback locally; device settings and ASIO buffer are unchanged.'
if ($interactive) { Write-Host 'Keep Spotify playing. Press SPACE when you hear a crackle; Q stops early.' }
Write-Host "Output: $outDir"
try {
    $recorder.Begin($devices[0].EndpointId, $outDir, $DurationSeconds)
    while (-not $recorder.Started -and -not $recorder.Finished) { Start-Sleep -Milliseconds 25 }
    if ($recorder.Started) { Write-Host "Capture started: $($recorder.StartedLocal)" }
    $nextProgress = 30
    while (-not $recorder.Finished) {
        if ($interactive -and [Console]::KeyAvailable) {
            $key = [Console]::ReadKey($true)
            if ($key.Key -eq [ConsoleKey]::Spacebar) {
                $mark = [pscustomobject]@{ ElapsedSeconds = $recorder.ElapsedSeconds; LocalTime = (Get-Date -Format o); Label = 'heard-crackle' }
                $markers.Add($mark)
                $markers.ToArray() | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $outDir 'markers.json') -Encoding UTF8
                Write-Host ('Marked crackle at +{0:N2}s' -f $mark.ElapsedSeconds)
            } elseif ($key.Key -eq [ConsoleKey]::Q) { $recorder.StopRequested = $true }
        }
        if ($recorder.ElapsedSeconds -ge $nextProgress) {
            Write-Host ("Recorded {0}s / {1}s; markers: {2}" -f $nextProgress, $DurationSeconds, $markers.Count)
            $nextProgress += 30
        }
        Start-Sleep -Milliseconds 25
    }
} finally {
    $recorder.Dispose()
    [pscustomobject]@{
        StartedLocal=$recorder.StartedLocal; OriginQpc=$recorder.OriginQpc; QpcFrequency=[Diagnostics.Stopwatch]::Frequency
        EndpointId=$devices[0].EndpointId; SampleRate=$recorder.SampleRate; Channels=$recorder.Channels; Bits=$recorder.Bits
        Frames=$recorder.Frames; AudioSeconds= if ($recorder.SampleRate) { $recorder.Frames / [double]$recorder.SampleRate } else { 0 }
        Packets=$recorder.Packets; DiscontinuitiesAfterFirstPacket=$recorder.Discontinuities; InitialPacketFlags=$recorder.InitialFlags
        TimestampErrors=$recorder.TimestampErrors; PositionGaps=$recorder.PositionGaps; GapFrames=$recorder.GapFrames
        Peak=$recorder.Peak; NonFiniteSamples=$recorder.NonFiniteSamples; Markers=$markers.Count; Error=$recorder.Error
        CaptureQueueFrames=$recorder.CaptureBufferFrames
        Note='Capture queue belongs to this observer, not the Focusrite ASIO buffer. Capture discontinuities alone do not prove audible playback defects. WAV packets are concatenated; packets.tsv maps file frames to timestamps.'
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outDir 'summary.json') -Encoding UTF8
    ConvertTo-Json -InputObject @($markers.ToArray()) -Depth 3 | Set-Content -LiteralPath (Join-Path $outDir 'markers.json') -Encoding UTF8
    if (Test-Path -LiteralPath $statsPath) { Copy-Item -LiteralPath $statsPath -Destination (Join-Path $outDir 'focusrite-stats-after.txt') }
}
if ($recorder.Error) { throw $recorder.Error }
if ($recorder.Frames -eq 0) { Write-Warning 'No playback samples arrived. Make sure Spotify is playing through the Scarlett.' }
Write-Host "Saved: $outDir"
Write-Host ("Audio: {0:N1}s; marked crackles: {1}; capture discontinuities after startup: {2}" -f ($recorder.Frames / [double]$recorder.SampleRate), $markers.Count, $recorder.Discontinuities)
