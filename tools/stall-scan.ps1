param(
  [Parameter(Mandatory=$true)][string]$TracePath,
  [double]$GapMs = 5.0,
  [double]$FocusFromS = 0,
  [double]$FocusToS = 0,
  [string]$OutFile = ''
)
# Fast single-pass scan of a FocusriteUsb queue ETL for the fault chain
# (stall -> re-prime -> pairing skew -> orphan). No per-event CSV is produced,
# so it runs in seconds instead of minutes. Analysis only - run unelevated.
$ErrorActionPreference = 'Stop'
$analysisRoot = Join-Path $env:LOCALAPPDATA 'FocusriteDiagnostics\AnalysisTools'
$unsafeLoaded = [Reflection.Assembly]::LoadFrom((Join-Path $analysisRoot 'System.Runtime.CompilerServices.Unsafe.6.1.2\lib\net462\System.Runtime.CompilerServices.Unsafe.dll'))
$resolver = [ResolveEventHandler]{param($sender,$eventArgs) if($eventArgs.Name -like 'System.Runtime.CompilerServices.Unsafe,*'){return $unsafeLoaded};return $null}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
[Reflection.Assembly]::LoadFrom((Join-Path $analysisRoot 'System.Memory.4.5.5\lib\net461\System.Memory.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $analysisRoot 'System.Buffers.4.5.1\lib\net461\System.Buffers.dll')) | Out-Null
$traceAssembly = Join-Path $analysisRoot 'TraceEvent.3.2.6\lib\netstandard2.0\Microsoft.Diagnostics.Tracing.TraceEvent.dll'
$netstandard = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\netstandard\v4.0_2.0.0.0__cc7b13ffcd2ddd51\netstandard.dll'
[Reflection.Assembly]::LoadFrom($traceAssembly) | Out-Null
Add-Type -Path (Join-Path $PSScriptRoot 'StallScan.cs') -ReferencedAssemblies @($traceAssembly, $netstandard, 'System.Core.dll')
$report = [FocusriteDiagnostics.StallScan]::Analyze($TracePath, $GapMs, $FocusFromS, $FocusToS)
if ($OutFile) { $report | Out-File -LiteralPath $OutFile -Encoding utf8 }
$report
